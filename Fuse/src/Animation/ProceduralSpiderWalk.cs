using System;
using System.Numerics;
using Fuse.Core;
using Fuse.Debug;
using Fuse.Enemy;
using Fuse.Scene;
using JoltPhysicsSharp;

namespace Fuse.Animation;

/// <summary>
/// Keeps the spider's feet planted in world space and solves the thigh/leg chain
/// towards those planted positions.
/// </summary>
public sealed class ProceduralSpiderWalk : IGizmoDrawable
{
    private const float MinStepDistanceThreshold = 0.90f;
    private const float MaxStepDistanceThreshold = 2.60f;
    private const float StepSpeed = 6.0f;
    private const float ComfortableMinReachFraction = 0.30f;
    private const float Epsilon = 0.0001f;

    private Skeleton _skeleton = null!;
    private readonly SpiderSurfaceSolver _surfaceSolver;
    private readonly SpiderLocomotionProfile _profile;
    private LegState[] _legs = Array.Empty<LegState>();
    private readonly int[] _nextGaitPair = new int[2];
    private readonly int[] _activeGaitPair = { -1, -1 };
    private int _nextGaitGroup;
    private bool _initialized;
    private Matrix4x4 _lastModelMatrix = Matrix4x4.Identity;

    public Matrix4x4[]? FinalBoneMatrices { get; private set; }

    public event Action<int, Vector3, Vector3>? FootLanded;


    private struct LegState
    {
        public int Hip;
        public int Knee;
        public int Ankle;
        public int Tip;
        public int GaitGroup;
        public int GaitPair;

        public float Length1;
        public float Length2;
        public float Length3;
        public float TipReach;
        public float CalibratedLegLength;
        public float FootRadiusWorld;
        public float FootOffsetWorld;
        public float MinimumStepLiftWorld;
        public float MaximumStepLiftWorld;
        public Vector3 RestFootModel;
        public Vector3 RestOutwardModel;
        public Vector3 RestKneeDirectionModel;
        public Matrix4x4 HipRestLocal;
        public Matrix4x4 KneeRestLocal;
        public Matrix4x4 AnkleRestLocal;

        public Vector3 CurrentFootWorld;
        public Vector3 TargetFootWorld;
        public Vector3 StepStartWorld;
        public Vector3 CurrentFootNormalWorld;
        public Vector3 TargetFootNormalWorld;
        public Vector3 StepStartNormalWorld;
        public SpiderSurfaceContact PlantedContact;
        public SpiderSurfaceContact TargetContact;
        public SpiderSurfaceContact StableDesiredContact;
        public BodyID PendingSurfaceBody;
        public Vector3 PendingSurfaceNormal;
        public int PendingSurfaceFrames;
        public int BlockedStepFrames;
        public float StepUrgency;
        public bool WantsStep;
        public float StepProgress;
        public bool IsStepping;
        public bool HasPlantedFoot;

        // Debug info
        public Vector3 DebugRayStart;
        public Vector3 DebugRayEnd;
        public Vector3 DebugIdealBeforeRaycast;
        public bool DebugRaycastHit;
        public Vector3 DebugLandingPoint;
    }

    public ProceduralSpiderWalk(
        SceneManager scene,
        SpiderSurfaceSolver surfaceSolver,
        SpiderLocomotionProfile? profile = null)
    {
        _surfaceSolver = surfaceSolver;
        _profile = profile ?? SpiderLocomotionProfile.Default;
    }

    public void SetFinalBoneMatrices(Matrix4x4[] matrices) => FinalBoneMatrices = matrices;

    internal void Initialize(Skeleton skeleton, SpiderEnemy.LegData[] data)
    {
        Debug.DebugDrawer.Register(this);
        _skeleton = skeleton;
        FinalBoneMatrices = new Matrix4x4[_skeleton.Bones.Length];

        _skeleton.ComputeGlobalTransforms();

        int count = System.Math.Min(data.Length, 8);
        _legs = new LegState[count];

        for (int i = 0; i < count; i++)
        {
            _legs[i] = new LegState
            {
                Hip = -1,
                Knee = -1,
                Ankle = -1,
                Tip = -1,
                StepProgress = 1.0f
            };

            int hip = data[i].ThighNodeIndex;
            int knee = data[i].SegmentNodeIndices[0];
            int ankle = data[i].SegmentNodeIndices[1];
            int tip = data[i].SegmentNodeIndices[2] >= 0 ? data[i].SegmentNodeIndices[2] : ankle;

            if (!IsValidNode(hip) || !IsValidNode(knee) || !IsValidNode(ankle) || !IsValidNode(tip))
            {
                Logger.Warn($"[SpiderWalk] Leg {i} has an incomplete chain: hip={hip}, knee={knee}, ankle={ankle}, tip={tip}");
                continue;
            }

            Vector3 hipPosition = GetSkeletonPoint(_skeleton.Nodes[hip].Global);
            Vector3 kneePosition = GetSkeletonPoint(_skeleton.Nodes[knee].Global);
            Vector3 anklePosition = GetSkeletonPoint(_skeleton.Nodes[ankle].Global);
            Vector3 tipPosition = GetSkeletonPoint(_skeleton.Nodes[tip].Global);

            float length1 = Vector3.Distance(hipPosition, kneePosition);
            float length2 = Vector3.Distance(kneePosition, anklePosition);
            float length3 = Vector3.Distance(anklePosition, tipPosition);
            float tipReach = Vector3.Distance(kneePosition, tipPosition);
            if (length1 <= Epsilon || length2 <= Epsilon || length3 <= Epsilon || tipReach <= Epsilon)
            {
                Logger.Warn($"[SpiderWalk] Leg {i} has zero-length IK segments.");
                continue;
            }

            bool isLeft = i < 4;
            int pairIndex = i % 4;
            int gaitGroup = (pairIndex % 2 == 0) ? (isLeft ? 0 : 1) : (isLeft ? 1 : 0);
            // Each group has two diagonal pairs: (L0,R1)/(L2,R3) and
            // (L1,R0)/(L3,R2). Moving a pair at a time reads as a crawl,
            // rather than all four legs on one side jumping together.
            int gaitPair = pairIndex < 2 ? 0 : 1;

            _legs[i] = new LegState
            {
                Hip = hip,
                Knee = knee,
                Ankle = ankle,
                Tip = tip,
                GaitGroup = gaitGroup,
                GaitPair = gaitPair,
                Length1 = length1,
                Length2 = length2,
                Length3 = length3,
                TipReach = tipReach,
                CalibratedLegLength = length1 + length2 + length3,
                RestFootModel = tipPosition,
                // The pole is captured once from the authored pose. It stops
                // the solver from choosing a different (inside-out) knee side
                // when a leg reaches an uneven surface.
                RestOutwardModel = NormalizeOrFallback(
                    new Vector3(tipPosition.X, 0f, tipPosition.Z),
                    NormalizeOrFallback(tipPosition - hipPosition, Vector3.UnitX)),
                RestKneeDirectionModel = NormalizeOrFallback(kneePosition - hipPosition, Vector3.UnitY),
                HipRestLocal = _skeleton.Nodes[hip].RestLocal,
                KneeRestLocal = _skeleton.Nodes[knee].RestLocal,
                AnkleRestLocal = _skeleton.Nodes[ankle].RestLocal,
                StepProgress = 1.0f,
            };
        }

        _initialized = true;
    }

