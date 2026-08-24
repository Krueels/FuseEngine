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
    else if (uPass == 4) {            // Composite Final
        vec3 sceneCol = texture(uScene, vTexCoord).rgb;
        vec3 bloomCol = texture(uBloom, vTexCoord).rgb * uBloomTint * uBloomScale;
        
        // Aplica iterações extras de Kawase no bloom se uKawaseIterations > 1
        for (int i = 1; i < uKawaseIterations; i++) {
            int radius = uKawaseRadius * (1 << i); // 2, 4, 8...
            bloomCol = KawaseBlur(uBloom, vTexCoord, uTexelSize, radius, uBloomAnamorphicRatio);
        }
        
        if (uDebugView == 1) {
            FragColor = vec4(sceneCol, 1.0);
        } else if (uDebugView == 2) {
            FragColor = vec4(bloomCol, 1.0);
        } else if (uDebugView == 3) {
            vec3 col = texture(uScene, vTexCoord).rgb;
            float mask = BloomThreshold(col);
            FragColor = vec4(col * mask, 1.0);
        } else {
            vec3 final = ToneMapACES(sceneCol + bloomCol * uBloomStrength);
            FragColor = vec4(final, 1.0);
        }
    }
}