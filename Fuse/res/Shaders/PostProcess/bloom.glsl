float BloomThreshold(vec3 color) {
    float brightness = dot(color, vec3(0.2126, 0.7152, 0.0722));
    return smoothstep(uBloomThreshold - uBloomKnee, uBloomThreshold + uBloomKnee, brightness);
}

vec3 KawaseBlur(sampler2D tex, vec2 uv, vec2 texelSize, int radius, float anamorphicRatio) {
    vec3 sum = texture(tex, uv).rgb * 0.25;
    vec2 offset = texelSize * float(radius);
    offset.x *= anamorphicRatio;

    sum += texture(tex, uv + vec2(-offset.x, 0)).rgb * 0.125;
    sum += texture(tex, uv + vec2( offset.x, 0)).rgb * 0.125;
    sum += texture(tex, uv + vec2(0, -offset.y)).rgb * 0.125;
    sum += texture(tex, uv + vec2(0,  offset.y)).rgb * 0.125;

    offset *= 2.0;
    offset.x *= anamorphicRatio;
    sum += texture(tex, uv + vec2(-offset.x, 0)).rgb * 0.0625;
    sum += texture(tex, uv + vec2( offset.x, 0)).rgb * 0.0625;
    sum += texture(tex, uv + vec2(0, -offset.y)).rgb * 0.0625;
    sum += texture(tex, uv + vec2(0,  offset.y)).rgb * 0.0625;

    return sum;
}

vec4 BloomExtract(vec2 uv) {
    // A fonte do bloom é exclusivamente a radiância emissiva.
    // Reflexos do cubemap e highlights especulares permanecem apenas na cena.
    vec3 col = texture(uEmissive, uv).rgb;
    float mask = BloomThreshold(col);
    return vec4(col * mask, 1.0);
}

vec4 KawaseStep1(vec2 uv) {
    vec3 col = KawaseBlur(uScene, uv, uTexelSize, uKawaseRadius, uBloomAnamorphicRatio);
    return vec4(col, 1.0);
}

vec4 KawaseStep2(vec2 uv) {
    vec3 col = KawaseBlur(uScene, uv, uTexelSize, uKawaseRadius * 2, uBloomAnamorphicRatio);
    return vec4(col, 1.0);
}

vec4 BloomComposite(vec2 uv) {
    vec4 scene = texture(uScene, uv);
    vec3 sceneCol = scene.rgb;
    vec3 bloomCol = texture(uBloom, uv).rgb * uBloomTint * uBloomScale;

    if (uDebugView == 1) {
        return vec4(sceneCol, scene.a);
    } else if (uDebugView == 2) {
        return vec4(bloomCol, scene.a);
    } else if (uDebugView == 3) {
        vec3 emissiveCol = texture(uEmissive, uv).rgb;
        float mask = BloomThreshold(emissiveCol);
        return vec4(emissiveCol * mask, scene.a);
    } else {
        return vec4(sceneCol + bloomCol * uBloomStrength, scene.a);
    }
}
