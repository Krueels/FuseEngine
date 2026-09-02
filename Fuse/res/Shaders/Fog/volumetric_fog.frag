#version 330 core
#include "../skybox_common.glsl"
#include "../lighting.glsl"

in vec2 vTexCoord;

layout(location = 0) out vec4 fragColor;
layout(location = 1) out float fragDepth;

uniform sampler2D uSceneDepth;
uniform sampler2D uFogHistory;
uniform sampler2D uFogDepthHistory;
uniform sampler3D uFogNoise;
uniform sampler2DArrayShadow uDirectionalShadowMap;
uniform sampler2DArrayShadow uSpotShadowMap;
uniform samplerCube uPointShadowMap0;
uniform samplerCube uPointShadowMap1;
uniform samplerCube uPointShadowMap2;
uniform samplerCube uPointShadowMap3;

uniform mat4 uInvViewProj;
uniform mat4 uPreviousViewProj;
// lighting.glsl already exposes a uCameraPosition member through
// LightingBlock. Keep the fog camera explicit so the fullscreen pass does not
// redeclare that block member as a standalone uniform.
uniform vec3 uFogCameraPosition;
uniform vec3 uPreviousCameraPosition;
uniform vec3 uSunDirection;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform float uSunAngularRadiusDegrees;
uniform vec3 uSkyZenithColor;
uniform vec3 uSkyHorizonColor;
uniform vec3 uSkyGroundColor;
uniform vec3 uNightZenithColor;
uniform vec3 uNightHorizonColor;
uniform vec3 uStarColor;
uniform float uAtmosphereStrength;
uniform float uRayleighStrength;
uniform float uMieStrength;
uniform float uStarIntensity;
uniform float uStarDensity;
uniform float uSkyExposure;
uniform vec3 uFogAmbientColor;

uniform float uFogDensity;
uniform float uFogBaseHeight;
uniform float uFogHeightFalloff;
uniform float uFogSkyDensity;
uniform float uFogSkyHeightFalloff;
uniform float uFogMaxDistance;
uniform float uFogNoiseScale;
uniform float uFogNoiseStrength;
uniform vec2 uFogWindDirection;
uniform float uFogWindSpeed;
uniform float uFogAnisotropy;
uniform float uFogAbsorption;
uniform float uFogAmbientStrength;
uniform float uFogSunScattering;
uniform bool uFogLightShaftsEnabled;
uniform float uFogLightShaftStrength;
uniform int uFogRaySteps;
uniform float uFogTemporalBlend;
uniform float uFogTime;
uniform float uPreviousFogTime;
uniform vec2 uFogHistoryTexelSize;
uniform bool uFogNoiseEnabled;
uniform bool uHistoryValid;
uniform bool uProceduralSky;
uniform bool uDirectionalShadowEnabled;
uniform int uFogFrameIndex;

float FogHash13(vec3 value)
{
    value = fract(value * 0.1031);
    value += dot(value, value.yzx + 33.33);
    return fract((value.x + value.y) * value.z);
}

float FogValueNoise(vec3 position)
{
    vec3 cell = floor(position);
    vec3 local = fract(position);
    local = local * local * (3.0 - 2.0 * local);

    float c000 = FogHash13(cell + vec3(0.0, 0.0, 0.0));
    float c100 = FogHash13(cell + vec3(1.0, 0.0, 0.0));
    float c010 = FogHash13(cell + vec3(0.0, 1.0, 0.0));
    float c110 = FogHash13(cell + vec3(1.0, 1.0, 0.0));
    float c001 = FogHash13(cell + vec3(0.0, 0.0, 1.0));
    float c101 = FogHash13(cell + vec3(1.0, 0.0, 1.0));
    float c011 = FogHash13(cell + vec3(0.0, 1.0, 1.0));
    float c111 = FogHash13(cell + vec3(1.0, 1.0, 1.0));

    float x00 = mix(c000, c100, local.x);
    float x10 = mix(c010, c110, local.x);
    float x01 = mix(c001, c101, local.x);
    float x11 = mix(c011, c111, local.x);
    return mix(mix(x00, x10, local.y), mix(x01, x11, local.y), local.z);
}

