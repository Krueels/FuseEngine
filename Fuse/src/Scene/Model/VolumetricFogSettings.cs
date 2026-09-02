using System.Numerics;
using System.Text.Json.Nodes;

namespace Fuse.Scene.Model;

/// <summary>
/// Map-persistent controls for the height-aware volumetric fog pass. Fog is
/// disabled by default so maps created before this feature keep their
/// original rendering cost and appearance.
/// </summary>
public sealed class VolumetricFogSettings
{
    public bool Enabled { get; set; }
    public float Density { get; set; } = 0.018f;
    public float BaseHeight { get; set; } = 0.0f;
    public float HeightFalloff { get; set; } = 180.0f;
    public float MaxDistance { get; set; } = 4000.0f;
    public float NoiseScale { get; set; } = 0.004f;
    public float NoiseStrength { get; set; } = 0.35f;
    public Vector2 WindDirection { get; set; } = Vector2.Normalize(new Vector2(1.0f, 0.25f));
    public float WindSpeed { get; set; } = 2.0f;
    public float Anisotropy { get; set; } = 0.15f;
    public float Absorption { get; set; } = 1.0f;
    public float AmbientStrength { get; set; } = 0.65f;
    public float SunScattering { get; set; } = 1.0f;
    public int RaySteps { get; set; } = 24;
    public float ResolutionScale { get; set; } = 0.5f;
    public float TemporalBlend { get; set; } = 0.88f;

    public VolumetricFogSettings Clone() => new()
    {
        Enabled = Enabled,
        Density = Density,
        BaseHeight = BaseHeight,
        HeightFalloff = HeightFalloff,
        MaxDistance = MaxDistance,
        NoiseScale = NoiseScale,
        NoiseStrength = NoiseStrength,
        WindDirection = WindDirection,
        WindSpeed = WindSpeed,
        Anisotropy = Anisotropy,
        Absorption = Absorption,
        AmbientStrength = AmbientStrength,
        SunScattering = SunScattering,
        RaySteps = RaySteps,
        ResolutionScale = ResolutionScale,
        TemporalBlend = TemporalBlend
    };

    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled,
        ["density"] = Density,
        ["base_height"] = BaseHeight,
        ["height_falloff"] = HeightFalloff,
        ["max_distance"] = MaxDistance,
        ["noise_scale"] = NoiseScale,
        ["noise_strength"] = NoiseStrength,
        ["wind_direction"] = new JsonArray(WindDirection.X, WindDirection.Y),
        ["wind_speed"] = WindSpeed,
        ["anisotropy"] = Anisotropy,
        ["absorption"] = Absorption,
        ["ambient_strength"] = AmbientStrength,
        ["sun_scattering"] = SunScattering,
        ["ray_steps"] = RaySteps,
        ["resolution_scale"] = ResolutionScale,
        ["temporal_blend"] = TemporalBlend
    };

    public static VolumetricFogSettings FromJson(JsonObject? source)
    {
        var settings = new VolumetricFogSettings();
        if (source == null)
            return settings;

        settings.Enabled = ReadBool(source, "enabled", settings.Enabled);
        settings.Density = System.Math.Clamp(ReadFloat(source, "density", settings.Density), 0.0f, 1.0f);
        settings.BaseHeight = ReadFloat(source, "base_height", settings.BaseHeight);
        settings.HeightFalloff = System.Math.Clamp(ReadFloat(source, "height_falloff", settings.HeightFalloff), 0.1f, 10000.0f);
        settings.MaxDistance = System.Math.Clamp(ReadFloat(source, "max_distance", settings.MaxDistance), 10.0f, 50000.0f);
        settings.NoiseScale = System.Math.Clamp(ReadFloat(source, "noise_scale", settings.NoiseScale), 0.00001f, 1.0f);
        settings.NoiseStrength = System.Math.Clamp(ReadFloat(source, "noise_strength", settings.NoiseStrength), 0.0f, 1.0f);
        settings.WindDirection = ReadVec2(source, "wind_direction", settings.WindDirection);
        if (settings.WindDirection.LengthSquared() > 1e-8f)
            settings.WindDirection = Vector2.Normalize(settings.WindDirection);
        else
            settings.WindDirection = Vector2.UnitX;
        settings.WindSpeed = System.Math.Clamp(ReadFloat(source, "wind_speed", settings.WindSpeed), -100.0f, 100.0f);
        settings.Anisotropy = System.Math.Clamp(ReadFloat(source, "anisotropy", settings.Anisotropy), -0.8f, 0.9f);
        settings.Absorption = System.Math.Clamp(ReadFloat(source, "absorption", settings.Absorption), 0.01f, 20.0f);
        settings.AmbientStrength = System.Math.Clamp(ReadFloat(source, "ambient_strength", settings.AmbientStrength), 0.0f, 4.0f);
        settings.SunScattering = System.Math.Clamp(ReadFloat(source, "sun_scattering", settings.SunScattering), 0.0f, 8.0f);
        settings.RaySteps = System.Math.Clamp(ReadInt(source, "ray_steps", settings.RaySteps), 8, 128);
        settings.ResolutionScale = System.Math.Clamp(ReadFloat(source, "resolution_scale", settings.ResolutionScale), 0.25f, 1.0f);
        settings.TemporalBlend = System.Math.Clamp(ReadFloat(source, "temporal_blend", settings.TemporalBlend), 0.0f, 0.98f);
        return settings;
    }

    private static Vector2 ReadVec2(JsonObject source, string key, Vector2 fallback)
    {
        if (source.TryGetPropertyValue(key, out JsonNode? node) &&
            node is JsonArray array && array.Count >= 2)
        {
            try
            {
                return new Vector2((float)array[0]!, (float)array[1]!);
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }
        return fallback;
    }

    private static float ReadFloat(JsonObject source, string key, float fallback)
    {
        try
        {
            return source.TryGetPropertyValue(key, out JsonNode? node) && node != null
                ? node.GetValue<float>()
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static int ReadInt(JsonObject source, string key, int fallback)
    {
        try
        {
            return source.TryGetPropertyValue(key, out JsonNode? node) && node != null
                ? node.GetValue<int>()
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static bool ReadBool(JsonObject source, string key, bool fallback)
    {
        try
        {
            return source.TryGetPropertyValue(key, out JsonNode? node) && node != null
                ? node.GetValue<bool>()
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }
}
