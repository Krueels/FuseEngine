#version 330 core
in vec2 vTexCoord;
in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec3 vViewPos;

out vec4 fragColor;

// Você pode alterar esse valor para aumentar o desfoque das luzes Point e Spot
#define SHADOW_BLUR_MULTIPLIER 0.2 

#define MAX_POINT_LIGHTS 8
#define MAX_SPOT_LIGHTS 4

struct PointLight {
    vec3 position;
    vec3 color;
    float radius;
    int shadowMapIndex;
    float shadowBias;
};

struct SpotLight {
    vec3 position;
    vec3 direction;
    vec3 color;
    float radius;
    float innerCos;
    float outerCos;
    bool castShadows;
    float shadowBias;
};

uniform vec3 uCameraPos;
uniform int uPointLightCount;
uniform PointLight uPointLights[MAX_POINT_LIGHTS];
uniform int uSpotLightCount;
uniform SpotLight uSpotLights[MAX_SPOT_LIGHTS];
uniform sampler2D uTexture;
uniform sampler2DArrayShadow uShadowMap;
uniform sampler2DArrayShadow uSpotShadowMap;
uniform samplerCubeShadow uPointShadowMap0;
uniform samplerCubeShadow uPointShadowMap1;
uniform samplerCubeShadow uPointShadowMap2;
uniform samplerCubeShadow uPointShadowMap3;
uniform bool uUseTexture;
uniform bool uEnableShadowFilter;
uniform float uShadowBiasFactor;
uniform float uShadowBiasBase;
uniform float uShadowSpread;
uniform bool uEnableShadows;
uniform mat4 uLightSpaceMatrices[3];
uniform mat4 uSpotLightSpaceMatrices[MAX_SPOT_LIGHTS];
uniform float uCascadePlaneDistances[3];
uniform vec3 uColor;
uniform vec3 uLightDir;     // direção da luz (apontando PARA a fonte)
uniform vec3 uLightColor;
uniform float uAmbient;

// Emissive
uniform bool uIsEmissive;
uniform vec3 uEmissiveColor;
uniform float uEmissiveStrength;

// Motion Blur mask: 1.0 = viewmodel (sem blur), 0.0 = world (com blur)
uniform float uIsViewmodel;

// Matriz de Poisson Disk para amostragem difusa (Soft Shadows)
const vec2 poissonDisk[16] = vec2[]( 
    vec2( -0.94201624, -0.39906216 ), vec2( 0.94558609, -0.76890725 ), 
    vec2( -0.094184101, -0.92938870 ), vec2( 0.34495938, 0.29387760 ), 
    vec2( -0.91588581, 0.45771432 ), vec2( -0.81544232, -0.87912464 ), 
    vec2( -0.38277543, 0.27676845 ), vec2( 0.97484398, 0.75648379 ), 
    vec2( 0.44323325, -0.97511554 ), vec2( 0.53742981, -0.47373420 ), 
    vec2( -0.26496911, -0.41893023 ), vec2( 0.79197514, 0.19090188 ), 
    vec2( -0.24188840, 0.99706507 ), vec2( -0.81409955, 0.91437590 ), 
    vec2( 0.19984126, 0.78641367 ), vec2( 0.14383161, -0.14100790 ) 
);

// Função de ruído para girar o disco de Poisson aleatoriamente por pixel (Dithering)
float interleaved_gradient_noise(vec2 position_screen) {
    vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
    return fract(magic.z * fract(dot(position_screen, magic.xy)));
}

