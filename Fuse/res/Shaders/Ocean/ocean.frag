#version 430 core

in vec3 vWorldPosition;
in vec3 vWorldNormal;
in vec2 vWaveSlope;
in vec3 vWaveDisplacement;
in float vWaveFoam;

layout(early_fragment_tests) in;
layout(location = 0) out vec4 fragColor;
layout(rgba16f, binding = 0) uniform image2D uWaterSurfaceDataImage;

uniform sampler2D uSceneColor;
uniform sampler2D uSceneDepth;
uniform samplerCube uPrefilteredEnvMap;
uniform bool uUseIbl;
uniform float uIblIntensity;

uniform mat4 uInvViewProj;
uniform vec3 uCameraPosition;
uniform vec3 uSunDirection;
uniform vec3 uSunColor;
uniform vec3 uSkyZenithColor;
uniform vec3 uSkyHorizonColor;
uniform vec3 uSkyGroundColor;
uniform float uWaterLevel;
uniform float uReflectionStrength;
uniform float uRefractionStrength;
uniform float uAbsorptionDistance;
uniform float uSurfaceRoughness;
uniform vec3 uShallowColor;
uniform vec3 uDeepColor;
uniform vec3 uFoamColor;
uniform float uFoamStrength;
uniform float uFoamDepth;
uniform float uWaveAmplitude;
uniform float uWaveTime;
uniform vec2 uWaveDirection;
uniform sampler2D uOceanNormalMap;
uniform bool uUseOceanNormalMap;
uniform float uOceanNormalMapStrength;
uniform float uOceanNormalMapScale;
uniform float uOceanNormalMapDistortion;
uniform int uDebugView;
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
    return worldPosition.xyz / max(worldPosition.w, 0.00001);
}

vec3 SampleSkyReflection(vec3 direction)
{
    float height = clamp(direction.y * 0.5 + 0.5, 0.0, 1.0);
    if (height < 0.5)
    {
        float groundFactor = smoothstep(0.0, 0.5, height);
        return mix(uSkyGroundColor, uSkyHorizonColor, groundFactor);
    }
    return mix(uSkyHorizonColor, uSkyZenithColor, (height - 0.5) * 2.0);
}

vec3 DecodeOceanNormal(vec3 encoded)
{
    vec3 normal = encoded * 2.0 - 1.0;
    normal.y = max(normal.y, 0.05);
    return normalize(normal);
}

vec2 OceanNormalMapWarp(vec2 worldPosition)
{
    float distortion = max(uOceanNormalMapDistortion, 0.0);
    vec2 safeDirection = length(uWaveDirection) > 0.001
        ? normalize(uWaveDirection)
        : vec2(1.0, 0.0);
    vec2 baseCoordinates = worldPosition *
        max(uOceanNormalMapScale, 0.001);
    vec2 spectralWarp = vWaveDisplacement.xz *
        max(uOceanNormalMapScale, 0.001) * distortion;
    vec2 slopeWarp = vec2(vWaveSlope.y, -vWaveSlope.x) *
        distortion * 0.08;
    float phaseA = dot(baseCoordinates, vec2(1.31, -0.77)) +
        uWaveTime * 0.43;
    float phaseB = dot(baseCoordinates, vec2(-0.64, 1.17)) -
        uWaveTime * 0.31;
    vec2 domainWarp = vec2(sin(phaseA), cos(phaseB)) *
        (0.028 * distortion);
    vec2 flow = safeDirection * uWaveTime * 0.006;
    return spectralWarp + slopeWarp + domainWarp + flow;
}

vec3 ApplyOceanNormalDetail(vec3 baseNormal)
{
    if (!uUseOceanNormalMap || uOceanNormalMapStrength <= 0.001)
        return baseNormal;

    float scale = max(uOceanNormalMapScale, 0.001);
    vec2 worldPosition = vWorldPosition.xz;
    vec2 baseUv = worldPosition * scale;
    vec2 warp = OceanNormalMapWarp(worldPosition);
    vec2 flow = length(uWaveDirection) > 0.001
        ? normalize(uWaveDirection) * uWaveTime * 0.006
        : vec2(uWaveTime * 0.006, 0.0);

    // Two incommensurate samples break up the texture's own repetition. The
    // warp follows the simulated displacement and a slow procedural field, so
    // this adds fine surface detail without inventing a second macro wave.
    vec3 normalA = DecodeOceanNormal(texture(
        uOceanNormalMap,
        baseUv + warp).rgb);
    vec3 normalB = DecodeOceanNormal(texture(
        uOceanNormalMap,
        baseUv * 1.71 - flow * 0.63 + warp.yx * 0.72 +
        vec2(0.37, -0.23)).rgb);
    vec3 detailTangent = normalize(normalA + normalB);

    vec3 tangentX = vec3(1.0, 0.0, 0.0) -
        baseNormal * baseNormal.x;
    if (length(tangentX) < 0.001)
        tangentX = vec3(0.0, 0.0, 1.0) -
            baseNormal * baseNormal.z;
    tangentX = normalize(tangentX);
    vec3 tangentZ = normalize(cross(tangentX, baseNormal));
    vec3 detailWorld = normalize(
        tangentX * detailTangent.x +
        baseNormal * detailTangent.y +
        tangentZ * detailTangent.z);

    return normalize(mix(
        baseNormal,
        detailWorld,
        clamp(uOceanNormalMapStrength, 0.0, 1.0)));
}

