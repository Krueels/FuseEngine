#version 330 core
in vec2 vUV;
out vec4 FragColor;
uniform sampler2D uTexture;
uniform vec4 uColor;
void main() {
    vec4 c = texture(uTexture, vUV) * uColor;
    if (c.a < 0.1) discard;
    FragColor = c;
}