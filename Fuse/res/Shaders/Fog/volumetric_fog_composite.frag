#version 330 core

in vec2 vTexCoord;
layout(location = 0) out vec4 fragColor;

uniform sampler2D uSceneColor;
uniform sampler2D uFogColor;
uniform vec2 uFogTexelSize;
uniform bool uUpsampleFog;
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

vec4 SampleFog(vec2 uv)
{
    if (!uUpsampleFog)
        return texture(uFogColor, uv);

    // The fog buffer is linear filtered, but four taps suppress the visible
    // checkerboard/jitter pattern when it runs at half resolution.
    vec2 offset = uFogTexelSize * 0.35;
    vec4 result = texture(uFogColor, uv - vec2(offset.x, offset.y));
    result += texture(uFogColor, uv + vec2(offset.x, -offset.y));
    result += texture(uFogColor, uv + vec2(-offset.x, offset.y));
    result += texture(uFogColor, uv + vec2(offset.x, offset.y));
    return result * 0.25;
}

void main()
{
    vec3 scene = texture(uSceneColor, vTexCoord).rgb;
    if (uSceneIsSrgb)
        scene = SrgbToLinear(scene);

    vec4 fog = SampleFog(vTexCoord);
    vec3 composed = scene * clamp(fog.a, 0.0, 1.0) + fog.rgb;
    if (uOutputSrgb)
        composed = LinearToSrgb(composed);
    fragColor = vec4(composed, 1.0);
}