    public void Update(
        float dt,
        float speed,
        Vector3 forward,
        Vector3 bodyVelocity,
        Vector3 bodyPosition,
        Quaternion modelWorldRotation,
        Vector3 modelScale,
        Matrix4x4 modelMatrix,
        BodyID selfBody,
        SpiderSurfaceContact bodySurfaceContact)
    {
        if (!_initialized)
            return;

        _lastModelMatrix = modelMatrix;
        modelScale = SanitizeScale(modelScale);
        _surfaceSolver.BeginFrame();
        ResetLegPose();
        _skeleton.ComputeGlobalTransforms();

        Vector3 bodyUp = bodySurfaceContact.IsValid
            ? bodySurfaceContact.Normal
            : NormalizeOrFallback(Vector3.Transform(Vector3.UnitY, modelWorldRotation), Vector3.UnitY);
        Vector3 walkForward = ProjectOnPlane(bodyVelocity, bodyUp);
        if (walkForward.LengthSquared() <= Epsilon * Epsilon)
            walkForward = ProjectOnPlane(forward, bodyUp);
        if (walkForward.LengthSquared() <= Epsilon * Epsilon)
            walkForward = ProjectOnPlane(Vector3.Transform(Vector3.UnitZ, modelWorldRotation), bodyUp);
        walkForward = NormalizeOrFallback(walkForward, Vector3.UnitZ);

        bool group0IsStepping = false;
        bool group1IsStepping = false;
        foreach (var leg in _legs)
        {
            if (!leg.IsStepping)
                continue;

            if (leg.GaitGroup == 0) group0IsStepping = true;
            else group1IsStepping = true;
        }

        UpdateGaitSchedule(group0IsStepping, group1IsStepping);

        for (int i = 0; i < _legs.Length; i++)
        {
            ref LegState leg = ref _legs[i];
            if (!IsValidLeg(leg))
                continue;

            Vector3 hipWorld = ModelToWorld(GetSkeletonPoint(_skeleton.Nodes[leg.Hip].Global), bodyPosition, modelWorldRotation, modelScale);
            Vector3 desiredFootWorld = ModelToWorld(leg.RestFootModel, bodyPosition, modelWorldRotation, modelScale);
            leg.DebugIdealBeforeRaycast = desiredFootWorld;

            Vector3 outwardDirWorld = desiredFootWorld - hipWorld;
            if (outwardDirWorld.LengthSquared() > Epsilon)
                outwardDirWorld = Vector3.Normalize(outwardDirWorld);
            else
                outwardDirWorld = Vector3.Transform(i < 4 ? -Vector3.UnitX : Vector3.UnitX, modelWorldRotation);

            float reachScale = MathF.Max(modelScale.X, MathF.Max(modelScale.Y, modelScale.Z));
            leg.FootRadiusWorld = System.Math.Clamp(
                leg.CalibratedLegLength * reachScale * _profile.FootRadiusFractionOfLeg,
                _profile.MinimumFootRadiusWorld,
                _profile.MaximumFootRadiusWorld);
            leg.FootOffsetWorld = System.Math.Clamp(
                leg.CalibratedLegLength * reachScale * _profile.FootSurfaceOffsetFractionOfLeg,
                _profile.MinimumFootOffsetWorld,
                _profile.MaximumFootOffsetWorld);
            leg.FootOffsetWorld = MathF.Max(leg.FootOffsetWorld, leg.FootRadiusWorld);
            float maxReach = (leg.Length1 + leg.TipReach) * reachScale * 0.995f;
            leg.MinimumStepLiftWorld = System.Math.Clamp(
                maxReach * _profile.MinimumStepLiftFractionOfReach,
                _profile.MinimumStepLiftWorld,
                _profile.MaximumStepLiftWorld);
            leg.MaximumStepLiftWorld = System.Math.Clamp(
                maxReach * _profile.MaximumStepLiftFractionOfReach,
                leg.MinimumStepLiftWorld,
                _profile.MaximumStepLiftWorld);
            // Contact selection must accept any physically reachable support.
            // Surface selection may use a very close contact. The IK solver
            // itself still clamps to its mechanical minimum afterwards.
            float surfaceMinReach = 0.02f;

            SpiderSurfaceContact desiredSurface = FindSupportSurface(
                hipWorld,
                desiredFootWorld,
                bodyUp,
                outwardDirWorld,
                surfaceMinReach,
                maxReach,
                selfBody,
                leg.PlantedContact,
                out leg.DebugRayStart,
                out leg.DebugRayEnd);
            desiredSurface = StabilizeSurfaceCandidate(ref leg, desiredSurface);
            leg.DebugRaycastHit = desiredSurface.IsValid;
            Vector3 safeDesiredFoot = desiredSurface.IsValid
                ? GetOffsetContactPoint(desiredSurface, leg.FootOffsetWorld)
                : ClampToReachableBand(hipWorld, desiredFootWorld, surfaceMinReach, maxReach, bodyUp);

            if (!leg.HasPlantedFoot)
            {
                leg.CurrentFootWorld = safeDesiredFoot;
                leg.TargetFootWorld = safeDesiredFoot;
                leg.StepStartWorld = safeDesiredFoot;
                leg.CurrentFootNormalWorld = desiredSurface.IsValid ? desiredSurface.Normal : bodyUp;
                leg.TargetFootNormalWorld = leg.CurrentFootNormalWorld;
                leg.StepStartNormalWorld = leg.CurrentFootNormalWorld;
                leg.PlantedContact = desiredSurface;
                leg.HasPlantedFoot = true;
            }
            else
            {
                if (!leg.IsStepping)
                {
                    // A planted foot follows its support body through its local
                    // anchor. It is never snapped against world-down.
                    leg.PlantedContact = _surfaceSolver.Refresh(leg.PlantedContact);
                    if (leg.PlantedContact.IsValid)
                    {
                        leg.CurrentFootWorld = GetOffsetContactPoint(leg.PlantedContact, leg.FootOffsetWorld);
                        leg.CurrentFootNormalWorld = leg.PlantedContact.Normal;
                    }
                }

                bool oppositeGroupIsStepping = leg.GaitGroup == 0 ? group1IsStepping : group0IsStepping;
                Vector3 footError = safeDesiredFoot - leg.CurrentFootWorld;
                float forwardError = Vector3.Dot(footError, walkForward);
                Vector3 lateralError = footError - walkForward * forwardError;
                // A large model must not make tiny rapid steps. The actual
                // stride is derived from its measured chain length, clamped to
                // keep an imported small model usable as well.
                float strideThreshold = System.Math.Clamp(maxReach * 0.16f, MinStepDistanceThreshold, MaxStepDistanceThreshold);
                float lateralThreshold = MathF.Max(strideThreshold * 0.85f, 0.85f);
                float landingStride = System.Math.Clamp(maxReach * 0.23f, 1.60f, 3.80f);
                bool footIsTrailing = forwardError > strideThreshold;
                bool footIsOutOfPosition = lateralError.LengthSquared() > lateralThreshold * lateralThreshold;
                bool isMoving = speed > 0.05f;
                // A missing planted contact is recovered even while the body is
                // idle. This prevents a single failed probe from leaving a leg
                // floating until the next patrol movement begins.
                bool needsContactRecovery = !leg.PlantedContact.IsValid;
                bool emergencyReposition = isMoving &&
                                           (forwardError > strideThreshold * 2.5f ||
                                            lateralError.LengthSquared() > lateralThreshold * lateralThreshold * 2.5f);
                leg.StepUrgency = MathF.Max(0f, forwardError - strideThreshold) +
                                  MathF.Max(0f, lateralError.Length() - lateralThreshold) +
                                  (desiredSurface.IsValid ? 0f : 2f);
                leg.WantsStep = (isMoving && (footIsTrailing || footIsOutOfPosition)) || needsContactRecovery;
                bool scheduledPair = IsScheduledPair(leg);

                // The alternate gait is preserved during normal movement, but a
                // badly trailing leg is allowed to recover immediately instead
                // of remaining planted while the other group finishes its step.
                if (!leg.IsStepping && (scheduledPair || emergencyReposition) &&
                    (isMoving || emergencyReposition || needsContactRecovery) &&
                    (!oppositeGroupIsStepping || emergencyReposition) &&
                    (footIsTrailing || footIsOutOfPosition || needsContactRecovery))
                {
                    Vector3 landingPoint = safeDesiredFoot + walkForward * landingStride + bodyVelocity * 0.18f;
                    SpiderSurfaceContact landingSurface = FindSupportSurface(
                        hipWorld,
                        landingPoint,
                        bodyUp,
                        outwardDirWorld,
                        surfaceMinReach,
                        maxReach,
                        selfBody,
                        leg.PlantedContact,
                        out _,
                        out _);
                    landingSurface = StabilizeSurfaceCandidate(ref leg, landingSurface);

                    if (!landingSurface.IsValid)
                    {
                        // Missing raycast data must never freeze a leg. The
                        // fallback stays within the IK reach band and retains
                        // the latest known support normal for its lift arc.
                        landingSurface = desiredSurface.IsValid
                            ? desiredSurface
                            : new SpiderSurfaceContact(
                                true,
                                safeDesiredFoot - NormalizeOrFallback(leg.CurrentFootNormalWorld, bodyUp) * leg.FootOffsetWorld,
                                NormalizeOrFallback(leg.CurrentFootNormalWorld, bodyUp),
                                default,
                                null,
                                safeDesiredFoot - NormalizeOrFallback(leg.CurrentFootNormalWorld, bodyUp) * leg.FootOffsetWorld,
                                NormalizeOrFallback(leg.CurrentFootNormalWorld, bodyUp),
                                0f);
                    }

                    Vector3 candidateTarget = GetOffsetContactPoint(landingSurface, leg.FootOffsetWorld);
                    float candidateLiftHeight = GetStepLiftHeight(
                        leg.CurrentFootWorld,
                        candidateTarget,
                        leg.CurrentFootNormalWorld,
                        landingSurface.Normal,
                        leg.MinimumStepLiftWorld,
                        leg.MaximumStepLiftWorld);
                    bool pathIsClear = _surfaceSolver.IsFootStepPathClear(
                        leg.CurrentFootWorld,
                        candidateTarget,
                        leg.CurrentFootNormalWorld,
                        landingSurface.Normal,
                        candidateLiftHeight,
                        leg.FootRadiusWorld,
                        selfBody,
                        leg.PlantedContact,
                        landingSurface,
                        out _);

                    if (!pathIsClear &&
                        leg.BlockedStepFrames < _profile.MaximumBlockedStepFrames)
                    {
                        // A blocked path may be transient while contacts change
                        // at an edge. Delay briefly, but never let this veto the
                        // procedural gait forever.
                        leg.BlockedStepFrames++;
                        goto FinishStepScheduling;
                    }

                    if (!pathIsClear)
                    {
                        // First try a shorter stride. If even that query is
                        // ambiguous, retain the former fail-open behaviour so a
                        // bad collision sample cannot pin a leg in world space.
                        Vector3 shorterPoint = Vector3.Lerp(safeDesiredFoot, landingPoint, 0.45f);
                        SpiderSurfaceContact shorterSurface = FindSupportSurface(
                            hipWorld,
                            shorterPoint,
                            bodyUp,
                            outwardDirWorld,
                            surfaceMinReach,
                            maxReach,
                            selfBody,
                            leg.PlantedContact,
                            out _,
                            out _);
                        shorterSurface = StabilizeSurfaceCandidate(ref leg, shorterSurface);
                        if (shorterSurface.IsValid)
                        {
                            Vector3 shorterTarget = GetOffsetContactPoint(shorterSurface, leg.FootOffsetWorld);
                            float shorterLift = GetStepLiftHeight(
                                leg.CurrentFootWorld,
                                shorterTarget,
                                leg.CurrentFootNormalWorld,
                                shorterSurface.Normal,
                                leg.MinimumStepLiftWorld,
                                leg.MaximumStepLiftWorld);
                            if (_surfaceSolver.IsFootStepPathClear(
                                    leg.CurrentFootWorld,
                                    shorterTarget,
                                    leg.CurrentFootNormalWorld,
                                    shorterSurface.Normal,
                                    shorterLift,
                                    leg.FootRadiusWorld,
                                    selfBody,
                                    leg.PlantedContact,
                                    shorterSurface,
                                    out _))
                            {
                                landingSurface = shorterSurface;
                                candidateTarget = shorterTarget;
                            }
                        }
                    }

                    leg.BlockedStepFrames = 0;

                    leg.IsStepping = true;
                    leg.StepProgress = 0.0f;
                    leg.StepStartWorld = leg.CurrentFootWorld;
                    leg.StepStartNormalWorld = NormalizeOrFallback(leg.CurrentFootNormalWorld, bodyUp);
                    leg.TargetFootWorld = candidateTarget;
                    leg.TargetFootNormalWorld = landingSurface.Normal;
                    leg.TargetContact = landingSurface;
                    leg.WantsStep = false;
                    leg.DebugLandingPoint = leg.TargetFootWorld;

                    if (!emergencyReposition)
                        _activeGaitPair[leg.GaitGroup] = leg.GaitPair;

                    if (leg.GaitGroup == 0) group0IsStepping = true;
                    else group1IsStepping = true;
                }

            FinishStepScheduling:
                UpdateFootStep(ref leg, i, dt, speed);
            }

            Vector3 targetInModel = WorldToModel(leg.CurrentFootWorld, bodyPosition, modelWorldRotation, modelScale);
            SolveLegIK(ref leg, targetInModel);
        }

        if (FinalBoneMatrices != null)
            _skeleton.ComputeFinalBoneMatrices(FinalBoneMatrices);
    }