float FogNoise(vec3 worldPosition, float time)
{
    vec3 position = worldPosition * max(uFogNoiseScale, 0.00001);
    position.xz += uFogWindDirection * uFogWindSpeed * time * 0.01;

    if (!uFogNoiseEnabled)
        return FogValueNoise(position * 3.0);

    // The cloud base volume is tileable and already generated at 128³. A
    // second, cheaper value-noise octave breaks up the otherwise obvious
    // repetition when the fog spans a large map.
    float volume = texture(uFogNoise, position).r;
    float breakup = FogValueNoise(position * 2.17 + vec3(11.0, 3.0, 7.0));
    return clamp(volume * 0.72 + breakup * 0.28, 0.0, 1.0);
}

float HeightDensity(vec3 worldPosition)
{
    float aboveBase = max(worldPosition.y - uFogBaseHeight, 0.0);
    return exp(-aboveBase / max(uFogHeightFalloff, 0.1));
}

float SkyAtmosphereDensity(vec3 worldPosition)
{
    float aboveBase = max(worldPosition.y - uFogBaseHeight, 0.0);
    return max(uFogSkyDensity, 0.0) *
        exp(-aboveBase / max(uFogSkyHeightFalloff, 0.1));
}

float FogDensityAt(vec3 worldPosition, float time)
{
    float noise = FogNoise(worldPosition, time);
    float noiseFactor = mix(1.0, mix(0.55, 1.25, noise), clamp(uFogNoiseStrength, 0.0, 1.0));
    float groundFog = max(uFogDensity, 0.0) * HeightDensity(worldPosition) * noiseFactor;
    // The atmospheric layer is intentionally independent from the dense
    // ground layer. It keeps participating media present along sky rays
    // without turning the low-altitude fog into a solid wall.
    return groundFog + SkyAtmosphereDensity(worldPosition);
}

float InterleavedFogNoise(vec2 pixel, int frameIndex)
{
    vec2 frameOffset = vec2(float(frameIndex & 7), float((frameIndex >> 3) & 7));
    return fract(52.9829189 * fract(dot(
        pixel + frameOffset * 17.0,
        vec2(0.06711056, 0.00583715))));
}

float HenyeyGreensteinFog(float cosineTheta, float anisotropy)
{
    float g = clamp(anisotropy, -0.8, 0.9);
    float squared = 1.0 - g * g;
    float denominator = pow(max(1.0 + g * g - 2.0 * g * cosineTheta, 0.001), 1.5);
    return squared / (12.5663706 * denominator);
}

float SunTransmittance(vec3 position, float time)
{
    vec3 direction = normalize(uSunDirection);
    float distanceToSample = max(uFogHeightFalloff * 4.0, 80.0);
    float stepLength = distanceToSample / 4.0;
    float opticalDepth = 0.0;

    for (int i = 0; i < 4; ++i)
    {
        float travel = (float(i) + 0.5) * stepLength;
        vec3 samplePosition = position + direction * travel;
        opticalDepth += FogDensityAt(samplePosition, time) * stepLength * uFogAbsorption;
        if (opticalDepth > 8.0)
            break;
    }
    return exp(-opticalDepth);
}

float SampleDirectionalShadow(vec3 position)
{
    if (!uDirectionalShadowEnabled || !DirectionalShadowsEnabled())
        return 1.0;

    float viewDistance = length(position - uFogCameraPosition);
    int cascade = viewDistance < uCascadeDistancesAndFade.x
        ? 0
        : (viewDistance < uCascadeDistancesAndFade.y ? 1 : 2);
    vec4 lightClip = uLightSpaceMatrices[cascade] * vec4(position, 1.0);
    if (lightClip.w <= 0.000001)
        return 1.0;

    vec3 shadowCoordinate = lightClip.xyz / lightClip.w * 0.5 + 0.5;
    if (shadowCoordinate.z <= 0.0 || shadowCoordinate.z >= 1.0 ||
        shadowCoordinate.x <= 0.0 || shadowCoordinate.x >= 1.0 ||
        shadowCoordinate.y <= 0.0 || shadowCoordinate.y >= 1.0)
    {
        return 1.0;
    }

    float bias = uShadowParams.x + uShadowParams.y *
        (1.0 - max(dot(normalize(uSunDirection), vec3(0.0, 1.0, 0.0)), 0.0));
    vec2 texelSize = 1.0 / vec2(textureSize(uDirectionalShadowMap, 0).xy);
    float visibility = 0.0;
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        vec2 offset = vec2(float(x), float(y)) * texelSize *
            max(uShadowParams.z, 1.0);
        visibility += texture(
            uDirectionalShadowMap,
            vec4(shadowCoordinate.xy + offset, float(cascade), shadowCoordinate.z - bias));
    }
    return visibility / 9.0;
}

