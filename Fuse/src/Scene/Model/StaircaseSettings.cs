using System.Text.Json.Nodes;

namespace Fuse.Scene.Model;

/// <summary>
/// Procedural staircase parameters stored alongside the source object's bounds.
/// A zero <see cref="StepCount"/> means that the count is derived from
/// <see cref="StepHeight"/>; a positive count requests that exact number of
/// steps.
/// </summary>
public sealed class StaircaseSettings
{
    public const int MaximumStepCount = 256;

    public float StepHeight { get; set; } = 0.25f;
    public int StepCount { get; set; }
    public int Direction { get; set; } = 1;

    public StaircaseSettings Clone() => new()
    {
        StepHeight = StepHeight,
        StepCount = StepCount,
        Direction = Direction
    };

    public static StaircaseSettings FromJson(JsonObject json)
    {
        var settings = new StaircaseSettings();

        if (json.TryGetPropertyValue("step_height", out JsonNode? stepHeightNode) &&
            stepHeightNode != null)
        {
            try { settings.StepHeight = (float)stepHeightNode; }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
        }

        if (json.TryGetPropertyValue("step_count", out JsonNode? stepCountNode) &&
            stepCountNode != null)
        {
            try { settings.StepCount = (int)stepCountNode; }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
        }

        if (json.TryGetPropertyValue("direction", out JsonNode? directionNode) &&
            directionNode != null)
        {
            try { settings.Direction = (int)directionNode; }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
        }

        settings.Sanitize();
        return settings;
    }

    public JsonObject ToJson()
    {
        Sanitize();
        return new JsonObject
        {
            ["step_height"] = StepHeight,
            ["step_count"] = StepCount,
            ["direction"] = Direction
        };
    }

    public void Sanitize()
    {
        if (!float.IsFinite(StepHeight) || StepHeight <= 0.0001f)
            StepHeight = 0.25f;

        StepCount = System.Math.Clamp(StepCount, 0, MaximumStepCount);
        Direction = Direction < 0 ? -1 : 1;
    }
}
