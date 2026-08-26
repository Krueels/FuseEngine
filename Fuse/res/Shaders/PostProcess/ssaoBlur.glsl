vec4 SSAOBlur(vec2 uv) {
    float centerAO = texture(uScene, uv).r;
    float centerDepth = texture(uDepth, uv).r;

    float sum = 0.0;
    float wsum = 0.0;
    int radius = 2;

    for (int x = -radius; x <= radius; x++) {
        for (int y = -radius; y <= radius; y++) {
            vec2 offset = vec2(float(x), float(y)) * uTexelSize;
            float sampleAO = texture(uScene, uv + offset).r;
            float sampleDepth = texture(uDepth, uv + offset).r;

            float w = exp(-float(x * x + y * y) / 8.0);
            float depthDiff = abs(centerDepth - sampleDepth);
            w *= exp(-depthDiff * depthDiff * 10000.0);

            sum += sampleAO * w;
            wsum += w;
        }
    }

    return vec4(vec3(sum / wsum), 1.0);
}
