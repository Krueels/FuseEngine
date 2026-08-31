#version 430 core
#include "lighting.glsl"

#define FUSE_FORWARD_PLUS_TILE_SIZE 16
#define FUSE_FORWARD_PLUS_MAX_LIGHTS_PER_TILE 64
#define FUSE_FORWARD_PLUS_MAX_POINT_LIGHTS 128
#define FUSE_FORWARD_PLUS_MAX_SPOT_LIGHTS 128

layout(std430, binding = 2) readonly buffer ForwardPlusTileListBuffer {
    uvec4 uForwardPlusTileLists[];
};

layout(std430, binding = 3) readonly buffer ForwardPlusLightIndexBuffer {
    uint uForwardPlusLightIndices[];
};

layout(std430, binding = 4) readonly buffer ForwardPlusLightBuffer {
    PointLightData uForwardPlusPointLights[FUSE_FORWARD_PLUS_MAX_POINT_LIGHTS];
    SpotLightData uForwardPlusSpotLights[FUSE_FORWARD_PLUS_MAX_SPOT_LIGHTS];
};

uniform bool uUseForwardPlus;
uniform int uForwardPlusTileCountX;
uniform int uForwardPlusTileCountY;
uniform int uForwardPlusPointCount;
uniform int uForwardPlusSpotCount;

in vec2 vTexCoord;
in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec3 vWorldTangent;
in vec3 vWorldBitangent;
in vec3 vViewPos;

layout(location = 0) out vec4 fragColor;
layout(location = 1) out vec4 fragEmissive;

uniform sampler2D uTexture;
uniform sampler2DArrayShadow uShadowMap;
uniform sampler2DArrayShadow uSpotShadowMap;
uniform samplerCube uPointShadowMap0;
uniform samplerCube uPointShadowMap1;
uniform samplerCube uPointShadowMap2;
uniform samplerCube uPointShadowMap3;
uniform bool uUseTexture;
uniform vec3 uColor;
uniform int uDebugView;
uniform samplerCube uDiffuseIrradianceMap;
uniform samplerCube uPrefilteredEnvMap;
uniform sampler2D uBrdfLut;
uniform bool uUseIbl;
uniform float uIblIntensity;
uniform bool uOutputSrgb;

struct MaterialSurface {
    vec3 baseColor;
    vec3 tangentNormal;
    float roughness;
    float metallic;
    vec3 emission;
    float alpha;
    float ao;
    float hasNormalMap;
    float legacyLighting;
};

uniform int uMaterialAlphaMode;
uniform float uMaterialAlphaCutoff;
uniform bool uMaterialReceiveShadows;

#ifndef FUSE_CUSTOM_MATERIAL
MaterialSurface EvaluateMaterial(vec2 materialUv)
{
    MaterialSurface surface;
    vec4 texel = uUseTexture ? texture(uTexture, materialUv) : vec4(uColor, 1.0);
    surface.baseColor = texel.rgb;
    surface.tangentNormal = vec3(0.0, 0.0, 1.0);
    surface.roughness = 0.5;
    surface.metallic = 0.0;
    surface.emission = vec3(0.0);
    surface.alpha = texel.a;
    surface.ao = 1.0;
    surface.hasNormalMap = 0.0;
    surface.legacyLighting = 1.0;
    return surface;
}
#else
/*__FUSE_MATERIAL_GRAPH__*/
#endif

uniform bool uIsEmissive;
uniform vec3 uEmissiveColor;
uniform float uEmissiveStrength;
uniform float uIsViewmodel;

const vec2 pcfOffsets[4] = vec2[](
    vec2(-0.5, -0.5), vec2(0.5, -0.5),
    vec2(-0.5,  0.5), vec2(0.5,  0.5)
);

