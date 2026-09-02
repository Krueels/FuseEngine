#version 430 core

in vec3 vWorldPosition;
in vec3 vWorldNormal;
in vec2 vWaveSlope;
in vec3 vWaveDisplacement;
in float vWaveFoam;

layout(early_fragment_tests) in;
layout(location = 0) out vec4 fragColor;
layout(rgba16f, binding = 0) uniform image2D uWaterSurfaceDataImage;

uniform sampler2D uSceneColor;
uniform sampler2D uSceneDepth;
uniform samplerCube uPrefilteredEnvMap;
uniform bool uUseIbl;
uniform float uIblIntensity;

uniform mat4 uView;
uniform mat4 uProj;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPosition;
uniform vec3 uSunDirection;
uniform vec3 uSunColor;
uniform vec3 uSkyZenithColor;
uniform vec3 uSkyHorizonColor;
uniform vec3 uSkyGroundColor;
uniform float uWaterLevel;
uniform float uReflectionStrength;
uniform float uRefractionStrength;
uniform float uAbsorptionDistance;
uniform float uSurfaceRoughness;
uniform vec3 uShallowColor;
uniform vec3 uDeepColor;
uniform vec3 uFoamColor;
uniform float uFoamStrength;
uniform float uFoamDepth;
uniform float uWaveAmplitude;
uniform float uWaveTime;
uniform vec2 uWaveDirection;
uniform float uWaveChoppiness;
uniform sampler2D uOceanNormalMap;
uniform bool uUseOceanNormalMap;
uniform float uOceanNormalMapStrength;
uniform float uOceanNormalMapScale;
uniform float uOceanNormalMapDistortion;
uniform int uDebugView;
uniform bool uSceneIsSrgb;
uniform bool uOutputSrgb;

// The ocean surface is rendered after the forward pass. Re-declaring the
// lighting block here lets the water use the same light selection and shadow
// data as the forward materials.
struct OceanPointLightData
{
    vec4 positionRadius;
    vec4 colorShadowIndex;
    vec4 params;
};

struct OceanSpotLightData
{
    vec4 positionRadius;
    vec4 directionInnerCos;
    vec4 colorOuterCos;
    vec4 shadowParams;
};

layout(std140, binding = 1) uniform OceanLightingBlock
{
    vec4 oceanLightCounts;
    vec4 oceanDirectionalDirectionAmbient;
    vec4 oceanDirectionalColorCascadeBlend;
    vec4 oceanShadowParams;
    vec4 oceanCascadeDistancesAndFade;
    vec4 oceanCascadeTexelSizes;
    vec4 oceanLightingCameraPosition;
    mat4 oceanLightSpaceMatrices[3];
    mat4 oceanSpotLightSpaceMatrices[4];
    OceanPointLightData oceanPointLights[8];
    OceanSpotLightData oceanSpotLights[4];
};

// The ocean pass uses unit 1 for scene depth. The renderer mirrors the
// directional shadow array to unit 7, while units 2-6 remain the local-light
// shadow maps used by the forward pass.
layout(binding = 7) uniform sampler2DArrayShadow uOceanDirectionalShadowMap;
layout(binding = 2) uniform sampler2DArrayShadow uOceanSpotShadowMap;
layout(binding = 3) uniform samplerCube uOceanPointShadowMap0;
layout(binding = 4) uniform samplerCube uOceanPointShadowMap1;
layout(binding = 5) uniform samplerCube uOceanPointShadowMap2;
layout(binding = 6) uniform samplerCube uOceanPointShadowMap3;

vec3 SrgbToLinear(vec3 color)
{
    return pow(max(color, vec3(0.0)), vec3(2.2));
}

vec3 LinearToSrgb(vec3 color)
{
    return pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
}

