#version 430 core

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 BrightColor;
layout(location = 2) out vec4 NormalColor;
layout(location = 3) out vec4 MaterialColor;

in vec2 vUv;
in vec3 vNormal;
in vec3 vViewDirection;
in float vHeight;
in float vVariation;
flat in int vLod;
flat in int vSpecies;

uniform vec3 uLightDirection;
uniform vec3 uLightColor;
uniform float uAmbient;
uniform vec3 uRootColor;
uniform vec3 uMidColor;
uniform vec3 uTipColor;
uniform vec3 uSpeciesTint[4];
uniform int uSpeciesCount;
uniform float uAmbientOcclusion;
uniform float uTranslucency;
uniform bool uDebugLodColors;
uniform bool uOutputSrgb;

vec3 LinearToSrgb(vec3 value)
{
    vec3 lo = value * 12.92;
    vec3 hi = 1.055 * pow(max(value, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055;
    return mix(hi, lo, lessThanEqual(value, vec3(0.0031308)));
}

void main()
{
    float centered = 1.0 - abs(vUv.x * 2.0 - 1.0);
    float serration = sin(vHeight * 37.0 + vVariation * 19.0) * 0.025;
    float coverage = centered - 0.035 + serration;
    if (coverage < 0.0 || vHeight > 0.995 && centered < 0.28)
        discard;

    vec3 baseColor = vHeight < 0.52
        ? mix(uRootColor, uMidColor, smoothstep(0.0, 0.52, vHeight))
        : mix(uMidColor, uTipColor, smoothstep(0.52, 1.0, vHeight));
    baseColor *= mix(0.82, 1.18, vVariation);
    int species = clamp(vSpecies, 0, max(uSpeciesCount - 1, 0));
    baseColor *= uSpeciesTint[species];

    vec3 normal = normalize(vNormal);
    vec3 lightDirection = normalize(uLightDirection);
    float frontLight = max(dot(normal, lightDirection), 0.0);
    float backLight = max(dot(-normal, lightDirection), 0.0) * 0.38;
    float rootOcclusion = mix(1.0 - uAmbientOcclusion, 1.0, smoothstep(0.0, 0.48, vHeight));
    vec3 lighting = vec3(max(uAmbient, 0.015)) * rootOcclusion;
    lighting += uLightColor * (frontLight + backLight);

    float transmission = pow(max(dot(-lightDirection, normalize(vViewDirection)), 0.0), 4.0);
    transmission *= smoothstep(0.18, 1.0, vHeight) * uTranslucency;
    vec3 color = baseColor * lighting + baseColor * uLightColor * transmission;

    if (uDebugLodColors)
        color = vLod == 0 ? vec3(0.1, 1.0, 0.15) :
                (vLod == 1 ? vec3(1.0, 0.75, 0.05) : vec3(1.0, 0.12, 0.08));

    vec3 outputColor = uOutputSrgb ? LinearToSrgb(color) : color;
    FragColor = vec4(outputColor, 1.0);
    BrightColor = vec4(max(color - vec3(1.0), vec3(0.0)), 1.0);
    NormalColor = vec4(normal * 0.5 + 0.5, 1.0);
    MaterialColor = vec4(0.78, 0.0, 1.0, 0.0);
}
