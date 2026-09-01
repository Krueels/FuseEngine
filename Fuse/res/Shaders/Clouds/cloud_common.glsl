uniform sampler3D uCloudBaseNoise;
uniform sampler3D uCloudDetailNoise;
uniform sampler2D uCloudWeatherMap;
uniform float uCloudBaseHeight;
uniform float uCloudThickness;
uniform float uCloudCoverage;
uniform float uCloudDensity;
uniform float uCloudScale;
uniform float uCloudDetailScale;
uniform float uCloudDetailStrength;
uniform vec2 uCloudWindDirection;
uniform float uCloudWindSpeed;
uniform float uCloudTime;
// The procedural textures are tileable by design.  Their world projection is
// intentionally kept independent from the editor's visual scale so changing
// Scale cannot turn the whole cloud layer into a small repeating grid.
uniform float uCloudWorldTileSize;
// The cloud layer is a local spherical shell. The sphere center follows the
// camera horizontally, which keeps the horizon stable without requiring a
// planet-sized scene in Fuse.
uniform vec3 uCloudSphereCenter;
uniform float uCloudInnerRadius;
uniform float uCloudOuterRadius;
// 0 = weather mix, 1 = stratus, 2 = stratocumulus, 3 = cumulus.
uniform int uCloudPreset;

float CloudSaturate(float value)
{
    return clamp(value, 0.0, 1.0);
}

float CloudRemap(float value, float oldMinimum, float oldMaximum, float newMinimum, float newMaximum)
{
    float normalized = (value - oldMinimum) / max(oldMaximum - oldMinimum, 0.0001);
    return mix(newMinimum, newMaximum, normalized);
}

float CloudHeightGradient(float heightFraction, vec4 gradient)
{
    return CloudSaturate(
        smoothstep(gradient.x, gradient.y, heightFraction) -
        smoothstep(gradient.z, gradient.w, heightFraction));
}

float CloudHeightProfile(float heightFraction, float cloudType)
{
    // These are the three vertical profiles used by the reference renderer.
    // Blending the profiles before evaluating the gradient preserves a broad,
    // rounded top for cumulus clouds instead of producing a flat plate.
    const vec4 stratus = vec4(0.00, 0.10, 0.20, 0.30);
    const vec4 stratocumulus = vec4(0.02, 0.20, 0.48, 0.625);
    const vec4 cumulus = vec4(0.00, 0.1625, 0.88, 0.98);

    float stratusFactor = 1.0 - CloudSaturate(cloudType * 2.0);
    float stratocumulusFactor = 1.0 - abs(cloudType - 0.5) * 2.0;
    float cumulusFactor = CloudSaturate((cloudType - 0.5) * 2.0);
    vec4 gradient = stratus * stratusFactor +
        stratocumulus * stratocumulusFactor + cumulus * cumulusFactor;
    return CloudHeightGradient(heightFraction, gradient);
}

float CloudHeightFraction(vec3 worldPosition)
{
    return (length(worldPosition - uCloudSphereCenter) - uCloudInnerRadius) /
        max(uCloudOuterRadius - uCloudInnerRadius, 0.001);
}

float CloudInnerSurfaceHeight(vec2 worldXZ)
{
    vec2 horizontalOffset = worldXZ - uCloudSphereCenter.xz;
    float horizontalDistanceSquared = dot(horizontalOffset, horizontalOffset);
    float radiusSquared = uCloudInnerRadius * uCloudInnerRadius;
    if (horizontalDistanceSquared >= radiusSquared)
        return uCloudBaseHeight;

    return uCloudSphereCenter.y + sqrt(max(radiusSquared - horizontalDistanceSquared, 0.0));
}

vec4 SampleCloudWeather(vec3 worldPosition)
{
    vec2 windOffset = uCloudWindDirection * uCloudWindSpeed * uCloudTime;
    // Weather lives on a much larger projection than the 3D shape volume.
    // Keeping both at nearly the same period made every repeated volume tile
    // receive the same coverage and exposed the texture grid.
    float weatherTileSize = max(uCloudWorldTileSize * 2.8284271, 8192.0);
    vec2 weatherUv = (worldPosition.xz + windOffset * 0.18) / weatherTileSize;
    return texture(uCloudWeatherMap, weatherUv);
}