float SampleFogPointShadowMap(int shadowMapIndex, vec3 direction)
{
    vec3 sampleDirection = normalize(direction);
    if (shadowMapIndex == 0)
        return texture(uPointShadowMap0, sampleDirection).r;
    if (shadowMapIndex == 1)
        return texture(uPointShadowMap1, sampleDirection).r;
    if (shadowMapIndex == 2)
        return texture(uPointShadowMap2, sampleDirection).r;
    if (shadowMapIndex == 3)
        return texture(uPointShadowMap3, sampleDirection).r;
    return 1.0;
}

float FogPointShadow(PointLightData light, vec3 worldPosition)
{
    int shadowMapIndex = int(round(light.colorShadowIndex.w));
    if (shadowMapIndex < 0 || shadowMapIndex >= 4)
        return 0.0;

    vec3 lightToSample = worldPosition - light.positionRadius.xyz;
    float distanceToLight = length(lightToSample);
    float compareDepth = distanceToLight * light.params.y - light.params.x;
    float storedDepth = SampleFogPointShadowMap(shadowMapIndex, lightToSample);
    float visibility = compareDepth <= storedDepth ? 1.0 : 0.0;

    if (!LightingShadowFilterEnabled())
        return 1.0 - visibility;

    // Four directional taps match the point-light shadow filtering used by
    // the opaque material shader, while keeping the fog pass affordable.
    const vec3 sampleDirections[4] = vec3[](
        vec3(1.0, 1.0, 1.0),
        vec3(1.0, -1.0, -1.0),
        vec3(-1.0, -1.0, 1.0),
        vec3(-1.0, 1.0, -1.0));
    float diskRadius = mix(0.0005, 0.0035, clamp(compareDepth, 0.0, 1.0)) *
        uShadowParams.z;
    float shadow = 0.0;
    for (int i = 0; i < 4; ++i)
    {
        float tapDepth = SampleFogPointShadowMap(
            shadowMapIndex,
            lightToSample + sampleDirections[i] * diskRadius);
        shadow += compareDepth <= tapDepth ? 0.0 : 1.0;
    }
    return shadow * 0.25;
}

float FogSpotShadow(int lightIndex, SpotLightData light, vec3 worldPosition)
{
    if (light.shadowParams.x < 0.5 || lightIndex < 0 || lightIndex >= 4)
        return 0.0;

    vec4 lightClip = uSpotLightSpaceMatrices[lightIndex] *
        vec4(worldPosition, 1.0);
    if (lightClip.w <= 0.000001)
        return 0.0;

    vec3 projected = lightClip.xyz / lightClip.w * 0.5 + 0.5;
    if (projected.z <= 0.0 || projected.z > 1.0 ||
        projected.x < 0.0 || projected.x > 1.0 ||
        projected.y < 0.0 || projected.y > 1.0)
        return 0.0;

    float bias = max(light.shadowParams.y * 0.1, 0.00001);
    if (!LightingShadowFilterEnabled())
    {
        float visibility = texture(uSpotShadowMap,
            vec4(projected.xy, float(lightIndex), projected.z - bias));
        return 1.0 - visibility;
    }

    vec2 texelSize = 1.0 / vec2(textureSize(uSpotShadowMap, 0).xy);
    float shadow = 0.0;
    const vec2 pcfOffsets[4] = vec2[](
        vec2(-0.5, -0.5), vec2(0.5, -0.5),
        vec2(-0.5, 0.5), vec2(0.5, 0.5));
    for (int i = 0; i < 4; ++i)
    {
        float visibility = texture(uSpotShadowMap, vec4(
            projected.xy + pcfOffsets[i] * texelSize * uShadowParams.z,
            float(lightIndex), projected.z - bias));
        shadow += 1.0 - visibility;
    }
    return shadow * 0.25;
}

