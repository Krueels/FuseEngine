#version 430 core

layout(location = 0) in vec3 aPos;

out vec3 vWorldPosition;
out vec3 vWorldNormal;

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
uniform sampler2D uWaveBand0;
uniform sampler2D uWaveBand1;
uniform sampler2D uWaveBand2;
uniform float uWaveBandWorldSize0;
uniform float uWaveBandWorldSize1;
uniform float uWaveBandWorldSize2;

const float PI = 3.14159265359;
const float TWO_PI = 6.28318530718;

vec2 ForwardDirection()
{
    return length(uWaveDirection) > 0.001
        ? normalize(uWaveDirection)
        : vec2(1.0, 0.0);
}

vec3 SampleBand(sampler2D bandTexture, float worldSize, vec2 worldPosition)
{
    vec2 uv = fract(worldPosition / max(worldSize, 1.0));
    // The compute texture stores horizontal X, height, horizontal Z.
    return textureLod(bandTexture, uv, 0.0).xyz;
}

vec3 SampleWaveDisplacement(vec2 worldPosition)
{
    if (!uUseWaveTextures)
    {
        vec2 forward = ForwardDirection();
        vec2 side = vec2(-forward.y, forward.x);
        vec3 displacement = vec3(0.0);

        const vec2 directions[12] = vec2[](
            vec2(1.00,  0.06), vec2(-0.74,  0.67), vec2(0.38, -0.93), vec2(0.86, -0.51),
            vec2(0.97, -0.23), vec2(-0.48,  0.88), vec2(0.15, -1.00), vec2(0.72,  0.69),
            vec2(1.00,  0.31), vec2(-0.83,  0.55), vec2(0.52, -0.86), vec2(-0.18, -0.99));
        const float amplitudes[12] = float[](
            0.42, 0.18, 0.09, 0.035,
            0.14, 0.08, 0.045, 0.025,
            0.055, 0.032, 0.018, 0.010);
        const float lengths[12] = float[](
            24.0, 14.0, 8.0, 4.5,
            12.0, 7.0, 4.0, 2.6,
            3.8, 2.4, 1.5, 0.9);
        const float speeds[12] = float[](
            0.42, 0.48, 0.56, 0.64,
            0.78, 0.86, 0.95, 1.04,
            1.26, 1.34, 1.47, 1.61);
        const float phases[12] = float[](
            0.17, 2.41, 4.88, 1.33,
            1.79, 4.20, 0.62, 3.51,
            3.14, 0.91, 5.37, 2.28);

        for (int i = 0; i < 12; ++i)
        {
            vec2 direction = normalize(
                forward * directions[i].x + side * directions[i].y);
            float waveNumber = TWO_PI / max(uWaveLength * lengths[i] / 24.0, 0.45);
            float phase = waveNumber * dot(direction, worldPosition) -
                uWaveTime * uWaveSpeed * speeds[i] + phases[i];
            float componentAmplitude = uWaveAmplitude * amplitudes[i];
            displacement += vec3(
                direction.x * componentAmplitude * uWaveChoppiness * cos(phase),
                componentAmplitude * (sin(phase) + 0.075 * sin(phase * 2.0 + 0.31)),
                direction.y * componentAmplitude * uWaveChoppiness * cos(phase));
        }
        return displacement;
    }

    return SampleBand(uWaveBand0, uWaveBandWorldSize0, worldPosition) +
        SampleBand(uWaveBand1, uWaveBandWorldSize1, worldPosition) +
        SampleBand(uWaveBand2, uWaveBandWorldSize2, worldPosition);
}

void main()
{
    // aPos is generated as an adaptive continuous grid. The cells are small
    // near the camera and widen toward the far edge of the ocean patch.
    vec2 samplePosition = aPos.xz * uOceanSize + uOceanOrigin.xz;
    vec3 displacement = SampleWaveDisplacement(samplePosition);

    float normalStep = uUseWaveTextures
        ? clamp(uWaveBandWorldSize2 / 128.0, 0.25, 2.0)
        : max(uWaveLength * 0.0125, 0.25);
    vec3 displacementX = SampleWaveDisplacement(samplePosition + vec2(normalStep, 0.0));
    vec3 displacementZ = SampleWaveDisplacement(samplePosition + vec2(0.0, normalStep));

    vec3 worldPosition = vec3(
        samplePosition.x + displacement.x,
        uWaterLevel + displacement.y,
        samplePosition.y + displacement.z);
    vec3 tangentX = vec3(
        1.0 + (displacementX.x - displacement.x) / normalStep,
        (displacementX.y - displacement.y) / normalStep,
        (displacementX.z - displacement.z) / normalStep);
    vec3 tangentZ = vec3(
        (displacementZ.x - displacement.x) / normalStep,
        (displacementZ.y - displacement.y) / normalStep,
        1.0 + (displacementZ.z - displacement.z) / normalStep);
    vec3 worldNormal = normalize(cross(tangentZ, tangentX));

    vWorldPosition = worldPosition;
    vWorldNormal = worldNormal;
    gl_Position = uProj * uView * vec4(worldPosition, 1.0);
}
