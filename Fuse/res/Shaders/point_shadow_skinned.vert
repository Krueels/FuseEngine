#version 430 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aTexCoord;
layout(location = 5) in ivec4 aBoneIds;
layout(location = 6) in vec4 aWeights;

const int MAX_BONES = 192;
layout(std430, binding = 0) buffer BonesBuffer {
    mat4 uBones[MAX_BONES];
};

uniform mat4 uLightSpaceMatrix;
uniform mat4 uModel;
uniform vec2 uUvScale;
uniform vec2 uUvOffset;
uniform float uUvRotation;

out vec3 vWorldPos;
out vec2 vTexCoord;

void main()
{
    mat4 skin = aWeights.x * uBones[aBoneIds.x]
              + aWeights.y * uBones[aBoneIds.y]
              + aWeights.z * uBones[aBoneIds.z]
              + aWeights.w * uBones[aBoneIds.w];

    vec2 uv = aTexCoord * uUvScale;
    float sinR = sin(uUvRotation);
    float cosR = cos(uUvRotation);
    vTexCoord = vec2(uv.x * cosR - uv.y * sinR, uv.x * sinR + uv.y * cosR) + uUvOffset;

    vec4 worldPos = uModel * (skin * vec4(aPos, 1.0));
    vWorldPos = worldPos.xyz;
    gl_Position = uLightSpaceMatrix * worldPos;
}