float SampleDirectionalCascade(int cascadeIndex, vec3 worldPos, vec3 normal, vec3 lightDir)
{
    float texelWorldSize = max(uCascadeTexelSizes[cascadeIndex], 0.0001);
    float slope = 1.0 - max(dot(normal, lightDir), 0.0);
    float normalOffset = texelWorldSize * mix(0.35, 1.15, slope);
    vec3 offsetPos = worldPos + normal * normalOffset;

    vec4 fragPosLightSpace = uLightSpaceMatrices[cascadeIndex] * vec4(offsetPos, 1.0);
    vec3 projCoords = fragPosLightSpace.xyz / fragPosLightSpace.w;
    projCoords = projCoords * 0.5 + 0.5;

    if (projCoords.z <= 0.0 || projCoords.z > 1.0 ||
        projCoords.x < 0.0 || projCoords.x > 1.0 ||
        projCoords.y < 0.0 || projCoords.y > 1.0)
        return 0.0;

    float cascadeScale = 1.0 + float(cascadeIndex) * 0.65;
    float bias = (uShadowParams.x + uShadowParams.y * slope) * cascadeScale;

    if (!LightingShadowFilterEnabled()) {
        float visibility = texture(uShadowMap,
            vec4(projCoords.xy, cascadeIndex, projCoords.z - bias));
        return 1.0 - visibility;
    }

    vec2 texelSize = 1.0 / vec2(textureSize(uShadowMap, 0).xy);
    float shadow = 0.0;
    for (int i = 0; i < 4; i++) {
        vec2 offset = pcfOffsets[i] * texelSize * uShadowParams.z;
        float visibility = texture(uShadowMap,
            vec4(projCoords.xy + offset, cascadeIndex, projCoords.z - bias));
        shadow += 1.0 - visibility;
    }
    return shadow * 0.25;
}

float DirectionalShadow(vec3 worldPos, vec3 normal, vec3 lightDir)
{
    float viewDepth = abs(vViewPos.z);
    int cascadeIndex = 2;
    if (viewDepth < uCascadeDistancesAndFade.x) cascadeIndex = 0;
    else if (viewDepth < uCascadeDistancesAndFade.y) cascadeIndex = 1;

    float shadow = SampleDirectionalCascade(cascadeIndex, worldPos, normal, lightDir);

    if (cascadeIndex < 2) {
        float splitNear = cascadeIndex == 0 ? 0.0 : uCascadeDistancesAndFade[cascadeIndex - 1];
        float splitFar = uCascadeDistancesAndFade[cascadeIndex];
        float blendWidth = max((splitFar - splitNear) * uDirectionalColorCascadeBlend.w, 0.02);
        float blend = smoothstep(splitFar - blendWidth, splitFar, viewDepth);
        if (blend > 0.0) {
            float nextShadow = SampleDirectionalCascade(cascadeIndex + 1, worldPos, normal, lightDir);
            shadow = mix(shadow, nextShadow, blend);
        }
    }

    float fadeStart = uCascadeDistancesAndFade.w;
    float fadeEnd = uCascadeDistancesAndFade.z;
    if (fadeEnd > fadeStart)
        shadow *= 1.0 - smoothstep(fadeStart, fadeEnd, viewDepth);
    return shadow;
}

float SamplePointShadowMap(int shadowMapIndex, vec3 direction, float compareDepth)
{
    vec3 sampleDirection = normalize(direction);
    float storedDepth = 1.0;

    if (shadowMapIndex == 0)
        storedDepth = texture(uPointShadowMap0, sampleDirection).r;
    else if (shadowMapIndex == 1)
        storedDepth = texture(uPointShadowMap1, sampleDirection).r;
    else if (shadowMapIndex == 2)
        storedDepth = texture(uPointShadowMap2, sampleDirection).r;
    else if (shadowMapIndex == 3)
        storedDepth = texture(uPointShadowMap3, sampleDirection).r;

    return compareDepth <= storedDepth ? 1.0 : 0.0;
}