vec3 ReconstructWorld(vec2 uv, float depth)
{
    vec4 clipPosition = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 worldPosition = uInvViewProj * clipPosition;
    float safeW = abs(worldPosition.w) > 0.00001
        ? worldPosition.w
        : (worldPosition.w < 0.0 ? -0.00001 : 0.00001);
    return worldPosition.xyz / safeW;
}

float Hash21(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float ValueNoise(vec2 p)
{
    vec2 cell = floor(p);
    vec2 local = fract(p);
    local = local * local * (3.0 - 2.0 * local);
    float a = Hash21(cell);
    float b = Hash21(cell + vec2(1.0, 0.0));
    float c = Hash21(cell + vec2(0.0, 1.0));
    float d = Hash21(cell + vec2(1.0, 1.0));
    return mix(mix(a, b, local.x), mix(c, d, local.x), local.y);
}

float FoamBreakup(vec2 worldPosition)
{
    // A low-frequency field prevents whitecaps from looking like a uniform
    // ring. The second octave keeps the transition soft at a distance.
    vec2 coordinates = worldPosition * vec2(0.0067, 0.0083);
    float large = ValueNoise(coordinates + vec2(uWaveTime * 0.003, 0.0));
    float small = ValueNoise(coordinates * 2.37 -
        vec2(0.0, uWaveTime * 0.005));
    return clamp(large * 0.72 + small * 0.28, 0.0, 1.0);
}

vec2 ProceduralMicroSlope(vec2 worldPosition)
{
    // Four incommensurate wave trains supply sub-pixel normal variation even
    // when the optional detail texture is unavailable or reserved by IBL.
    const vec2 directionA = vec2(0.9740, 0.2242);
    const vec2 directionB = vec2(-0.2966, 0.9550);
    const vec2 directionC = vec2(0.7845, -0.6201);
    const vec2 directionD = vec2(-0.9143, -0.4029);
    float time = uWaveTime * (0.16 + 0.035 * max(uWaveChoppiness, 0.0));
    vec2 slope = vec2(0.0);

    float phaseA = dot(worldPosition, directionA) * 0.070 + time * 1.13;
    float phaseB = dot(worldPosition, directionB) * 0.115 - time * 0.77;
    float phaseC = dot(worldPosition, directionC) * 0.190 + time * 0.61;
    float phaseD = dot(worldPosition, directionD) * 0.310 - time * 0.43;
    slope += directionA * cos(phaseA) * 0.026;
    slope += directionB * cos(phaseB) * 0.018;
    slope += directionC * cos(phaseC) * 0.010;
    slope += directionD * cos(phaseD) * 0.005;
    return slope * (0.60 + 0.16 * clamp(uWaveChoppiness, 0.0, 3.0));
}

vec3 SampleSkyReflection(vec3 direction)
{
    float height = clamp(direction.y * 0.5 + 0.5, 0.0, 1.0);
    if (height < 0.5)
    {
        float groundFactor = smoothstep(0.0, 0.5, height);
        return mix(uSkyGroundColor, uSkyHorizonColor, groundFactor);
    }
    return mix(uSkyHorizonColor, uSkyZenithColor, (height - 0.5) * 2.0);
}

const float OCEAN_PI = 3.14159265359;
const vec2 OCEAN_PCF_OFFSETS[4] = vec2[](
    vec2(-0.5, -0.5), vec2(0.5, -0.5),
    vec2(-0.5,  0.5), vec2(0.5,  0.5));

float DistributionGgx(vec3 normal, vec3 halfVector, float roughness)
{
    float alpha = roughness * roughness;
    float alphaSquared = alpha * alpha;
    float nDotH = max(dot(normal, halfVector), 0.0);
    float denominator = nDotH * nDotH * (alphaSquared - 1.0) + 1.0;
    return alphaSquared /
        max(OCEAN_PI * denominator * denominator, 0.000001);
}

float GeometrySchlickGgx(float nDot, float roughness)
{
    float k = ((roughness + 1.0) * (roughness + 1.0)) / 8.0;
    return nDot / max(nDot * (1.0 - k) + k, 0.000001);
}

float GeometrySmith(vec3 normal, vec3 viewDirection,
                    vec3 lightDirection, float roughness)
{
    return GeometrySchlickGgx(
               max(dot(normal, viewDirection), 0.0), roughness) *
           GeometrySchlickGgx(
               max(dot(normal, lightDirection), 0.0), roughness);
}

vec3 FresnelSchlick(float cosine, vec3 f0)
{
    return f0 + (1.0 - f0) *
        pow(1.0 - clamp(cosine, 0.0, 1.0), 5.0);
}

vec3 EvaluateWaterSpecular(vec3 normal, vec3 viewDirection,
                           vec3 lightDirection, vec3 radiance,
                           float roughness)
{
    float nDotV = max(dot(normal, viewDirection), 0.0);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotV <= 0.0 || nDotL <= 0.0)
        return vec3(0.0);

    vec3 halfVector = normalize(viewDirection + lightDirection);
    const vec3 waterF0 = vec3(0.02037);
    vec3 fresnel = FresnelSchlick(
        max(dot(halfVector, viewDirection), 0.0), waterF0);
    float distribution = DistributionGgx(normal, halfVector, roughness);
    float geometry = GeometrySmith(
        normal, viewDirection, lightDirection, roughness);
    return distribution * geometry * fresnel * radiance * nDotL /
        max(4.0 * nDotV * nDotL, 0.0001);
}

