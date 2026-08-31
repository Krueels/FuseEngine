#version 330 core
#include "lighting.glsl"

uniform sampler2D uDepthTex;
uniform sampler2D uReceiverNormal;
uniform sampler2D uReceiverMaterial;
uniform sampler2D uDecalAlbedo;

uniform mat4 uInvViewProj;
uniform mat4 uInvDecalModel;
uniform vec2 uScreenSize;
uniform float uOpacity;

uniform samplerCube uDiffuseIrradianceMap;
uniform samplerCube uPrefilteredEnvMap;
uniform sampler2D uBrdfLut;
uniform bool uUseIbl;
uniform float uIblIntensity;
uniform sampler2DArrayShadow uShadowMap;
uniform sampler2DArrayShadow uSpotShadowMap;
uniform samplerCube uPointShadowMap0;
uniform samplerCube uPointShadowMap1;
uniform samplerCube uPointShadowMap2;
uniform samplerCube uPointShadowMap3;
uniform mat4 uView;
uniform bool uMaterialReceiveShadows;

out vec4 outColor;

const float FUSE_DECAL_PI = 3.14159265359;
const float FUSE_DECAL_MAX_PREFILTER_MIP = 4.0;

const vec2 FUSE_DECAL_PCF_OFFSETS[4] = vec2[](
    vec2(-0.5, -0.5),
    vec2(0.5, -0.5),
    vec2(-0.5, 0.5),
    vec2(0.5, 0.5)
);

float DecalSampleDirectionalCascade(
    int cascadeIndex,
    vec3 worldPos,
    vec3 normal,
    vec3 lightDirection)
{
    float texelWorldSize = max(
        uCascadeTexelSizes[cascadeIndex],
        0.0001);
    float slope = 1.0 - max(
        dot(normal, lightDirection),
        0.0);
    float normalOffset = texelWorldSize *
        mix(0.35, 1.15, slope);

    vec4 lightSpacePosition =
        uLightSpaceMatrices[cascadeIndex] *
        vec4(worldPos + normal * normalOffset, 1.0);
    vec3 projected =
        lightSpacePosition.xyz /
        lightSpacePosition.w;
    projected = projected * 0.5 + 0.5;

    if (projected.z <= 0.0 || projected.z > 1.0 ||
        projected.x < 0.0 || projected.x > 1.0 ||
        projected.y < 0.0 || projected.y > 1.0)
        return 0.0;

    float cascadeScale = 1.0 +
        float(cascadeIndex) * 0.65;
    float bias = (
        uShadowParams.x +
        uShadowParams.y * slope
    ) * cascadeScale;

    if (!LightingShadowFilterEnabled())
    {
        float visibility = texture(
            uShadowMap,
            vec4(projected.xy, cascadeIndex, projected.z - bias));
        return 1.0 - visibility;
    }

    vec2 texelSize = 1.0 /
        vec2(textureSize(uShadowMap, 0).xy);
    float shadow = 0.0;

    for (int i = 0; i < 4; i++)
    {
        vec2 offset =
            FUSE_DECAL_PCF_OFFSETS[i] *
            texelSize *
            uShadowParams.z;
        float visibility = texture(
            uShadowMap,
            vec4(
                projected.xy + offset,
                cascadeIndex,
                projected.z - bias));
        shadow += 1.0 - visibility;
    }

    return shadow * 0.25;
}

float DecalDirectionalShadow(
    vec3 worldPos,
    vec3 normal,
    vec3 lightDirection)
{
    float viewDepth = abs(
        (uView * vec4(worldPos, 1.0)).z);
    int cascadeIndex = 2;

    if (viewDepth < uCascadeDistancesAndFade.x)
        cascadeIndex = 0;
    else if (viewDepth < uCascadeDistancesAndFade.y)
        cascadeIndex = 1;

    float shadow = DecalSampleDirectionalCascade(
        cascadeIndex,
        worldPos,
        normal,
        lightDirection);

    if (cascadeIndex < 2)
    {
        float splitNear = cascadeIndex == 0
            ? 0.0
            : uCascadeDistancesAndFade[cascadeIndex - 1];
        float splitFar =
            uCascadeDistancesAndFade[cascadeIndex];
        float blendWidth = max(
            (splitFar - splitNear) *
            uDirectionalColorCascadeBlend.w,
            0.02);
        float blend = smoothstep(
            splitFar - blendWidth,
            splitFar,
            viewDepth);

        if (blend > 0.0)
        {
            float nextShadow = DecalSampleDirectionalCascade(
                cascadeIndex + 1,
                worldPos,
                normal,
                lightDirection);
            shadow = mix(shadow, nextShadow, blend);
        }
    }

    float fadeStart = uCascadeDistancesAndFade.w;
    float fadeEnd = uCascadeDistancesAndFade.z;
    if (fadeEnd > fadeStart)
    {
        shadow *= 1.0 - smoothstep(
            fadeStart,
            fadeEnd,
            viewDepth);
    }

    return shadow;
}