    private void UpdateGaitSchedule(bool group0IsStepping, bool group1IsStepping)
    {
        UpdateGroupSchedule(0, group0IsStepping);
        UpdateGroupSchedule(1, group1IsStepping);
    }

    private void UpdateGroupSchedule(int group, bool isStepping)
    {
        if (isStepping)
        {
            if (_activeGaitPair[group] >= 0)
                return;

            foreach (var leg in _legs)
            {
                if (leg.GaitGroup == group && leg.IsStepping)
                {
                    _activeGaitPair[group] = leg.GaitPair;
                    return;
                }
            }
            return;
        }

        if (_activeGaitPair[group] < 0)
            return;

        _nextGaitPair[group] = 1 - _activeGaitPair[group];
        _activeGaitPair[group] = -1;
        _nextGaitGroup = 1 - group;
    }

    private bool IsScheduledPair(in LegState leg)
    {
        if (leg.GaitGroup != _nextGaitGroup)
            return false;

        int activePair = _activeGaitPair[leg.GaitGroup];
        return activePair >= 0
            ? activePair == leg.GaitPair
            : _nextGaitPair[leg.GaitGroup] == leg.GaitPair;
    }

    private void UpdateFootStep(ref LegState leg, int legIndex, float dt, float movementSpeed)
    {
        if (!leg.IsStepping)
            return;

        if (leg.TargetContact.IsValid)
        {
            leg.TargetContact = _surfaceSolver.Refresh(leg.TargetContact);
            leg.TargetFootWorld = GetOffsetContactPoint(leg.TargetContact, leg.FootOffsetWorld);
            leg.TargetFootNormalWorld = leg.TargetContact.Normal;
        }

        float speedAdjustedStepRate = StepSpeed + MathF.Max(0f, movementSpeed) * 0.75f;
        leg.StepProgress = MathF.Min(leg.StepProgress + dt * speedAdjustedStepRate, 1.0f);
        Vector3 linearPosition = Vector3.Lerp(leg.StepStartWorld, leg.TargetFootWorld, leg.StepProgress);
        float arcHeight = MathF.Sin(leg.StepProgress * MathF.PI) * GetStepLiftHeight(leg);
        Vector3 liftNormal = NormalizeOrFallback(
            Vector3.Lerp(leg.StepStartNormalWorld, leg.TargetFootNormalWorld, leg.StepProgress),
            leg.TargetFootNormalWorld);
        // Lift away from the surface normal: up on floors and outward from walls.
        leg.CurrentFootWorld = linearPosition + liftNormal * arcHeight;

        if (leg.StepProgress >= 1.0f)
        {
            leg.CurrentFootWorld = leg.TargetFootWorld;
            leg.CurrentFootNormalWorld = leg.TargetFootNormalWorld;

            bool landedOnRealSurface = leg.TargetContact.IsValid && leg.TargetContact.BodyId.IsValid;

            if (landedOnRealSurface)
            {
                leg.PlantedContact = leg.TargetContact;

                FootLanded?.Invoke(legIndex, leg.CurrentFootWorld, leg.CurrentFootNormalWorld);
            }
            leg.IsStepping = false;
        }
    }

