using System.Numerics;
using System.Text.Json.Nodes;

namespace Fuse.Scene.Model;

public enum SkyboxMode
{
    Texture,
    Procedural
}

/// <summary>
/// Serializable settings for the map environment. Texture skyboxes keep the
/// existing path-based format; procedural skyboxes use these values for both
/// the visible atmosphere and the generated environment cubemap.
/// </summary>
public sealed class SkyboxSettings
{
    public SkyboxMode Mode { get; set; } = SkyboxMode.Texture;
    public Vector3 ZenithColor { get; set; } = new(0.08f, 0.20f, 0.55f);
    public Vector3 HorizonColor { get; set; } = new(0.65f, 0.24f, 0.10f);
    public Vector3 GroundColor { get; set; } = new(0.025f, 0.030f, 0.045f);
    public Vector3 NightZenithColor { get; set; } = new(0.002f, 0.005f, 0.020f);
    public Vector3 NightHorizonColor { get; set; } = new(0.010f, 0.014f, 0.035f);
    public Vector3 SunColor { get; set; } = Vector3.One;
    public Vector3 StarColor { get; set; } = new(0.70f, 0.82f, 1.0f);
    public float SunIntensity { get; set; } = 4.0f;
    public float SunAngularRadiusDegrees { get; set; } = 0.27f;
    public float AtmosphereStrength { get; set; } = 1.0f;
    public float RayleighStrength { get; set; } = 1.0f;
    public float MieStrength { get; set; } = 1.0f;
    public float StarIntensity { get; set; } = 2.5f;
    public float StarDensity { get; set; } = 1.0f;
    public float Exposure { get; set; } = 1.0f;

    public SkyboxSettings Clone() => new()
    {
        Mode = Mode,
        ZenithColor = ZenithColor,
        HorizonColor = HorizonColor,
        GroundColor = GroundColor,
        NightZenithColor = NightZenithColor,
        NightHorizonColor = NightHorizonColor,
        SunColor = SunColor,
        StarColor = StarColor,
        SunIntensity = SunIntensity,
        SunAngularRadiusDegrees = SunAngularRadiusDegrees,
        AtmosphereStrength = AtmosphereStrength,
        RayleighStrength = RayleighStrength,
        MieStrength = MieStrength,
        StarIntensity = StarIntensity,
        StarDensity = StarDensity,
        Exposure = Exposure
    };

    public JsonObject ToJson() => new()
    {
        ["mode"] = Mode == SkyboxMode.Procedural ? "procedural" : "texture",
        ["zenith_color"] = Vec3ToJson(ZenithColor),
        ["horizon_color"] = Vec3ToJson(HorizonColor),
        ["ground_color"] = Vec3ToJson(GroundColor),
        ["night_zenith_color"] = Vec3ToJson(NightZenithColor),
        ["night_horizon_color"] = Vec3ToJson(NightHorizonColor),
        ["sun_color"] = Vec3ToJson(SunColor),
        ["star_color"] = Vec3ToJson(StarColor),
        ["sun_intensity"] = SunIntensity,
        ["sun_angular_radius_degrees"] = SunAngularRadiusDegrees,
        ["atmosphere_strength"] = AtmosphereStrength,
        ["rayleigh_strength"] = RayleighStrength,
        ["mie_strength"] = MieStrength,
        ["star_intensity"] = StarIntensity,
        ["star_density"] = StarDensity,
        ["exposure"] = Exposure
    };

    public static SkyboxSettings FromJson(JsonObject? source)
    {
        var settings = new SkyboxSettings();
        if (source == null)
            return settings;

        string mode = source.TryGetPropertyValue("mode", out JsonNode? modeNode)
            ? ((string?)modeNode ?? "")
            : "";
        settings.Mode = mode.Equals("procedural", StringComparison.OrdinalIgnoreCase)
            ? SkyboxMode.Procedural
            : SkyboxMode.Texture;

        settings.ZenithColor = ReadVec3(source, "zenith_color", settings.ZenithColor);
        settings.HorizonColor = ReadVec3(source, "horizon_color", settings.HorizonColor);
        settings.GroundColor = ReadVec3(source, "ground_color", settings.GroundColor);
        settings.NightZenithColor = ReadVec3(source, "night_zenith_color", settings.NightZenithColor);
        settings.NightHorizonColor = ReadVec3(source, "night_horizon_color", settings.NightHorizonColor);
        settings.SunColor = ReadVec3(source, "sun_color", settings.SunColor);
        settings.StarColor = ReadVec3(source, "star_color", settings.StarColor);
        settings.SunIntensity = MathF.Max(
            0.0f,
            ReadFloat(source, "sun_intensity", settings.SunIntensity));
        settings.SunAngularRadiusDegrees = System.Math.Clamp(
            ReadFloat(source, "sun_angular_radius_degrees", settings.SunAngularRadiusDegrees),
            0.01f,
            10.0f);
        settings.AtmosphereStrength = MathF.Max(
            0.0f,
            ReadFloat(source, "atmosphere_strength", settings.AtmosphereStrength));
        settings.RayleighStrength = MathF.Max(
            0.0f,
            ReadFloat(source, "rayleigh_strength", settings.RayleighStrength));
        settings.MieStrength = MathF.Max(
            0.0f,
            ReadFloat(source, "mie_strength", settings.MieStrength));
        settings.StarIntensity = MathF.Max(
            0.0f,
            ReadFloat(source, "star_intensity", settings.StarIntensity));
        settings.StarDensity = System.Math.Clamp(
            ReadFloat(source, "star_density", settings.StarDensity),
            0.0f,
            2.0f);
        settings.Exposure = MathF.Max(
            0.001f,
            ReadFloat(source, "exposure", settings.Exposure));
        return settings;
    }

    private static JsonArray Vec3ToJson(Vector3 value) =>
        new(value.X, value.Y, value.Z);

    private static Vector3 ReadVec3(JsonObject source, string key, Vector3 fallback)
    {
        if (source.TryGetPropertyValue(key, out JsonNode? node) &&
            node is JsonArray array && array.Count >= 3)
        {
            return new Vector3(
                (float)array[0]!,
                (float)array[1]!,
                (float)array[2]!);
        }

        return fallback;
    }

    private static float ReadFloat(JsonObject source, string key, float fallback)
    {
        if (source.TryGetPropertyValue(key, out JsonNode? node) && node != null)
        {
            try
            {
                return node.GetValue<float>();
            }
            catch (InvalidOperationException)
            {
                // Keep the default for malformed optional settings.
            }
        }

        return fallback;
    }
}