float DecalSamplePointShadowMap(
    int shadowMapIndex,
    vec3 direction,
    float compareDepth)
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

float DecalPointShadow(
    PointLightData light,
    vec3 worldPos)
{
    int shadowMapIndex = int(
        round(light.colorShadowIndex.w));
    if (shadowMapIndex < 0)
        return 0.0;

    vec3 fragmentToLight =
        worldPos - light.positionRadius.xyz;
    float currentDepth =
        length(fragmentToLight) * light.params.y;
    float compareDepth = currentDepth - light.params.x;
    vec3 direction = normalize(fragmentToLight);

    if (!LightingShadowFilterEnabled())
    {
        return 1.0 - DecalSamplePointShadowMap(
            shadowMapIndex,
            direction,
            compareDepth);
    }

    const vec3 sampleDirections[4] = vec3[](
        vec3(1, 1, 1),
        vec3(1, -1, -1),
        vec3(-1, -1, 1),
        vec3(-1, 1, -1)
    );

    float diskRadius = mix(
        0.0005,
        0.0035,
        currentDepth) *
        uShadowParams.z;
    float shadow = 0.0;

    for (int i = 0; i < 4; i++)
    {
        shadow += 1.0 -
            DecalSamplePointShadowMap(
                shadowMapIndex,
                direction + sampleDirections[i] * diskRadius,
                compareDepth);
    }

    return shadow * 0.25;
}

float DecalSpotShadow(
    int lightIndex,
    SpotLightData light,
    vec3 worldPos,
    vec3 normal,
    vec3 lightDirection)
{
    if (light.shadowParams.x < 0.5)
        return 0.0;

    vec4 lightSpacePosition =
        uSpotLightSpaceMatrices[lightIndex] *
        vec4(worldPos, 1.0);
    vec3 projected =
        lightSpacePosition.xyz /
        lightSpacePosition.w;
    projected = projected * 0.5 + 0.5;

    if (projected.z <= 0.0 || projected.z > 1.0 ||
        projected.x < 0.0 || projected.x > 1.0 ||
        projected.y < 0.0 || projected.y > 1.0)
        return 0.0;

    float slope = 1.0 - max(
        dot(normal, lightDirection),
        0.0);
    float bias = max(
        light.shadowParams.y * slope,
        light.shadowParams.y * 0.1);
    int layer = int(
        light.shadowParams.z + 0.5);

    if (!LightingShadowFilterEnabled())
    {
        float visibility = texture(
            uSpotShadowMap,
            vec4(
                projected.xy,
                layer,
                projected.z - bias));
        return 1.0 - visibility;
    }

    vec2 texelSize = 1.0 /
        vec2(textureSize(uSpotShadowMap, 0).xy);
    float shadow = 0.0;

    for (int i = 0; i < 4; i++)
    {
        float visibility = texture(
            uSpotShadowMap,
            vec4(
                projected.xy +
                    FUSE_DECAL_PCF_OFFSETS[i] *
                    texelSize *
                    uShadowParams.z,
                layer,
                projected.z - bias));
        shadow += 1.0 - visibility;
    }

    return shadow * 0.25;
}

float DecalDistributionGGX(
    vec3 normal,
    vec3 halfVector,
    float roughness)
{
    float alpha = roughness * roughness;
    float alphaSquared = alpha * alpha;
    float nDotH = max(dot(normal, halfVector), 0.0);
    float denominator = nDotH * nDotH * (alphaSquared - 1.0) + 1.0;

    return alphaSquared /
        max(FUSE_DECAL_PI * denominator * denominator, 0.000001);
}

float DecalGeometrySchlickGGX(float nDot, float roughness)
{
    float k = ((roughness + 1.0) * (roughness + 1.0)) / 8.0;

    return nDot /
        max(nDot * (1.0 - k) + k, 0.000001);
}

float DecalGeometrySmith(
    vec3 normal,
    vec3 viewDirection,
    vec3 lightDirection,
    float roughness)
{
    return DecalGeometrySchlickGGX(
        max(dot(normal, viewDirection), 0.0),
        roughness) *
        DecalGeometrySchlickGGX(
        max(dot(normal, lightDirection), 0.0),
        roughness);
}

