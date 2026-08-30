#version 330 core
#include "lighting.glsl"

in vec2 vTexCoord;
in vec3 vWorldPos;
in vec3 vWorldNormal;
in vec3 vViewPos;

out vec4 fragColor;

uniform sampler2D uTexture;
uniform sampler2DArrayShadow uShadowMap;
uniform sampler2DArrayShadow uSpotShadowMap;
uniform samplerCube uPointShadowMap0;
uniform samplerCube uPointShadowMap1;
uniform samplerCube uPointShadowMap2;
uniform samplerCube uPointShadowMap3;
uniform bool uUseTexture;
uniform vec3 uColor;

struct MaterialSurface {
    vec3 baseColor;
    vec3 tangentNormal;
    float roughness;
    float metallic;
    vec3 emission;
    float alpha;
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

void main()
{
    MaterialSurface material = EvaluateMaterial(vTexCoord);
    if (uMaterialAlphaMode == 1 && material.alpha < uMaterialAlphaCutoff)
        discard;

    vec3 color = material.baseColor;
    vec3 normal = normalize(vWorldNormal);
    if (material.hasNormalMap > 0.5) {
        vec3 dp1 = dFdx(vWorldPos);
        vec3 dp2 = dFdy(vWorldPos);
        vec2 duv1 = dFdx(vTexCoord);
        vec2 duv2 = dFdy(vTexCoord);
        vec3 dp2perp = cross(dp2, normal);
        vec3 dp1perp = cross(normal, dp1);
        vec3 tangent = dp2perp * duv1.x + dp1perp * duv2.x;
        vec3 bitangent = dp2perp * duv1.y + dp1perp * duv2.y;
        float invMax = inversesqrt(max(max(dot(tangent, tangent), dot(bitangent, bitangent)), 0.000001));
        mat3 tangentFrame = mat3(tangent * invMax, bitangent * invMax, normal);
        normal = normalize(tangentFrame * material.tangentNormal);
    }

    vec3 viewDir = normalize(uCameraPosition.xyz - vWorldPos);
    vec3 lightDir = normalize(uDirectionalDirectionAmbient.xyz);
    vec3 directionalColor = uDirectionalColorCascadeBlend.rgb;
    float roughness = clamp(material.roughness, 0.02, 1.0);
    float metallic = clamp(material.metallic, 0.0, 1.0);
    float shininess = mix(128.0, 4.0, roughness * roughness);
    float diffuseWeight = 1.0 - metallic;
    vec3 specularColor = mix(vec3(0.04), color, metallic);

    vec3 ambient = uDirectionalDirectionAmbient.w * directionalColor;
    float diffuseAmount = max(dot(normal, lightDir), 0.0);
    vec3 halfDir = normalize(lightDir + viewDir);
    float specularAmount = pow(max(dot(normal, halfDir), 0.0), shininess);
    float legacySpecularAmount = pow(max(dot(normal, halfDir), 0.0), 32.0);

    float directionalShadow = 0.0;
    if (uMaterialReceiveShadows && DirectionalShadowsEnabled() && length(directionalColor) > 0.0001)
        directionalShadow = DirectionalShadow(vWorldPos, normal, lightDir);

    vec3 materialDirectional = ambient * color + (1.0 - directionalShadow) * directionalColor *
        (diffuseAmount * color * diffuseWeight + specularAmount * specularColor);
    vec3 legacyDirectional = (ambient + (1.0 - directionalShadow) *
        (diffuseAmount + legacySpecularAmount * 0.5) * directionalColor) * color;
    vec3 result = mix(materialDirectional, legacyDirectional, material.legacyLighting);

    for (int i = 0; i < PointLightCount(); i++) {
        PointLightData light = uPointLights[i];
        vec3 lightVector = light.positionRadius.xyz - vWorldPos;
        float distanceToLight = length(lightVector);
        float radius = light.positionRadius.w;
        if (distanceToLight > radius || distanceToLight <= 0.0001) continue;

        vec3 localLightDir = lightVector / distanceToLight;
        float falloff = clamp(1.0 - (distanceToLight * distanceToLight) / (radius * radius), 0.0, 1.0);
        float attenuation = falloff * falloff;
        float diffuse = max(dot(normal, localLightDir), 0.0);
        vec3 localHalf = normalize(localLightDir + viewDir);
        float specular = pow(max(dot(normal, localHalf), 0.0), shininess);
        float legacySpecular = pow(max(dot(normal, localHalf), 0.0), 32.0);
        float shadow = uMaterialReceiveShadows ? PointShadow(light, vWorldPos) : 0.0;
        vec3 materialContribution = (1.0 - shadow) * light.colorShadowIndex.rgb * attenuation *
            (diffuse * color * diffuseWeight + specular * specularColor);
        vec3 legacyContribution = (1.0 - shadow) * (diffuse + legacySpecular * 0.5) *
            light.colorShadowIndex.rgb * attenuation * color;
        result += mix(materialContribution, legacyContribution, material.legacyLighting);
    }

    for (int i = 0; i < SpotLightCount(); i++) {
        SpotLightData light = uSpotLights[i];
        vec3 lightVector = light.positionRadius.xyz - vWorldPos;
        float distanceToLight = length(lightVector);
        float radius = light.positionRadius.w;
        if (distanceToLight > radius || distanceToLight <= 0.0001) continue;

        vec3 localLightDir = lightVector / distanceToLight;
        float theta = -dot(localLightDir, light.directionInnerCos.xyz);
        float epsilon = max(light.directionInnerCos.w - light.colorOuterCos.w, 0.0001);
        float spotFactor = clamp((theta - light.colorOuterCos.w) / epsilon, 0.0, 1.0);
        if (spotFactor < 0.001) continue;

        float falloff = clamp(1.0 - (distanceToLight * distanceToLight) / (radius * radius), 0.0, 1.0);
        float attenuation = falloff * falloff;
        float diffuse = max(dot(normal, localLightDir), 0.0);
        vec3 localHalf = normalize(localLightDir + viewDir);
        float specular = pow(max(dot(normal, localHalf), 0.0), shininess);
        float legacySpecular = pow(max(dot(normal, localHalf), 0.0), 32.0);
        float shadow = uMaterialReceiveShadows ? SpotShadow(i, light, vWorldPos, normal, localLightDir) : 0.0;
        vec3 materialContribution = (1.0 - shadow) * light.colorOuterCos.rgb * attenuation * spotFactor *
            (diffuse * color * diffuseWeight + specular * specularColor);
        vec3 legacyContribution = (1.0 - shadow) * (diffuse + legacySpecular * 0.5) *
            light.colorOuterCos.rgb * attenuation * spotFactor * color;
        result += mix(materialContribution, legacyContribution, material.legacyLighting);
    }

    result += material.emission;

    if (uIsEmissive)
        result += uEmissiveColor * uEmissiveStrength;

    // Opaque and masked materials must write an opaque framebuffer alpha.
    // Keeping alpha at zero made the ImGui material preview blend the lit RGB
    // away as transparent, which looked like an unlit/black preview.
    float outputAlpha = uMaterialAlphaMode == 2 ? material.alpha : 1.0;
    outputAlpha = max(uIsViewmodel, outputAlpha);
    fragColor = vec4(result, outputAlpha);
}
