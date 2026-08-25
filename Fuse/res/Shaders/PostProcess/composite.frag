#version 330 core
layout(location = 0) out vec4 FragColor;

uniform int uPass;
uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uExposure;
uniform float uBloomStrength;
uniform float uBloomThreshold;
uniform float uBloomKnee;
uniform vec2 uTexelSize;
uniform int uDebugView;
uniform int uKawaseRadius;
uniform int uKawaseIterations;

// Bloom Expansion
uniform float uBloomScale;
uniform vec3 uBloomTint;
uniform float uBloomAnamorphicRatio;

// Motion Blur (FlaxEngine-style)
uniform sampler2D uDepth;
uniform mat4 uInvViewProj;
uniform mat4 uPrevVP;
uniform float uMotionBlurIntensity;
uniform int uMotionBlurSamples;
uniform vec2 uScreenSize;

in vec2 vTexCoord;

vec3 ToneMapACES(vec3 x) {
    x *= uExposure;
    return (x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14);
}

float BloomThreshold(vec3 color) {
    float brightness = dot(color, vec3(0.2126, 0.7152, 0.0722));
    return smoothstep(uBloomThreshold - uBloomKnee, uBloomThreshold + uBloomKnee, brightness);
}

// Kawase Blur - usa bilinear filtering com suporte anamórfico
vec3 KawaseBlur(sampler2D tex, vec2 uv, vec2 texelSize, int radius, float anamorphicRatio) {
    vec3 sum = texture(tex, uv).rgb * 0.25;
    vec2 offset = texelSize * float(radius);
    offset.x *= anamorphicRatio; // anamórfico horizontal
    
    sum += texture(tex, uv + vec2(-offset.x, 0)).rgb * 0.125;
    sum += texture(tex, uv + vec2( offset.x, 0)).rgb * 0.125;
    sum += texture(tex, uv + vec2(0, -offset.y)).rgb * 0.125;
    sum += texture(tex, uv + vec2(0,  offset.y)).rgb * 0.125;
    
    offset *= 2.0; // próximo nível aproveita bilinear
    offset.x *= anamorphicRatio;
    sum += texture(tex, uv + vec2(-offset.x, 0)).rgb * 0.0625;
    sum += texture(tex, uv + vec2( offset.x, 0)).rgb * 0.0625;
    sum += texture(tex, uv + vec2(0, -offset.y)).rgb * 0.0625;
    sum += texture(tex, uv + vec2(0,  offset.y)).rgb * 0.0625;
    
    return sum;
}

// Motion Blur helpers (Jorge Jimenez 2014)
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

void main() {
    if (uPass == 0) {                 // Copy HDR
        FragColor = vec4(texture(uScene, vTexCoord).rgb, 1.0);
    }
    else if (uPass == 1) {            // Bloom Extract
        vec3 col = texture(uScene, vTexCoord).rgb;
        float mask = BloomThreshold(col);
        FragColor = vec4(col * mask, 1.0);
    }
    else if (uPass == 2) {            // Kawase Step 1
        vec3 col = KawaseBlur(uScene, vTexCoord, uTexelSize, uKawaseRadius, uBloomAnamorphicRatio);
        FragColor = vec4(col, 1.0);
    }
    else if (uPass == 3) {            // Kawase Step 2 (raio dobrado)
        vec3 col = KawaseBlur(uScene, vTexCoord, uTexelSize, uKawaseRadius * 2, uBloomAnamorphicRatio);
        FragColor = vec4(col, 1.0);
    }
    else if (uPass == 4) {            // Bloom Composite (HDR, sem tonemap)
        vec4 scene = texture(uScene, vTexCoord);
        vec3 sceneCol = scene.rgb;
        vec3 bloomCol = texture(uBloom, vTexCoord).rgb * uBloomTint * uBloomScale;

        if (uDebugView == 1) {
            FragColor = vec4(sceneCol, scene.a);
        } else if (uDebugView == 2) {
            FragColor = vec4(bloomCol, scene.a);
        } else if (uDebugView == 3) {
            float mask = BloomThreshold(sceneCol);
            FragColor = vec4(sceneCol * mask, scene.a);
        } else {
            FragColor = vec4(sceneCol + bloomCol * uBloomStrength, scene.a);
        }
    }
    else if (uPass == 5) {            // Motion Blur
        vec3 color = texture(uScene, vTexCoord).rgb;
        float depth = texture(uDepth, vTexCoord).r;

        // Viewmodel: pula motion blur
        if (texture(uScene, vTexCoord).a > 0.5) {
            FragColor = vec4(color, 1.0);
            return;
        }

        vec3 worldPos = ReconstructWorldPos(vTexCoord, depth);
        vec4 prevClipPos = uPrevVP * vec4(worldPos, 1.0);
        vec2 prevUV = (prevClipPos.xy / prevClipPos.w) * 0.5 + 0.5;
        vec2 velocity = (vTexCoord - prevUV) * uMotionBlurIntensity;

        // Clampa velocidade máxima para evitar esticar além do razoável
        velocity = clamp(velocity, vec2(-0.05), vec2(0.05));

        float velocityPixels = length(velocity * uScreenSize);
        if (velocityPixels < 0.5) {
            FragColor = vec4(color, 1.0);
            return;
        }

        float noise = InterleavedGradientNoise(gl_FragCoord.xy);
        int samples = clamp(uMotionBlurSamples, 2, 64);
        vec3 sum = vec3(0.0);

        for (int i = 0; i < samples; i++) {
            float t = (float(i) + noise) / float(samples) - 0.5;
            vec2 sampleUV = clamp(vTexCoord + t * velocity, vec2(0.0), vec2(1.0));
            sum += texture(uScene, sampleUV).rgb;
        }

        FragColor = vec4(sum / float(samples), 1.0);
    }
    else if (uPass == 6) {            // Tonemap only (saída final)
        vec3 col = texture(uScene, vTexCoord).rgb;
        FragColor = vec4(ToneMapACES(col), 1.0);
    }
}