vec3 DecalFresnelSchlick(float cosTheta, vec3 f0)
{
    return f0 +
        (1.0 - f0) *
        pow(1.0 - clamp(cosTheta, 0.0, 1.0), 5.0);
}

vec3 DecalFresnelSchlickRoughness(
    float cosTheta,
    vec3 f0,
    float roughness)
{
    return f0 +
        (max(vec3(1.0 - roughness), f0) - f0) *
        pow(1.0 - clamp(cosTheta, 0.0, 1.0), 5.0);
}

vec3 DecalEvaluateCookTorrance(
    vec3 normal,
    vec3 viewDirection,
    vec3 lightDirection,
    vec3 radiance,
    vec3 albedo,
    float metallic,
    float roughness)
{
    float nDotV = max(dot(normal, viewDirection), 0.0);
    float nDotL = max(dot(normal, lightDirection), 0.0);

    if (nDotV <= 0.0 || nDotL <= 0.0)
        return vec3(0.0);

    vec3 halfVector = normalize(viewDirection + lightDirection);
    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    vec3 fresnel = DecalFresnelSchlick(
        max(dot(halfVector, viewDirection), 0.0),
        f0);

    float distribution = DecalDistributionGGX(
        normal,
        halfVector,
        roughness);

    float geometry = DecalGeometrySmith(
        normal,
        viewDirection,
        lightDirection,
        roughness);

    vec3 specular =
        (distribution * geometry * fresnel) /
        max(4.0 * nDotV * nDotL, 0.0001);

    vec3 diffuse =
        (1.0 - fresnel) *
        (1.0 - metallic) *
        albedo /
        FUSE_DECAL_PI;

    return (diffuse + specular) * radiance * nDotL;
}

vec3 DecalEvaluateIbl(
    vec3 normal,
    vec3 viewDirection,
    vec3 albedo,
    float metallic,
    float roughness,
    float ao)
{
    if (!uUseIbl)
    {
        return uDirectionalDirectionAmbient.w *
            uDirectionalColorCascadeBlend.rgb *
            albedo *
            ao;
    }

    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    float nDotV = max(dot(normal, viewDirection), 0.0);

    vec3 fresnel = DecalFresnelSchlickRoughness(
        nDotV,
        f0,
        roughness);

    vec3 kS = fresnel;
    vec3 kD = (1.0 - kS) * (1.0 - metallic);

    vec3 irradiance =
        texture(uDiffuseIrradianceMap, normal).rgb;

    vec3 diffuse = irradiance * albedo;

    vec3 reflection =
        reflect(-viewDirection, normal);

    vec3 prefiltered =
        textureLod(
            uPrefilteredEnvMap,
            reflection,
            roughness * FUSE_DECAL_MAX_PREFILTER_MIP).rgb;

    vec2 brdf =
        texture(uBrdfLut, vec2(nDotV, roughness)).rg;

    vec3 specular =
        prefiltered *
        (fresnel * brdf.x + brdf.y);

    return (kD * diffuse + specular) *
        ao *
        uIblIntensity;
}