float ShadowCalculation(vec3 worldPos, vec3 normal, vec3 lightDir)
{
    // Seleciona a cascata baseada na profundidade da view da câmera
    int cascadeIndex = 2;
    for(int i = 0; i < 2; ++i)
    {
        if(abs(vViewPos.z) < uCascadePlaneDistances[i])
        {
            cascadeIndex = i;
            break;
        }
    }

    // Normal Offset Bias (reduzido para evitar descolamento extremo)
    float normalOffsetScale = 0.02; // 2 cm
    if (cascadeIndex == 1) normalOffsetScale *= 2.0;
    if (cascadeIndex == 2) normalOffsetScale *= 4.0;
    
    vec3 offsetPos = worldPos + normal * normalOffsetScale;
    vec4 fragPosLightSpace = uLightSpaceMatrices[cascadeIndex] * vec4(offsetPos, 1.0);

    // perform perspective divide
    vec3 projCoords = fragPosLightSpace.xyz / fragPosLightSpace.w;
    // transform to [0,1] range
    projCoords = projCoords * 0.5 + 0.5;
    
    if(projCoords.z > 1.0)
        return 0.0;
        
    // get depth of current fragment from light's perspective
    float currentDepth = projCoords.z;
    
    // Depth bias mantido incrivelmente minúsculo (0.000005)
    // Na nossa projeção (-2000 a 2000), 0.000005 representa 2 centímetros!
    float bias = 0.000005;
    
    // PCF - Estilo Unity (Fixed Filter)
    // Removemos o ruído aleatório que causa o efeito de dither "spray"
    // e usamos 4 amostras fixas bilineares em formato de quadrado para um blur 3x3 suave
    float shadow = 0.0;
    vec2 texelSize = 1.0 / vec2(textureSize(uShadowMap, 0));
    
    vec2 offsets[4] = vec2[](
        vec2(-0.5, -0.5), vec2(0.5, -0.5), vec2(-0.5, 0.5), vec2(0.5, 0.5)
    );
    
    for(int i = 0; i < 4; i++)
    {
        // Multiplicamos por uShadowSpread mas mantemos as posições relativas fixas
        vec2 offset = offsets[i] * (uShadowSpread * texelSize);
        float visibility = texture(uShadowMap, vec4(projCoords.xy + offset, cascadeIndex, currentDepth - bias)); 
        shadow += (1.0 - visibility);
    }
    shadow /= 4.0;
    
    return shadow;
}

