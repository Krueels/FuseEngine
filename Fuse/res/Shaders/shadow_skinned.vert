#version 430 core
layout(location = 0) in vec3 aPos;
layout(location = 3) in ivec4 aBoneIds;
layout(location = 4) in vec4 aWeights;

const int MAX_BONES = 192;
layout(std430, binding = 0) buffer BonesBuffer {
    mat4 uBones[MAX_BONES];
};

uniform mat4 uLightSpaceMatrix;
uniform mat4 uModel;

void main()
{
    mat4 skin = aWeights.x * uBones[aBoneIds.x]
              + aWeights.y * uBones[aBoneIds.y]
              + aWeights.z * uBones[aBoneIds.z]
              + aWeights.w * uBones[aBoneIds.w];

    gl_Position = uLightSpaceMatrix * uModel * (skin * vec4(aPos, 1.0));
}