float SampleOceanDirectionalCascade(int cascadeIndex,
                                    vec3 worldPosition, vec3 normal,
                                    vec3 lightDirection)
{
    float texelWorldSize = max(
        oceanCascadeTexelSizes[cascadeIndex], 0.0001);
    float slope = 1.0 - max(dot(normal, lightDirection), 0.0);
    float normalOffset = texelWorldSize * mix(0.35, 1.15, slope);
    vec4 lightSpacePosition = oceanLightSpaceMatrices[cascadeIndex] *
        vec4(worldPosition + normal * normalOffset, 1.0);
    if (abs(lightSpacePosition.w) < 0.00001)
        return 0.0;

    vec3 projected = lightSpacePosition.xyz / lightSpacePosition.w;
    projected = projected * 0.5 + 0.5;
    if (projected.z <= 0.0 || projected.z > 1.0 ||
        any(lessThan(projected.xy, vec2(0.0))) ||
        any(greaterThan(projected.xy, vec2(1.0))))
        return 0.0;

    float cascadeScale = 1.0 + float(cascadeIndex) * 0.65;
    float bias = (oceanShadowParams.x + oceanShadowParams.y * slope) *
        cascadeScale;
    if (oceanLightCounts.w <= 0.5)
    {
        float visibility = texture(uOceanDirectionalShadowMap,
            vec4(projected.xy, float(cascadeIndex), projected.z - bias));
        return 1.0 - visibility;
    }

    vec2 texelSize = 1.0 / vec2(
        textureSize(uOceanDirectionalShadowMap, 0).xy);
    float shadow = 0.0;
    for (int i = 0; i < 4; i++)
    {
        float visibility = texture(uOceanDirectionalShadowMap,
            vec4(projected.xy + OCEAN_PCF_OFFSETS[i] * texelSize *
                     oceanShadowParams.z,
                 float(cascadeIndex), projected.z - bias));
        shadow += 1.0 - visibility;
    }
    return shadow * 0.25;
}

