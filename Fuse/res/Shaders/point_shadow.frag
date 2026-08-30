#version 330 core
in vec3 vWorldPos;
in vec2 vTexCoord;

uniform vec3 uLightPos;
uniform float uRadius;
uniform bool uShadowAlphaMask;
uniform bool uShadowUseAlphaTexture;
uniform sampler2D uShadowAlphaTexture;
uniform float uShadowAlpha;
uniform float uShadowAlphaCutoff;

void main()
{
    if (uShadowAlphaMask) {
        float alpha = uShadowUseAlphaTexture ? texture(uShadowAlphaTexture, vTexCoord).a : uShadowAlpha;
        if (alpha < uShadowAlphaCutoff)
            discard;
    }
    float dist = length(vWorldPos - uLightPos);
    gl_FragDepth = clamp(dist / uRadius, 0.0, 1.0);
}