void main() {
    vec3 color = uUseTexture ? texture(uTexture, vTexCoord).rgb : uColor;
    vec3 norm = normalize(vWorldNormal);
    
    // === Luz Direcional (existente) ===
    vec3 ambient = uAmbient * uLightColor;
    
    vec3 lightDir = normalize(uLightDir);
    float diff = max(dot(norm, lightDir), 0.0);
    vec3 diffuse = diff * uLightColor;
    
    // specular (Blinn-Phong)
    vec3 viewDir = normalize(uCameraPos - vWorldPos);
    vec3 halfDir = normalize(lightDir + viewDir);
    float spec = pow(max(dot(norm, halfDir), 0.0), 32.0);
    vec3 specular = spec * uLightColor * 0.5;
    
    float shadow = 0.0;
    if (uEnableShadows) {
        shadow = ShadowCalculation(vWorldPos, norm, lightDir);
    }
    
    vec3 result = (ambient + (1.0 - shadow) * (diffuse + specular)) * color;
    
    // === Point Lights ===
    for (int i = 0; i < uPointLightCount; i++) {
        vec3 pos = uPointLights[i].position;
        vec3 col = uPointLights[i].color;
        float radius = uPointLights[i].radius;
        
        vec3 lightVec = pos - vWorldPos;
        float dist = length(lightVec);
        if (dist > radius) continue;
        
        vec3 lightDirPL = normalize(lightVec);
        float falloff = clamp(1.0 - (dist * dist) / (radius * radius), 0.0, 1.0);
        float atten = falloff * falloff;
        
        diff = max(dot(norm, lightDirPL), 0.0);
        vec3 halfPL = normalize(lightDirPL + viewDir);
        spec = pow(max(dot(norm, halfPL), 0.0), 32.0);
        
        float pointShadow = 0.0;
        if (uPointLights[i].shadowMapIndex >= 0) {
            vec3 fragToLight = vWorldPos - pos;
            float currentDepth = length(fragToLight) / radius;
            float bias = uPointLights[i].shadowBias;
            if (uEnableShadowFilter) {
                // PCF com grid esférico de amostras otimizado via Hardware PCF (4 amostras)
                vec3 sampleOffsetDirections[4] = vec3[](
                   vec3( 1,  1,  1), vec3( 1, -1, -1), vec3(-1, -1,  1), vec3(-1,  1, -1)
                );
                float shadows = 0.0;
                float diskRadius = 0.05 * SHADOW_BLUR_MULTIPLIER;
                vec3 L = normalize(fragToLight);
                for(int j = 0; j < 4; ++j) {
                    vec3 offset = sampleOffsetDirections[j] * diskRadius;
                    vec3 sampleDir = L + offset;
                    float visibility = 1.0;
                    if (uPointLights[i].shadowMapIndex == 0) {
                        visibility = texture(uPointShadowMap0, vec4(sampleDir, currentDepth - bias));
                    } else if (uPointLights[i].shadowMapIndex == 1) {
                        visibility = texture(uPointShadowMap1, vec4(sampleDir, currentDepth - bias));
                    } else if (uPointLights[i].shadowMapIndex == 2) {
                        visibility = texture(uPointShadowMap2, vec4(sampleDir, currentDepth - bias));
                    } else if (uPointLights[i].shadowMapIndex == 3) {
                        visibility = texture(uPointShadowMap3, vec4(sampleDir, currentDepth - bias));
                    }
                    shadows += (1.0 - visibility);
                }
                pointShadow = shadows / 4.0;
            } else {
                float visibility = 1.0;
                vec3 L = normalize(fragToLight);
                if (uPointLights[i].shadowMapIndex == 0) {
                    visibility = texture(uPointShadowMap0, vec4(L, currentDepth - bias));
                } else if (uPointLights[i].shadowMapIndex == 1) {
                    visibility = texture(uPointShadowMap1, vec4(L, currentDepth - bias));
                } else if (uPointLights[i].shadowMapIndex == 2) {
                    visibility = texture(uPointShadowMap2, vec4(L, currentDepth - bias));
                } else if (uPointLights[i].shadowMapIndex == 3) {
                    visibility = texture(uPointShadowMap3, vec4(L, currentDepth - bias));
                }
                pointShadow = 1.0 - visibility;
            }
        }
        
        result += (1.0 - pointShadow) * (diff + spec * 0.5) * col * atten * color;
    }
    
    // === Spot Lights ===
    for (int i = 0; i < uSpotLightCount; i++) {
        vec3 pos = uSpotLights[i].position;
        vec3 dir = uSpotLights[i].direction;
        vec3 col = uSpotLights[i].color;
        float radius = uSpotLights[i].radius;
        float innerCos = uSpotLights[i].innerCos;
        float outerCos = uSpotLights[i].outerCos;
        
        vec3 lightVec = pos - vWorldPos;
        float dist = length(lightVec);
        if (dist > radius) continue;
        
        vec3 lightDirSL = normalize(lightVec);
        
        // Cone falloff (lightDirSL = fragment→light, negate for light→fragment)
        float theta = -dot(lightDirSL, dir); // dir is already normalized
        float epsilon = max(innerCos - outerCos, 0.0001);
        float spotFactor = clamp((theta - outerCos) / epsilon, 0.0, 1.0);
        if (spotFactor < 0.001) continue;
        
        float falloff = clamp(1.0 - (dist * dist) / (radius * radius), 0.0, 1.0);
        float atten = falloff * falloff;
        
        diff = max(dot(norm, lightDirSL), 0.0);
        vec3 halfSL = normalize(lightDirSL + viewDir);
        spec = pow(max(dot(norm, halfSL), 0.0), 32.0);
        
        float spotShadow = 0.0;
        if (uSpotLights[i].castShadows) {
            vec4 fragPosSpotSpace = uSpotLightSpaceMatrices[i] * vec4(vWorldPos, 1.0);
            vec3 projCoords = fragPosSpotSpace.xyz / fragPosSpotSpace.w;
            projCoords = projCoords * 0.5 + 0.5;
            
            if (projCoords.z <= 1.0 && projCoords.x >= 0.0 && projCoords.x <= 1.0 && projCoords.y >= 0.0 && projCoords.y <= 1.0) {
                float currentDepth = projCoords.z;
                float bias = max(uSpotLights[i].shadowBias * (1.0 - dot(norm, lightDirSL)), uSpotLights[i].shadowBias * 0.1);
                if (uEnableShadowFilter) {
                    vec2 texelSize = (1.0 / vec2(textureSize(uSpotShadowMap, 0))) * SHADOW_BLUR_MULTIPLIER;
                    vec2 offsets[4] = vec2[](
                        vec2(-0.5, -0.5), vec2(0.5, -0.5), vec2(-0.5, 0.5), vec2(0.5, 0.5)
                    );
                    for(int j = 0; j < 4; ++j) {
                        float visibility = texture(uSpotShadowMap, vec4(projCoords.xy + offsets[j] * texelSize, i, currentDepth - bias)); 
                        spotShadow += (1.0 - visibility);
                    }
                    spotShadow /= 4.0;
                } else {
                    float visibility = texture(uSpotShadowMap, vec4(projCoords.xy, i, currentDepth - bias));
                    spotShadow = 1.0 - visibility;
                }
            }
        }
        
        result += (1.0 - spotShadow) * (diff + spec * 0.5) * col * atten * spotFactor * color;
    }

    // Emissive
    if (uIsEmissive) {
        result += uEmissiveColor * uEmissiveStrength;    
    }

    fragColor = vec4(result, uIsViewmodel);
}