float OceanDirectionalShadow(vec3 worldPosition, vec3 normal,
                             vec3 lightDirection)
{
    if (oceanLightCounts.z <= 0.5)
        return 0.0;

    float viewDepth = abs((uView * vec4(worldPosition, 1.0)).z);
    int cascadeIndex = 2;
    if (viewDepth < oceanCascadeDistancesAndFade.x)
        cascadeIndex = 0;
    else if (viewDepth < oceanCascadeDistancesAndFade.y)
        cascadeIndex = 1;

    float shadow = SampleOceanDirectionalCascade(
        cascadeIndex, worldPosition, normal, lightDirection);
    if (cascadeIndex < 2)
    {
        float splitNear = cascadeIndex == 0
            ? 0.0
            : oceanCascadeDistancesAndFade[cascadeIndex - 1];
        float splitFar = oceanCascadeDistancesAndFade[cascadeIndex];
        float blendWidth = max(
            (splitFar - splitNear) *
                oceanDirectionalColorCascadeBlend.w,
            0.02);
        float blend = smoothstep(
            splitFar - blendWidth, splitFar, viewDepth);
        if (blend > 0.0)
        {
            float nextShadow = SampleOceanDirectionalCascade(
                cascadeIndex + 1, worldPosition, normal, lightDirection);
            shadow = mix(shadow, nextShadow, blend);
        }
    }

    float fadeStart = oceanCascadeDistancesAndFade.w;
    float fadeEnd = oceanCascadeDistancesAndFade.z;
    if (fadeEnd > fadeStart)
        shadow *= 1.0 - smoothstep(fadeStart, fadeEnd, viewDepth);
    return clamp(shadow, 0.0, 1.0);
}

float SampleOceanPointShadowMap(int shadowMapIndex,
                                vec3 direction, float compareDepth)
{
    vec3 sampleDirection = normalize(direction);
    float storedDepth = 1.0;
    if (shadowMapIndex == 0)
        storedDepth = texture(
            uOceanPointShadowMap0, sampleDirection).r;
    else if (shadowMapIndex == 1)
        storedDepth = texture(
            uOceanPointShadowMap1, sampleDirection).r;
    else if (shadowMapIndex == 2)
        storedDepth = texture(
            uOceanPointShadowMap2, sampleDirection).r;
    else if (shadowMapIndex == 3)
        storedDepth = texture(
            uOceanPointShadowMap3, sampleDirection).r;
    return compareDepth <= storedDepth ? 1.0 : 0.0;
}

float OceanPointShadow(OceanPointLightData light, vec3 worldPosition)
{
    int shadowMapIndex = int(round(light.colorShadowIndex.w));
    if (shadowMapIndex < 0 || shadowMapIndex > 3)
        return 0.0;

    vec3 fragmentToLight = worldPosition - light.positionRadius.xyz;
    float currentDepth = length(fragmentToLight) * light.params.y;
    float compareDepth = currentDepth - light.params.x;
    vec3 direction = normalize(fragmentToLight);
    if (oceanLightCounts.w <= 0.5)
        return 1.0 - SampleOceanPointShadowMap(
            shadowMapIndex, direction, compareDepth);

    const vec3 sampleDirections[4] = vec3[](
        vec3( 1.0,  1.0,  1.0), vec3( 1.0, -1.0, -1.0),
        vec3(-1.0, -1.0,  1.0), vec3(-1.0,  1.0, -1.0));
    float diskRadius = mix(0.0005, 0.0035, currentDepth) *
        oceanShadowParams.z;
    float shadow = 0.0;
    for (int i = 0; i < 4; i++)
        shadow += 1.0 - SampleOceanPointShadowMap(
            shadowMapIndex,
            direction + sampleDirections[i] * diskRadius,
            compareDepth);
    return shadow * 0.25;
}

