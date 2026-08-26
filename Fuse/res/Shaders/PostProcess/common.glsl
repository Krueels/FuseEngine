#version 330 core
layout(location = 0) out vec4 FragColor;

uniform int uPass;
uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uExposure;
uniform float uBloomStrength;
uniform float uBloomThreshold;
uniform float uBloomKnee;
uniform vec2 uTexelSize;
uniform int uDebugView;
uniform int uKawaseRadius;
uniform int uKawaseIterations;

uniform float uBloomScale;
uniform vec3 uBloomTint;
uniform float uBloomAnamorphicRatio;

uniform sampler2D uDepth;
uniform mat4 uInvViewProj;
uniform mat4 uPrevVP;
uniform float uMotionBlurIntensity;
uniform int uMotionBlurSamples;
uniform vec2 uScreenSize;

// SSAO
uniform sampler2D uSsao;
uniform float uSsaoIntensity;

in vec2 vTexCoord;

vec3 ToneMapACES(vec3 x) {
    x *= uExposure;
    return (x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14);
}