uniform sampler2D uSsaoNoiseTex;
uniform vec3 uSamples[64];
uniform mat4 uProjection;
uniform mat4 uInvProj;
uniform float uSsaoRadius;
uniform float uSsaoBias;
uniform int uSsaoKernelSize;

vec3 ViewPosFromDepth(vec2 uv, float depth) {
    float z = depth * 2.0 - 1.0;
    vec4 clipPos = vec4(uv * 2.0 - 1.0, z, 1.0);
    vec4 viewPos = uInvProj * clipPos;
    return viewPos.xyz / viewPos.w;
}

vec3 ReconstructNormal(vec2 uv) {
    vec2 ts = 1.0 / uScreenSize;

    float depthC = texture(uDepth, uv).r;
    float depthL = texture(uDepth, uv - vec2(ts.x, 0)).r;
    float depthR = texture(uDepth, uv + vec2(ts.x, 0)).r;
    float depthU = texture(uDepth, uv + vec2(0, ts.y)).r;
    float depthD = texture(uDepth, uv - vec2(0, ts.y)).r;

    vec3 posC = ViewPosFromDepth(uv, depthC);
    vec3 posL = ViewPosFromDepth(uv - vec2(ts.x, 0), depthL);
    vec3 posR = ViewPosFromDepth(uv + vec2(ts.x, 0), depthR);
    vec3 posU = ViewPosFromDepth(uv + vec2(0, ts.y), depthU);
    vec3 posD = ViewPosFromDepth(uv - vec2(0, ts.y), depthD);

    vec3 dX = (depthR > depthL) ? posR - posC : posC - posL;
    vec3 dY = (depthU > depthD) ? posU - posC : posC - posD;

    return normalize(cross(dX, dY));
}

vec4 SSAO(vec2 uv) {
    float depth = texture(uDepth, uv).r;
    if (depth >= 1.0) return vec4(1.0);

    vec3 fragPos = ViewPosFromDepth(uv, depth);
    vec3 normal = ReconstructNormal(uv);

    vec2 noiseScale = uScreenSize / 4.0;
    vec3 randomVec = texture(uSsaoNoiseTex, uv * noiseScale).xyz;

    vec3 tangent = normalize(randomVec - normal * dot(randomVec, normal));
    vec3 bitangent = cross(normal, tangent);
    mat3 TBN = mat3(tangent, bitangent, normal);

    float occlusion = 0.0;
    int kernelSize = clamp(uSsaoKernelSize, 0, 64);
    for (int i = 0; i < kernelSize; i++) {
        vec3 samplePos = TBN * uSamples[i];
        samplePos = fragPos + samplePos * uSsaoRadius;

        vec4 offset = uProjection * vec4(samplePos, 1.0);
        offset.xyz /= offset.w;
        offset.xyz = offset.xyz * 0.5 + 0.5;

        float sampleDepth = texture(uDepth, offset.xy).r;
        vec3 realPos = ViewPosFromDepth(offset.xy, sampleDepth);

        float rangeCheck = smoothstep(0.0, 1.0, uSsaoRadius / abs(fragPos.z - realPos.z));
        occlusion += (realPos.z >= samplePos.z + uSsaoBias ? 1.0 : 0.0) * rangeCheck;
    }

    occlusion = 1.0 - (occlusion / float(kernelSize));
    return vec4(vec3(occlusion), 1.0);
}
