#version 330 core
in vec3 vWorldPos;

out vec4 fragColor;

uniform sampler2D uSkyTexture;
uniform bool uOutputSrgb;

void main() {
    vec3 dir = normalize(vWorldPos);
        vec2 uv = vec2(
        atan(dir.z, dir.x) * 0.15915494 + 0.5,
        asin(clamp(dir.y, -0.9999, 0.9999)) * 0.31830988 + 0.5
    );
    uv.x = uv.x * 0.9999 + 0.00005;
    vec3 color = texture(uSkyTexture, uv).rgb;
    if (uOutputSrgb)
        color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
    fragColor = vec4(color, 1.0);
}
