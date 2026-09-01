#version 330 core
#include "skybox_common.glsl"
in vec3 vWorldPos;

out vec4 fragColor;

uniform sampler2D uSkyTexture;
uniform bool uOutputSrgb;
uniform bool uProceduralSky;
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

void main() {
    vec3 dir = normalize(vWorldPos);
    vec3 color;
    if (uProceduralSky) {
        color = EvaluateProceduralSky(
            dir,
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
    } else {
        vec2 uv = vec2(
            atan(dir.z, dir.x) * 0.15915494 + 0.5,
            asin(clamp(dir.y, -0.9999, 0.9999)) * 0.31830988 + 0.5
        );
        uv.x = uv.x * 0.9999 + 0.00005;
        color = texture(uSkyTexture, uv).rgb;
    }
    if (uOutputSrgb)
        color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
    fragColor = vec4(color, 1.0);
}