float FogViewOpticalDepth(
    vec3 rayOrigin,
    vec3 rayDirection,
    float distanceToSample,
    float time)
{
    if (distanceToSample <= 0.001)
        return 0.0;

    const int viewSteps = 4;
    float stepLength = distanceToSample / float(viewSteps);
    float opticalDepth = 0.0;
    for (int i = 0; i < viewSteps; ++i)
    {
        float distanceAlongRay = (float(i) + 0.5) * stepLength;
        vec3 position = rayOrigin + rayDirection * distanceAlongRay;
        opticalDepth += FogDensityAt(position, time) * stepLength * uFogAbsorption;
        if (opticalDepth > 12.0)
            return 12.0;
    }
    return opticalDepth;
}

float FogLightTransmittance(
    vec3 worldPosition,
    vec3 lightPosition,
    float time)
{
    vec3 lightVector = lightPosition - worldPosition;
    float distanceToLight = length(lightVector);
    if (distanceToLight <= 0.001)
        return 1.0;

    const int lightPathSteps = 3;
    vec3 lightDirection = lightVector / distanceToLight;
    float stepLength = distanceToLight / float(lightPathSteps);
    float opticalDepth = 0.0;
    for (int i = 0; i < lightPathSteps; ++i)
    {
        float distanceAlongLight = (float(i) + 0.5) * stepLength;
        vec3 position = worldPosition + lightDirection * distanceAlongLight;
        opticalDepth += FogDensityAt(position, time) * stepLength * uFogAbsorption;
        if (opticalDepth > 12.0)
            return 0.0;
    }
    return exp(-opticalDepth);
}

vec3 EvaluatePointFogLight(
    PointLightData light,
    vec3 worldPosition,
    vec3 rayDirection)
{
    vec3 toLight = light.positionRadius.xyz - worldPosition;
    float distanceToLight = length(toLight);
    float radius = light.positionRadius.w;
    if (distanceToLight <= 0.0001 || distanceToLight >= radius)
        return vec3(0.0);

    vec3 lightDirection = toLight / distanceToLight;
    float rangeFade = clamp(1.0 - distanceToLight / max(radius, 0.0001), 0.0, 1.0);
    float attenuation = rangeFade * rangeFade /
        max(distanceToLight * distanceToLight, 0.0625);
    float phase = HenyeyGreensteinFog(
        dot(-rayDirection, lightDirection), uFogAnisotropy);
    float visibility = uFogLightShaftsEnabled
        ? 1.0 - FogPointShadow(light, worldPosition)
        : 1.0;
    float lightTransmittance = FogLightTransmittance(
        worldPosition, light.positionRadius.xyz, uFogTime);
    return light.colorShadowIndex.rgb * attenuation * phase *
        lightTransmittance * visibility;
}

vec3 EvaluateSpotFogLight(
    int lightIndex,
    SpotLightData light,
    vec3 worldPosition,
    vec3 rayDirection)
{
    vec3 toLight = light.positionRadius.xyz - worldPosition;
    float distanceToLight = length(toLight);
    float radius = light.positionRadius.w;
    if (distanceToLight <= 0.0001 || distanceToLight >= radius)
        return vec3(0.0);

    vec3 lightDirection = toLight / distanceToLight;
    float theta = -dot(lightDirection, light.directionInnerCos.xyz);
    float cone = smoothstep(
        light.colorOuterCos.w,
        light.directionInnerCos.w,
        theta);
    if (cone <= 0.0001)
        return vec3(0.0);

    float rangeFade = clamp(1.0 - distanceToLight / max(radius, 0.0001), 0.0, 1.0);
    float attenuation = rangeFade * rangeFade /
        max(distanceToLight * distanceToLight, 0.0625);
    float phase = HenyeyGreensteinFog(
        dot(-rayDirection, lightDirection), uFogAnisotropy);
    float visibility = uFogLightShaftsEnabled
        ? 1.0 - FogSpotShadow(lightIndex, light, worldPosition)
        : 1.0;
    float lightTransmittance = FogLightTransmittance(
        worldPosition, light.positionRadius.xyz, uFogTime);
    return light.colorOuterCos.rgb * attenuation * cone * phase *
        lightTransmittance * visibility;
}