void main()
{
    vec2 screenUV = gl_FragCoord.xy / uScreenSize;
    float depth = texture(uDepthTex, screenUV).r;
    if (depth >= 1.0)
        discard;

    vec4 ndc = vec4(
        screenUV * 2.0 - 1.0,
        depth * 2.0 - 1.0,
        1.0);

    vec4 worldPosH = uInvViewProj * ndc;
    vec3 worldPos = worldPosH.xyz / worldPosH.w;

    vec4 localPos = uInvDecalModel * vec4(worldPos, 1.0);
    vec3 p = localPos.xyz / localPos.w;
    if (abs(p.x) > 0.5 || abs(p.y) > 0.5 || abs(p.z) > 0.5)
        discard;

    // Use the geometric normal only to choose the projection axis.
    vec3 dX = dFdx(worldPos);
    vec3 dY = dFdy(worldPos);
    vec3 geometricNormal = normalize(cross(dX, dY));
    vec3 localNormal = normalize(
        (uInvDecalModel * vec4(geometricNormal, 0.0)).xyz);
    vec3 absNormal = abs(localNormal);

    // Use the final receiver normal, including its normal map, for lighting.
    vec3 encodedNormal = texture(uReceiverNormal, screenUV).xyz;
    vec3 decodedNormal = encodedNormal * 2.0 - 1.0;
    if (dot(decodedNormal, decodedNormal) < 0.25)
        discard;
    vec3 surfaceNormal = normalize(decodedNormal);

    vec2 uv;
    if (absNormal.z >= absNormal.x && absNormal.z >= absNormal.y)
    {
        uv = p.xy + 0.5;
    }
    else if (absNormal.x >= absNormal.y)
    {
        uv = vec2(
            0.5 + p.x + sign(p.x) * abs(p.z),
            p.y + 0.5);
    }
    else
    {
        uv = vec2(
            p.x + 0.5,
            0.5 + p.y + sign(p.y) * abs(p.z));
    }

    if (uv.x < 0.0 || uv.x > 1.0 ||
        uv.y < 0.0 || uv.y > 1.0)
        discard;

    vec4 albedo = texture(uDecalAlbedo, uv);
    if (albedo.a < 0.01)
        discard;

    vec4 receiverMaterial =
        texture(uReceiverMaterial, screenUV);

    float roughness = clamp(
        receiverMaterial.r,
        0.02,
        1.0);

    float metallic = clamp(
        receiverMaterial.g,
        0.0,
        1.0);

    float ao = clamp(
        receiverMaterial.b,
        0.0,
        1.0);

    vec3 viewDirection = normalize(
        uCameraPosition.xyz - worldPos);

    vec3 totalLight = DecalEvaluateIbl(
        surfaceNormal,
        viewDirection,
        albedo.rgb,
        metallic,
        roughness,
        ao);

    vec3 directionalColor =
        uDirectionalColorCascadeBlend.rgb;

    if (length(directionalColor) > 0.001)
    {
        vec3 directionalDirection =
            normalize(uDirectionalDirectionAmbient.xyz);

        float directionalShadow = 0.0;
        if (uMaterialReceiveShadows &&
            DirectionalShadowsEnabled())
        {
            directionalShadow = DecalDirectionalShadow(
                worldPos,
                surfaceNormal,
                directionalDirection);
        }

        totalLight += (1.0 - directionalShadow) *
            DecalEvaluateCookTorrance(
            surfaceNormal,
            viewDirection,
            directionalDirection,
            directionalColor,
            albedo.rgb,
            metallic,
            roughness);
    }

    for (int i = 0; i < PointLightCount(); i++)
    {
        PointLightData light = uPointLights[i];
        vec3 toLight =
            light.positionRadius.xyz - worldPos;
        float distanceToLight = length(toLight);
        float radius = light.positionRadius.w;

        if (distanceToLight <= 0.0001 ||
            distanceToLight > radius)
            continue;

        vec3 lightDirection =
            toLight / distanceToLight;
        float rangeFade = clamp(
            1.0 - distanceToLight / max(radius, 0.0001),
            0.0,
            1.0);
        float attenuation =
            rangeFade * rangeFade /
            max(distanceToLight * distanceToLight, 0.01);

        float pointShadow = uMaterialReceiveShadows
            ? DecalPointShadow(light, worldPos)
            : 0.0;

        totalLight += (1.0 - pointShadow) *
            DecalEvaluateCookTorrance(
                surfaceNormal,
                viewDirection,
                lightDirection,
                light.colorShadowIndex.rgb * attenuation,
                albedo.rgb,
                metallic,
                roughness);
    }

    for (int i = 0; i < SpotLightCount(); i++)
    {
        SpotLightData light = uSpotLights[i];
        vec3 toLight =
            light.positionRadius.xyz - worldPos;
        float distanceToLight = length(toLight);
        float radius = light.positionRadius.w;

        if (distanceToLight <= 0.0001 ||
            distanceToLight > radius)
            continue;

        vec3 lightDirection =
            toLight / distanceToLight;
        float theta = -dot(
            lightDirection,
            light.directionInnerCos.xyz);
        float epsilon = max(
            light.directionInnerCos.w -
            light.colorOuterCos.w,
            0.0001);
        float spotIntensity = clamp(
            (theta - light.colorOuterCos.w) / epsilon,
            0.0,
            1.0);

        if (spotIntensity <= 0.001)
            continue;

        float rangeFade = clamp(
            1.0 - distanceToLight / max(radius, 0.0001),
            0.0,
            1.0);
        float attenuation =
            rangeFade * rangeFade /
            max(distanceToLight * distanceToLight, 0.01);

        float spotShadow = uMaterialReceiveShadows
            ? DecalSpotShadow(
                i,
                light,
                worldPos,
                surfaceNormal,
                lightDirection)
            : 0.0;

        totalLight += (1.0 - spotShadow) *
            DecalEvaluateCookTorrance(
                surfaceNormal,
                viewDirection,
                lightDirection,
                light.colorOuterCos.rgb *
                    attenuation *
                    spotIntensity,
                albedo.rgb,
                metallic,
                roughness);
    }

    outColor = vec4(
        max(totalLight, vec3(0.0)),
        albedo.a * uOpacity);
}
