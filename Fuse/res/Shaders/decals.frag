#version 330 core

#define MAX_POINT_LIGHTS 8
#define MAX_SPOT_LIGHTS 4

struct PointLight {
    vec3 position;
    vec3 color;
    float radius;
};

struct SpotLight {
    vec3 position;
    vec3 direction;
    vec3 color;
    float radius;
    float innerCos;
    float outerCos;
};

uniform sampler2D uDepthTex;
uniform sampler2D uDecalAlbedo;
uniform mat4 uInvViewProj;
uniform mat4 uInvDecalModel;
uniform vec2 uScreenSize;
uniform float uOpacity;

// Lighting uniforms
uniform vec3 uCameraPos;
uniform float uAmbient;
uniform vec3 uLightDir;
uniform vec3 uLightColor;
uniform int uPointLightCount;
uniform PointLight uPointLights[MAX_POINT_LIGHTS];
uniform int uSpotLightCount;
uniform SpotLight uSpotLights[MAX_SPOT_LIGHTS];

out vec4 outColor;

void main() {
    vec2 screenUV = gl_FragCoord.xy / uScreenSize;

    float depth = texture(uDepthTex, screenUV).r;
    if (depth >= 1.0) discard;

    // Reconstruct World Position from Depth Buffer
    vec4 ndc = vec4(screenUV * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 worldPosH = uInvViewProj * ndc;
    vec3 worldPos = worldPosH.xyz / worldPosH.w;

    // Transform World Position to Decal Box Local Space [-0.5, 0.5]^3
    vec4 localPos = uInvDecalModel * vec4(worldPos, 1.0);
    vec3 p = localPos.xyz / localPos.w;

    // Clip fragments outside the oriented bounding box
    if (abs(p.x) > 0.5 || abs(p.y) > 0.5 || abs(p.z) > 0.5) discard;

    // Geometric surface normal from screen-space derivatives
    vec3 dX = dFdx(worldPos);
    vec3 dY = dFdy(worldPos);
    vec3 surfNormalWorld = normalize(cross(dX, dY));
    vec3 localNormal = normalize((uInvDecalModel * vec4(surfNormalWorld, 0.0)).xyz);

    vec3 absN = abs(localNormal);
    vec2 uv;

    // Corner unfolding: smoothly wrap 90-degree corners without stretching
    if (absN.z >= absN.x && absN.z >= absN.y) {
        // Front impact face: standard XY mapping
        uv = p.xy + 0.5;
    } else if (absN.x >= absN.y) {
        // Side wall: seamlessly unfold around horizontal corner
        float distX = p.x + sign(p.x) * abs(p.z);
        uv = vec2(0.5 + distX, p.y + 0.5);
    } else {
        // Top / Bottom wall: seamlessly unfold around vertical corner
        float distY = p.y + sign(p.y) * abs(p.z);
        uv = vec2(p.x + 0.5, 0.5 + distY);
    }

    // Clip texture outside [0, 1] range to avoid wrapping / edge bleeding
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) discard;

    vec4 albedo = texture(uDecalAlbedo, uv);
    if (albedo.a < 0.01) discard;

    // Forward Lighting Calculation
    vec3 totalLight = vec3(uAmbient);

    // Directional Sunlight
    if (length(uLightColor) > 0.001) {
        float NdotL = max(dot(surfNormalWorld, uLightDir), 0.0);
        totalLight += uLightColor * NdotL;
    }

    // Point Lights
    for (int i = 0; i < uPointLightCount; i++) {
        vec3 toLight = uPointLights[i].position - worldPos;
        float dist = length(toLight);
        if (dist < uPointLights[i].radius && dist > 0.0001) {
            vec3 L = toLight / dist;
            float NdotL = max(dot(surfNormalWorld, L), 0.0);
            float atten = clamp(1.0 - (dist / uPointLights[i].radius), 0.0, 1.0);
            atten *= atten; // Quadratic falloff
            totalLight += uPointLights[i].color * NdotL * atten;
        }
    }

    // Spot Lights (including Player Flashlight)
    for (int i = 0; i < uSpotLightCount; i++) {
        vec3 toLight = uSpotLights[i].position - worldPos;
        float dist = length(toLight);
        if (dist < uSpotLights[i].radius && dist > 0.0001) {
            vec3 L = toLight / dist;
            float theta = dot(L, -uSpotLights[i].direction);
            if (theta > uSpotLights[i].outerCos) {
                float epsilon = max(uSpotLights[i].innerCos - uSpotLights[i].outerCos, 0.0001);
                float spotIntensity = clamp((theta - uSpotLights[i].outerCos) / epsilon, 0.0, 1.0);
                float NdotL = max(dot(surfNormalWorld, L), 0.0);
                float atten = clamp(1.0 - (dist / uSpotLights[i].radius), 0.0, 1.0);
                atten *= atten;
                totalLight += uSpotLights[i].color * NdotL * atten * spotIntensity;
            }
        }
    }

    vec3 litColor = albedo.rgb * totalLight;
    outColor = vec4(litColor, albedo.a * uOpacity);
}