    private static float GetStepLiftHeight(in LegState leg)
    {
        return GetStepLiftHeight(
            leg.StepStartWorld,
            leg.TargetFootWorld,
            leg.StepStartNormalWorld,
            leg.TargetFootNormalWorld,
            leg.MinimumStepLiftWorld,
            leg.MaximumStepLiftWorld);
    }

    private static float GetStepLiftHeight(
        Vector3 start,
        Vector3 target,
        Vector3 startNormal,
        Vector3 targetNormal,
        float minimumHeight,
        float maximumHeight)
    {
        float strideLength = Vector3.Distance(start, target);
        float normalChange = 1f - System.Math.Clamp(Vector3.Dot(
            NormalizeOrFallback(startNormal, Vector3.UnitY),
            NormalizeOrFallback(targetNormal, Vector3.UnitY)),
            -1f,
            1f);
        return System.Math.Clamp(
            minimumHeight + strideLength * 0.20f + normalChange * 0.18f,
            minimumHeight,
            maximumHeight);
    }

    private void ResetLegPose()
    {
        foreach (LegState leg in _legs)
        {
            if (!IsValidLeg(leg))
                continue;

            _skeleton.Nodes[leg.Hip].Local = leg.HipRestLocal;
            _skeleton.Nodes[leg.Knee].Local = leg.KneeRestLocal;
            _skeleton.Nodes[leg.Ankle].Local = leg.AnkleRestLocal;
        }
    }

    /// <summary>
    /// Conservative two-bone solve to the toe. The imported model's foot and
    /// toe joints are not a straight mechanical chain, so solving all three as
    /// FABRIK made the mesh fold inside-out. The ankle stays in its authored
    /// pose while the hip/knee drive the actual toe contact.
    /// </summary>
    private void SolveLegIK(ref LegState leg, Vector3 requestedTarget)
    {
        if (TrySolveThreeSegmentIK(ref leg, requestedTarget))
            return;

        SolveTwoBoneLegIK(ref leg, requestedTarget);
    }

