float InterleavedGradientNoise(vec2 screenPos) {
    vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
    return fract(magic.z * fract(dot(screenPos, magic.xy)));
}

vec3 ReconstructWorldPos(vec2 uv, float depth) {
    float z = depth * 2.0 - 1.0;
    vec4 clipPos = vec4(uv * 2.0 - 1.0, z, 1.0);
    vec4 worldPos = uInvViewProj * clipPos;
    return worldPos.xyz / worldPos.w;
}

vec4 MotionBlur(vec2 uv) {
    vec3 color = texture(uScene, uv).rgb;
    float depth = texture(uDepth, uv).r;

    if (texture(uScene, uv).a > 0.5) {
        return vec4(color, 1.0);
    }

    vec3 worldPos = ReconstructWorldPos(uv, depth);
    vec4 prevClipPos = uPrevVP * vec4(worldPos, 1.0);
    vec2 prevUV = (prevClipPos.xy / prevClipPos.w) * 0.5 + 0.5;
    vec2 velocity = (uv - prevUV) * uMotionBlurIntensity;

    velocity = clamp(velocity, vec2(-0.05), vec2(0.05));

    float velocityPixels = length(velocity * uScreenSize);
    if (velocityPixels < 0.5) {
        return vec4(color, 1.0);
    }

    float noise = InterleavedGradientNoise(gl_FragCoord.xy);
    int samples = clamp(uMotionBlurSamples, 2, 64);
    vec3 sum = vec3(0.0);

    for (int i = 0; i < samples; i++) {
        float t = (float(i) + noise) / float(samples) - 0.5;
        vec2 sampleUV = clamp(uv + t * velocity, vec2(0.0), vec2(1.0));
        sum += texture(uScene, sampleUV).rgb;
    }

    return vec4(sum / float(samples), 1.0);
}