float SampleCloudDensity(vec3 worldPosition)
{
    float heightFraction = CloudHeightFraction(worldPosition);
    if (heightFraction <= 0.0 || heightFraction >= 1.0)
        return 0.0;

    vec2 windOffset = uCloudWindDirection * uCloudWindSpeed * uCloudTime;
    // Higher cloud layers are pushed farther by the wind. Besides looking more
    // natural, this prevents the volume from forming vertically aligned stacks.
    float windShear = heightFraction * heightFraction * 70.0;
    vec2 horizontalPosition = worldPosition.xz + windOffset +
        uCloudWindDirection * windShear;

    vec4 weather = SampleCloudWeather(worldPosition);

    // World scale changes the size of features continuously. The old clamp
    // compressed most editor values into the same two visual sizes, which made
    // the 3D volume read as a repeated grid. The exponent keeps the control
    // useful over a wide range without making the smallest values unusable.
    float scaleRatio = max(uCloudScale, 0.0001) / 0.0035;
    float visualScale = pow(scaleRatio, 0.35);
    float worldTileSize = max(uCloudWorldTileSize, 16384.0);
    float shellRadius = max(uCloudInnerRadius, 1.0);
    vec2 shellProjection = horizontalPosition / shellRadius + vec2(0.5);
    vec2 basePlane = shellProjection * (shellRadius * visualScale / worldTileSize);
    vec2 weatherWarp = vec2(weather.b, weather.g) * 2.0 - 1.0;
    basePlane += weatherWarp * 0.32;
    basePlane = mat2(0.8660254, -0.5000000,
                     0.5000000,  0.8660254) * basePlane;

    // Y is deliberately parameterized by normalized cloud height. Sampling
    // world-space Y directly was the source of the horizontal layer artifacts.
    vec3 baseUv = vec3(
        basePlane.x,
        0.11 + heightFraction * 0.78 + (weather.b - 0.5) * 0.07,
        basePlane.y);

    float cloudType = CloudSaturate(weather.g * 0.86 + 0.07);
    if (uCloudPreset == 1)
        cloudType = 0.0;
    else if (uCloudPreset == 2)
        cloudType = 0.5;
    else if (uCloudPreset == 3)
        cloudType = 1.0;
    float heightProfile = CloudHeightProfile(heightFraction, cloudType);

    // R contains the broad Perlin-Worley shape. GBA contain progressively
    // smaller Worley octaves used to erode that shape without losing its body.
    vec4 lowFrequencyNoise = texture(uCloudBaseNoise, baseUv);
    float lowFrequencyFbm = dot(lowFrequencyNoise.gba, vec3(0.625, 0.250, 0.125));
    float broadShape = CloudSaturate(CloudRemap(
        lowFrequencyNoise.r,
        -(1.0 - lowFrequencyFbm),
        1.0,
        0.0,
        1.0));
    // Dividing by the normalized height keeps the cloud base from becoming a
    // continuous horizontal sheet while retaining the profile's rounded top.
    broadShape *= CloudSaturate(heightProfile / max(heightFraction, 0.08));

    float weatherCoverage = smoothstep(0.12, 0.88, weather.r);
    float localCoverage = CloudSaturate(
        uCloudCoverage * mix(0.28, 1.45, weatherCoverage) +
        (weather.b - 0.5) * 0.05);
    float coverageThreshold = 1.0 - localCoverage;
    broadShape = CloudSaturate(
        CloudRemap(broadShape, coverageThreshold, 1.0, 0.0, 1.0));
    if (broadShape <= 0.001)
        return 0.0;

    float detailFrequency = visualScale *
        max(1.0, uCloudDetailScale * 0.25) / 768.0;
    vec2 detailPlane = horizontalPosition * detailFrequency;
    detailPlane = mat2(0.777146,  0.629320,
                      -0.629320,  0.777146) * detailPlane;
    detailPlane += vec2(weather.g, weather.b) * 1.17 + vec2(11.37, -7.91);
    vec3 detailUv = vec3(
        detailPlane.x,
        heightFraction * (1.7 + uCloudDetailScale * 0.16) +
            uCloudTime * 0.003 + weather.r * 0.31,
        detailPlane.y);
    vec3 highFrequencyNoise = texture(uCloudDetailNoise, detailUv).rgb;
    float highFrequencyFbm = dot(highFrequencyNoise, vec3(0.625, 0.250, 0.125));

    // Erode mostly at the cloud boundary. Inverting the erosion toward the
    // cloud base creates wisps while preserving the dense body above it.
    float heightAwareDetail = mix(highFrequencyFbm, 1.0 - highFrequencyFbm,
        CloudSaturate(heightFraction * 10.0));
    float edgeFactor = 1.0 - smoothstep(0.18, 0.78, broadShape);
    float erosion = heightAwareDetail * (1.0 - broadShape) *
        uCloudDetailStrength * mix(0.20, 0.92, edgeFactor);
    float finalShape = CloudSaturate(CloudRemap(
        (broadShape - erosion) * 1.75,
        heightAwareDetail * 0.12,
        1.0,
        0.0,
        1.0));

    return finalShape * uCloudDensity;
}

