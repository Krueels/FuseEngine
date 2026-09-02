using System.Numerics;
using System.Text.Json.Nodes;

namespace Fuse.Scene.Model;

/// <summary>
/// Map-persistent settings for the global ocean surface and its optional water
/// interaction. The ocean surface remains a render pass; physics uses these
/// values to sample it and apply forces to existing bodies.
/// </summary>
public sealed class OceanSettings
{
    public bool Enabled { get; set; }
    public float WaterLevel { get; set; } = 0.0f;
    public float OceanSize { get; set; } = 4096.0f;
    public int GridResolution { get; set; } = 128;

    public float WaveAmplitude { get; set; } = 0.75f;
    public float WaveLength { get; set; } = 38.0f;
    public float WaveSpeed { get; set; } = 1.25f;
    public float WaveChoppiness { get; set; } = 0.65f;
    public Vector2 WaveDirection { get; set; } = Vector2.Normalize(new Vector2(1.0f, 0.25f));
    public float WindSpeed { get; set; } = 18.0f;
    public float SmallWaveLength { get; set; } = 0.75f;
    public int SpectrumSeed { get; set; } = 1337;
    public int DebugView { get; set; }

    // Physics. These values are map-persistent so a map can opt out without
    // changing the global physics world or the player controller.
    public bool PhysicsEnabled { get; set; } = true;
    public float WaterDensity { get; set; } = 1000.0f;
    public float BuoyancyStrength { get; set; } = 1.0f;
    // Legacy property/JSON name retained for map compatibility. The runtime
    // interprets it as the dimensionless quadratic drag coefficient (Cd).
    public float WaterLinearDrag { get; set; } = 1.05f;
    public float WaterAngularDrag { get; set; } = 1.0f;
    public int BuoyancyProbeCount { get; set; } = 8;
    public float PlayerGravityScale { get; set; } = 0.55f;
    public float PlayerSinkAcceleration { get; set; } = 3.5f;
    public float PlayerSwimUpAcceleration { get; set; } = 16.0f;
    public float PlayerSwimUpSpeed { get; set; } = 4.0f;
    public float PlayerWaterMoveSpeed { get; set; } = 2.5f;
    public float PlayerWaterDrag { get; set; } = 3.0f;
    public float PlayerSurfaceFloatStrength { get; set; } = 4.0f;

    public bool NormalMapEnabled { get; set; } = true;
    public float NormalMapStrength { get; set; } = 0.32f;
    public float NormalMapScale { get; set; } = 0.035f;
    public float NormalMapDistortion { get; set; } = 0.75f;

    public Vector3 ShallowColor { get; set; } = new(0.035f, 0.28f, 0.30f);
    public Vector3 DeepColor { get; set; } = new(0.005f, 0.045f, 0.075f);
    public Vector3 FoamColor { get; set; } = new(0.75f, 0.92f, 0.95f);
    public float ReflectionStrength { get; set; } = 0.78f;
    public float RefractionStrength { get; set; } = 0.035f;
    public float AbsorptionDistance { get; set; } = 42.0f;
    public float SurfaceRoughness { get; set; } = 0.16f;
    public float FoamStrength { get; set; } = 0.18f;
    public float FoamDepth { get; set; } = 1.5f;

    public bool UnderwaterEnabled { get; set; } = true;
    public Vector3 UnderwaterColor { get; set; } = new(0.015f, 0.12f, 0.16f);
    public float UnderwaterFogDensity { get; set; } = 0.035f;
    public float UnderwaterDistortion { get; set; } = 0.004f;
    public float UnderwaterDarkening { get; set; } = 0.18f;

