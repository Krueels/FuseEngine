#version 330 core

in vec2 vTexCoord;
layout(location = 0) out vec4 fragColor;

uniform sampler2D uSceneColor;
uniform sampler2D uCloudColor;
uniform sampler2D uCloudDepth;
uniform sampler2D uSceneDepth;
uniform vec2 uCloudTexelSize;
uniform bool uDepthAwareUpsample;
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

float CloudAmount(vec4 cloud)
{
    return clamp(1.0 - cloud.a, 0.0, 1.0);
}

vec4 BilateralCloudUpsample(vec2 uv)
{
    vec4 centerCloud = texture(uCloudColor, uv);
    float centerSceneDepth = texture(uSceneDepth, uv).r;
    float centerCloudDepth = texture(uCloudDepth, uv).r;
    float centerCloudAmount = CloudAmount(centerCloud);

    // Convert the full-resolution UV into the continuous low-resolution
    // texel coordinate. A small 3x3 reconstruction is used instead of a
    // single bilinear footprint. The ray marcher is intentionally jittered;
    // the extra neighboring samples remove the remaining stipple pattern
    // without increasing the expensive ray-march resolution.
    vec2 lowCoordinate = uv / max(uCloudTexelSize, vec2(0.000001)) - 0.5;
    vec2 lowBase = floor(lowCoordinate);
    vec2 lowFraction = fract(lowCoordinate);
    vec2 lowUvMinimum = uCloudTexelSize * 0.5;
    vec2 lowUvMaximum = vec2(1.0) - lowUvMinimum;

    vec4 accumulatedCloud = vec4(0.0);
    float accumulatedWeight = 0.0;
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        vec2 spatialOffset = vec2(float(x), float(y)) - lowFraction;
        float weight = exp(-dot(spatialOffset, spatialOffset) * 0.78);
        if (weight <= 0.00001)
            continue;

        vec2 candidateUv = (lowBase + vec2(float(x), float(y)) + 0.5) *
            uCloudTexelSize;
        candidateUv = clamp(candidateUv, lowUvMinimum, lowUvMaximum);

        vec4 candidateCloud = texture(uCloudColor, candidateUv);
        float candidateSceneDepth = texture(uSceneDepth, candidateUv).r;
        float candidateCloudDepth = texture(uCloudDepth, candidateUv).r;

        // Scene depth protects the cloud edge against bleeding over nearby
        // geometry. The threshold stays permissive for distant geometry and
        // the sky, where the scene depth is normally exactly 1.
        float sceneDepthDifference = abs(candidateSceneDepth - centerSceneDepth);
        float sceneDepthWeight = exp(-sceneDepthDifference * 96.0);

        // Cloud depth is linear distance inside the cloud pass. Only compare
        // it when both samples contain cloud; at a cloud edge keep a partial
        // contribution so the reconstructed silhouette remains smooth.
        float candidateCloudAmount = CloudAmount(candidateCloud);
        float cloudDepthWeight = 1.0;
        if (centerCloudAmount > 0.02 && candidateCloudAmount > 0.02)
        {
            float cloudDepthDifference = abs(candidateCloudDepth - centerCloudDepth);
            cloudDepthWeight = exp(-cloudDepthDifference * 10.0);
        }
        else if (centerCloudAmount > 0.02 || candidateCloudAmount > 0.02)
        {
            cloudDepthWeight = 0.82;
        }

        weight *= sceneDepthWeight * cloudDepthWeight;
        accumulatedCloud += candidateCloud * weight;
        accumulatedWeight += weight;
    }

    return accumulatedWeight > 0.00001
        ? accumulatedCloud / accumulatedWeight
        : centerCloud;
}

void main()
{
    vec3 scene = texture(uSceneColor, vTexCoord).rgb;
    if (uSceneIsSrgb)
        scene = SrgbToLinear(scene);

    vec4 cloud = uDepthAwareUpsample
        ? BilateralCloudUpsample(vTexCoord)
        : texture(uCloudColor, vTexCoord);
    vec3 composed = scene * cloud.a + cloud.rgb;
    if (uOutputSrgb)
        composed = LinearToSrgb(composed);
    fragColor = vec4(composed, 1.0);
}
