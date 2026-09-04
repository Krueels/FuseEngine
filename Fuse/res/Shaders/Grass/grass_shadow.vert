#version 430 core

#include "grass_deform.glsl"

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aUv;

struct InstanceData {
    vec4 positionHeight;
    vec4 normalWidth;
    vec4 parameters;
};

layout(std430, binding = 6) readonly buffer DrawInstanceBuffer {
    InstanceData instances[];
};

uniform mat4 uLightSpaceMatrix;

out vec2 vUv;
out float vHeight;
out float vVariation;

void main()
{
    InstanceData instance = instances[gl_InstanceID];
    GrassDeformation deformation = DeformGrassBlade(
        aPosition,
        instance.positionHeight,
        instance.normalWidth,
        instance.parameters);
    vec3 worldPosition = uCameraPosition + instance.positionHeight.xyz + deformation.offset;
    vUv = aUv;
    vHeight = deformation.height;
    vVariation = deformation.variation;
    gl_Position = uLightSpaceMatrix * vec4(worldPosition, 1.0);
}