float PointShadow(PointLightData light, vec3 worldPos)
{
    int shadowMapIndex = int(round(light.colorShadowIndex.w));
    if (shadowMapIndex < 0) return 0.0;

    vec3 fragToLight = worldPos - light.positionRadius.xyz;
    float currentDepth = length(fragToLight) * light.params.y;
    float compareDepth = currentDepth - light.params.x;
    vec3 direction = normalize(fragToLight);

    if (!LightingShadowFilterEnabled())
        return 1.0 - SamplePointShadowMap(shadowMapIndex, direction, compareDepth);

    const vec3 sampleDirections[4] = vec3[](
        vec3( 1,  1,  1), vec3( 1, -1, -1),
        vec3(-1, -1,  1), vec3(-1,  1, -1)
    );
    float diskRadius = mix(0.0005, 0.0035, currentDepth) * uShadowParams.z;
    float shadow = 0.0;
    for (int i = 0; i < 4; i++)
        shadow += 1.0 - SamplePointShadowMap(
            shadowMapIndex, direction + sampleDirections[i] * diskRadius, compareDepth);
    return shadow * 0.25;
}

float SpotShadow(int lightIndex, SpotLightData light, vec3 worldPos, vec3 normal, vec3 lightDirection)
{
    if (light.shadowParams.x < 0.5) return 0.0;

    vec4 fragPosSpotSpace = uSpotLightSpaceMatrices[lightIndex] * vec4(worldPos, 1.0);
    vec3 projCoords = fragPosSpotSpace.xyz / fragPosSpotSpace.w;
    projCoords = projCoords * 0.5 + 0.5;
    if (projCoords.z <= 0.0 || projCoords.z > 1.0 ||
        projCoords.x < 0.0 || projCoords.x > 1.0 ||
        projCoords.y < 0.0 || projCoords.y > 1.0)
        return 0.0;

    float slope = 1.0 - max(dot(normal, lightDirection), 0.0);
    float bias = max(light.shadowParams.y * slope, light.shadowParams.y * 0.1);
    int layer = int(light.shadowParams.z + 0.5);

    if (!LightingShadowFilterEnabled()) {
        float visibility = texture(uSpotShadowMap,
            vec4(projCoords.xy, layer, projCoords.z - bias));
        return 1.0 - visibility;
    }

    vec2 texelSize = 1.0 / vec2(textureSize(uSpotShadowMap, 0).xy);
    float shadow = 0.0;
    for (int i = 0; i < 4; i++) {
        float visibility = texture(uSpotShadowMap,
            vec4(projCoords.xy + pcfOffsets[i] * texelSize * uShadowParams.z,
                 layer, projCoords.z - bias));
        shadow += 1.0 - visibility;
    }
    return shadow * 0.25;
}

const float FUSE_PI = 3.14159265359;
const float FUSE_MAX_PREFILTER_MIP = 4.0;

float DistributionGGX(vec3 normal, vec3 halfVector, float roughness)
{
    float alpha = roughness * roughness;
    float alphaSquared = alpha * alpha;
    float nDotH = max(dot(normal, halfVector), 0.0);
    float denominator = nDotH * nDotH * (alphaSquared - 1.0) + 1.0;
    return alphaSquared / max(FUSE_PI * denominator * denominator, 0.000001);
}

float GeometrySchlickGGX(float nDot, float roughness)
{
    float k = ((roughness + 1.0) * (roughness + 1.0)) / 8.0;
    return nDot / max(nDot * (1.0 - k) + k, 0.000001);
}

float GeometrySmith(vec3 normal, vec3 viewDirection, vec3 lightDirection, float roughness)
{
    return GeometrySchlickGGX(max(dot(normal, viewDirection), 0.0), roughness) *
           GeometrySchlickGGX(max(dot(normal, lightDirection), 0.0), roughness);
}

vec3 FresnelSchlick(float cosTheta, vec3 f0)
{
    return f0 + (1.0 - f0) * pow(1.0 - clamp(cosTheta, 0.0, 1.0), 5.0);
}

vec3 FresnelSchlickRoughness(float cosTheta, vec3 f0, float roughness)
{
    return f0 + (max(vec3(1.0 - roughness), f0) - f0) *
           pow(1.0 - clamp(cosTheta, 0.0, 1.0), 5.0);
}

