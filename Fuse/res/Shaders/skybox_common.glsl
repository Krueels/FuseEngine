const float FUSE_SKY_PI = 3.14159265359;

float SkyHash21(vec2 value)
{
    vec3 p = fract(vec3(value.x, value.y, value.x) * 0.1031);
    p += vec3(dot(p, p.yzx + vec3(33.33)));
    return fract((p.x + p.y) * p.z);
}

vec2 SkyHash22(vec2 value)
{
    return vec2(
        SkyHash21(value + vec2(17.0, 59.4)),
        SkyHash21(value + vec2(83.7, 21.5)));
}

float EvaluateProceduralStars(vec3 direction, float starDensity)
{
    float density = clamp(starDensity, 0.0, 2.0);
    float enabled = smoothstep(0.0, 0.08, density);
    vec2 sphericalUv = vec2(
        atan(direction.z, direction.x) * (0.5 / FUSE_SKY_PI) + 0.5,
        asin(clamp(direction.y, -1.0, 1.0)) / FUSE_SKY_PI + 0.5);
    vec2 grid = vec2(180.0, 90.0) * max(density, 0.05);
    vec2 cell = floor(sphericalUv * grid);
    vec2 local = fract(sphericalUv * grid);
    vec2 jitter = SkyHash22(cell) - vec2(0.5);
    vec2 starCenter = vec2(0.5) + jitter * 0.72;
    float distanceToStar = length(local - starCenter);
    float starSize = mix(
        0.025,
        0.090,
        SkyHash21(cell + vec2(8.3, 17.1)));
    float starShape = 1.0 - smoothstep(
        starSize * 0.15,
        starSize,
        distanceToStar);
    float starCandidate = step(0.9915, SkyHash21(cell));
    float brightness = mix(
        0.55,
        1.25,
        SkyHash21(cell + vec2(47.2, 91.6)));
    return starCandidate * starShape * brightness * enabled;
}

vec3 EvaluateProceduralSky(
    vec3 direction,
    vec3 sunDirection,
    vec3 sunColor,
    float sunIntensity,
    float sunAngularRadiusDegrees,
    vec3 zenithColor,
    vec3 horizonColor,
    vec3 groundColor,
    vec3 nightZenithColor,
    vec3 nightHorizonColor,
    float atmosphereStrength,
    float rayleighStrength,
    float mieStrength,
    vec3 starColor,
    float starIntensity,
    float starDensity)
{
    vec3 viewDirection = normalize(direction);
    vec3 lightDirection = normalize(sunDirection);

    float height = clamp(viewDirection.y, -1.0, 1.0);
    float aboveHorizon = smoothstep(0.0, 0.18, height);
    float skyGradient = pow(clamp(height, 0.0, 1.0), 0.42);
    vec3 skyColor = mix(horizonColor, zenithColor, skyGradient);

    float belowHorizon = 1.0 - smoothstep(-0.30, 0.0, height);
    vec3 color = mix(skyColor, groundColor, belowHorizon);

    float sunHeight = clamp(lightDirection.y, -1.0, 1.0);
    float dayAmount = smoothstep(-0.12, 0.12, sunHeight);
    float horizonBand = pow(max(1.0 - abs(height), 0.0), 2.4);
    float horizonWarmth = 1.0 - smoothstep(-0.10, 0.35, sunHeight);
    vec3 warmHorizon = mix(horizonColor, sunColor, 0.18 * horizonWarmth);
    color = mix(color, warmHorizon, horizonBand * atmosphereStrength * dayAmount * 0.55);

    float sunAlignment = max(dot(viewDirection, lightDirection), 0.0);
    float rayleighScatter = pow(max(1.0 - abs(dot(viewDirection, vec3(0.0, 1.0, 0.0))), 0.0), 1.6);
    float mieScatter = pow(sunAlignment, mix(8.0, 32.0, clamp(mieStrength * 0.25, 0.0, 1.0)));
    vec3 scatteringTint = mix(vec3(0.20, 0.36, 1.0), sunColor, 0.35 * horizonWarmth);
    color += scatteringTint * rayleighScatter * rayleighStrength * atmosphereStrength * dayAmount * 0.12;
    color += sunColor * mieScatter * mieStrength * atmosphereStrength * dayAmount * 0.45;

    float angularRadius = radians(max(sunAngularRadiusDegrees, 0.01));
    float angularDistance = acos(clamp(sunAlignment, -1.0, 1.0));
    float sunDisk = 1.0 - smoothstep(angularRadius * 0.72, angularRadius, angularDistance);
    float sunHalo = pow(sunAlignment, 256.0) * 0.35 + pow(sunAlignment, 32.0) * 0.10;
    color += sunColor * sunIntensity * (sunDisk + sunHalo) * dayAmount;

    float nightAmount = 1.0 - dayAmount;
    float nightGradient = pow(clamp(height, 0.0, 1.0), 0.42);
    vec3 nightColor = mix(nightHorizonColor, nightZenithColor, nightGradient);
    nightColor = mix(nightColor, groundColor * 0.04, belowHorizon);
    color = mix(nightColor, color, dayAmount);

    float stars = EvaluateProceduralStars(viewDirection, starDensity);
    color += starColor * starIntensity * stars * nightAmount * aboveHorizon;
    return max(color, vec3(0.0));
}