    private void SolveTwoBoneLegIK(ref LegState leg, Vector3 requestedTarget)
    {
        _skeleton.Nodes[leg.Hip].Local = leg.HipRestLocal;
        _skeleton.Nodes[leg.Knee].Local = leg.KneeRestLocal;
        _skeleton.Nodes[leg.Ankle].Local = leg.AnkleRestLocal;
        _skeleton.ComputeGlobalTransforms();

        Vector3 hipPosition = GetSkeletonPoint(_skeleton.Nodes[leg.Hip].Global);
        Vector3 toTarget = requestedTarget - hipPosition;
        float requestedDistance = toTarget.Length();
        if (requestedDistance <= Epsilon)
            return;

        Vector3 targetDirection = toTarget / requestedDistance;
        float minReach = MathF.Max(
            MathF.Abs(leg.Length1 - leg.TipReach) + 0.0005f,
            (leg.Length1 + leg.TipReach) * ComfortableMinReachFraction);
        float maxReach = MathF.Max(minReach, leg.Length1 + leg.TipReach - 0.0005f);
        float distance = System.Math.Clamp(requestedDistance, minReach, maxReach);
        Vector3 target = hipPosition + targetDirection * distance;

        Vector3 preferredKneeDirection = NormalizeOrFallback(
            leg.RestOutwardModel * 0.90f + Vector3.UnitY * 1.15f,
            leg.RestKneeDirectionModel);
        Vector3 pole = ProjectOnPlane(preferredKneeDirection, targetDirection);
        if (pole.LengthSquared() <= Epsilon * Epsilon)
            pole = ProjectOnPlane(leg.RestKneeDirectionModel, targetDirection);
        if (pole.LengthSquared() <= Epsilon * Epsilon)
            pole = Vector3.Cross(targetDirection, Vector3.UnitZ);
        if (pole.LengthSquared() <= Epsilon * Epsilon)
            pole = Vector3.Cross(targetDirection, Vector3.UnitX);
        pole = Vector3.Normalize(pole);

        float cosHip = System.Math.Clamp(
            (leg.Length1 * leg.Length1 + distance * distance - leg.TipReach * leg.TipReach) /
            (2.0f * leg.Length1 * distance),
            -1.0f,
            1.0f);
        float hipAngle = MathF.Acos(cosHip);
        Vector3 desiredKnee = hipPosition + targetDirection * (MathF.Cos(hipAngle) * leg.Length1) +
                              pole * (MathF.Sin(hipAngle) * leg.Length1);

        Vector3 currentKnee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);
        ApplyAimRotation(leg.Hip, leg.HipRestLocal, currentKnee - hipPosition, desiredKnee - hipPosition);
        _skeleton.ComputeGlobalTransforms();

