#version 430 core

in vec2 vTexCoord;
layout(location = 0) out vec4 fragColor;

uniform sampler2D uSceneColor;
uniform sampler2D uSceneDepth;
uniform sampler2D uWaterSurfaceData;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPosition;
uniform vec3 uUnderwaterColor;
uniform vec3 uSunDirection;
uniform vec3 uSunColor;
uniform float uUnderwaterFogDensity;
uniform float uUnderwaterDistortion;
uniform float uUnderwaterDarkening;
uniform float uTime;
uniform float uCameraSubmersion;
uniform float uWaterlineSoftness;
uniform bool uSceneIsSrgb;
uniform bool uOutputSrgb;

vec3 SrgbToLinear(vec3 color)
{
    return pow(max(color, vec3(0.0)), vec3(2.2));
}

vec3 LinearToSrgb(vec3 color)
{
    return pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
}

vec3 ReconstructWorld(vec2 uv, float depth)
{
    vec4 clipPosition = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 worldPosition = uInvViewProj * clipPosition;
    float safeW = abs(worldPosition.w) > 0.00001
        ? worldPosition.w
        : (worldPosition.w < 0.0 ? -0.00001 : 0.00001);
    return worldPosition.xyz / safeW;
}

float SampleSurfaceDepth(vec2 uv, float fallbackDepth)
{
    vec4 surfaceData = texture(uWaterSurfaceData, uv);
    return surfaceData.g > 0.001 ? surfaceData.r : fallbackDepth;
}

void main()
{
    float depth = texture(uSceneDepth, vTexCoord).r;
    bool hasSceneDepth = depth < 0.9999;
    vec3 visibleWorld = ReconstructWorld(vTexCoord, depth);

    vec3 farWorld = ReconstructWorld(vTexCoord, 1.0);
    vec3 rayDelta = farWorld - uCameraPosition;
    vec3 rayDirection = length(rayDelta) > 0.0001
        ? normalize(rayDelta)
        : vec3(0.0, 0.0, -1.0);

    float visibleDistance = hasSceneDepth
        ? max(dot(visibleWorld - uCameraPosition, rayDirection), 0.0)
        : 100000.0;

    // R/G/B/A contain depth, coverage and the exact rasterized surface
    // normal written by water_surface_data.frag. No independent wave field is
    // evaluated here, so the underwater boundary is the same boundary that
    // the visible ocean produced.
    vec4 surfaceData = texture(uWaterSurfaceData, vTexCoord);
    float waterCoverage = clamp(surfaceData.g, 0.0, 1.0);
    bool hasWaterSurface = waterCoverage > 0.001 && surfaceData.r < 0.9999;
    vec3 waterWorld = hasWaterSurface
        ? ReconstructWorld(vTexCoord, surfaceData.r)
        : uCameraPosition;
    float waterDistance = hasWaterSurface
        ? max(dot(waterWorld - uCameraPosition, rayDirection), 0.0)
        : -1.0;
    bool waterBeforeScene = hasWaterSurface && waterDistance > 0.0;

    // Surface coverage means "the ocean surface is visible at this pixel";
    // it is not an underwater mask. Using it directly tinted the visible
    // ocean with underwater fog whenever the camera approached the surface.
    // The camera-side query is supplied from the same displaced wave field as
    // the visible mesh. For a submerged camera, pixels occupied by the water
    // surface itself stay on the surface shading path; the remaining pixels
    // are the scene seen through/under the surface. This preserves a stable
    // screen-space waterline when the camera straddles a wave.
    float waterlineSoftness = max(uWaterlineSoftness, 0.001);
    float fullUnderwater = smoothstep(
        -waterlineSoftness,
        waterlineSoftness,
        uCameraSubmersion);
    float waterMask = fullUnderwater * (1.0 - waterCoverage);

    // Refraction follows the rasterized surface instead of a second animated
    // screen-space wave. The depth gradient gives the projected slope and the
    // stored normal keeps the response stable on shallow slopes.
    vec2 texelSize = 1.0 / vec2(textureSize(uWaterSurfaceData, 0));
    float leftDepth = SampleSurfaceDepth(
        clamp(vTexCoord - vec2(texelSize.x, 0.0), vec2(0.001), vec2(0.999)),
        surfaceData.r);
    float rightDepth = SampleSurfaceDepth(
        clamp(vTexCoord + vec2(texelSize.x, 0.0), vec2(0.001), vec2(0.999)),
        surfaceData.r);
    float downDepth = SampleSurfaceDepth(
        clamp(vTexCoord - vec2(0.0, texelSize.y), vec2(0.001), vec2(0.999)),
        surfaceData.r);
    float upDepth = SampleSurfaceDepth(
        clamp(vTexCoord + vec2(0.0, texelSize.y), vec2(0.001), vec2(0.999)),
        surfaceData.r);
    vec2 depthGradient = clamp(
        vec2(rightDepth - leftDepth, upDepth - downDepth),
        vec2(-0.08),
        vec2(0.08));
    vec2 surfaceNormalXZ = hasWaterSurface
        ? surfaceData.ba * 2.0 - 1.0
        : vec2(0.0);
    vec2 surfaceDistortion = (
        depthGradient * 0.25 + surfaceNormalXZ * 0.02) *
        uUnderwaterDistortion;
    vec2 distortedUv = clamp(
        vTexCoord + surfaceDistortion * waterMask,
        vec2(0.001),
        vec2(0.999));

    vec3 airColor = texture(uSceneColor, vTexCoord).rgb;
    vec3 waterColor = texture(uSceneColor, distortedUv).rgb;
    if (uSceneIsSrgb)
    {
        airColor = SrgbToLinear(airColor);
        waterColor = SrgbToLinear(waterColor);
    }
    vec3 color = mix(airColor, waterColor, waterMask);

    float distanceThroughWater = hasSceneDepth
        ? visibleDistance
        : 1000.0;
    if (waterBeforeScene)
        distanceThroughWater = min(distanceThroughWater, waterDistance);

    float fog = 1.0 - exp(-max(uUnderwaterFogDensity, 0.0) *
        max(distanceThroughWater, 0.0));
    float effectiveFog = fog * waterMask;
    color = mix(color, uUnderwaterColor, clamp(effectiveFog, 0.0, 1.0));
    color *= 1.0 - uUnderwaterDarkening *
        clamp(effectiveFog, 0.0, 1.0);

    vec3 causticWorld = hasSceneDepth
        ? visibleWorld
        : uCameraPosition + rayDirection * min(distanceThroughWater, 1000.0);
    float caustic = 0.5 + 0.5 * sin(
        causticWorld.x * 0.12 + uTime * 2.0) *
        sin(causticWorld.z * 0.16 - uTime * 1.4);
    vec3 safeSunDirection = length(uSunDirection) > 0.001
        ? normalize(uSunDirection)
        : vec3(0.0, 1.0, 0.0);
    float sunAmount = max(safeSunDirection.y, 0.0);
    color += max(uSunColor, vec3(0.0)) * caustic * sunAmount *
        (1.0 - effectiveFog) * waterMask * 0.025;

    if (uOutputSrgb)
        color = LinearToSrgb(color);
    fragColor = vec4(max(color, vec3(0.0)), 1.0);
}
