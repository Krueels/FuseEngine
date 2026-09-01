#version 330 core
#include "cloud_common.glsl"

in vec2 vTexCoord;
layout(location = 0) out vec4 fragColor;

uniform sampler2D uSceneDepth;
uniform sampler2D uCloudHistory;
uniform mat4 uInvViewProj;
uniform mat4 uPreviousViewProj;
uniform vec3 uCameraPosition;
uniform vec3 uSunDirection;
uniform vec3 uSunColor;
uniform float uCloudMaxDistance;
uniform int uCloudPrimarySteps;
uniform int uCloudLightSteps;
uniform float uCloudTemporalBlend;
uniform float uCloudAnisotropy;
uniform float uCloudAbsorption;
uniform float uCloudAmbientStrength;
uniform float uPreviousCloudTime;
uniform bool uHistoryValid;
uniform int uCloudFrameIndex;

float InterleavedGradientNoise(vec2 pixel, int frameIndex)
{
    vec2 frameOffset = vec2(float(frameIndex & 7), float((frameIndex >> 3) & 7));
    return fract(52.9829189 * fract(dot(pixel + frameOffset * 17.0, vec2(0.06711056, 0.00583715))));
}

vec2 ConeKernel(int index)
{
    if (index == 0) return vec2( 0.000,  0.000);
    if (index == 1) return vec2( 0.535,  0.151);
    if (index == 2) return vec2(-0.297,  0.584);
    if (index == 3) return vec2(-0.642, -0.226);
    if (index == 4) return vec2( 0.168, -0.771);
    if (index == 5) return vec2( 0.797, -0.337);
    return vec2(-0.173, 0.918);
}

float LightTransmittance(vec3 position)
{
    int lightSteps = max(uCloudLightSteps, 1);
    float lightDistance = uCloudThickness / max(abs(uSunDirection.y), 0.16);
    float lightStep = lightDistance / float(lightSteps);
    float opticalDepth = 0.0;

    vec3 tangent = normalize(abs(uSunDirection.y) < 0.98
        ? cross(uSunDirection, vec3(0.0, 1.0, 0.0))
        : cross(uSunDirection, vec3(1.0, 0.0, 0.0)));
    vec3 bitangent = normalize(cross(uSunDirection, tangent));

    for (int i = 0; i < 24; ++i)
    {
        if (i >= lightSteps)
            break;

        float travel = (float(i) + 0.65) * lightStep;
        float coneRadius = travel * 0.055;
        vec2 coneOffset = ConeKernel(i % 7) * coneRadius;
        vec3 samplePosition = position + uSunDirection * travel +
            tangent * coneOffset.x + bitangent * coneOffset.y;
        opticalDepth += CloudOpticalDepth(
            SampleCloudDensity(samplePosition), lightStep, uCloudAbsorption);
        if (opticalDepth > 8.0)
            break;
    }

    return exp(-opticalDepth);
}

vec3 CompressedSunRadiance()
{
    float maximumChannel = max(max(uSunColor.r, uSunColor.g), uSunColor.b);
    if (maximumChannel <= 0.0001)
        return vec3(0.0);

    vec3 tint = uSunColor / maximumChannel;
    // Very warm editor lights previously clipped the cloud to fluorescent
    // yellow. Keep their hue, but limit and gently desaturate their radiance.
    tint = mix(vec3(1.0), tint, 0.62);
    return tint * min(maximumChannel, 2.5);
}

