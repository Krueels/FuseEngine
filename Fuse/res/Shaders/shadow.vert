#version 330 core
layout (location = 0) in vec3 aPos;
layout (location = 1) in vec2 aTexCoord;

out vec2 vTexCoord;

uniform mat4 uLightSpaceMatrix;
uniform mat4 uModel;
uniform vec2 uUvScale;
uniform vec2 uUvOffset;
uniform float uUvRotation;

void main()
{
    vec2 uv = aTexCoord * uUvScale;
    float sinR = sin(uUvRotation);
    float cosR = cos(uUvRotation);
    vTexCoord = vec2(uv.x * cosR - uv.y * sinR, uv.x * sinR + uv.y * cosR) + uUvOffset;
    gl_Position = uLightSpaceMatrix * uModel * vec4(aPos, 1.0);
}