float OceanSpotShadow(int lightIndex, OceanSpotLightData light,
                      vec3 worldPosition, vec3 normal,
                      vec3 lightDirection)
{
    if (light.shadowParams.x < 0.5)
        return 0.0;

    vec4 lightSpacePosition = oceanSpotLightSpaceMatrices[lightIndex] *
        vec4(worldPosition, 1.0);
    if (abs(lightSpacePosition.w) < 0.00001)
        return 0.0;
    vec3 projected = lightSpacePosition.xyz / lightSpacePosition.w;
    projected = projected * 0.5 + 0.5;
    if (projected.z <= 0.0 || projected.z > 1.0 ||
        any(lessThan(projected.xy, vec2(0.0))) ||
        any(greaterThan(projected.xy, vec2(1.0))))
        return 0.0;

    float slope = 1.0 - max(dot(normal, lightDirection), 0.0);
    float bias = max(light.shadowParams.y * slope,
                     light.shadowParams.y * 0.1);
    int layer = int(light.shadowParams.z + 0.5);
    if (layer < 0 || layer > 3)
        return 0.0;

    if (oceanLightCounts.w <= 0.5)
    {
        float visibility = texture(uOceanSpotShadowMap,
            vec4(projected.xy, float(layer), projected.z - bias));
        return 1.0 - visibility;
    }

    vec2 texelSize = 1.0 / vec2(
        textureSize(uOceanSpotShadowMap, 0).xy);
    float shadow = 0.0;
    for (int i = 0; i < 4; i++)
    {
        float visibility = texture(uOceanSpotShadowMap,
            vec4(projected.xy + OCEAN_PCF_OFFSETS[i] * texelSize *
                     oceanShadowParams.z,
                 float(layer), projected.z - bias));
        shadow += 1.0 - visibility;
    }
    return shadow * 0.25;
}

vec3 EvaluateOceanLocalLights(vec3 worldPosition, vec3 normal,
                              vec3 viewDirection, float roughness)
{
    vec3 total = vec3(0.0);
    int pointCount = clamp(int(oceanLightCounts.x + 0.5), 0, 8);
    int spotCount = clamp(int(oceanLightCounts.y + 0.5), 0, 4);

    for (int i = 0; i < pointCount; i++)
    {
        OceanPointLightData light = oceanPointLights[i];
        vec3 toLight = light.positionRadius.xyz - worldPosition;
        float distanceToLight = length(toLight);
        float radius = light.positionRadius.w;
        if (distanceToLight <= 0.0001 || distanceToLight > radius)
            continue;
        vec3 lightDirection = toLight / distanceToLight;
        float rangeFade = clamp(1.0 - distanceToLight /
            max(radius, 0.0001), 0.0, 1.0);
        float attenuation = rangeFade * rangeFade /
            max(distanceToLight * distanceToLight, 0.01);
        float shadow = OceanPointShadow(light, worldPosition);
        vec3 radiance = light.colorShadowIndex.rgb * attenuation;
        float nDotL = max(dot(normal, lightDirection), 0.0);
        total += (1.0 - shadow) * (EvaluateWaterSpecular(
            normal, viewDirection, lightDirection,
            radiance, roughness) + radiance * nDotL * 0.018);
    }

    for (int i = 0; i < spotCount; i++)
    {
        OceanSpotLightData light = oceanSpotLights[i];
        vec3 toLight = light.positionRadius.xyz - worldPosition;
        float distanceToLight = length(toLight);
        float radius = light.positionRadius.w;
        if (distanceToLight <= 0.0001 || distanceToLight > radius)
            continue;
        vec3 lightDirection = toLight / distanceToLight;
        float theta = -dot(lightDirection,
            light.directionInnerCos.xyz);
        float epsilon = max(light.directionInnerCos.w -
            light.colorOuterCos.w, 0.0001);
        float spotFactor = clamp((theta - light.colorOuterCos.w) /
            epsilon, 0.0, 1.0);
        if (spotFactor <= 0.001)
            continue;
        float rangeFade = clamp(1.0 - distanceToLight /
            max(radius, 0.0001), 0.0, 1.0);
        float attenuation = rangeFade * rangeFade /
            max(distanceToLight * distanceToLight, 0.01);
        float shadow = OceanSpotShadow(
            i, light, worldPosition, normal, lightDirection);
        vec3 radiance = light.colorOuterCos.rgb * attenuation * spotFactor;
        float nDotL = max(dot(normal, lightDirection), 0.0);
        total += (1.0 - shadow) * (EvaluateWaterSpecular(
            normal, viewDirection, lightDirection,
            radiance, roughness) + radiance * nDotL * 0.018);
    }
    return total;
}

