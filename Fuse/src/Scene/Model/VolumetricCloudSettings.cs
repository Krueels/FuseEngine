using System.Numerics;
using System.Text.Json.Nodes;

namespace Fuse.Scene.Model;

/// <summary>
/// Map-persistent controls for the volumetric cloud layer. The defaults keep
/// clouds disabled so older maps retain their original appearance and cost.
/// </summary>
public sealed class VolumetricCloudSettings
{
    public bool Enabled { get; set; }
    public float BaseHeight { get; set; } = 180.0f;
    public float Thickness { get; set; } = 140.0f;
    public float Coverage { get; set; } = 0.52f;
    public float Density { get; set; } = 1.0f;
    public float Scale { get; set; } = 0.0035f;
    public float DetailScale { get; set; } = 4.0f;
    public float DetailStrength { get; set; } = 0.35f;
    public Vector2 WindDirection { get; set; } = Vector2.Normalize(new Vector2(1.0f, 0.3f));
    public float WindSpeed { get; set; } = 3.0f;
    public float MaxDistance { get; set; } = 3000.0f;
    public int PrimarySteps { get; set; } = 64;
    public int LightSteps { get; set; } = 6;
    public float ResolutionScale { get; set; } = 0.5f;
    public float TemporalBlend { get; set; } = 0.88f;
    public float Anisotropy { get; set; } = 0.55f;
    public float Absorption { get; set; } = 1.0f;
    public float AmbientStrength { get; set; } = 0.22f;
    public bool ShadowsEnabled { get; set; } = true;
    public float ShadowStrength { get; set; } = 0.55f;
    public float ShadowExtent { get; set; } = 2200.0f;
    public int ShadowResolution { get; set; } = 256;
    public float ShadowUpdateInterval { get; set; } = 0.12f;

    public VolumetricCloudSettings Clone() => new()
    {
        Enabled = Enabled,
        BaseHeight = BaseHeight,
        Thickness = Thickness,
        Coverage = Coverage,
        Density = Density,
        Scale = Scale,
        DetailScale = DetailScale,
        DetailStrength = DetailStrength,
        WindDirection = WindDirection,
        WindSpeed = WindSpeed,
        MaxDistance = MaxDistance,
        PrimarySteps = PrimarySteps,
        LightSteps = LightSteps,
        ResolutionScale = ResolutionScale,
        TemporalBlend = TemporalBlend,
        Anisotropy = Anisotropy,
        Absorption = Absorption,
        AmbientStrength = AmbientStrength,
        ShadowsEnabled = ShadowsEnabled,
        ShadowStrength = ShadowStrength,
        ShadowExtent = ShadowExtent,
        ShadowResolution = ShadowResolution,
        ShadowUpdateInterval = ShadowUpdateInterval
    };

    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled,
        ["base_height"] = BaseHeight,
        ["thickness"] = Thickness,
        ["coverage"] = Coverage,
        ["density"] = Density,
        ["scale"] = Scale,
        ["detail_scale"] = DetailScale,
        ["detail_strength"] = DetailStrength,
        ["wind_direction"] = new JsonArray(WindDirection.X, WindDirection.Y),
        ["wind_speed"] = WindSpeed,
        ["max_distance"] = MaxDistance,
        ["primary_steps"] = PrimarySteps,
        ["light_steps"] = LightSteps,
        ["resolution_scale"] = ResolutionScale,
        ["temporal_blend"] = TemporalBlend,
        ["anisotropy"] = Anisotropy,
        ["absorption"] = Absorption,
        ["ambient_strength"] = AmbientStrength,
        ["shadows_enabled"] = ShadowsEnabled,
        ["shadow_strength"] = ShadowStrength,
        ["shadow_extent"] = ShadowExtent,
        ["shadow_resolution"] = ShadowResolution,
        ["shadow_update_interval"] = ShadowUpdateInterval
    };

    public static VolumetricCloudSettings FromJson(JsonObject? source)
    {
        var settings = new VolumetricCloudSettings();
        if (source == null)
            return settings;

        settings.Enabled = ReadBool(source, "enabled", settings.Enabled);
        settings.BaseHeight = ReadFloat(source, "base_height", settings.BaseHeight);
        settings.Thickness = MathF.Max(1.0f, ReadFloat(source, "thickness", settings.Thickness));
        settings.Coverage = System.Math.Clamp(ReadFloat(source, "coverage", settings.Coverage), 0.0f, 1.0f);
        settings.Density = System.Math.Clamp(ReadFloat(source, "density", settings.Density), 0.0f, 8.0f);
        settings.Scale = System.Math.Clamp(ReadFloat(source, "scale", settings.Scale), 0.00001f, 1.0f);
        settings.DetailScale = System.Math.Clamp(ReadFloat(source, "detail_scale", settings.DetailScale), 1.0f, 16.0f);
        settings.DetailStrength = System.Math.Clamp(ReadFloat(source, "detail_strength", settings.DetailStrength), 0.0f, 1.0f);
        settings.WindDirection = ReadVec2(source, "wind_direction", settings.WindDirection);
        if (settings.WindDirection.LengthSquared() > 1e-8f)
            settings.WindDirection = Vector2.Normalize(settings.WindDirection);
        else
            settings.WindDirection = Vector2.UnitX;
        settings.WindSpeed = System.Math.Clamp(ReadFloat(source, "wind_speed", settings.WindSpeed), -100.0f, 100.0f);
        settings.MaxDistance = System.Math.Clamp(ReadFloat(source, "max_distance", settings.MaxDistance), 10.0f, 20000.0f);
        settings.PrimarySteps = System.Math.Clamp(ReadInt(source, "primary_steps", settings.PrimarySteps), 64, 128);
        settings.LightSteps = System.Math.Clamp(ReadInt(source, "light_steps", settings.LightSteps), 6, 24);
        settings.ResolutionScale = System.Math.Clamp(ReadFloat(source, "resolution_scale", settings.ResolutionScale), 0.25f, 1.0f);
        settings.TemporalBlend = System.Math.Clamp(ReadFloat(source, "temporal_blend", settings.TemporalBlend), 0.0f, 0.98f);
        settings.Anisotropy = System.Math.Clamp(ReadFloat(source, "anisotropy", settings.Anisotropy), -0.8f, 0.9f);
        settings.Absorption = System.Math.Clamp(ReadFloat(source, "absorption", settings.Absorption), 0.05f, 8.0f);
        settings.AmbientStrength = System.Math.Clamp(ReadFloat(source, "ambient_strength", settings.AmbientStrength), 0.0f, 2.0f);
        settings.ShadowsEnabled = ReadBool(source, "shadows_enabled", settings.ShadowsEnabled);
        settings.ShadowStrength = System.Math.Clamp(ReadFloat(source, "shadow_strength", settings.ShadowStrength), 0.0f, 1.0f);
        settings.ShadowExtent = System.Math.Clamp(ReadFloat(source, "shadow_extent", settings.ShadowExtent), 50.0f, 20000.0f);
        settings.ShadowResolution = System.Math.Clamp(ReadInt(source, "shadow_resolution", settings.ShadowResolution), 64, 1024);
        settings.ShadowUpdateInterval = System.Math.Clamp(ReadFloat(source, "shadow_update_interval", settings.ShadowUpdateInterval), 0.0f, 2.0f);
        return settings;
    }

    private static Vector2 ReadVec2(JsonObject source, string key, Vector2 fallback)
    {
        if (source.TryGetPropertyValue(key, out JsonNode? node) && node is JsonArray array && array.Count >= 2)
            return new Vector2((float)array[0]!, (float)array[1]!);
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