vec3 EvaluateCookTorrance(vec3 normal, vec3 viewDirection, vec3 lightDirection,
                          vec3 radiance, vec3 albedo, float metallic, float roughness)
{
    float nDotV = max(dot(normal, viewDirection), 0.0);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotV <= 0.0 || nDotL <= 0.0)
        return vec3(0.0);

    vec3 halfVector = normalize(viewDirection + lightDirection);
    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    vec3 fresnel = FresnelSchlick(max(dot(halfVector, viewDirection), 0.0), f0);
    float distribution = DistributionGGX(normal, halfVector, roughness);
    float geometry = GeometrySmith(normal, viewDirection, lightDirection, roughness);
    vec3 specular = (distribution * geometry * fresnel) /
                    max(4.0 * nDotV * nDotL, 0.0001);
    vec3 diffuse = (1.0 - fresnel) * (1.0 - metallic) * albedo / FUSE_PI;
    return (diffuse + specular) * radiance * nDotL;
}

vec3 EvaluateIbl(vec3 normal, vec3 viewDirection, vec3 albedo,
                float metallic, float roughness, float ao)
{
    if (!uUseIbl)
        return uDirectionalDirectionAmbient.w * uDirectionalColorCascadeBlend.rgb * albedo * ao;

    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    float nDotV = max(dot(normal, viewDirection), 0.0);
    vec3 fresnel = FresnelSchlickRoughness(nDotV, f0, roughness);
    vec3 kS = fresnel;
    vec3 kD = (1.0 - kS) * (1.0 - metallic);
    vec3 irradiance = texture(uDiffuseIrradianceMap, normal).rgb;
    vec3 diffuse = irradiance * albedo;
    vec3 reflection = reflect(-viewDirection, normal);
    vec3 prefiltered = textureLod(uPrefilteredEnvMap, reflection,
                                   roughness * FUSE_MAX_PREFILTER_MIP).rgb;
    vec2 brdf = texture(uBrdfLut, vec2(nDotV, roughness)).rg;
    vec3 specular = prefiltered * (fresnel * brdf.x + brdf.y);
    return (kD * diffuse + specular) * ao * uIblIntensity;
}

vec3 EvaluatePointLight(PointLightData light, vec3 worldPos, vec3 normal,
                        vec3 viewDir, vec3 color, float metallic,
                        float roughness, float legacyLighting)
{
    vec3 lightVector = light.positionRadius.xyz - worldPos;
    float distanceToLight = length(lightVector);
    float radius = light.positionRadius.w;
    if (distanceToLight > radius || distanceToLight <= 0.0001)
        return vec3(0.0);

    vec3 localLightDir = lightVector / distanceToLight;
    float rangeFade = clamp(1.0 - distanceToLight / max(radius, 0.0001), 0.0, 1.0);
    float attenuation = rangeFade * rangeFade / max(distanceToLight * distanceToLight, 0.01);
    float diffuse = max(dot(normal, localLightDir), 0.0);
    vec3 localHalf = normalize(localLightDir + viewDir);
    float legacySpecular = pow(max(dot(normal, localHalf), 0.0), 32.0);
    float shadow = uMaterialReceiveShadows ? PointShadow(light, worldPos) : 0.0;
    vec3 materialContribution = (1.0 - shadow) * EvaluateCookTorrance(
        normal, viewDir, localLightDir, light.colorShadowIndex.rgb * attenuation,
        color, metallic, roughness);
    vec3 legacyContribution = (1.0 - shadow) * (diffuse + legacySpecular * 0.5) *
        light.colorShadowIndex.rgb * attenuation * color;
    return mix(materialContribution, legacyContribution, legacyLighting);
}