bool IntersectFogLightVolume(
    vec3 rayOrigin,
    vec3 rayDirection,
    vec3 lightPosition,
    float lightRadius,
    out float enterDistance,
    out float exitDistance)
{
    vec3 toLight = lightPosition - rayOrigin;
    float projection = dot(toLight, rayDirection);
    float perpendicularSquared = max(
        dot(toLight, toLight) - projection * projection,
        0.0);
    float radiusSquared = lightRadius * lightRadius;
    if (perpendicularSquared >= radiusSquared)
        return false;

    float halfChord = sqrt(max(radiusSquared - perpendicularSquared, 0.0));
    enterDistance = projection - halfChord;
    exitDistance = projection + halfChord;
    return exitDistance > 0.0;
}

bool IntersectSpotFogVolume(
    vec3 rayOrigin,
    vec3 rayDirection,
    SpotLightData light,
    out float enterDistance,
    out float exitDistance)
{
    // Start with the same finite range used by the material light. The cone
    // test below then removes the spherical part that is outside the spot.
    if (!IntersectFogLightVolume(
            rayOrigin,
            rayDirection,
            light.positionRadius.xyz,
            light.positionRadius.w,
            enterDistance,
            exitDistance))
        return false;

    enterDistance = max(enterDistance, 0.0);
    exitDistance = min(exitDistance, light.positionRadius.w);
    if (exitDistance <= enterDistance)
        return false;

    vec3 axis = normalize(light.directionInnerCos.xyz);
    vec3 relativeOrigin = rayOrigin - light.positionRadius.xyz;
    float originAlongAxis = dot(relativeOrigin, axis);
    float rayAlongAxis = dot(rayDirection, axis);

    // A finite spotlight only exists in front of its apex. Clip the interval
    // against that half-space before solving the cone equation.
    if (abs(rayAlongAxis) <= 0.000001)
    {
        if (originAlongAxis <= 0.0)
            return false;
    }
    else if (rayAlongAxis > 0.0)
    {
        enterDistance = max(enterDistance, -originAlongAxis / rayAlongAxis);
    }
    else
    {
        exitDistance = min(exitDistance, -originAlongAxis / rayAlongAxis);
    }
    if (exitDistance <= enterDistance)
        return false;

    // radial² <= axial² * tan²(theta), written as a quadratic in the ray
    // distance. The positive-axial clip above removes the mirrored backward
    // cone. Clamping the cosine keeps very wide spots numerically stable.
    float outerCos = clamp(light.colorOuterCos.w, 0.05, 0.9999);
    float inverseCosSquared = 1.0 / max(outerCos * outerCos, 0.0025);
    float quadraticA = dot(rayDirection, rayDirection) -
        rayAlongAxis * rayAlongAxis * inverseCosSquared;
    float quadraticB = 2.0 * (dot(relativeOrigin, rayDirection) -
        originAlongAxis * rayAlongAxis * inverseCosSquared);
    float quadraticC = dot(relativeOrigin, relativeOrigin) -
        originAlongAxis * originAlongAxis * inverseCosSquared;

    const float quadraticEpsilon = 0.000001;
    if (abs(quadraticA) <= quadraticEpsilon)
    {
        if (abs(quadraticB) <= quadraticEpsilon)
        {
            if (quadraticC > 0.0)
                return false;
        }
        else
        {
            float root = -quadraticC / quadraticB;
            if (quadraticB > 0.0)
                exitDistance = min(exitDistance, root);
            else
                enterDistance = max(enterDistance, root);
        }
    }
    else
    {
        float discriminant = quadraticB * quadraticB -
            4.0 * quadraticA * quadraticC;
        if (discriminant < 0.0)
        {
            if (quadraticA > 0.0)
                return false;
        }
        else
        {
            float rootDistance = sqrt(max(discriminant, 0.0));
            float root0 = (-quadraticB - rootDistance) / (2.0 * quadraticA);
            float root1 = (-quadraticB + rootDistance) / (2.0 * quadraticA);
            float firstRoot = min(root0, root1);
            float secondRoot = max(root0, root1);
            if (quadraticA > 0.0)
            {
                enterDistance = max(enterDistance, firstRoot);
                exitDistance = min(exitDistance, secondRoot);
            }
            else if (rayAlongAxis >= 0.0)
            {
                // For a forward-facing ray the positive cone is the forward
                // branch of the double-cone quadratic.
                enterDistance = max(enterDistance, secondRoot);
            }
            else
            {
                exitDistance = min(exitDistance, firstRoot);
            }
        }
    }

    return exitDistance > enterDistance;
}

