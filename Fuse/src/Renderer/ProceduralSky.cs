using System.Numerics;
using Fuse.Scene.Model;

namespace Fuse.Renderer;

/// <summary>
/// Shared procedural-sky helpers used by the runtime, Blowtorch and the IBL
/// cubemap generator. The sun direction convention matches the lighting
/// buffer: it points from the scene toward the directional light source.
/// </summary>
public static class ProceduralSky
{
    public static readonly Vector3 FallbackSunDirection =
        Vector3.Normalize(new Vector3(0.35f, 0.80f, 0.20f));

    public static void ResolveSun(
        IReadOnlyList<Light> lights,
        out Vector3 sunDirection,
        out Vector3 directionalLightColor)
    {
        for (int i = 0; i < lights.Count; i++)
        {
            Light light = lights[i];
            if (!light.Enabled || light.Type != LightType.Directional)
                continue;

            sunDirection = light.Direction.LengthSquared() > 1e-8f
                ? -Vector3.Normalize(light.Direction)
                : FallbackSunDirection;
            directionalLightColor = light.Color * MathF.Max(light.Intensity, 0.0f);
            return;
        }

        sunDirection = FallbackSunDirection;
        directionalLightColor = Vector3.One;
    }

    public static void ApplyShaderParameters(
        Shader shader,
        SkyboxSettings settings,
        Vector3 sunDirection,
        Vector3 directionalLightColor)
    {
        Vector3 normalizedSunDirection = sunDirection.LengthSquared() > 1e-8f
            ? Vector3.Normalize(sunDirection)
            : FallbackSunDirection;
        Vector3 lightColor = directionalLightColor.LengthSquared() > 1e-8f
            ? directionalLightColor
            : Vector3.One;

        shader.SetBool("uProceduralSky", settings.Mode == SkyboxMode.Procedural);
        shader.SetVec3("uSunDirection", normalizedSunDirection);
        shader.SetVec3("uSunColor", settings.SunColor * lightColor);
        shader.SetFloat("uSunIntensity", MathF.Max(settings.SunIntensity, 0.0f));
        shader.SetFloat("uSunAngularRadiusDegrees", System.Math.Clamp(settings.SunAngularRadiusDegrees, 0.01f, 10.0f));
        shader.SetVec3("uSkyZenithColor", settings.ZenithColor);
        shader.SetVec3("uSkyHorizonColor", settings.HorizonColor);
        shader.SetVec3("uSkyGroundColor", settings.GroundColor);
        shader.SetVec3("uNightZenithColor", settings.NightZenithColor);
        shader.SetVec3("uNightHorizonColor", settings.NightHorizonColor);
        shader.SetVec3("uStarColor", settings.StarColor);
        shader.SetFloat("uAtmosphereStrength", MathF.Max(settings.AtmosphereStrength, 0.0f));
        shader.SetFloat("uRayleighStrength", MathF.Max(settings.RayleighStrength, 0.0f));
        shader.SetFloat("uMieStrength", MathF.Max(settings.MieStrength, 0.0f));
        shader.SetFloat("uStarIntensity", MathF.Max(settings.StarIntensity, 0.0f));
        shader.SetFloat("uStarDensity", System.Math.Clamp(settings.StarDensity, 0.0f, 2.0f));
        shader.SetFloat("uSkyExposure", MathF.Max(settings.Exposure, 0.001f));
    }

    public static Vector3 EstimateAmbientColor(SkyboxSettings settings) =>
        EstimateAmbientColor(settings, FallbackSunDirection);

    public static Vector3 EstimateAmbientColor(
        SkyboxSettings settings,
        Vector3 sunDirection)
    {
        float sunHeight = sunDirection.LengthSquared() > 1e-8f
            ? Vector3.Normalize(sunDirection).Y
            : FallbackSunDirection.Y;
        float dayAmount = SmoothStep(-0.12f, 0.12f, sunHeight);
        Vector3 daySky = settings.ZenithColor * 0.55f + settings.HorizonColor * 0.45f;
        Vector3 nightSky = settings.NightZenithColor * 0.55f + settings.NightHorizonColor * 0.45f;
        Vector3 sky = Vector3.Lerp(nightSky, daySky, dayAmount);
        return Vector3.Max(sky * MathF.Max(settings.Exposure, 0.001f), Vector3.Zero);
    }

    public static ulong ComputeSettingsSignature(SkyboxSettings settings)
    {
        ulong hash = 1469598103934665603UL;
        Mix(ref hash, (uint)settings.Mode);
        Mix(ref hash, settings.ZenithColor);
        Mix(ref hash, settings.HorizonColor);
        Mix(ref hash, settings.GroundColor);
        Mix(ref hash, settings.NightZenithColor);
        Mix(ref hash, settings.NightHorizonColor);
        Mix(ref hash, settings.SunColor);
        Mix(ref hash, settings.StarColor);
        Mix(ref hash, settings.SunIntensity);
        Mix(ref hash, settings.SunAngularRadiusDegrees);
        Mix(ref hash, settings.AtmosphereStrength);
        Mix(ref hash, settings.RayleighStrength);
        Mix(ref hash, settings.MieStrength);
        Mix(ref hash, settings.StarIntensity);
        Mix(ref hash, settings.StarDensity);
        Mix(ref hash, settings.Exposure);
        return hash;
    }

    public static ulong ComputeIblSignature(
        SkyboxSettings settings,
        Vector3 sunDirection,
        Vector3 directionalLightColor)
    {
        ulong hash = ComputeSettingsSignature(settings);
        Mix(ref hash, sunDirection);
        Mix(ref hash, directionalLightColor);
        return hash;
    }

    private static void Mix(ref ulong hash, Vector3 value)
    {
        Mix(ref hash, value.X);
        Mix(ref hash, value.Y);
        Mix(ref hash, value.Z);
    }

    private static void Mix(ref ulong hash, float value) =>
        Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    private static void Mix(ref ulong hash, uint value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = System.Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
