namespace Fuse.Enemy;

/// <summary>
/// Centralises the dimensions used by the spider's physical proxy and
/// procedural legs. Per-leg values are still derived from the imported rig.
/// </summary>
public sealed class SpiderLocomotionProfile
{
    public static SpiderLocomotionProfile Default { get; } = new();

    public float BodyRadius { get; init; } = 0.60f;
    public float BodyCylinderHeight { get; init; } = 0.30f;
    public float VisualScale { get; init; } = 10.0f;

    public float FootRadiusFractionOfLeg { get; init; } = 0.025f;
    public float FootSurfaceOffsetFractionOfLeg { get; init; } = 0.018f;
    public float MinimumFootRadiusWorld { get; init; } = 0.045f;
    public float MaximumFootRadiusWorld { get; init; } = 0.16f;
    public float MinimumFootOffsetWorld { get; init; } = 0.025f;
    public float MaximumFootOffsetWorld { get; init; } = 0.12f;
    public float MinimumStepLiftFractionOfReach { get; init; } = 0.055f;
    public float MaximumStepLiftFractionOfReach { get; init; } = 0.14f;
    public float MinimumStepLiftWorld { get; init; } = 0.30f;
    public float MaximumStepLiftWorld { get; init; } = 1.45f;

    public int SurfaceSwitchConfirmationFrames { get; init; } = 3;
    public float SurfaceNormalContinuity { get; init; } = 0.78f;
    public int MaximumBlockedStepFrames { get; init; } = 2;
}
