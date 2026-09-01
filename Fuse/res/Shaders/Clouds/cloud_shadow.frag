#version 330 core
#include "cloud_common.glsl"

in vec2 vTexCoord;
layout(location = 0) out float fragShadow;

uniform vec2 uCloudShadowCenter;
uniform float uCloudShadowExtent;
uniform vec3 uSunDirection;
uniform float uCloudAbsorption;

void main()
{
    if (uSunDirection.y <= 0.015)
    {
        fragShadow = 1.0;
        return;
    }

    vec2 worldXZ = uCloudShadowCenter + (vTexCoord * 2.0 - 1.0) * uCloudShadowExtent;
    vec3 start = vec3(worldXZ.x, uCloudBaseHeight + 0.01, worldXZ.y);
    float travelDistance = uCloudThickness / max(uSunDirection.y, 0.05);
    const int shadowSteps = 16;
    float stepLength = travelDistance / float(shadowSteps);
    float opticalDepth = 0.0;

    for (int i = 0; i < shadowSteps; ++i)
    {
        vec3 samplePosition = start + uSunDirection * (float(i) + 0.5) * stepLength;
        opticalDepth += CloudOpticalDepth(
            SampleCloudDensity(samplePosition), stepLength, uCloudAbsorption);
    }

    fragShadow = exp(-opticalDepth);
}