vec3 EvaluateSpotLight(int lightIndex, SpotLightData light, vec3 worldPos,
                       vec3 normal, vec3 viewDir, vec3 color, float metallic,
                       float roughness, float legacyLighting)
{
    vec3 lightVector = light.positionRadius.xyz - worldPos;
    float distanceToLight = length(lightVector);
    float radius = light.positionRadius.w;
    if (distanceToLight > radius || distanceToLight <= 0.0001)
        return vec3(0.0);

    vec3 localLightDir = lightVector / distanceToLight;
    float theta = -dot(localLightDir, light.directionInnerCos.xyz);
    float epsilon = max(light.directionInnerCos.w - light.colorOuterCos.w, 0.0001);
    float spotFactor = clamp((theta - light.colorOuterCos.w) / epsilon, 0.0, 1.0);
    if (spotFactor < 0.001)
        return vec3(0.0);

    float rangeFade = clamp(1.0 - distanceToLight / max(radius, 0.0001), 0.0, 1.0);
    float attenuation = rangeFade * rangeFade / max(distanceToLight * distanceToLight, 0.01);
    float diffuse = max(dot(normal, localLightDir), 0.0);
    vec3 localHalf = normalize(localLightDir + viewDir);
    float legacySpecular = pow(max(dot(normal, localHalf), 0.0), 32.0);
    float shadow = uMaterialReceiveShadows
        ? SpotShadow(lightIndex, light, worldPos, normal, localLightDir)
        : 0.0;
    vec3 materialContribution = (1.0 - shadow) * EvaluateCookTorrance(
        normal, viewDir, localLightDir,
        light.colorOuterCos.rgb * attenuation * spotFactor,
        color, metallic, roughness);
    vec3 legacyContribution = (1.0 - shadow) * (diffuse + legacySpecular * 0.5) *
        light.colorOuterCos.rgb * attenuation * spotFactor * color;
    return mix(materialContribution, legacyContribution, legacyLighting);
}

