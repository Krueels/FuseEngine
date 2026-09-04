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
    public float BodySurfaceMargin { get; init; } = 0.08f;
    public float BodyClearance => BodyRadius + BodyCylinderHeight * 0.5f + BodySurfaceMargin;
    public float MaximumTurnSpeedDegrees { get; init; } = 240f;
    public float SurfaceTransitionProbeWorld { get; init; } = 2.6f;
    public float MaximumSurfaceTransitionSeconds { get; init; } = 6f;
    public float StepTriggerFractionOfReach { get; init; } = 0.12f;
    public float StanceRadiusScale { get; init; } = 0.82f;
    public float StepPredictionSeconds { get; init; } = 0.12f;
    public float StepDurationSeconds { get; init; } = 0.22f;
    public float MinimumStepDurationSeconds { get; init; } = 0.12f;
    public int MaximumSwingLegs { get; init; } = 2;
    public int MinimumSupportLegs { get; init; } = 4;
    public float SupportPolygonMargin { get; init; } = 0.02f;
    public float ContactToleranceWorld { get; init; } = 0.015f;
    public float CollisionSkinWorld { get; init; } = 0.008f;
    public float LegRadiusFractionOfFoot { get; init; } = 0.55f;
    public float ContactPatchNormalAlignment { get; init; } = 0.85f;
    public float MinimumContactNormalAlignment { get; init; } = -0.15f;
    public float ProbeHeightFractionOfReach { get; init; } = 0.35f;
    public float ProbeDistanceFractionOfReach { get; init; } = 1.25f;
    public float ReplanIntervalSeconds { get; init; } = 0.08f;
    public float TeleportResetDistance { get; init; } = 8f;
    public SpiderJointLimits JointLimits { get; init; } = new();
    // Optional overrides keyed by the authored hip node, e.g. L.thigh.0.
    public IReadOnlyDictionary<string, SpiderJointLimits> LegJointLimits { get; init; } =
        new Dictionary<string, SpiderJointLimits>(StringComparer.OrdinalIgnoreCase);

    public float FootRadiusFractionOfLeg { get; init; } = 0.025f;
    public float FootSurfaceOffsetFractionOfLeg { get; init; } = 0.018f;
    public float MinimumFootRadiusWorld { get; init; } = 0.045f;
    public float MaximumFootRadiusWorld { get; init; } = 0.16f;
    public float MinimumFootOffsetWorld { get; init; } = 0.025f;
    public float MaximumFootOffsetWorld { get; init; } = 0.12f;
    public float MinimumStepLiftFractionOfReach { get; init; } = 0.055f;
    public float MaximumStepLiftFractionOfReach { get; init; } = 0.45f;
    public float MinimumStepLiftWorld { get; init; } = 0.30f;
    public float MaximumStepLiftWorld { get; init; } = 1.45f;

    public int SurfaceSwitchConfirmationFrames { get; init; } = 3;
    public float SurfaceNormalContinuity { get; init; } = 0.78f;
    public int MaximumBlockedStepFrames { get; init; } = 2;
}

public sealed class SpiderJointLimits
{
    public float HipSwingDegrees { get; init; } = 105f;
    public float HipYawDegrees { get; init; } = 80f;
    public float MinimumKneeBendDegrees { get; init; } = 5f;
    public float MaximumKneeBendDegrees { get; init; } = 165f;
    public float MaximumAnkleBendDegrees { get; init; } = 155f;
    public float KneePlaneDegrees { get; init; } = 55f;
    public float FootPitchDegrees { get; init; } = 65f;
}
