#version 330 core

in vec2 vTexCoord;
layout(location = 0) out vec4 fragColor;

uniform sampler2D uSceneColor;
uniform sampler2D uCloudColor;
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

void main()
{
    vec3 scene = texture(uSceneColor, vTexCoord).rgb;
    if (uSceneIsSrgb)
        scene = SrgbToLinear(scene);

    vec4 cloud = texture(uCloudColor, vTexCoord);
    if (uDepthAwareUpsample)
    {
        float centerDepth = texture(uSceneDepth, vTexCoord).r;
        vec2 lowPixel = floor(vTexCoord / uCloudTexelSize - 0.5) + 0.5;
        float bestDifference = 2.0;
        vec4 bestCloud = cloud;
        for (int y = 0; y <= 1; ++y)
        for (int x = 0; x <= 1; ++x)
        {
            vec2 candidateUv = (lowPixel + vec2(float(x), float(y))) * uCloudTexelSize;
            candidateUv = clamp(candidateUv, uCloudTexelSize * 0.5, vec2(1.0) - uCloudTexelSize * 0.5);
            float candidateDepth = texture(uSceneDepth, candidateUv).r;
            float difference = abs(candidateDepth - centerDepth);
            if (difference < bestDifference)
            {
                bestDifference = difference;
                bestCloud = texture(uCloudColor, candidateUv);
            }
        }
        cloud = bestCloud;
    }
    vec3 composed = scene * cloud.a + cloud.rgb;
    if (uOutputSrgb)
        composed = LinearToSrgb(composed);
    fragColor = vec4(composed, 1.0);
}