void main()
{
    MaterialSurface material = EvaluateMaterial(vTexCoord);
    if (uMaterialAlphaMode == 1 && material.alpha < uMaterialAlphaCutoff)
        discard;

    vec3 color = material.baseColor;
    vec3 normal = normalize(vWorldNormal);
    if (material.hasNormalMap > 0.5) {
        vec3 tangent = vWorldTangent;
        vec3 bitangent = vWorldBitangent;
        if (dot(tangent, tangent) < 0.000001 || dot(bitangent, bitangent) < 0.000001) {
            vec3 dp1 = dFdx(vWorldPos);
            vec3 dp2 = dFdy(vWorldPos);
            vec2 duv1 = dFdx(vTexCoord);
            vec2 duv2 = dFdy(vTexCoord);
            vec3 dp2perp = cross(dp2, normal);
            vec3 dp1perp = cross(normal, dp1);
            tangent = dp2perp * duv1.x + dp1perp * duv2.x;
            bitangent = dp2perp * duv1.y + dp1perp * duv2.y;
        }
        tangent = normalize(tangent - normal * dot(normal, tangent));
        bitangent = normalize(bitangent - normal * dot(normal, bitangent));
        if (dot(cross(normal, tangent), bitangent) < 0.0)
            bitangent = -bitangent;
        mat3 tangentFrame = mat3(tangent, bitangent, normal);
        normal = normalize(tangentFrame * material.tangentNormal);
    }

    if (uDebugView == 6)
    {
        fragColor = vec4(normal * 0.5 + 0.5, 1.0);
        fragEmissive = vec4(0.0);
        return;
    }

    vec3 viewDir = normalize(uCameraPosition.xyz - vWorldPos);
    vec3 lightDir = normalize(uDirectionalDirectionAmbient.xyz);
    vec3 directionalColor = uDirectionalColorCascadeBlend.rgb;
    float roughness = clamp(material.roughness, 0.02, 1.0);
    float metallic = clamp(material.metallic, 0.0, 1.0);
    float ambient = uDirectionalDirectionAmbient.w;
    float diffuseAmount = max(dot(normal, lightDir), 0.0);
    vec3 halfDir = normalize(lightDir + viewDir);
    float legacySpecularAmount = pow(max(dot(normal, halfDir), 0.0), 32.0);

    float directionalShadow = 0.0;
    if (uMaterialReceiveShadows && DirectionalShadowsEnabled() && length(directionalColor) > 0.0001)
        directionalShadow = DirectionalShadow(vWorldPos, normal, lightDir);

    vec3 materialDirectional = (1.0 - directionalShadow) *
        EvaluateCookTorrance(normal, viewDir, lightDir, directionalColor, color, metallic, roughness);
    vec3 legacyDirectional = (ambient + (1.0 - directionalShadow) *
        (diffuseAmount + legacySpecularAmount * 0.5) * directionalColor) * color;
    vec3 result = mix(EvaluateIbl(normal, viewDir, color, metallic, roughness, material.ao) + materialDirectional,
                      legacyDirectional, material.legacyLighting);

    if (uUseForwardPlus) {
        ivec2 tile = ivec2(gl_FragCoord.xy) / FUSE_FORWARD_PLUS_TILE_SIZE;
        tile.x = clamp(tile.x, 0, uForwardPlusTileCountX - 1);
        tile.y = clamp(tile.y, 0, uForwardPlusTileCountY - 1);
        uint tileIndex = uint(tile.y * uForwardPlusTileCountX + tile.x);
        uvec4 lightCounts = uForwardPlusTileLists[tileIndex];
        uint tileBase = tileIndex * uint(FUSE_FORWARD_PLUS_MAX_LIGHTS_PER_TILE * 2);

        for (uint localIndex = 0u; localIndex < lightCounts.x; localIndex++) {
            uint lightIndex = uForwardPlusLightIndices[tileBase + localIndex];
            if (lightIndex < uint(uForwardPlusPointCount))
                result += EvaluatePointLight(
                    uForwardPlusPointLights[lightIndex], vWorldPos, normal, viewDir,
                    color, metallic, roughness, material.legacyLighting);
        }

        for (uint localIndex = 0u; localIndex < lightCounts.y; localIndex++) {
            uint lightIndex = uForwardPlusLightIndices[
                tileBase + uint(FUSE_FORWARD_PLUS_MAX_LIGHTS_PER_TILE) + localIndex];
            if (lightIndex < uint(uForwardPlusSpotCount))
                result += EvaluateSpotLight(
                    int(lightIndex), uForwardPlusSpotLights[lightIndex], vWorldPos,
                    normal, viewDir, color, metallic, roughness, material.legacyLighting);
        }
    } else {
        for (int i = 0; i < PointLightCount(); i++)
            result += EvaluatePointLight(
                uPointLights[i], vWorldPos, normal, viewDir,
                color, metallic, roughness, material.legacyLighting);

        for (int i = 0; i < SpotLightCount(); i++)
            result += EvaluateSpotLight(
                i, uSpotLights[i], vWorldPos, normal, viewDir,
                color, metallic, roughness, material.legacyLighting);
    }

    result += material.emission;

    if (uIsEmissive)
        result += uEmissiveColor * uEmissiveStrength;

    // Saída independente para o bloom: somente emissão do material.
    // Não incluir iluminação direta, especular, IBL ou reflexos do cubemap.
    vec3 emissiveRadiance = max(material.emission, vec3(0.0));
    if (uIsEmissive)
        emissiveRadiance += max(uEmissiveColor * uEmissiveStrength, vec3(0.0));

    // Opaque and masked materials must write an opaque framebuffer alpha.
    // Keeping alpha at zero made the ImGui material preview blend the lit RGB
    // away as transparent, which looked like an unlit/black preview.
    float outputAlpha = uMaterialAlphaMode == 2 ? material.alpha : 1.0;
    outputAlpha = max(uIsViewmodel, outputAlpha);
    vec3 outputColor = uOutputSrgb
        ? pow(max(result, vec3(0.0)), vec3(1.0 / 2.2))
        : result;
    fragColor = vec4(outputColor, outputAlpha);
    fragEmissive = vec4(emissiveRadiance, outputAlpha);
}
