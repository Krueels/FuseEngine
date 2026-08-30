#version 330 core

in vec2 vTexCoord;

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
    // gl_FragDepth is automatically set
}
