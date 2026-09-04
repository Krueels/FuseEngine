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

uniform mat4 uView;
uniform mat4 uProj;

out vec2 vUv;
out vec3 vNormal;
out vec3 vViewDirection;
out float vHeight;
out float vVariation;
flat out int vLod;
flat out int vSpecies;

void main()
{
    InstanceData instance = instances[gl_InstanceID];
    int encodedVariant = int(instance.parameters.w + 0.5);
    int lod = encodedVariant & 3;
    int species = encodedVariant >> 2;
    GrassDeformation deformation = DeformGrassBlade(
        aPosition,
        instance.positionHeight,
        instance.normalWidth,
        instance.parameters);
    vec3 relativePosition = instance.positionHeight.xyz + deformation.offset;
    vec3 viewPosition = mat3(uView) * relativePosition;

    vUv = aUv;
    vNormal = deformation.normal;
    vViewDirection = normalize(-relativePosition);
    vHeight = deformation.height;
    vVariation = deformation.variation;
    vLod = lod;
    vSpecies = species;
    gl_Position = uProj * vec4(viewPosition, 1.0);
}
