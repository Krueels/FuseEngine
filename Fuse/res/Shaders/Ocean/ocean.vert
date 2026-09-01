#version 430 core

layout(location = 0) in vec3 aPos;

out vec3 vWorldPosition;
out vec3 vWorldNormal;
out vec2 vWaveSlope;
out vec3 vWaveDisplacement;
out float vWaveFoam;

uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uOceanOrigin;
uniform float uWaterLevel;
uniform float uOceanSize;
uniform float uWaveTime;
uniform float uWaveAmplitude;
uniform float uWaveLength;
uniform float uWaveSpeed;
uniform float uWaveChoppiness;
uniform vec2 uWaveDirection;

uniform bool uUseWaveTextures;
uniform sampler2D uWaveSurface0;
uniform sampler2D uWaveSurface1;
uniform sampler2D uWaveSurface2;
uniform sampler2D uWaveSlope0;
uniform sampler2D uWaveSlope1;
uniform sampler2D uWaveSlope2;
uniform float uWavePatchSize0;
uniform float uWavePatchSize1;
uniform float uWavePatchSize2;
uniform vec2 uWaveOffset0;
uniform vec2 uWaveOffset1;
uniform vec2 uWaveOffset2;

const float TWO_PI = 6.28318530718;

vec2 ForwardDirection()
{
    return length(uWaveDirection) > 0.001
        ? normalize(uWaveDirection)
        : vec2(1.0, 0.0);
}

vec4 SampleSurface(int band, vec2 worldPosition)
{
    if (band == 0)
    {
        vec2 uv = fract((worldPosition + uWaveOffset0) /
            max(uWavePatchSize0, 1.0));
        return textureLod(uWaveSurface0, uv, 0.0);
    }
    if (band == 1)
    {
        vec2 uv = fract((worldPosition + uWaveOffset1) /
            max(uWavePatchSize1, 1.0));
        return textureLod(uWaveSurface1, uv, 0.0);
    }

    vec2 uv = fract((worldPosition + uWaveOffset2) /
        max(uWavePatchSize2, 1.0));
    return textureLod(uWaveSurface2, uv, 0.0);
}

vec2 SampleSlope(int band, vec2 worldPosition)
{
    if (band == 0)
    {
        vec2 uv = fract((worldPosition + uWaveOffset0) /
            max(uWavePatchSize0, 1.0));
        return textureLod(uWaveSlope0, uv, 0.0).rg;
    }
    if (band == 1)
    {
        vec2 uv = fract((worldPosition + uWaveOffset1) /
            max(uWavePatchSize1, 1.0));
        return textureLod(uWaveSlope1, uv, 0.0).rg;
    }

    vec2 uv = fract((worldPosition + uWaveOffset2) /
        max(uWavePatchSize2, 1.0));
    return textureLod(uWaveSlope2, uv, 0.0).rg;
}

vec3 SampleWaveDisplacement(vec2 worldPosition)
{
    if (!uUseWaveTextures)
    {
        vec2 forward = ForwardDirection();
        vec2 side = vec2(-forward.y, forward.x);
        vec3 displacement = vec3(0.0);
        const vec2 directions[4] = vec2[](
            vec2(1.00, 0.05),
            vec2(-0.62, 0.78),
            vec2(0.37, -0.93),
            vec2(0.91, -0.42));
        const float lengths[4] = float[](1.0, 0.52, 0.23, 0.10);
        const float amplitudes[4] = float[](0.62, 0.24, 0.10, 0.04);

        for (int i = 0; i < 4; ++i)
        {
            vec2 direction = normalize(
                forward * directions[i].x + side * directions[i].y);
            float wavelength = max(uWaveLength * lengths[i], 0.5);
            float waveNumber = TWO_PI / wavelength;
            float phase = dot(worldPosition, direction) * waveNumber -
                uWaveTime * uWaveSpeed * (0.7 + float(i) * 0.2);
            float amplitude = uWaveAmplitude * amplitudes[i];
            displacement += vec3(
                direction.x * amplitude * uWaveChoppiness * cos(phase),
                amplitude * sin(phase),
                direction.y * amplitude * uWaveChoppiness * cos(phase));
        }
        return displacement;
    }

    vec3 displacement = vec3(0.0);
    for (int band = 0; band < 3; ++band)
    {
        vec4 surface = SampleSurface(band, worldPosition);
        displacement += vec3(surface.r, surface.g, surface.b);
    }
    return displacement;
}

vec2 SampleWaveSlope(vec2 worldPosition)
{
    if (!uUseWaveTextures)
        return vec2(0.0);

    return SampleSlope(0, worldPosition) +
        SampleSlope(1, worldPosition) +
        SampleSlope(2, worldPosition);
}

float SampleWaveFoam(vec2 worldPosition)
{
    if (!uUseWaveTextures)
        return 0.0;

    return max(
        SampleSurface(0, worldPosition).a,
        max(
            SampleSurface(1, worldPosition).a,
            SampleSurface(2, worldPosition).a));
}

void main()
{
    // aPos is an adaptive continuous grid. The simulation resolution remains
    // fixed at 128² while the geometry concentrates vertices near the camera.
    vec2 samplePosition = aPos.xz * uOceanSize + uOceanOrigin.xz;
    vec3 displacement = SampleWaveDisplacement(samplePosition);

    float normalStep = uUseWaveTextures
        ? clamp(uWavePatchSize2 / 128.0, 0.20, 2.0)
        : max(uWaveLength * 0.0125, 0.25);
    vec3 displacementX = SampleWaveDisplacement(
        samplePosition + vec2(normalStep, 0.0));
    vec3 displacementZ = SampleWaveDisplacement(
        samplePosition + vec2(0.0, normalStep));

    vec2 slope = SampleWaveSlope(samplePosition);
    if (!uUseWaveTextures)
    {
        slope = vec2(
            (displacementX.y - displacement.y) / normalStep,
            (displacementZ.y - displacement.y) / normalStep);
    }

    vec3 worldPosition = vec3(
        samplePosition.x + displacement.x,
        uWaterLevel + displacement.y,
        samplePosition.y + displacement.z);
    vec3 tangentX = vec3(
        1.0 + (displacementX.x - displacement.x) / normalStep,
        slope.x,
        (displacementX.z - displacement.z) / normalStep);
    vec3 tangentZ = vec3(
        (displacementZ.x - displacement.x) / normalStep,
        slope.y,
        1.0 + (displacementZ.z - displacement.z) / normalStep);
    vec3 worldNormal = normalize(cross(tangentZ, tangentX));

    vWorldPosition = worldPosition;
    vWorldNormal = worldNormal;
    vWaveSlope = slope;
    vWaveDisplacement = displacement;
    vWaveFoam = SampleWaveFoam(samplePosition);
    gl_Position = uProj * uView * vec4(worldPosition, 1.0);
}
