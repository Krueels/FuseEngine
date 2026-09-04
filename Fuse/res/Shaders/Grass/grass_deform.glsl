uniform float uTime;
uniform vec2 uWindDirection;
uniform float uWindStrength;
uniform float uWindSpeed;
uniform float uGustStrength;
uniform float uGustScale;
uniform vec3 uCameraPosition;

struct GrassDeformation
{
    vec3 offset;
    vec3 normal;
    float height;
    float variation;
};

GrassDeformation DeformGrassBlade(
    vec3 bladeVertex,
    vec4 positionHeight,
    vec4 normalWidth,
    vec4 parameters)
{
    vec3 terrainNormal = normalize(normalWidth.xyz);
    float widthScale = normalWidth.w;
    float bladeHeight = positionHeight.w;
    float yaw = parameters.x + bladeVertex.z;
    float phase = parameters.y;
    float randomValue = parameters.z;
    int encodedVariant = int(parameters.w + 0.5);
    int lod = encodedVariant & 3;

    vec3 reference = abs(terrainNormal.y) < 0.95
        ? vec3(0.0, 1.0, 0.0)
        : vec3(0.0, 0.0, 1.0);
    vec3 tangent = normalize(cross(reference, terrainNormal));
    vec3 bitangent = normalize(cross(terrainNormal, tangent));
    vec3 bladeSide = tangent * cos(yaw) + bitangent * sin(yaw);

    vec2 wind2 = normalize(length(uWindDirection) > 0.0001
        ? uWindDirection
        : vec2(1.0, 0.0));
    vec3 windDirection = normalize(vec3(wind2.x, 0.0, wind2.y));
    vec2 stableWorldPosition = positionHeight.xz + uCameraPosition.xz;
    float t = clamp(bladeVertex.y, 0.0, 1.0);
    float macroWave = sin(uTime * uWindSpeed + phase +
                          dot(stableWorldPosition, wind2) * uGustScale);
    float microWave = sin(uTime * (uWindSpeed * 2.37 + 0.13) +
                          phase * 1.71 + t * 4.0);
    float gust = 0.5 + 0.5 * sin(uTime * uWindSpeed * 0.37 +
                                dot(stableWorldPosition, wind2) * uGustScale * 0.37 + phase);
    float bendAmount = (macroWave * uWindStrength +
                        microWave * 0.12 * uWindStrength +
                        gust * uGustStrength) * t * t;
    bendAmount *= mix(0.78, 1.18, randomValue);

    float lodWidth = lod == 2 ? 2.1 : (lod == 1 ? 1.25 : 1.0);
    float lodHeight = lod == 2 ? 0.90 : 1.0;
    vec3 localOffset = bladeSide * bladeVertex.x * widthScale * lodWidth;
    localOffset += terrainNormal * (t * bladeHeight * lodHeight);
    localOffset += windDirection * (bendAmount * bladeHeight * 0.34);
    localOffset += bladeSide * (bendAmount * bendAmount * bladeHeight * 0.055);

    GrassDeformation result;
    result.offset = localOffset;
    result.normal = normalize(mix(bladeSide, terrainNormal, 0.34) -
                              windDirection * bendAmount * 0.16);
    result.height = t;
    result.variation = randomValue;
    return result;
}
