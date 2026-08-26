vec4 TonemapOnly(vec2 uv) {
    vec3 col = texture(uScene, uv).rgb;
    if (uSsaoIntensity > 0.0) {
        float ao = texture(uSsao, uv).r;
        col *= ao;
    }
    return vec4(ToneMapACES(col), 1.0);
}