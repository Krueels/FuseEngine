#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in vec3 aNormal;
layout(location = 3) in vec3 aTangent;
layout(location = 4) in vec3 aBitangent;

out vec2 vTexCoord;
out vec3 vWorldPos;
out vec3 vWorldNormal;
out vec3 vWorldTangent;
out vec3 vWorldBitangent;
out vec3 vViewPos;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec2 uUvScale;
uniform vec2 uUvOffset;
uniform float uUvRotation;

void main() {
    vec2 uv = aTexCoord * uUvScale;
    float sinR = sin(uUvRotation);
    float cosR = cos(uUvRotation);
    uv = vec2(uv.x * cosR - uv.y * sinR, uv.x * sinR + uv.y * cosR);
    uv += uUvOffset;
    vTexCoord = uv;
    vec4 worldPos = uModel * vec4(aPos, 1.0);
    vWorldPos = worldPos.xyz;
    mat3 normalMatrix = mat3(transpose(inverse(uModel)));
    vWorldNormal = normalMatrix * aNormal;
    vWorldTangent = normalMatrix * aTangent;
    vWorldBitangent = normalMatrix * aBitangent;
    vViewPos = (uView * worldPos).xyz;
    gl_Position = uProj * uView * worldPos;
}
