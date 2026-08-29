#define MAX_POINT_LIGHTS 8
#define MAX_SPOT_LIGHTS 4

struct PointLightData {
    vec4 positionRadius;
    vec4 colorShadowIndex;
    vec4 params;
};

struct SpotLightData {
    vec4 positionRadius;
    vec4 directionInnerCos;
    vec4 colorOuterCos;
    vec4 shadowParams;
};

layout(std140) uniform LightingBlock {
    // x: point count, y: spot count, z: shadows enabled, w: filtering enabled
    vec4 uLightCounts;
    // xyz: direction toward the source, w: ambient amount
    vec4 uDirectionalDirectionAmbient;
    // rgb: directional color, w: cascade blend fraction
    vec4 uDirectionalColorCascadeBlend;
    // x: base bias, y: slope bias, z: PCF spread, w: shadow far plane
    vec4 uShadowParams;
    // xyz: cascade splits, w: fade start
    vec4 uCascadeDistancesAndFade;
    // xyz: world-space texel sizes for the three cascades
    vec4 uCascadeTexelSizes;
    vec4 uCameraPosition;
    mat4 uLightSpaceMatrices[3];
    mat4 uSpotLightSpaceMatrices[MAX_SPOT_LIGHTS];
    PointLightData uPointLights[MAX_POINT_LIGHTS];
    SpotLightData uSpotLights[MAX_SPOT_LIGHTS];
};

int PointLightCount() { return int(uLightCounts.x + 0.5); }
int SpotLightCount() { return int(uLightCounts.y + 0.5); }
bool DirectionalShadowsEnabled() { return uLightCounts.z > 0.5; }
bool LightingShadowFilterEnabled() { return uLightCounts.w > 0.5; }
