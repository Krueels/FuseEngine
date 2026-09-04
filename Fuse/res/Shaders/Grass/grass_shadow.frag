#version 430 core

in vec2 vUv;
in float vHeight;
in float vVariation;

void main()
{
    float centered = 1.0 - abs(vUv.x * 2.0 - 1.0);
    float serration = sin(vHeight * 37.0 + vVariation * 19.0) * 0.025;
    float coverage = centered - 0.035 + serration;
    if (coverage < 0.0 || (vHeight > 0.995 && centered < 0.28))
        discard;
}