vec3 DecodeOceanNormal(vec3 encoded)
{
    vec3 normal = encoded * 2.0 - 1.0;
    normal.y = max(normal.y, 0.05);
    return normalize(normal);
}

vec2 OceanNormalMapWarp(vec2 worldPosition)
{
    float distortion = max(uOceanNormalMapDistortion, 0.0);
    vec2 safeDirection = length(uWaveDirection) > 0.001
        ? normalize(uWaveDirection)
        : vec2(1.0, 0.0);
    vec2 baseCoordinates = worldPosition *
        max(uOceanNormalMapScale, 0.001);
    vec2 spectralWarp = vWaveDisplacement.xz *
        max(uOceanNormalMapScale, 0.001) * distortion;
    vec2 slopeWarp = vec2(vWaveSlope.y, -vWaveSlope.x) *
        distortion * 0.08;
    float phaseA = dot(baseCoordinates, vec2(1.31, -0.77)) +
        uWaveTime * 0.43;
    float phaseB = dot(baseCoordinates, vec2(-0.64, 1.17)) -
        uWaveTime * 0.31;
    vec2 domainWarp = vec2(sin(phaseA), cos(phaseB)) *
        (0.028 * distortion);
    vec2 flow = safeDirection * uWaveTime * 0.006;
    return spectralWarp + slopeWarp + domainWarp + flow;
}

vec3 ApplyOceanNormalDetail(vec3 baseNormal)
{
    vec2 worldPosition = vWorldPosition.xz;
    vec2 proceduralSlope = ProceduralMicroSlope(worldPosition);
    vec3 detailTangent = normalize(vec3(
        -proceduralSlope.x, 1.0, -proceduralSlope.y));

    // The renderer reserves a separate texture unit for this map, so it stays
    // available together with the IBL prefiltered environment.
    if (uUseOceanNormalMap && uOceanNormalMapStrength > 0.001)
    {
        float scale = max(uOceanNormalMapScale, 0.001);
        vec2 baseUv = worldPosition * scale;
        vec2 warp = OceanNormalMapWarp(worldPosition);
        vec2 flow = length(uWaveDirection) > 0.001
            ? normalize(uWaveDirection) * uWaveTime * 0.006
            : vec2(uWaveTime * 0.006, 0.0);

        // Three rotated/scaled samples plus the simulated displacement keep
        // the normal map from becoming a visible repeating tile.
        vec3 normalA = DecodeOceanNormal(texture(
            uOceanNormalMap, baseUv + warp).rgb);
        vec3 normalB = DecodeOceanNormal(texture(
            uOceanNormalMap,
            baseUv * 1.71 - flow * 0.63 + warp.yx * 0.72 +
            vec2(0.37, -0.23)).rgb);
        vec3 normalC = DecodeOceanNormal(texture(
            uOceanNormalMap,
            baseUv * 0.63 + flow * 0.41 - warp * 1.31 +
            vec2(-0.19, 0.43)).rgb);
        vec3 mapTangent = normalize(
            normalA * 0.52 + normalB * 0.30 + normalC * 0.18);
        mapTangent.xy += proceduralSlope * 0.42;
        detailTangent = normalize(mix(
            detailTangent, mapTangent,
            clamp(uOceanNormalMapStrength, 0.0, 1.0)));
    }

    vec3 tangentX = vec3(1.0, 0.0, 0.0) -
        baseNormal * baseNormal.x;
    if (length(tangentX) < 0.001)
        tangentX = vec3(0.0, 0.0, 1.0) -
            baseNormal * baseNormal.z;
    tangentX = normalize(tangentX);
    vec3 tangentZ = normalize(cross(tangentX, baseNormal));
    vec3 detailWorld = normalize(
        tangentX * detailTangent.x +
        baseNormal * detailTangent.y +
        tangentZ * detailTangent.z);

    float proceduralStrength = clamp(
        0.18 + max(uWaveChoppiness, 0.0) * 0.035, 0.18, 0.32);
    float detailStrength = max(
        proceduralStrength,
        clamp(uOceanNormalMapStrength, 0.0, 1.0) * 0.82);
    return normalize(mix(baseNormal, detailWorld,
        clamp(detailStrength, 0.0, 0.90)));
}