vec3 EvaluateLocalLightsAlongViewRay(
    vec3 rayOrigin,
    vec3 rayDirection,
    float endDistance,
    float time)
{
    vec3 result = vec3(0.0);
    const int pointVolumeSamples = 4;
    const int spotVolumeSamples = 8;

    for (int i = 0; i < MAX_POINT_LIGHTS; ++i)
    {
        if (i >= PointLightCount())
            break;

        PointLightData light = uPointLights[i];
        float enterDistance;
        float exitDistance;
        if (!IntersectFogLightVolume(
                rayOrigin,
                rayDirection,
                light.positionRadius.xyz,
                light.positionRadius.w,
                enterDistance,
                exitDistance))
            continue;

        enterDistance = max(enterDistance, 0.0);
        exitDistance = min(exitDistance, endDistance);
        if (exitDistance <= enterDistance)
            continue;

        float segmentLength = (exitDistance - enterDistance) / float(pointVolumeSamples);
        float viewTransmittance = exp(-FogViewOpticalDepth(
            rayOrigin, rayDirection, enterDistance, time));
        for (int sample = 0; sample < pointVolumeSamples; ++sample)
        {
            float distanceAlongRay = enterDistance +
                (float(sample) + 0.5) * segmentLength;
            vec3 worldPosition = rayOrigin + rayDirection * distanceAlongRay;
            float density = FogDensityAt(worldPosition, time);
            float sampleAlpha = 1.0 - exp(-density *
                max(uFogAbsorption, 0.01) * segmentLength);
            result += viewTransmittance * sampleAlpha *
                EvaluatePointFogLight(light, worldPosition, rayDirection);
            viewTransmittance *= 1.0 - sampleAlpha;
        }
    }

    for (int i = 0; i < MAX_SPOT_LIGHTS; ++i)
    {
        if (i >= SpotLightCount())
            break;

        SpotLightData light = uSpotLights[i];
        float enterDistance;
        float exitDistance;
        if (!IntersectSpotFogVolume(
                rayOrigin,
                rayDirection,
                light,
                enterDistance,
                exitDistance))
            continue;

        enterDistance = max(enterDistance, 0.0);
        exitDistance = min(exitDistance, endDistance);
        if (exitDistance <= enterDistance)
            continue;

        // The cone interval is analytic. Eight samples preserve the cone's
        // soft edge and its density variation without skipping a narrow beam.
        float segmentLength = (exitDistance - enterDistance) / float(spotVolumeSamples);
        float viewTransmittance = exp(-FogViewOpticalDepth(
            rayOrigin, rayDirection, enterDistance, time));
        for (int sample = 0; sample < spotVolumeSamples; ++sample)
        {
            float distanceAlongRay = enterDistance +
                (float(sample) + 0.5) * segmentLength;
            vec3 worldPosition = rayOrigin + rayDirection * distanceAlongRay;
            float density = FogDensityAt(worldPosition, time);
            float sampleAlpha = 1.0 - exp(-density *
                max(uFogAbsorption, 0.01) * segmentLength);
            result += viewTransmittance * sampleAlpha *
                EvaluateSpotFogLight(i, light, worldPosition, rayDirection);
            viewTransmittance *= 1.0 - sampleAlpha;
        }
    }

    return result * max(uFogLightShaftStrength, 0.0);
}

vec3 FogAmbient(vec3 rayDirection)
{
    if (!uProceduralSky)
        return max(uFogAmbientColor, vec3(0.0));

    vec3 ambientDirection = normalize(vec3(
        -rayDirection.x * 0.18,
        0.92,
        -rayDirection.z * 0.18));
    return EvaluateProceduralSky(
        ambientDirection,
        uSunDirection,
        uSunColor,
        uSunIntensity,
        uSunAngularRadiusDegrees,
        uSkyZenithColor,
        uSkyHorizonColor,
        uSkyGroundColor,
        uNightZenithColor,
        uNightHorizonColor,
        uAtmosphereStrength,
        uRayleighStrength,
        uMieStrength,
        uStarColor,
        uStarIntensity,
        uStarDensity) * uSkyExposure;
}