    public OceanSettings Clone() => new()
    {
        Enabled = Enabled,
        WaterLevel = WaterLevel,
        OceanSize = OceanSize,
        GridResolution = GridResolution,
        WaveAmplitude = WaveAmplitude,
        WaveLength = WaveLength,
        WaveSpeed = WaveSpeed,
        WaveChoppiness = WaveChoppiness,
        WaveDirection = WaveDirection,
        WindSpeed = WindSpeed,
        SmallWaveLength = SmallWaveLength,
        SpectrumSeed = SpectrumSeed,
        DebugView = DebugView,
        PhysicsEnabled = PhysicsEnabled,
        WaterDensity = WaterDensity,
        BuoyancyStrength = BuoyancyStrength,
        WaterLinearDrag = WaterLinearDrag,
        WaterAngularDrag = WaterAngularDrag,
        BuoyancyProbeCount = BuoyancyProbeCount,
        PlayerGravityScale = PlayerGravityScale,
        PlayerSinkAcceleration = PlayerSinkAcceleration,
        PlayerSwimUpAcceleration = PlayerSwimUpAcceleration,
        PlayerSwimUpSpeed = PlayerSwimUpSpeed,
        PlayerWaterMoveSpeed = PlayerWaterMoveSpeed,
        PlayerWaterDrag = PlayerWaterDrag,
        PlayerSurfaceFloatStrength = PlayerSurfaceFloatStrength,
        NormalMapEnabled = NormalMapEnabled,
        NormalMapStrength = NormalMapStrength,
        NormalMapScale = NormalMapScale,
        NormalMapDistortion = NormalMapDistortion,
        ShallowColor = ShallowColor,
        DeepColor = DeepColor,
        FoamColor = FoamColor,
        ReflectionStrength = ReflectionStrength,
        RefractionStrength = RefractionStrength,
        AbsorptionDistance = AbsorptionDistance,
        SurfaceRoughness = SurfaceRoughness,
        FoamStrength = FoamStrength,
        FoamDepth = FoamDepth,
        UnderwaterEnabled = UnderwaterEnabled,
        UnderwaterColor = UnderwaterColor,
        UnderwaterFogDensity = UnderwaterFogDensity,
        UnderwaterDistortion = UnderwaterDistortion,
        UnderwaterDarkening = UnderwaterDarkening
    };

    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled,
        ["water_level"] = WaterLevel,
        ["ocean_size"] = OceanSize,
        ["grid_resolution"] = GridResolution,
        ["wave_amplitude"] = WaveAmplitude,
        ["wave_length"] = WaveLength,
        ["wave_speed"] = WaveSpeed,
        ["wave_choppiness"] = WaveChoppiness,
        ["wave_direction"] = new JsonArray(WaveDirection.X, WaveDirection.Y),
        ["wind_speed"] = WindSpeed,
        ["small_wave_length"] = SmallWaveLength,
        ["spectrum_seed"] = SpectrumSeed,
        ["debug_view"] = DebugView,
        ["physics_enabled"] = PhysicsEnabled,
        ["water_density"] = WaterDensity,
        ["buoyancy_strength"] = BuoyancyStrength,
        ["water_linear_drag"] = WaterLinearDrag,
        ["water_angular_drag"] = WaterAngularDrag,
        ["buoyancy_probe_count"] = BuoyancyProbeCount,
        ["player_gravity_scale"] = PlayerGravityScale,
        ["player_sink_acceleration"] = PlayerSinkAcceleration,
        ["player_swim_up_acceleration"] = PlayerSwimUpAcceleration,
        ["player_swim_up_speed"] = PlayerSwimUpSpeed,
        ["player_water_move_speed"] = PlayerWaterMoveSpeed,
        ["player_water_drag"] = PlayerWaterDrag,
        ["player_surface_float_strength"] = PlayerSurfaceFloatStrength,
        ["normal_map_enabled"] = NormalMapEnabled,
        ["normal_map_strength"] = NormalMapStrength,
        ["normal_map_scale"] = NormalMapScale,
        ["normal_map_distortion"] = NormalMapDistortion,
        ["shallow_color"] = Vec3ToJson(ShallowColor),
        ["deep_color"] = Vec3ToJson(DeepColor),
        ["foam_color"] = Vec3ToJson(FoamColor),
        ["reflection_strength"] = ReflectionStrength,
        ["refraction_strength"] = RefractionStrength,
        ["absorption_distance"] = AbsorptionDistance,
        ["surface_roughness"] = SurfaceRoughness,
        ["foam_strength"] = FoamStrength,
        ["foam_depth"] = FoamDepth,
        ["underwater_enabled"] = UnderwaterEnabled,
        ["underwater_color"] = Vec3ToJson(UnderwaterColor),
        ["underwater_fog_density"] = UnderwaterFogDensity,
        ["underwater_distortion"] = UnderwaterDistortion,
        ["underwater_darkening"] = UnderwaterDarkening
    };

    public static OceanSettings FromJson(JsonObject? source)
    {
        var settings = new OceanSettings();
        if (source == null)
            return settings;

        settings.Enabled = ReadBool(source, "enabled", settings.Enabled);
        settings.WaterLevel = ReadFloat(source, "water_level", settings.WaterLevel);
        settings.OceanSize = MathF.Max(64.0f, ReadFloat(source, "ocean_size", settings.OceanSize));
        settings.GridResolution = System.Math.Clamp(
            ReadInt(source, "grid_resolution", settings.GridResolution), 32, 256);
        settings.WaveAmplitude = MathF.Max(0.0f, ReadFloat(source, "wave_amplitude", settings.WaveAmplitude));
        settings.WaveLength = MathF.Max(0.5f, ReadFloat(source, "wave_length", settings.WaveLength));
        settings.WaveSpeed = System.Math.Clamp(ReadFloat(source, "wave_speed", settings.WaveSpeed), -100.0f, 100.0f);
        settings.WaveChoppiness = System.Math.Clamp(ReadFloat(source, "wave_choppiness", settings.WaveChoppiness), 0.0f, 2.0f);
        settings.WaveDirection = ReadVec2(source, "wave_direction", settings.WaveDirection);
        if (settings.WaveDirection.LengthSquared() > 1e-8f)
            settings.WaveDirection = Vector2.Normalize(settings.WaveDirection);
        else
            settings.WaveDirection = Vector2.UnitX;
        settings.WindSpeed = System.Math.Clamp(ReadFloat(source, "wind_speed", settings.WindSpeed), 0.1f, 200.0f);
        settings.SmallWaveLength = System.Math.Clamp(
            ReadFloat(source, "small_wave_length", settings.SmallWaveLength), 0.05f, 20.0f);
        settings.SpectrumSeed = ReadInt(source, "spectrum_seed", settings.SpectrumSeed);
        settings.DebugView = System.Math.Clamp(ReadInt(source, "debug_view", settings.DebugView), 0, 3);
        settings.PhysicsEnabled = ReadBool(source, "physics_enabled", settings.PhysicsEnabled);
        settings.WaterDensity = System.Math.Clamp(
            ReadFloat(source, "water_density", settings.WaterDensity), 100.0f, 3000.0f);
        settings.BuoyancyStrength = System.Math.Clamp(
            ReadFloat(source, "buoyancy_strength", settings.BuoyancyStrength), 0.0f, 4.0f);
        settings.WaterLinearDrag = System.Math.Clamp(
            ReadFloat(source, "water_linear_drag", settings.WaterLinearDrag), 0.0f, 30.0f);
        settings.WaterAngularDrag = System.Math.Clamp(
            ReadFloat(source, "water_angular_drag", settings.WaterAngularDrag), 0.0f, 30.0f);
        settings.BuoyancyProbeCount = System.Math.Clamp(
            ReadInt(source, "buoyancy_probe_count", settings.BuoyancyProbeCount), 4, 16);
        settings.PlayerGravityScale = System.Math.Clamp(
            ReadFloat(source, "player_gravity_scale", settings.PlayerGravityScale), 0.0f, 2.0f);
        settings.PlayerSinkAcceleration = System.Math.Clamp(
            ReadFloat(source, "player_sink_acceleration", settings.PlayerSinkAcceleration), 0.0f, 50.0f);
        settings.PlayerSwimUpAcceleration = System.Math.Clamp(
            ReadFloat(source, "player_swim_up_acceleration", settings.PlayerSwimUpAcceleration), 0.0f, 100.0f);
        settings.PlayerSwimUpSpeed = System.Math.Clamp(
            ReadFloat(source, "player_swim_up_speed", settings.PlayerSwimUpSpeed), 0.0f, 30.0f);
        settings.PlayerWaterMoveSpeed = System.Math.Clamp(
            ReadFloat(source, "player_water_move_speed", settings.PlayerWaterMoveSpeed), 0.0f, 20.0f);
        settings.PlayerWaterDrag = System.Math.Clamp(
            ReadFloat(source, "player_water_drag", settings.PlayerWaterDrag), 0.0f, 30.0f);
        settings.PlayerSurfaceFloatStrength = System.Math.Clamp(
            ReadFloat(source, "player_surface_float_strength", settings.PlayerSurfaceFloatStrength), 0.0f, 20.0f);
        settings.NormalMapEnabled = ReadBool(
            source, "normal_map_enabled", settings.NormalMapEnabled);
        settings.NormalMapStrength = System.Math.Clamp(
            ReadFloat(source, "normal_map_strength", settings.NormalMapStrength), 0.0f, 1.0f);
        settings.NormalMapScale = System.Math.Clamp(
            ReadFloat(source, "normal_map_scale", settings.NormalMapScale), 0.001f, 0.25f);
        settings.NormalMapDistortion = System.Math.Clamp(
            ReadFloat(source, "normal_map_distortion", settings.NormalMapDistortion), 0.0f, 2.0f);

        settings.ShallowColor = ReadVec3(source, "shallow_color", settings.ShallowColor);
        settings.DeepColor = ReadVec3(source, "deep_color", settings.DeepColor);
        settings.FoamColor = ReadVec3(source, "foam_color", settings.FoamColor);
        settings.ReflectionStrength = System.Math.Clamp(ReadFloat(source, "reflection_strength", settings.ReflectionStrength), 0.0f, 2.0f);
        settings.RefractionStrength = System.Math.Clamp(ReadFloat(source, "refraction_strength", settings.RefractionStrength), 0.0f, 0.25f);
        settings.AbsorptionDistance = MathF.Max(0.1f, ReadFloat(source, "absorption_distance", settings.AbsorptionDistance));
        settings.SurfaceRoughness = System.Math.Clamp(ReadFloat(source, "surface_roughness", settings.SurfaceRoughness), 0.02f, 1.0f);
        settings.FoamStrength = System.Math.Clamp(ReadFloat(source, "foam_strength", settings.FoamStrength), 0.0f, 2.0f);
        settings.FoamDepth = MathF.Max(0.0f, ReadFloat(source, "foam_depth", settings.FoamDepth));
        settings.UnderwaterEnabled = ReadBool(source, "underwater_enabled", settings.UnderwaterEnabled);
        settings.UnderwaterColor = ReadVec3(source, "underwater_color", settings.UnderwaterColor);
        settings.UnderwaterFogDensity = MathF.Max(0.0f, ReadFloat(source, "underwater_fog_density", settings.UnderwaterFogDensity));
        settings.UnderwaterDistortion = System.Math.Clamp(ReadFloat(source, "underwater_distortion", settings.UnderwaterDistortion), 0.0f, 0.1f);
        settings.UnderwaterDarkening = System.Math.Clamp(ReadFloat(source, "underwater_darkening", settings.UnderwaterDarkening), 0.0f, 1.0f);
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

    private static Vector3 ReadVec3(JsonObject source, string key, Vector3 fallback)
    {
        if (source.TryGetPropertyValue(key, out JsonNode? node) &&
            node is JsonArray array && array.Count >= 3)
        {
            try
            {
                return new Vector3((float)array[0]!, (float)array[1]!, (float)array[2]!);
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

    private static JsonArray Vec3ToJson(Vector3 value) =>
        new(value.X, value.Y, value.Z);
}