void main()
{
    vec3 normal = normalize(vWorldNormal);
    vec3 viewDirection = normalize(uCameraPosition - vWorldPosition);
    // The ocean is rendered double-sided near the waterline. Face the normal
    // towards the camera for a stable Fresnel response on both sides.
    if (dot(normal, viewDirection) < 0.0)
        normal = -normal;
    normal = ApplyOceanNormalDetail(normal);

    imageStore(
        uWaterSurfaceDataImage,
        ivec2(gl_FragCoord.xy),
        vec4(
            gl_FragCoord.z,
            1.0,
            normal.x * 0.5 + 0.5,
            normal.z * 0.5 + 0.5));

    if (uDebugView != 0)
    {
        vec3 debugColor = vec3(0.0);
        if (uDebugView == 1)
        {
            float height = 0.5 +
                (vWorldPosition.y - uWaterLevel) /
                max(abs(uWaveAmplitude) * 2.0, 0.001);
            debugColor = vec3(clamp(height, 0.0, 1.0));
        }
        else if (uDebugView == 2)
        {
            float slope = length(vWaveSlope);
            debugColor = vec3(clamp(slope * 0.35, 0.0, 1.0));
        }
        else
        {
            debugColor = clamp(
                vec3(0.5) + vWaveDisplacement * 0.5,
                vec3(0.0),
                vec3(1.0));
        }
        fragColor = vec4(debugColor, 1.0);
        return;
    }

    float nDotV = max(dot(normal, viewDirection), 0.0);
    const float waterIor = 1.333;
    float f0 = pow((1.0 - waterIor) / (1.0 + waterIor), 2.0);
    float fresnel = f0 + (1.0 - f0) *
        pow(1.0 - nDotV, 5.0);

    vec2 sceneUv = gl_FragCoord.xy /
        vec2(textureSize(uSceneDepth, 0));
    float sceneDepth = texture(uSceneDepth, sceneUv).r;
    bool hasSceneSurface = sceneDepth < 0.9999;
    vec3 scenePosition = hasSceneSurface
        ? ReconstructWorld(sceneUv, sceneDepth)
        : vWorldPosition +
          normalize(vWorldPosition - uCameraPosition) *
          uAbsorptionDistance;

    float waterDepth = hasSceneSurface
        ? max(vWorldPosition.y - scenePosition.y, 0.0)
        : uAbsorptionDistance * 2.0;
    waterDepth /= max(abs(dot(normal, viewDirection)), 0.2);
    float depthFactor = clamp(
        waterDepth / max(uAbsorptionDistance, 0.001),
        0.0,
        1.0);
    float transmittance = exp(
        -waterDepth / max(uAbsorptionDistance, 0.001));

    vec2 screenUv = gl_FragCoord.xy /
        vec2(textureSize(uSceneColor, 0));
    vec2 refractionUv = clamp(
        screenUv + normal.xz * uRefractionStrength *
        (0.25 + depthFactor * 0.75),
        vec2(0.001),
        vec2(0.999));
    vec3 scene = texture(uSceneColor, refractionUv).rgb;
    if (uSceneIsSrgb)
        scene = SrgbToLinear(scene);

    vec3 waterBodyColor = mix(
        uShallowColor,
        uDeepColor,
        depthFactor);
    vec3 refractedColor = scene * transmittance +
        waterBodyColor * (1.0 - transmittance);

    vec3 reflectionDirection = reflect(-viewDirection, normal);
    vec3 reflectedColor = SampleSkyReflection(reflectionDirection);
    if (uUseIbl)
    {
        float mip = clamp(uSurfaceRoughness, 0.0, 1.0) * 5.0;
        reflectedColor = textureLod(
            uPrefilteredEnvMap,
            reflectionDirection,
            mip).rgb;
        reflectedColor *= max(uIblIntensity, 0.0);
    }

    vec3 sunDirection = normalize(uSunDirection);
    float sunHighlight = pow(
        max(dot(reflectionDirection, sunDirection), 0.0),
        mix(96.0, 900.0, 1.0 -
            clamp(uSurfaceRoughness, 0.0, 1.0)));
    reflectedColor += max(uSunColor, vec3(0.0)) *
        sunHighlight * 0.35;

    float reflectionWeight = clamp(
        fresnel * max(uReflectionStrength, 0.0),
        0.0,
        1.0);
    vec3 color = mix(
        refractedColor,
        reflectedColor,
        reflectionWeight);

    // Steep slopes receive less ambient light. Foam is driven by the same
    // spectral slope/crest data used to displace the mesh, never by a second
    // analytic wave set.
    float slopeFoam = smoothstep(
        0.42,
        1.35,
        length(vWaveSlope));
    float foam = max(slopeFoam, vWaveFoam) *
        (1.0 - smoothstep(
            0.0,
            max(uFoamDepth, 0.001),
            waterDepth)) *
        uFoamStrength;
    color = mix(
        color * mix(0.72, 1.0, clamp(normal.y, 0.0, 1.0)),
        uFoamColor,
        clamp(foam, 0.0, 1.0));

    if (uOutputSrgb)
        color = LinearToSrgb(color);
    fragColor = vec4(max(color, vec3(0.0)), 1.0);
}
