#version 330 core
#include "cloud_common.glsl"

in vec2 vTexCoord;
layout(location = 0) out vec4 fragColor;
layout(location = 1) out float fragDepth;

uniform sampler2D uSceneDepth;
uniform sampler2D uCloudHistory;
uniform sampler2D uCloudDepthHistory;
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
uniform vec2 uCloudHistoryTexelSize;
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
    float lightNear;
    float lightFar;
    if (!IntersectCloudLayer(position, normalize(uSunDirection), lightNear, lightFar))
        return 1.0;

    // The sun ray must stop at the actual outer shell. Using thickness divided
    // by the sun elevation overestimates the distance for a spherical layer
    // and samples the same cloud again after it has already exited the volume.
    float lightDistance = max(lightFar, 0.0);
    if (lightDistance <= 0.001)
        return 1.0;

    float lightStep = lightDistance / float(lightSteps);
    float opticalDepth = 0.0;

    vec3 sunDirection = normalize(uSunDirection);
    vec3 tangent = normalize(abs(sunDirection.y) < 0.98
        ? cross(sunDirection, vec3(0.0, 1.0, 0.0))
        : cross(sunDirection, vec3(1.0, 0.0, 0.0)));
    vec3 bitangent = normalize(cross(sunDirection, tangent));

    for (int i = 0; i < 24; ++i)
    {
        if (i >= lightSteps)
            break;

        float travel = (float(i) + 0.5) * lightStep;
        float coneRadius = travel * 0.055;
        vec2 coneOffset = ConeKernel(i % 7) * coneRadius;
        vec3 samplePosition = position + sunDirection * travel +
            tangent * coneOffset.x + bitangent * coneOffset.y;
        float sampleLength = min(
            lightStep,
            max(lightDistance - travel + 0.5 * lightStep, 0.0));
        if (sampleLength <= 0.0)
            break;

        CloudProperties lightProperties = EvaluateCloudProperties(samplePosition, false);
        opticalDepth += CloudOpticalDepthForProperties(
            lightProperties, sampleLength, uCloudAbsorption);
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

void SampleHistoryNeighborhood(
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
            uv + vec2(float(x), float(y)) * uCloudHistoryTexelSize,
            uCloudHistoryTexelSize * 0.5,
            vec2(1.0) - uCloudHistoryTexelSize * 0.5);
        vec4 sampleValue = texture(uCloudHistory, sampleUv);
        float sampleDepth = texture(uCloudDepthHistory, sampleUv).r;
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
        fragDepth = 1.0;
        return;
    }

    float startDistance = max(layerNear, 0.0);
    float endDistance = min(min(layerFar, sceneDistance), uCloudMaxDistance);
    if (endDistance <= startDistance)
    {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        fragDepth = 1.0;
        return;
    }

    int stepCount = clamp(uCloudPrimarySteps, 8, 128);
    float baseStepLength = (endDistance - startDistance) / float(stepCount);
    float rayJitter = InterleavedGradientNoise(gl_FragCoord.xy, uCloudFrameIndex);
    float transmittance = 1.0;
    vec3 cloudRadiance = vec3(0.0);
    float weightedDistance = 0.0;
    float weightTotal = 0.0;
    float cachedLightVisibility = 1.0;
    float distanceAlongRay = startDistance;
    int emptySteps = 0;
    bool coarseMode = false;

    float sunHeight = smoothstep(-0.08, 0.12, uSunDirection.y);
    float cosineTheta = dot(rayDirection, uSunDirection);
    float forwardPhase = HenyeyGreenstein(cosineTheta, uCloudAnisotropy);
    float backwardPhase = HenyeyGreenstein(cosineTheta, -0.22);
    float primaryPhase = mix(backwardPhase, forwardPhase, 0.78);
    float secondaryPhase = mix(
        HenyeyGreenstein(cosineTheta, uCloudAnisotropy * 0.35),
        HenyeyGreenstein(cosineTheta, -0.15),
        0.35);
    float multiScattering = CloudSaturate(uCloudMultiScattering);
    float phase = mix(primaryPhase, secondaryPhase, multiScattering * 0.65);
    vec3 sunRadiance = CompressedSunRadiance();

    for (int i = 0; i < 128; ++i)
    {
        if (i >= stepCount || transmittance < 0.008 ||
            distanceAlongRay >= endDistance)
            break;

        // After several empty samples, use a cheap density evaluation and a
        // longer step. When it finds the cloud again, the same segment is
        // revisited at the normal step size so thin edges are not skipped.
        float currentStepLength = coarseMode
            ? baseStepLength * 2.25
            : baseStepLength;
        float segmentJitter = fract(rayJitter + float(i) * 0.61803398875);
        float sampleDistance = min(
            distanceAlongRay + segmentJitter * currentStepLength,
            endDistance - 0.001);
        float sampleLength = min(
            currentStepLength,
            max(endDistance - distanceAlongRay, 0.001));
        vec3 samplePosition = uCameraPosition + rayDirection * sampleDistance;
        float distanceFade = 1.0 - smoothstep(
            uCloudMaxDistance * 0.80, uCloudMaxDistance, sampleDistance);
        CloudProperties cloud = EvaluateCloudProperties(samplePosition, !coarseMode);
        float density = cloud.density * distanceFade;
        if (density > 0.001)
        {
            if (coarseMode)
            {
                coarseMode = false;
                emptySteps = 0;
                continue;
            }

            cloud.density = density;
            float sampleExtinction = CloudOpticalDepthForProperties(
                cloud, sampleLength, uCloudAbsorption);
            float sampleAlpha = 1.0 - exp(-sampleExtinction);

            // Cone lighting changes more slowly than density, so one result can
            // safely serve two neighboring primary samples.
            if ((i & 1) == 0)
                cachedLightVisibility = LightTransmittance(samplePosition);
            float lightVisibility = cachedLightVisibility;

            float heightFraction = CloudSaturate(CloudHeightFraction(samplePosition));
            vec3 dayAmbientBottom = vec3(0.20, 0.25, 0.34);
            vec3 dayAmbientTop = vec3(0.42, 0.52, 0.68);
            vec3 nightAmbientBottom = vec3(0.008, 0.012, 0.025);
            vec3 nightAmbientTop = vec3(0.025, 0.040, 0.080);
            vec3 ambientBottom = mix(nightAmbientBottom, dayAmbientBottom, sunHeight);
            vec3 ambientTop = mix(nightAmbientTop, dayAmbientTop, sunHeight);
            vec3 ambientLight = mix(ambientBottom, ambientTop, heightFraction) *
                uCloudAmbientStrength * cloud.ambientOcclusion *
                mix(1.0, 0.72, cloud.rain);

            float powder = CloudPowderFactor(
                density,
                cosineTheta,
                lightVisibility,
                uCloudPowderEffect);
            vec3 directLight = sunRadiance * sunHeight * lightVisibility *
                phase * 3.25 * powder;
            vec3 multiScatterLight = sunRadiance * sunHeight *
                (1.0 - lightVisibility) * secondaryPhase *
                (2.2 * multiScattering) *
                mix(0.45, 1.0, CloudSaturate(powder - 1.0));
            vec3 sampleLight = ambientLight + directLight + multiScatterLight;

            float contribution = transmittance * sampleAlpha;
            cloudRadiance += sampleLight * contribution;
            weightedDistance += sampleDistance * contribution;
            weightTotal += contribution;
            transmittance *= 1.0 - sampleAlpha;
            distanceAlongRay += currentStepLength;
            emptySteps = 0;
        }
        else
        {
            distanceAlongRay += currentStepLength;
            if (!coarseMode)
            {
                emptySteps++;
                if (emptySteps >= 3)
                    coarseMode = true;
            }
        }
    }

    vec4 currentCloud = vec4(cloudRadiance, transmittance);
    float representativeDistance = weightTotal > 0.0001
        ? weightedDistance / weightTotal
        : uCloudMaxDistance;
    float currentCloudDepth = CloudSaturate(
        representativeDistance / max(uCloudMaxDistance, 1.0));
    if (uHistoryValid && weightTotal > 0.0001)
    {
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
            float historyDepth = texture(uCloudDepthHistory, previousUv).r;
            float depthDifference = abs(historyDepth - currentCloudDepth);

            // Neighborhood clipping removes isolated stale samples before
            // they can become persistent ghost trails. The separate depth
            // history rejects a reprojection that landed on another cloud.
            vec4 historyMinimum;
            vec4 historyMaximum;
            float depthMinimum;
            float depthMaximum;
            SampleHistoryNeighborhood(
                previousUv,
                historyMinimum,
                historyMaximum,
                depthMinimum,
                depthMaximum);
            vec4 historyMargin = vec4(0.025, 0.025, 0.025, 0.035) +
                abs(currentCloud) * 0.22;
            history = clamp(
                history,
                historyMinimum - historyMargin,
                historyMaximum + historyMargin);

            float depthMargin = 0.045 + currentCloudDepth * 0.16;
            bool depthValid = historyDepth < 0.999 &&
                depthDifference <= depthMargin &&
                historyDepth >= depthMinimum - depthMargin &&
                historyDepth <= depthMaximum + depthMargin;
            if (depthValid)
            {
                float transmittanceDifference = abs(history.a - currentCloud.a);
                float historyConfidence = exp(-transmittanceDifference * 16.0);
                float depthConfidence = exp(-depthDifference * 42.0);
                float blend = uCloudTemporalBlend * historyConfidence * depthConfidence;
                currentCloud = mix(currentCloud, history, blend);
            }
        }
    }

    fragColor = currentCloud;
    fragDepth = currentCloudDepth;
}
