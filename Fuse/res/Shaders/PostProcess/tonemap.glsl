vec4 TonemapOnly(vec2 uv) {
    vec3 col = texture(uScene, uv).rgb;
    return vec4(ToneMapACES(col), 1.0);
}