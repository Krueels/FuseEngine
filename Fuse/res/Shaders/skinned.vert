#version 430 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in vec3 aNormal;
layout(location = 3) in ivec4 aBoneIds;
layout(location = 4) in vec4 aWeights;

const int MAX_BONES = 192;
layout(std430, binding = 0) buffer BonesBuffer {
    mat4 uBones[MAX_BONES];
};

out vec2 vTexCoord;
out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec3 vViewPos;

uniform vec3 uCameraPos;
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec2 uUvScale;
uniform vec2 uUvOffset;
uniform float uUvRotation;

void main() {
    mat4 skin = aWeights.x * uBones[aBoneIds.x]
              + aWeights.y * uBones[aBoneIds.y]
              + aWeights.z * uBones[aBoneIds.z]
              + aWeights.w * uBones[aBoneIds.w];

    vec4 skinnedPos = skin * vec4(aPos, 1.0);
    vec3 skinnedNormal = mat3(skin) * aNormal;

    vec2 uv = aTexCoord * uUvScale;
    float sinR = sin(uUvRotation);
    float cosR = cos(uUvRotation);
    uv = vec2(uv.x * cosR - uv.y * sinR, uv.x * sinR + uv.y * cosR);
    uv += uUvOffset;
    vTexCoord = uv;

    vec4 worldPos = uModel * skinnedPos;
    vWorldPos = worldPos.xyz;
    vWorldNormal = mat3(transpose(inverse(uModel))) * skinnedNormal;
    vViewPos = (uView * worldPos).xyz;
    gl_Position = uProj * uView * worldPos;
}