// Converts the editor's normalized density to optical depth. Normalizing by
// layer thickness makes the same density setting behave consistently on thin
// and thick cloud layers and avoids a single ray step becoming fully opaque.
float CloudOpticalDepth(float density, float travelDistance, float absorption)
{
    float extinctionPerWorldUnit = 8.0 / max(uCloudThickness, 1.0);
    return max(density, 0.0) * max(travelDistance, 0.0) *
        max(absorption, 0.0) * extinctionPerWorldUnit;
}

bool CloudRaySphere(
    vec3 rayOrigin,
    vec3 rayDirection,
    float radius,
    out float nearDistance,
    out float farDistance)
{
    vec3 offset = rayOrigin - uCloudSphereCenter;
    float halfB = dot(offset, rayDirection);
    float c = dot(offset, offset) - radius * radius;
    float discriminant = halfB * halfB - c;
    if (discriminant < 0.0)
        return false;

    float root = sqrt(discriminant);
    nearDistance = -halfB - root;
    farDistance = -halfB + root;
    return farDistance >= 0.0;
}

bool IntersectCloudLayer(vec3 rayOrigin, vec3 rayDirection, out float nearDistance, out float farDistance)
{
    float outerNear;
    float outerFar;
    if (!CloudRaySphere(rayOrigin, rayDirection, uCloudOuterRadius, outerNear, outerFar))
        return false;

    float innerNear;
    float innerFar;
    bool intersectsInner = CloudRaySphere(
        rayOrigin, rayDirection, uCloudInnerRadius, innerNear, innerFar);
    float distanceToCenter = length(rayOrigin - uCloudSphereCenter);

    if (distanceToCenter < uCloudInnerRadius)
    {
        // The normal camera position is inside the hollow shell. We first
        // enter the cloud at the inner-sphere exit and leave at the outer exit.
        nearDistance = max(innerFar, 0.0);
        farDistance = outerFar;
    }
    else if (distanceToCenter < uCloudOuterRadius)
    {
        // Origin is already within the shell. If the ray points inward, the
        // inner sphere is the first boundary; otherwise the outer sphere is.
        nearDistance = 0.0;
        farDistance = outerFar;
        if (intersectsInner && innerNear > 0.0)
            farDistance = min(farDistance, innerNear);
    }
    else
    {
        // From outside, the interval ends when the ray reaches the inner
        // sphere, leaving only the shell between the two intersections.
        nearDistance = max(outerNear, 0.0);
        farDistance = outerFar;
        if (intersectsInner && innerNear > nearDistance)
            farDistance = min(farDistance, innerNear);
    }

    return farDistance > nearDistance;
}

float HenyeyGreenstein(float cosineTheta, float anisotropy)
{
    float g2 = anisotropy * anisotropy;
    float denominator = max(0.001, 1.0 + g2 - 2.0 * anisotropy * cosineTheta);
    return (1.0 - g2) / (12.5663706 * pow(denominator, 1.5));
}