void main()
{
    vec3 normal = normalize(vWorldNormal);
    vec3 viewDirection = normalize(uCameraPosition - vWorldPosition);
    // The ocean is rendered double-sided near the waterline. Face the normal
    // towards the camera for a stable Fresnel response on both sides.
    if (dot(normal, viewDirection) < 0.0)
        normal = -normal;
    normal = ApplyOceanNormalDetail(normal);

    imageStore(
        uWaterSurfaceDataImage,
        ivec2(gl_FragCoord.xy),
        vec4(
            gl_FragCoord.z,
            1.0,
            normal.x * 0.5 + 0.5,
            normal.z * 0.5 + 0.5));

    if (uDebugView != 0)
    {
        vec3 debugColor = vec3(0.0);
        if (uDebugView == 1)
        {
            float height = 0.5 +
                (vWorldPosition.y - uWaterLevel) /
                max(abs(uWaveAmplitude) * 2.0, 0.001);
            debugColor = vec3(clamp(height, 0.0, 1.0));
        }
        else if (uDebugView == 2)
        {
            float slope = length(vWaveSlope);
            debugColor = vec3(clamp(slope * 0.35, 0.0, 1.0));
        }
        else
        {
            debugColor = clamp(
                vec3(0.5) + vWaveDisplacement * 0.5,
                vec3(0.0),
                vec3(1.0));
        }
        fragColor = vec4(debugColor, 1.0);
        return;
    }

    float nDotV = max(dot(normal, viewDirection), 0.0);
    const float waterIor = 1.333;
    float f0 = pow((1.0 - waterIor) / (1.0 + waterIor), 2.0);
    float fresnel = f0 + (1.0 - f0) *
        pow(1.0 - nDotV, 5.0);

    vec2 sceneUv = gl_FragCoord.xy /
        vec2(textureSize(uSceneDepth, 0));
    float sceneDepth = texture(uSceneDepth, sceneUv).r;
    bool hasSceneSurface = sceneDepth < 0.9999;
    vec3 scenePosition = hasSceneSurface
        ? ReconstructWorld(sceneUv, sceneDepth)
        : vWorldPosition +
          normalize(vWorldPosition - uCameraPosition) *
          uAbsorptionDistance;

    float waterDepth = hasSceneSurface
        ? max(vWorldPosition.y - scenePosition.y, 0.0)
        : uAbsorptionDistance * 2.0;
    waterDepth /= max(abs(dot(normal, viewDirection)), 0.2);
    float depthFactor = clamp(
        waterDepth / max(uAbsorptionDistance, 0.001),
        0.0,
        1.0);
    float transmittance = exp(
        -waterDepth / max(uAbsorptionDistance, 0.001));

    vec2 screenUv = gl_FragCoord.xy /
        vec2(textureSize(uSceneColor, 0));
    vec2 refractionUv = clamp(
        screenUv + normal.xz * uRefractionStrength *
        (0.25 + depthFactor * 0.75),
        vec2(0.001),
        vec2(0.999));
    vec3 scene = texture(uSceneColor, refractionUv).rgb;
    if (uSceneIsSrgb)
        scene = SrgbToLinear(scene);

    vec3 waterBodyColor = mix(
        uShallowColor,
        uDeepColor,
        depthFactor);
    vec3 refractedColor = scene * transmittance +
        waterBodyColor * (1.0 - transmittance);

    vec3 reflectionDirection = reflect(-viewDirection, normal);
    vec3 reflectedColor = SampleSkyReflection(reflectionDirection);
    if (uUseIbl)
    {
        float mip = clamp(uSurfaceRoughness, 0.0, 1.0) * 5.0;
        reflectedColor = textureLod(
            uPrefilteredEnvMap,
            reflectionDirection,
            mip).rgb;
        reflectedColor *= max(uIblIntensity, 0.0);
    }

    vec3 sunDirection = length(uSunDirection) > 0.001
        ? normalize(uSunDirection)
        : vec3(0.0, 1.0, 0.0);
    float roughness = clamp(uSurfaceRoughness, 0.035, 0.82);
    // uSunDirection points from the scene toward the light source. The sky
    // switches to its night layer below the horizon, so the water must apply
    // the same elevation gate to the direct-sun terms; otherwise the sun's
    // stale directional-light color keeps lighting the ocean at night.
    float daylight = smoothstep(-0.12, 0.12, sunDirection.y);
    float solarVisibility = daylight * (1.0 - OceanDirectionalShadow(
        vWorldPosition, normal, sunDirection));
    vec3 sunRadiance = max(uSunColor, vec3(0.0)) * solarVisibility;
    vec3 directSunSpecular = EvaluateWaterSpecular(
        normal, viewDirection, sunDirection, sunRadiance, roughness);
    // A restrained broad lobe fills the gaps between the GGX highlights and
    // keeps distant waves readable when the sun is close to the horizon.
    float broadSunLobe = pow(max(dot(reflectionDirection,
        sunDirection), 0.0), mix(24.0, 180.0, 1.0 - roughness));
    reflectedColor += sunRadiance * broadSunLobe * 0.055;

    float reflectionWeight = clamp(
        fresnel * max(uReflectionStrength, 0.0),
        0.0,
        1.0);
    vec3 color = mix(
        refractedColor,
        reflectedColor,
        reflectionWeight);
    color += directSunSpecular * max(uReflectionStrength, 0.0);
    // Water has a small broad/subsurface response in addition to the sharp
    // reflection. It is intentionally restrained, but it makes a caster's
    // directional shadow readable on the surface instead of affecting only a
    // tiny GGX highlight.
    color += sunRadiance * max(dot(normal, sunDirection), 0.0) * 0.018;
    color += EvaluateOceanLocalLights(
        vWorldPosition, normal, viewDirection, roughness);

    // Steep slopes receive less ambient light. Whitecaps are independent of
    // scene depth; depth is used only for shoreline foam, so open water no
    // longer loses all foam merely because the seabed is far below it.
    float slope = length(vWaveSlope);
    float slopeFoam = smoothstep(0.055, 0.30, slope * 1.20);
    float simulatedCrest = smoothstep(0.04, 0.75, vWaveFoam);
    float whitecap = max(slopeFoam * 0.82, simulatedCrest);
    whitecap *= mix(0.36, 1.24, FoamBreakup(vWorldPosition.xz));
    float shorelineFoam = hasSceneSurface
        ? 1.0 - smoothstep(0.05,
            max(uFoamDepth, 0.001), waterDepth)
        : 0.0;
    float foamSignal = clamp(max(whitecap,
        shorelineFoam * 0.92), 0.0, 1.0);
    float foam = 1.0 - exp(-foamSignal *
        max(uFoamStrength, 0.0) * 5.5);
    color = mix(
        color * mix(0.72, 1.0, clamp(normal.y, 0.0, 1.0)),
        uFoamColor,
        clamp(foam, 0.0, 1.0));

    if (uOutputSrgb)
        color = LinearToSrgb(color);
    fragColor = vec4(max(color, vec3(0.0)), 1.0);
}
