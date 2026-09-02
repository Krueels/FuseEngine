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
uniform float uFogMaxDistance;
uniform float uFogNoiseScale;
uniform float uFogNoiseStrength;
uniform vec2 uFogWindDirection;
uniform float uFogWindSpeed;
uniform float uFogAnisotropy;
uniform float uFogAbsorption;
uniform float uFogAmbientStrength;
uniform float uFogSunScattering;
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

float FogDensityAt(vec3 worldPosition, float time)
{
    float noise = FogNoise(worldPosition, time);
    float noiseFactor = mix(1.0, mix(0.55, 1.25, noise), clamp(uFogNoiseStrength, 0.0, 1.0));
    return max(uFogDensity, 0.0) * HeightDensity(worldPosition) * noiseFactor;
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

    if (endDistance <= 0.01 || uFogDensity <= 0.000001)
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
            cachedSunVisibility = SunTransmittance(samplePosition, uFogTime) *
                SampleDirectionalShadow(samplePosition);

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

    vec4 currentFog = vec4(inScattering, transmittance);
    float representativeDistance = weightTotal > 0.0001
        ? weightedDistance / weightTotal
        : uFogMaxDistance;
    float currentFogDepth = clamp(representativeDistance / max(uFogMaxDistance, 1.0), 0.0, 1.0);

    if (uHistoryValid && weightTotal > 0.0001)
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

    fragColor = currentFog;
    fragDepth = currentFogDepth;
}