void SampleFogHistoryNeighborhood(
    vec2 uv,
    out vec4 minimumValue,
    out vec4 maximumValue,
    out float minimumDepth,
    out float maximumDepth)
{
    minimumValue = vec4(1000000.0);
    maximumValue = vec4(-1000000.0);
    minimumDepth = 1000000.0;
    maximumDepth = -1000000.0;
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        vec2 sampleUv = clamp(
            uv + vec2(float(x), float(y)) * uFogHistoryTexelSize,
            uFogHistoryTexelSize * 0.5,
            vec2(1.0) - uFogHistoryTexelSize * 0.5);
        vec4 sampleValue = texture(uFogHistory, sampleUv);
        float sampleDepth = texture(uFogDepthHistory, sampleUv).r;
        minimumValue = min(minimumValue, sampleValue);
        maximumValue = max(maximumValue, sampleValue);
        minimumDepth = min(minimumDepth, sampleDepth);
        maximumDepth = max(maximumDepth, sampleDepth);
    }
}

void main()
{
    float sceneDepth = texture(uSceneDepth, vTexCoord).r;
    vec4 farWorld = uInvViewProj * vec4(vTexCoord * 2.0 - 1.0, 1.0, 1.0);
    farWorld.xyz /= max(abs(farWorld.w), 0.000001);
    vec3 rayDirection = normalize(farWorld.xyz - uFogCameraPosition);

    vec4 sceneWorld = uInvViewProj * vec4(
        vTexCoord * 2.0 - 1.0,
        sceneDepth * 2.0 - 1.0,
        1.0);
    sceneWorld.xyz /= max(abs(sceneWorld.w), 0.000001);
    float sceneDistance = sceneDepth >= 0.999999
        ? uFogMaxDistance
        : length(sceneWorld.xyz - uFogCameraPosition);
    float endDistance = min(sceneDistance, uFogMaxDistance);

    if (endDistance <= 0.01 ||
        (uFogDensity <= 0.000001 && uFogSkyDensity <= 0.000001))
    {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        fragDepth = 1.0;
        return;
    }

    int stepCount = clamp(uFogRaySteps, 8, 128);
    float stepLength = endDistance / float(stepCount);
    float rayJitter = InterleavedFogNoise(gl_FragCoord.xy, uFogFrameIndex);
    float transmittance = 1.0;
    vec3 inScattering = vec3(0.0);
    float weightedDistance = 0.0;
    float weightTotal = 0.0;
    float cachedSunVisibility = 1.0;
    float sunHeight = smoothstep(-0.10, 0.16, uSunDirection.y);
    float phase = HenyeyGreensteinFog(dot(rayDirection, normalize(uSunDirection)), uFogAnisotropy);
    vec3 ambientLight = FogAmbient(rayDirection) * max(uFogAmbientStrength, 0.0);
    float distanceAlongRay = rayJitter * stepLength;

    for (int i = 0; i < 128; ++i)
    {
        if (i >= stepCount || transmittance < 0.004 || distanceAlongRay >= endDistance)
            break;

        float sampleDistance = min(distanceAlongRay + 0.5 * stepLength, endDistance);
        vec3 samplePosition = uFogCameraPosition + rayDirection * sampleDistance;
        float density = FogDensityAt(samplePosition, uFogTime);
        float extinction = density * max(uFogAbsorption, 0.01);
        float sampleAlpha = 1.0 - exp(-extinction * stepLength);

        if ((i & 1) == 0)
        {
            float shadowVisibility = uFogLightShaftsEnabled
                ? SampleDirectionalShadow(samplePosition)
                : 1.0;
            float shaftStrength = clamp(uFogLightShaftStrength, 0.0, 4.0);
            // Keep ordinary fog scattering available at low strength while
            // making shadowed regions noticeably darker as shaft strength
            // increases. This creates visible beams without adding a second
            // screen-space god-ray pass.
            float shaftVisibility = mix(1.0, shadowVisibility,
                min(shaftStrength, 1.0));
            shaftVisibility = pow(max(shaftVisibility, 0.0),
                1.0 + max(shaftStrength - 1.0, 0.0) * 2.0);
            cachedSunVisibility = SunTransmittance(samplePosition, uFogTime) *
                shaftVisibility;
        }

        vec3 directLight = uSunColor * uSunIntensity *
            max(uFogSunScattering, 0.0) * sunHeight *
            cachedSunVisibility * phase;
        vec3 sampleLight = ambientLight + directLight;
        float contribution = transmittance * sampleAlpha;
        inScattering += sampleLight * contribution;
        weightedDistance += sampleDistance * contribution;
        weightTotal += contribution;
        transmittance *= 1.0 - sampleAlpha;
        distanceAlongRay += stepLength;
    }

    // Local lights are integrated over the exact camera-ray/light-volume
    // intersection. This prevents a small point or spot light from vanishing
    // in sky pixels when the coarse atmospheric raymarch steps over it.
    vec3 localLightScattering = EvaluateLocalLightsAlongViewRay(
        uFogCameraPosition,
        rayDirection,
        endDistance,
        uFogTime);
    bool localLightsPresent = PointLightCount() > 0 || SpotLightCount() > 0;

    vec4 currentFog = vec4(inScattering, transmittance);
    float representativeDistance = weightTotal > 0.0001
        ? weightedDistance / weightTotal
        : uFogMaxDistance;
    float currentFogDepth = clamp(representativeDistance / max(uFogMaxDistance, 1.0), 0.0, 1.0);

    // The fog history texture also stores the current output. Local lights
    // can enter or leave the camera ray when the player turns, so reusing
    // that texture while any local source exists would preserve the old light
    // as a false duplicate behind the camera.
    if (uHistoryValid && weightTotal > 0.0001 && !localLightsPresent)
    {
        vec3 representativeWorld = uFogCameraPosition + rayDirection * representativeDistance;
        vec2 wind = uFogWindDirection * uFogWindSpeed *
            (uFogTime - uPreviousFogTime) * 0.01;
        representativeWorld.xz += wind;

        vec4 previousClip = uPreviousViewProj * vec4(representativeWorld, 1.0);
        vec2 previousUv = previousClip.xy / max(abs(previousClip.w), 0.000001) * 0.5 + 0.5;
        if (previousClip.w > 0.0 &&
            all(greaterThanEqual(previousUv, vec2(0.001))) &&
            all(lessThanEqual(previousUv, vec2(0.999))))
        {
            vec4 history = texture(uFogHistory, previousUv);
            float historyDepth = texture(uFogDepthHistory, previousUv).r;
            float depthDifference = abs(historyDepth - currentFogDepth);

            vec4 historyMinimum;
            vec4 historyMaximum;
            float depthMinimum;
            float depthMaximum;
            SampleFogHistoryNeighborhood(
                previousUv,
                historyMinimum,
                historyMaximum,
                depthMinimum,
                depthMaximum);
            vec4 historyMargin = vec4(0.02, 0.02, 0.02, 0.025) + abs(currentFog) * 0.18;
            history = clamp(history, historyMinimum - historyMargin, historyMaximum + historyMargin);

            float depthMargin = 0.08 + currentFogDepth * 0.18;
            bool depthValid = historyDepth < 0.999 &&
                depthDifference <= depthMargin &&
                historyDepth >= depthMinimum - depthMargin &&
                historyDepth <= depthMaximum + depthMargin;
            if (depthValid)
            {
                float confidence = exp(-depthDifference * 16.0) *
                    exp(-abs(history.a - currentFog.a) * 5.0);
                float blend = clamp(uFogTemporalBlend * confidence, 0.0, 0.95);
                currentFog = mix(currentFog, history, blend);
            }
        }
    }

    // Local lights are deliberately added after temporal reprojection. Their
    // finite volumes can enter or leave the camera ray when the player turns;
    // keeping them out of the history prevents a previous light from being
    // reprojected as a false duplicate behind the camera.
    currentFog.rgb += localLightScattering;
    fragColor = currentFog;
    fragDepth = currentFogDepth;
}
