#version 330 core
uniform sampler2D uDepthTex;
uniform sampler2D uDecalAlbedo;
uniform mat4 uInvViewProj;
uniform mat4 uInvDecalModel;
uniform vec2 uScreenSize;
uniform float uOpacity;

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

    outColor = vec4(albedo.rgb, albedo.a * uOpacity);
}







