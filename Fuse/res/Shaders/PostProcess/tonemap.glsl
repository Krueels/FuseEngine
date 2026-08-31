vec4 TonemapOnly(vec2 uv) {
    // Debug views
    if (uDebugView == 4) {
        float ao = texture(uSsao, uv).r;
        return vec4(vec3(ao), 1.0);
    }
    if (uDebugView == 5) {
        float depth = texture(uDepth, uv).r;
        float near = 5;
        float far = 2000.0;
        float linear = (2.0 * near * far) / (far + near - depth * (far - near));
        float t = clamp((linear - near) / (far - near), 0.0, 1.0);
        return vec4(vec3(t), 1.0);
    }

    if (uDebugView == 6)
    {
        return texture(uScene, uv);
    }

    vec3 col = texture(uScene, uv).rgb;
    if (uSsaoIntensity > 0.0) {
        float ao = texture(uSsao, uv).r;
        col *= ao;
    }
    if (uTonemapEnabled == 1) {
        col = ToneMapACES(col);
    }
    col = pow(max(col, vec3(0.0)), vec3(1.0 / max(uGamma, 0.001)));
    return vec4(col, 1.0);
}
