#version 330 core
#include "lighting.glsl"

uniform sampler2D uDepthTex;
uniform sampler2D uDecalAlbedo;
uniform mat4 uInvViewProj;
uniform mat4 uInvDecalModel;
uniform vec2 uScreenSize;
uniform float uOpacity;

out vec4 outColor;

void main()
{
    vec2 screenUV = gl_FragCoord.xy / uScreenSize;
    float depth = texture(uDepthTex, screenUV).r;
    if (depth >= 1.0) discard;

    vec4 ndc = vec4(screenUV * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 worldPosH = uInvViewProj * ndc;
    vec3 worldPos = worldPosH.xyz / worldPosH.w;

    vec4 localPos = uInvDecalModel * vec4(worldPos, 1.0);
    vec3 p = localPos.xyz / localPos.w;
    if (abs(p.x) > 0.5 || abs(p.y) > 0.5 || abs(p.z) > 0.5) discard;

    vec3 dX = dFdx(worldPos);
    vec3 dY = dFdy(worldPos);
    vec3 surfaceNormal = normalize(cross(dX, dY));
    vec3 localNormal = normalize((uInvDecalModel * vec4(surfaceNormal, 0.0)).xyz);
    vec3 absNormal = abs(localNormal);

    vec2 uv;
    if (absNormal.z >= absNormal.x && absNormal.z >= absNormal.y) {
        uv = p.xy + 0.5;
    } else if (absNormal.x >= absNormal.y) {
        uv = vec2(0.5 + p.x + sign(p.x) * abs(p.z), p.y + 0.5);
    } else {
        uv = vec2(p.x + 0.5, 0.5 + p.y + sign(p.y) * abs(p.z));
    }
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) discard;

    vec4 albedo = texture(uDecalAlbedo, uv);
    if (albedo.a < 0.01) discard;

    vec3 totalLight = vec3(uDirectionalDirectionAmbient.w);
    vec3 directionalColor = uDirectionalColorCascadeBlend.rgb;
    if (length(directionalColor) > 0.001) {
        float nDotL = max(dot(surfaceNormal, normalize(uDirectionalDirectionAmbient.xyz)), 0.0);
        totalLight += directionalColor * nDotL;
    }

    for (int i = 0; i < PointLightCount(); i++) {
        PointLightData light = uPointLights[i];
        vec3 toLight = light.positionRadius.xyz - worldPos;
        float distanceToLight = length(toLight);
        float radius = light.positionRadius.w;
        if (distanceToLight < radius && distanceToLight > 0.0001) {
            vec3 direction = toLight / distanceToLight;
            float nDotL = max(dot(surfaceNormal, direction), 0.0);
            float attenuation = clamp(1.0 - distanceToLight / radius, 0.0, 1.0);
            totalLight += light.colorShadowIndex.rgb * nDotL * attenuation * attenuation;
        }
    }

    for (int i = 0; i < SpotLightCount(); i++) {
        SpotLightData light = uSpotLights[i];
        vec3 toLight = light.positionRadius.xyz - worldPos;
        float distanceToLight = length(toLight);
        float radius = light.positionRadius.w;
        if (distanceToLight < radius && distanceToLight > 0.0001) {
            vec3 direction = toLight / distanceToLight;
            float theta = dot(direction, -light.directionInnerCos.xyz);
            float epsilon = max(light.directionInnerCos.w - light.colorOuterCos.w, 0.0001);
            float spotIntensity = clamp((theta - light.colorOuterCos.w) / epsilon, 0.0, 1.0);
            float nDotL = max(dot(surfaceNormal, direction), 0.0);
            float attenuation = clamp(1.0 - distanceToLight / radius, 0.0, 1.0);
            totalLight += light.colorOuterCos.rgb * nDotL * attenuation * attenuation * spotIntensity;
        }
    }

    outColor = vec4(albedo.rgb * totalLight, albedo.a * uOpacity);
}
