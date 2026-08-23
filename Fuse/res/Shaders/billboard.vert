#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUV;
out vec2 vUV;
uniform vec3 uWorldPos;
uniform vec2 uSize;
uniform mat4 uView;
uniform mat4 uProj;
void main() {
    vUV = aUV;
    vec3 right = vec3(uView[0][0], uView[1][0], uView[2][0]);
    vec3 up = vec3(uView[0][1], uView[1][1], uView[2][1]);
    vec3 pos = uWorldPos + right * aPos.x * uSize.x + up * aPos.y * uSize.y;
    gl_Position = uProj * uView * vec4(pos, 1.0);
}