void main()
{
    float sceneDepth = texture(uSceneDepth, vTexCoord).r;
    vec4 farWorld = uInvViewProj * vec4(vTexCoord * 2.0 - 1.0, 1.0, 1.0);
    farWorld.xyz /= max(abs(farWorld.w), 0.000001);
    vec3 rayDirection = normalize(farWorld.xyz - uCameraPosition);

    vec4 sceneWorld = uInvViewProj * vec4(vTexCoord * 2.0 - 1.0, sceneDepth * 2.0 - 1.0, 1.0);
    sceneWorld.xyz /= max(abs(sceneWorld.w), 0.000001);
    float sceneDistance = sceneDepth >= 0.999999
        ? uCloudMaxDistance
        : length(sceneWorld.xyz - uCameraPosition);

    float layerNear;
    float layerFar;
    if (!IntersectCloudLayer(uCameraPosition, rayDirection, layerNear, layerFar))
    {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    float startDistance = max(layerNear, 0.0);
    float endDistance = min(min(layerFar, sceneDistance), uCloudMaxDistance);
    if (endDistance <= startDistance)
    {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    int stepCount = clamp(uCloudPrimarySteps, 8, 128);
    float stepLength = (endDistance - startDistance) / float(stepCount);
    float rayJitter = InterleavedGradientNoise(gl_FragCoord.xy, uCloudFrameIndex);
    float transmittance = 1.0;
    vec3 cloudRadiance = vec3(0.0);
    float weightedDistance = 0.0;
    float weightTotal = 0.0;
    float cachedLightVisibility = 1.0;

    float sunHeight = smoothstep(-0.08, 0.12, uSunDirection.y);
    float cosineTheta = dot(rayDirection, uSunDirection);
    float forwardPhase = HenyeyGreenstein(cosineTheta, uCloudAnisotropy);
    float backwardPhase = HenyeyGreenstein(cosineTheta, -0.22);
    float phase = mix(backwardPhase, forwardPhase, 0.78);
    vec3 sunRadiance = CompressedSunRadiance();

    for (int i = 0; i < 128; ++i)
    {
        if (i >= stepCount || transmittance < 0.008)
            break;

        // A different low-discrepancy offset inside every segment removes the
        // coherent horizontal slices produced by one shared offset per ray.
        float segmentJitter = fract(rayJitter + float(i) * 0.61803398875);
        float distanceAlongRay = startDistance +
            (float(i) + segmentJitter) * stepLength;
        vec3 samplePosition = uCameraPosition + rayDirection * distanceAlongRay;
        float distanceFade = 1.0 - smoothstep(
            uCloudMaxDistance * 0.80, uCloudMaxDistance, distanceAlongRay);
        float density = SampleCloudDensity(samplePosition) * distanceFade;
        if (density > 0.001)
        {
            float sampleExtinction = CloudOpticalDepth(
                density, stepLength, uCloudAbsorption);
            float sampleAlpha = 1.0 - exp(-sampleExtinction);

            // Cone lighting changes more slowly than density, so one result can
            // safely serve two neighboring primary samples.
            if ((i & 1) == 0)
                cachedLightVisibility = LightTransmittance(samplePosition);
            float lightVisibility = cachedLightVisibility;

            float heightFraction = CloudSaturate(
                (samplePosition.y - uCloudBaseHeight) / max(uCloudThickness, 0.001));
            vec3 dayAmbientBottom = vec3(0.20, 0.25, 0.34);
            vec3 dayAmbientTop = vec3(0.42, 0.52, 0.68);
            vec3 nightAmbientBottom = vec3(0.008, 0.012, 0.025);
            vec3 nightAmbientTop = vec3(0.025, 0.040, 0.080);
            vec3 ambientBottom = mix(nightAmbientBottom, dayAmbientBottom, sunHeight);
            vec3 ambientTop = mix(nightAmbientTop, dayAmbientTop, sunHeight);
            vec3 ambientLight = mix(ambientBottom, ambientTop, heightFraction) *
                uCloudAmbientStrength;

            float powder = 1.0 - exp(-sampleExtinction * 2.0);
            float silverLining = 0.14 + phase * 1.65;
            vec3 directLight = sunRadiance * sunHeight * lightVisibility *
                silverLining * mix(0.72, 1.08, powder);
            vec3 sampleLight = ambientLight + directLight;

            float contribution = transmittance * sampleAlpha;
            cloudRadiance += sampleLight * contribution;
            weightedDistance += distanceAlongRay * contribution;
            weightTotal += contribution;
            transmittance *= 1.0 - sampleAlpha;
        }
    }

    vec4 currentCloud = vec4(cloudRadiance, transmittance);
    if (uHistoryValid && weightTotal > 0.0001)
    {
        float representativeDistance = weightedDistance / weightTotal;
        vec3 representativeWorld = uCameraPosition + rayDirection * representativeDistance;
        vec2 wind = uCloudWindDirection * uCloudWindSpeed *
            (uCloudTime - uPreviousCloudTime);
        representativeWorld.xz += wind;
        vec4 previousClip = uPreviousViewProj * vec4(representativeWorld, 1.0);
        vec2 previousUv = previousClip.xy / max(abs(previousClip.w), 0.000001) * 0.5 + 0.5;

        if (previousClip.w > 0.0 && all(greaterThanEqual(previousUv, vec2(0.001))) &&
            all(lessThanEqual(previousUv, vec2(0.999))))
        {
            vec4 history = texture(uCloudHistory, previousUv);
            float transmittanceDifference = abs(history.a - currentCloud.a);
            float historyConfidence = exp(-transmittanceDifference * 13.0);

            // A small neighborhood clamp prevents stale bright layers from
            // accumulating when the camera or the clouds move.
            vec3 colorRadius = vec3(0.075) + abs(currentCloud.rgb) * 0.40;
            history.rgb = clamp(
                history.rgb,
                currentCloud.rgb - colorRadius,
                currentCloud.rgb + colorRadius);
            history.a = clamp(history.a, currentCloud.a - 0.10, currentCloud.a + 0.10);

            float blend = uCloudTemporalBlend * historyConfidence;
            currentCloud = mix(currentCloud, history, blend);
        }
    }

    fragColor = currentCloud;
}