        Vector3 solvedKnee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);
        Vector3 currentTip = GetSkeletonPoint(_skeleton.Nodes[leg.Tip].Global);
        ApplyAimRotation(leg.Knee, leg.KneeRestLocal, currentTip - solvedKnee, target - solvedKnee);
        _skeleton.ComputeGlobalTransforms();
    }

    /// <summary>
    /// Attempts a limited three-segment FABRIK solve. Imported spider rigs are
    /// not guaranteed to form a clean mechanical chain, so every result is
    /// validated and the caller falls back to the established two-bone solve.
    /// </summary>
    private bool TrySolveThreeSegmentIK(ref LegState leg, Vector3 requestedTarget)
    {
        if (!IsDescendantOf(leg.Knee, leg.Hip) ||
            !IsDescendantOf(leg.Ankle, leg.Knee) ||
            !IsDescendantOf(leg.Tip, leg.Ankle))
        {
            return false;
        }

        _skeleton.Nodes[leg.Hip].Local = leg.HipRestLocal;
        _skeleton.Nodes[leg.Knee].Local = leg.KneeRestLocal;
        _skeleton.Nodes[leg.Ankle].Local = leg.AnkleRestLocal;
        _skeleton.ComputeGlobalTransforms();

        Vector3 root = GetSkeletonPoint(_skeleton.Nodes[leg.Hip].Global);
        Vector3 knee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);
        Vector3 ankle = GetSkeletonPoint(_skeleton.Nodes[leg.Ankle].Global);
        Vector3 tip = GetSkeletonPoint(_skeleton.Nodes[leg.Tip].Global);

        Vector3 toTarget = requestedTarget - root;
        float requestedDistance = toTarget.Length();
        if (requestedDistance <= Epsilon)
            return false;

        Vector3 targetDirection = toTarget / requestedDistance;
        float totalLength = leg.Length1 + leg.Length2 + leg.Length3;
        // The mechanical lower limit prevents the chain from folding in on the
        // body, while the upper limit avoids the singular fully-straight pose.
        float minReach = totalLength * ComfortableMinReachFraction;
        float maxReach = MathF.Max(minReach, totalLength - 0.0005f);
        float distance = System.Math.Clamp(requestedDistance, minReach, maxReach);
        Vector3 target = root + targetDirection * distance;

        Vector3 preferredBend = NormalizeOrFallback(
            leg.RestOutwardModel * 0.85f + Vector3.UnitY * 1.20f,
            leg.RestKneeDirectionModel);
        Vector3 pole = ProjectOnPlane(preferredBend, targetDirection);
        if (pole.LengthSquared() <= Epsilon * Epsilon)
            pole = ProjectOnPlane(leg.RestKneeDirectionModel, targetDirection);
        pole = NormalizeOrFallback(pole, Vector3.Cross(targetDirection, Vector3.UnitX));

        Vector3 restRootToKnee = NormalizeOrFallback(knee - root, preferredBend);
        Vector3 restKneeToAnkle = NormalizeOrFallback(ankle - knee, preferredBend);
        Vector3 restAnkleToTip = NormalizeOrFallback(tip - ankle, -preferredBend);

        // A few short passes are sufficient for a three-link leg and are much
        // more stable at edges than solving each joint independently.
        for (int iteration = 0; iteration < 7; iteration++)
        {
            tip = target;
            ankle = tip + NormalizeOrFallback(ankle - tip, -restAnkleToTip) * leg.Length3;
            knee = ankle + NormalizeOrFallback(knee - ankle, -restKneeToAnkle) * leg.Length2;

            knee = PullTowardPole(root, tip, knee, pole, 0.32f);
            ankle = PullTowardPole(root, tip, ankle, pole, 0.12f);

            knee = root + NormalizeOrFallback(knee - root, restRootToKnee) * leg.Length1;
            ankle = knee + NormalizeOrFallback(ankle - knee, restKneeToAnkle) * leg.Length2;
            tip = ankle + NormalizeOrFallback(tip - ankle, restAnkleToTip) * leg.Length3;
        }

        // Finish on the requested contact, then perform one exact backward /
        // forward pass so all three segment lengths remain intact.
        tip = target;
        ankle = tip + NormalizeOrFallback(ankle - tip, -restAnkleToTip) * leg.Length3;
        knee = ankle + NormalizeOrFallback(knee - ankle, -restKneeToAnkle) * leg.Length2;
        knee = root + NormalizeOrFallback(knee - root, restRootToKnee) * leg.Length1;
        ankle = knee + NormalizeOrFallback(ankle - knee, restKneeToAnkle) * leg.Length2;
        tip = ankle + NormalizeOrFallback(tip - ankle, restAnkleToTip) * leg.Length3;

        Vector3 originalKnee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);
        ApplyAimRotationLimited(leg.Hip, leg.HipRestLocal, originalKnee - root, knee - root, 105f);
        _skeleton.ComputeGlobalTransforms();

        Vector3 solvedKnee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);
        Vector3 solvedAnkleBeforeKneeRotation = GetSkeletonPoint(_skeleton.Nodes[leg.Ankle].Global);
        ApplyAimRotationLimited(
            leg.Knee,
            leg.KneeRestLocal,
            solvedAnkleBeforeKneeRotation - solvedKnee,
            ankle - solvedKnee,
            125f);
        _skeleton.ComputeGlobalTransforms();

        Vector3 solvedAnkle = GetSkeletonPoint(_skeleton.Nodes[leg.Ankle].Global);
        Vector3 solvedTipBeforeAnkleRotation = GetSkeletonPoint(_skeleton.Nodes[leg.Tip].Global);
        ApplyAimRotationLimited(
            leg.Ankle,
            leg.AnkleRestLocal,
            solvedTipBeforeAnkleRotation - solvedAnkle,
            tip - solvedAnkle,
            80f);
        _skeleton.ComputeGlobalTransforms();

        Vector3 finalKnee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);
        Vector3 finalTip = GetSkeletonPoint(_skeleton.Nodes[leg.Tip].Global);
        float residual = Vector3.Distance(finalTip, target);
        Vector3 finalBend = ProjectOnPlane(finalKnee - root, targetDirection);
        Vector3 preferredPole = ProjectOnPlane(pole, targetDirection);
        bool bendIsValid = finalBend.LengthSquared() > Epsilon * Epsilon &&
                           preferredPole.LengthSquared() > Epsilon * Epsilon &&
                           Vector3.Dot(Vector3.Normalize(finalBend), Vector3.Normalize(preferredPole)) > -0.10f;
        bool resultIsValid = IsFinite(finalKnee) &&
                             IsFinite(finalTip) &&
                             bendIsValid &&
                             residual <= MathF.Max(0.025f, totalLength * 0.12f);
        if (resultIsValid)
            return true;

        _skeleton.Nodes[leg.Hip].Local = leg.HipRestLocal;
        _skeleton.Nodes[leg.Knee].Local = leg.KneeRestLocal;
        _skeleton.Nodes[leg.Ankle].Local = leg.AnkleRestLocal;
        _skeleton.ComputeGlobalTransforms();
        return false;
    }

    private static Vector3 PullTowardPole(
        Vector3 root,
        Vector3 end,
        Vector3 joint,
        Vector3 pole,
        float weight)
    {
        Vector3 axis = end - root;
        if (axis.LengthSquared() <= Epsilon * Epsilon)
            return joint;

        axis = Vector3.Normalize(axis);
        Vector3 jointCenter = root + axis * Vector3.Dot(joint - root, axis);
        Vector3 radial = joint - jointCenter;
        Vector3 poleRadial = pole - axis * Vector3.Dot(pole, axis);
        if (radial.LengthSquared() <= Epsilon * Epsilon || poleRadial.LengthSquared() <= Epsilon * Epsilon)
            return joint;

        Vector3 desired = jointCenter + Vector3.Normalize(poleRadial) * radial.Length();
        return Vector3.Lerp(joint, desired, weight);
    }

    private void ApplyAimRotation(int nodeIndex, Matrix4x4 restLocal, Vector3 fromModel, Vector3 toModel)
    {
        if (fromModel.LengthSquared() <= Epsilon * Epsilon || toModel.LengthSquared() <= Epsilon * Epsilon)
            return;

        int parentIndex = _skeleton.Nodes[nodeIndex].Parent;
        Matrix4x4 parentGlobal = parentIndex >= 0 ? _skeleton.Nodes[parentIndex].Global : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentGlobal, out Matrix4x4 inverseParentGlobal))
            return;

        Vector3 fromInParent = Vector3.Normalize(TransformSkeletonDirection(inverseParentGlobal, fromModel));
        Vector3 toInParent = Vector3.Normalize(TransformSkeletonDirection(inverseParentGlobal, toModel));
        Matrix4x4 deltaRotation = CreateSkeletonRotation(RotationBetween(fromInParent, toInParent));

        Matrix4x4 translation = CreateSkeletonTranslation(GetSkeletonTranslation(restLocal));
        Matrix4x4 orientationAndScale = ClearSkeletonTranslation(restLocal);
        _skeleton.Nodes[nodeIndex].Local = translation * deltaRotation * orientationAndScale;
    }

    private void ApplyAimRotationLimited(
        int nodeIndex,
        Matrix4x4 restLocal,
        Vector3 fromModel,
        Vector3 toModel,
        float maximumAngleDegrees)
    {
        if (fromModel.LengthSquared() <= Epsilon * Epsilon ||
            toModel.LengthSquared() <= Epsilon * Epsilon)
        {
            return;
        }

        int parentIndex = _skeleton.Nodes[nodeIndex].Parent;
        Matrix4x4 parentGlobal = parentIndex >= 0
            ? _skeleton.Nodes[parentIndex].Global
            : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentGlobal, out Matrix4x4 inverseParentGlobal))
            return;

        Vector3 fromInParent = Vector3.Normalize(TransformSkeletonDirection(inverseParentGlobal, fromModel));
        Vector3 toInParent = Vector3.Normalize(TransformSkeletonDirection(inverseParentGlobal, toModel));
        Quaternion rotation = Quaternion.Normalize(RotationBetween(fromInParent, toInParent));
        float angle = 2f * MathF.Acos(System.Math.Clamp(MathF.Abs(rotation.W), 0f, 1f));
        float maximumAngle = maximumAngleDegrees * (MathF.PI / 180f);
        if (angle > maximumAngle && angle > Epsilon)
            rotation = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, rotation, maximumAngle / angle));

        Matrix4x4 translation = CreateSkeletonTranslation(GetSkeletonTranslation(restLocal));
        Matrix4x4 orientationAndScale = ClearSkeletonTranslation(restLocal);
        _skeleton.Nodes[nodeIndex].Local =
            translation * CreateSkeletonRotation(rotation) * orientationAndScale;
    }

    private bool IsDescendantOf(int nodeIndex, int ancestorIndex)
    {
        int current = nodeIndex;
        int guard = _skeleton.Nodes.Length;
        while (current >= 0 && current < _skeleton.Nodes.Length && guard-- > 0)
        {
            if (current == ancestorIndex)
                return true;
            current = _skeleton.Nodes[current].Parent;
        }

        return false;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private SpiderSurfaceContact FindSupportSurface(
        Vector3 hipWorld,
        Vector3 idealFootWorld,
        Vector3 bodyUp,
        Vector3 outwardDirWorld,
        float minReach,
        float maxReach,
        BodyID selfBody,
        SpiderSurfaceContact preferredContact,
        out Vector3 rayStart,
        out Vector3 rayEnd)
    {
        rayStart = idealFootWorld;
        rayEnd = idealFootWorld;
        if (_surfaceSolver.TryFindFootContact(
                hipWorld,
                idealFootWorld,
                bodyUp,
                outwardDirWorld,
                minReach,
                maxReach,
                selfBody,
                preferredContact,
                out SpiderSurfaceContact contact))
        {
            rayEnd = contact.Point;
            return contact;
        }

        return default;
    }

    private SpiderSurfaceContact StabilizeSurfaceCandidate(
        ref LegState leg,
        SpiderSurfaceContact candidate)
    {
        if (!candidate.IsValid)
            return candidate;

        SpiderSurfaceContact previous = leg.StableDesiredContact.IsValid
            ? _surfaceSolver.Refresh(leg.StableDesiredContact)
            : leg.PlantedContact.IsValid
                ? _surfaceSolver.Refresh(leg.PlantedContact)
                : default;

        if (!previous.IsValid)
        {
            leg.StableDesiredContact = candidate;
            leg.PendingSurfaceFrames = 0;
            return candidate;
        }

        float alignment = Vector3.Dot(
            NormalizeOrFallback(previous.Normal, candidate.Normal),
            NormalizeOrFallback(candidate.Normal, previous.Normal));
        bool sameSurface = previous.BodyId == candidate.BodyId &&
                           alignment >= _profile.SurfaceNormalContinuity;

        if (sameSurface)
        {
            Vector3 filteredNormal = NormalizeOrFallback(
                Vector3.Lerp(previous.Normal, candidate.Normal, 0.35f),
                candidate.Normal);
            SpiderSurfaceContact filtered = candidate.WithWorldPose(candidate.Point, filteredNormal);
            leg.StableDesiredContact = filtered;
            leg.PendingSurfaceFrames = 0;
            return filtered;
        }

        Vector3 pendingNormal = NormalizeOrFallback(candidate.Normal, previous.Normal);
        bool confirmsPendingSurface = leg.PendingSurfaceBody == candidate.BodyId &&
                                      Vector3.Dot(
                                          NormalizeOrFallback(leg.PendingSurfaceNormal, pendingNormal),
                                          pendingNormal) >= _profile.SurfaceNormalContinuity;
        leg.PendingSurfaceFrames = confirmsPendingSurface ? leg.PendingSurfaceFrames + 1 : 1;
        leg.PendingSurfaceBody = candidate.BodyId;
        leg.PendingSurfaceNormal = pendingNormal;

        if (leg.PendingSurfaceFrames >= _profile.SurfaceSwitchConfirmationFrames)
        {
            leg.StableDesiredContact = candidate;
            leg.PendingSurfaceFrames = 0;
            return candidate;
        }

        // Keep the newly probed point so the gait can continue. Only the normal
        // is damped while a sharp surface transition is being confirmed.
        Vector3 transitionalNormal = NormalizeOrFallback(
            Vector3.Lerp(previous.Normal, candidate.Normal, 0.18f),
            candidate.Normal);
        return candidate.WithWorldPose(candidate.Point, transitionalNormal);
    }

    private static Vector3 GetOffsetContactPoint(
        in SpiderSurfaceContact contact,
        float offset)
    {
        return contact.Point + NormalizeOrFallback(contact.Normal, Vector3.UnitY) * offset;
    }

    private bool IsValidNode(int nodeIndex) => nodeIndex >= 0 && nodeIndex < _skeleton.Nodes.Length;

    private bool IsValidLeg(in LegState leg) =>
        IsValidNode(leg.Hip) && IsValidNode(leg.Knee) && IsValidNode(leg.Ankle) && IsValidNode(leg.Tip) &&
        (leg.Length1 + leg.Length2 + leg.Length3) > 0.001f;

    private static Vector3 ModelToWorld(Vector3 modelPoint, Vector3 bodyPosition, Quaternion bodyRotation, Vector3 modelScale)
    {
        return bodyPosition + Vector3.Transform(modelPoint * modelScale, bodyRotation);
    }

    private static Vector3 WorldToModel(Vector3 worldPoint, Vector3 bodyPosition, Quaternion bodyRotation, Vector3 modelScale)
    {
        Vector3 modelPoint = Vector3.Transform(worldPoint - bodyPosition, Quaternion.Inverse(bodyRotation));
        return new Vector3(modelPoint.X / modelScale.X, modelPoint.Y / modelScale.Y, modelPoint.Z / modelScale.Z);
    }

    private static Vector3 SanitizeScale(Vector3 scale) => new(
        MathF.Max(MathF.Abs(scale.X), Epsilon),
        MathF.Max(MathF.Abs(scale.Y), Epsilon),
        MathF.Max(MathF.Abs(scale.Z), Epsilon));

    private static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal) =>
        vector - planeNormal * Vector3.Dot(vector, planeNormal);

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > Epsilon * Epsilon)
            return Vector3.Normalize(value);
        return Vector3.Normalize(fallback);
    }

    private static Vector3 ClampToReachableBand(
        Vector3 hipWorld,
        Vector3 targetWorld,
        float minReach,
        float maxReach,
        Vector3 fallbackDirection)
    {
        Vector3 offset = targetWorld - hipWorld;
        float distance = offset.Length();
        Vector3 direction = distance > Epsilon
            ? offset / distance
            : NormalizeOrFallback(fallbackDirection, Vector3.UnitY);
        return hipWorld + direction * System.Math.Clamp(distance, minReach, maxReach);
    }

    private static Vector3 GetSkeletonPoint(in Matrix4x4 matrix) => new(matrix.M14, matrix.M24, matrix.M34);

    private static Vector3 GetSkeletonTranslation(in Matrix4x4 matrix) => new(matrix.M14, matrix.M24, matrix.M34);

    private static Vector3 TransformSkeletonDirection(in Matrix4x4 matrix, Vector3 direction) => new(
        matrix.M11 * direction.X + matrix.M12 * direction.Y + matrix.M13 * direction.Z,
        matrix.M21 * direction.X + matrix.M22 * direction.Y + matrix.M23 * direction.Z,
        matrix.M31 * direction.X + matrix.M32 * direction.Y + matrix.M33 * direction.Z);

    private static Matrix4x4 CreateSkeletonTranslation(Vector3 translation)
    {
        Matrix4x4 matrix = Matrix4x4.Identity;
        matrix.M14 = translation.X;
        matrix.M24 = translation.Y;
        matrix.M34 = translation.Z;
        return matrix;
    }

    private static Matrix4x4 ClearSkeletonTranslation(Matrix4x4 matrix)
    {
        matrix.M14 = 0f;
        matrix.M24 = 0f;
        matrix.M34 = 0f;
        return matrix;
    }

    private static Matrix4x4 CreateSkeletonRotation(Quaternion rotation)
    {
        return Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(rotation));
    }

    private static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        float dot = System.Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.99999f)
            return Quaternion.Identity;

        if (dot < -0.99999f)
        {
            Vector3 axis = Vector3.Cross(from, Vector3.UnitY);
            if (axis.LengthSquared() <= Epsilon * Epsilon)
                axis = Vector3.Cross(from, Vector3.UnitX);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        Vector3 rotationAxis = Vector3.Normalize(Vector3.Cross(from, to));
        return Quaternion.CreateFromAxisAngle(rotationAxis, MathF.Acos(dot));
    }

    private Vector3 ModelToWorld(Vector3 modelPoint)
    {
        return Vector3.Transform(modelPoint, _lastModelMatrix);
    }

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (!_initialized) return;

        for (int i = 0; i < _legs.Length; i++)
        {
            var leg = _legs[i];
            if (!IsValidLeg(leg)) continue;

            Vector3 hipPos = ModelToWorld(GetSkeletonPoint(_skeleton.Nodes[leg.Hip].Global));
            Vector3 kneePos = ModelToWorld(GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global));
            Vector3 anklePos = ModelToWorld(GetSkeletonPoint(_skeleton.Nodes[leg.Ankle].Global));

            Vector3 footCurrent = leg.CurrentFootWorld;
            Vector3 footTarget = leg.IsStepping ? leg.TargetFootWorld : leg.CurrentFootWorld;

            Vector3 groupColor = leg.GaitGroup == 0
                ? new Vector3(1, 0.5f, 0)
                : new Vector3(0, 0.5f, 1);

            Vector3 rayColor = leg.DebugRaycastHit ? new Vector3(0, 1, 1) : new Vector3(1, 0, 0);
            drawer.PushLine(leg.DebugRayStart, leg.DebugRayEnd, rayColor);
            drawer.DrawSphere(leg.DebugRayStart, Quaternion.Identity, 0.04f, new Vector3(1, 1, 1));

            if (leg.DebugRaycastHit)
            {
                drawer.DrawSphere(leg.DebugRayEnd, Quaternion.Identity, 0.06f, new Vector3(0, 1, 1));
            }

            drawer.DrawSphere(leg.DebugIdealBeforeRaycast, Quaternion.Identity, 0.05f, new Vector3(1, 0.3f, 0.3f));

            if (leg.DebugRaycastHit)
            {
                drawer.PushLine(leg.DebugIdealBeforeRaycast, leg.DebugRayEnd, new Vector3(1, 0.3f, 0.3f));
            }

            drawer.PushLine(hipPos, kneePos, groupColor);
            drawer.PushLine(kneePos, anklePos, groupColor);
            drawer.PushLine(anklePos, footCurrent, groupColor);

            drawer.DrawSphere(footCurrent, Quaternion.Identity, 0.05f, new Vector3(0, 1, 0));
            Vector3 contactNormal = leg.IsStepping
                ? NormalizeOrFallback(leg.TargetFootNormalWorld, Vector3.UnitY)
                : NormalizeOrFallback(leg.CurrentFootNormalWorld, Vector3.UnitY);
            Vector3 contactColor = leg.IsStepping
                ? new Vector3(1f, 0f, 1f)
                : leg.PlantedContact.IsValid ? new Vector3(0.1f, 1f, 0.25f) : new Vector3(1f, 0.1f, 0.1f);
            drawer.PushLine(footCurrent, footCurrent + contactNormal * 0.22f, contactColor);

            drawer.DrawSphere(footTarget, Quaternion.Identity, 0.08f,
                leg.IsStepping ? new Vector3(1, 0, 1) : new Vector3(0, 1, 0));

            drawer.PushLine(hipPos, footTarget, leg.IsStepping ? new Vector3(1, 0, 1) : new Vector3(1, 1, 1));

            if (leg.IsStepping)
            {
                drawer.DrawSphere(leg.DebugLandingPoint, Quaternion.Identity, 0.07f, new Vector3(1, 1, 0));
                drawer.PushLine(footTarget, leg.DebugLandingPoint, new Vector3(1, 1, 0));

                Vector3 arcStart = leg.StepStartWorld;
                Vector3 arcEnd = leg.TargetFootWorld;
                int segments = 10;
                for (int s = 0; s < segments; s++)
                {
                    float t0 = (float)s / segments;
                    float t1 = (float)(s + 1) / segments;
                    Vector3 n0 = NormalizeOrFallback(Vector3.Lerp(leg.StepStartNormalWorld, leg.TargetFootNormalWorld, t0), leg.TargetFootNormalWorld);
                    Vector3 n1 = NormalizeOrFallback(Vector3.Lerp(leg.StepStartNormalWorld, leg.TargetFootNormalWorld, t1), leg.TargetFootNormalWorld);
                    float stepHeight = GetStepLiftHeight(leg);
                    Vector3 p0 = Vector3.Lerp(arcStart, arcEnd, t0) + n0 * MathF.Sin(t0 * MathF.PI) * stepHeight;
                    Vector3 p1 = Vector3.Lerp(arcStart, arcEnd, t1) + n1 * MathF.Sin(t1 * MathF.PI) * stepHeight;
                    drawer.PushLine(p0, p1, new Vector3(1, 0, 1));
                }
            }
        }
    }
}
