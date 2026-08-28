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
    private const float StepHeight = 0.55f;
    private const float StepDistanceThreshold = 0.58f;
    private const float LateralStepThreshold = 0.75f;
    private const float StepForwardPlacement = 1.90f;
    private const float StepSpeed = 6.0f;
    private const float RaycastDistance = 10.0f;
    private const float SurfaceProbeOffset = 1.35f;
    private const float ComfortableMinReachFraction = 0.30f;
    private const float FootPlantSnapHeight = 1.20f;
    private const float FootPlantSnapDistance = 2.50f;
    private const float Epsilon = 0.0001f;

    private Skeleton _skeleton = null!;
    private readonly SceneManager _sceneManager;
    private LegState[] _legs = Array.Empty<LegState>();
    private readonly int[] _nextGaitPair = new int[2];
    private readonly int[] _activeGaitPair = { -1, -1 };
    private int _nextGaitGroup;
    private bool _initialized;
    private Matrix4x4 _lastModelMatrix = Matrix4x4.Identity;

    public Matrix4x4[]? FinalBoneMatrices { get; private set; }

    private struct LegState
    {
        public int Hip;
        public int Knee;
        public int Tip;
        public int GaitGroup;
        public int GaitPair;

        public float Length1;
        public float Length2;
        public Vector3 RestFootModel;
        public Vector3 RestOutwardModel;
        public Vector3 RestKneeDirectionModel;
        public Matrix4x4 HipRestLocal;
        public Matrix4x4 KneeRestLocal;

        public Vector3 CurrentFootWorld;
        public Vector3 TargetFootWorld;
        public Vector3 StepStartWorld;
        public Vector3 CurrentFootNormalWorld;
        public Vector3 TargetFootNormalWorld;
        public Vector3 StepStartNormalWorld;
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

    private readonly struct SurfacePoint
    {
        public SurfacePoint(Vector3 position, Vector3 normal, bool isValid)
        {
            Position = position;
            Normal = normal;
            IsValid = isValid;
        }

        public Vector3 Position { get; }
        public Vector3 Normal { get; }
        public bool IsValid { get; }
    }

    public ProceduralSpiderWalk(SceneManager scene) => _sceneManager = scene;

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
                Tip = -1,
                StepProgress = 1.0f
            };

            int hip = data[i].ThighNodeIndex;
            int knee = data[i].SegmentNodeIndices[0];
            int ankle = data[i].SegmentNodeIndices[1];
            int tip = data[i].SegmentNodeIndices[2] >= 0 ? data[i].SegmentNodeIndices[2] : ankle;

            if (!IsValidNode(hip) || !IsValidNode(knee) || !IsValidNode(tip))
            {
                Logger.Warn($"[SpiderWalk] Leg {i} has an incomplete chain: hip={hip}, knee={knee}, tip={tip}");
                continue;
            }

            Vector3 hipPosition = GetSkeletonPoint(_skeleton.Nodes[hip].Global);
            Vector3 kneePosition = GetSkeletonPoint(_skeleton.Nodes[knee].Global);
            Vector3 tipPosition = GetSkeletonPoint(_skeleton.Nodes[tip].Global);

            float length1 = Vector3.Distance(hipPosition, kneePosition);
            float length2 = Vector3.Distance(kneePosition, tipPosition);
            if (length1 <= Epsilon || length2 <= Epsilon)
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
                Tip = tip,
                GaitGroup = gaitGroup,
                GaitPair = gaitPair,
                Length1 = length1,
                Length2 = length2,
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
                StepProgress = 1.0f,
            };
        }

        _initialized = true;
    }

    public void Update(
        float dt,
        float speed,
        Vector3 forward,
        Vector3 bodyPosition,
        Quaternion modelWorldRotation,
        Vector3 modelScale,
        Matrix4x4 modelMatrix,
        BodyID selfBody)
    {
        if (!_initialized)
            return;

        _lastModelMatrix = modelMatrix;
        modelScale = SanitizeScale(modelScale);
        _skeleton.ComputeGlobalTransforms();

        Vector3 bodyUp = NormalizeOrFallback(Vector3.Transform(Vector3.UnitY, modelWorldRotation), Vector3.UnitY);
        Vector3 walkForward = ProjectOnPlane(forward, bodyUp);
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
            float maxReach = (leg.Length1 + leg.Length2) * reachScale * 0.995f;
            // Contact selection must accept any physically reachable support.
            // Surface selection may use a very close contact. The IK solver
            // itself still clamps to its mechanical minimum afterwards.
            float surfaceMinReach = 0.02f;

            SurfacePoint desiredSurface = FindSupportSurface(
                hipWorld,
                desiredFootWorld,
                bodyUp,
                outwardDirWorld,
                surfaceMinReach,
                maxReach,
                selfBody,
                out leg.DebugRayStart,
                out leg.DebugRayEnd);
            leg.DebugRaycastHit = desiredSurface.IsValid;
            Vector3 safeDesiredFoot = desiredSurface.IsValid
                ? desiredSurface.Position
                : ClampToReachableBand(hipWorld, desiredFootWorld, surfaceMinReach, maxReach, bodyUp);

            if (!leg.HasPlantedFoot)
            {
                leg.CurrentFootWorld = safeDesiredFoot;
                leg.TargetFootWorld = safeDesiredFoot;
                leg.StepStartWorld = safeDesiredFoot;
                leg.CurrentFootNormalWorld = desiredSurface.IsValid ? desiredSurface.Normal : bodyUp;
                leg.TargetFootNormalWorld = leg.CurrentFootNormalWorld;
                leg.StepStartNormalWorld = leg.CurrentFootNormalWorld;
                leg.HasPlantedFoot = true;
            }
            else
            {
                if (!leg.IsStepping)
                    SnapPlantedFootToGround(ref leg, selfBody);

                bool oppositeGroupIsStepping = leg.GaitGroup == 0 ? group1IsStepping : group0IsStepping;
                Vector3 footError = safeDesiredFoot - leg.CurrentFootWorld;
                float forwardError = Vector3.Dot(footError, walkForward);
                Vector3 lateralError = footError - walkForward * forwardError;
                bool footIsTrailing = forwardError > StepDistanceThreshold;
                bool footIsOutOfPosition = lateralError.LengthSquared() > LateralStepThreshold * LateralStepThreshold;
                bool isMoving = speed > 0.05f;
                bool emergencyReposition = isMoving &&
                                           (forwardError > StepDistanceThreshold * 4f ||
                                            lateralError.LengthSquared() > LateralStepThreshold * LateralStepThreshold * 4f);
                bool scheduledPair = IsScheduledPair(leg);

                // The alternate gait is preserved during normal movement, but a
                // badly trailing leg is allowed to recover immediately instead
                // of remaining planted while the other group finishes its step.
                if (!leg.IsStepping && (scheduledPair || emergencyReposition) &&
                    (isMoving || emergencyReposition) &&
                    (!oppositeGroupIsStepping || emergencyReposition) &&
                    (footIsTrailing || footIsOutOfPosition))
                {
                    Vector3 landingPoint = safeDesiredFoot + walkForward * (StepForwardPlacement + speed * 0.25f);
                    SurfacePoint landingSurface = FindSupportSurface(
                        hipWorld,
                        landingPoint,
                        bodyUp,
                        outwardDirWorld,
                        surfaceMinReach,
                        maxReach,
                        selfBody,
                        out _,
                        out _);

                    if (!landingSurface.IsValid)
                    {
                        // Missing raycast data must never freeze a leg. The
                        // fallback stays within the IK reach band and retains
                        // the latest known support normal for its lift arc.
                        landingSurface = desiredSurface.IsValid
                            ? desiredSurface
                            : new SurfacePoint(
                                safeDesiredFoot,
                                NormalizeOrFallback(leg.CurrentFootNormalWorld, bodyUp),
                                true);
                    }

                    leg.IsStepping = true;
                    leg.StepProgress = 0.0f;
                    leg.StepStartWorld = leg.CurrentFootWorld;
                    leg.StepStartNormalWorld = NormalizeOrFallback(leg.CurrentFootNormalWorld, bodyUp);
                    leg.TargetFootWorld = landingSurface.Position;
                    leg.TargetFootNormalWorld = landingSurface.Normal;
                    leg.DebugLandingPoint = leg.TargetFootWorld;

                    if (!emergencyReposition)
                        _activeGaitPair[leg.GaitGroup] = leg.GaitPair;

                    if (leg.GaitGroup == 0) group0IsStepping = true;
                    else group1IsStepping = true;
                }

                UpdateFootStep(ref leg, dt, speed);
            }

            Vector3 targetInModel = WorldToModel(leg.CurrentFootWorld, bodyPosition, modelWorldRotation, modelScale);
            SolveTwoBoneIK(ref leg, targetInModel);
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

    private void UpdateFootStep(ref LegState leg, float dt, float movementSpeed)
    {
        if (!leg.IsStepping)
            return;

        float speedAdjustedStepRate = StepSpeed + MathF.Max(0f, movementSpeed) * 0.75f;
        leg.StepProgress = MathF.Min(leg.StepProgress + dt * speedAdjustedStepRate, 1.0f);
        Vector3 linearPosition = Vector3.Lerp(leg.StepStartWorld, leg.TargetFootWorld, leg.StepProgress);
        float arcHeight = MathF.Sin(leg.StepProgress * MathF.PI) * StepHeight;
        Vector3 liftNormal = NormalizeOrFallback(
            Vector3.Lerp(leg.StepStartNormalWorld, leg.TargetFootNormalWorld, leg.StepProgress),
            leg.TargetFootNormalWorld);
        // Lift away from the surface normal: up on floors and outward from walls.
        leg.CurrentFootWorld = linearPosition + liftNormal * arcHeight;

        if (leg.StepProgress >= 1.0f)
        {
            leg.CurrentFootWorld = leg.TargetFootWorld;
            leg.CurrentFootNormalWorld = leg.TargetFootNormalWorld;
            leg.IsStepping = false;
        }
    }

    private void SnapPlantedFootToGround(ref LegState leg, BodyID selfBody)
    {
        Vector3 probeStart = leg.CurrentFootWorld + Vector3.UnitY * FootPlantSnapHeight;
        if (!_sceneManager.Raycast(
                probeStart,
                -Vector3.UnitY,
                FootPlantSnapDistance,
                out var hit,
                selfBody) || hit.Normal.Y < 0.20f)
            return;

        // This only corrects nearby horizontal/sloped supports. A foot already
        // attached to a wall will not find a close floor and remains untouched.
        leg.CurrentFootWorld = hit.Position;
        leg.CurrentFootNormalWorld = NormalizeOrFallback(hit.Normal, Vector3.UnitY);
    }

    private void SolveTwoBoneIK(ref LegState leg, Vector3 requestedTarget)
    {
        _skeleton.Nodes[leg.Hip].Local = leg.HipRestLocal;
        _skeleton.Nodes[leg.Knee].Local = leg.KneeRestLocal;
        _skeleton.ComputeGlobalTransforms();

        Vector3 hipPosition = GetSkeletonPoint(_skeleton.Nodes[leg.Hip].Global);
        Vector3 toTarget = requestedTarget - hipPosition;
        float requestedDistance = toTarget.Length();
        if (requestedDistance <= Epsilon) return;

        Vector3 targetDirection = toTarget / requestedDistance;
        // A fully folded two-bone chain is technically valid IK, but visually it
        // makes a spider's knee snap back into its abdomen. Keep a comfortable
        // minimum extension and use a stable, authored outward/upward pole.
        float minReach = MathF.Max(
            MathF.Abs(leg.Length1 - leg.Length2) + 0.0005f,
            (leg.Length1 + leg.Length2) * ComfortableMinReachFraction);
        float maxReach = MathF.Max(minReach, leg.Length1 + leg.Length2 - 0.0005f);
        float distance = System.Math.Clamp(requestedDistance, minReach, maxReach);
        Vector3 target = hipPosition + targetDirection * distance;

        Vector3 currentKnee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);
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
            (leg.Length1 * leg.Length1 + distance * distance - leg.Length2 * leg.Length2) /
            (2.0f * leg.Length1 * distance),
            -1.0f,
            1.0f);
        float hipAngle = MathF.Acos(cosHip);
        Vector3 desiredKnee = hipPosition + targetDirection * (MathF.Cos(hipAngle) * leg.Length1) +
                              pole * (MathF.Sin(hipAngle) * leg.Length1);

        ApplyAimRotation(leg.Hip, leg.HipRestLocal, currentKnee - hipPosition, desiredKnee - hipPosition);
        _skeleton.ComputeGlobalTransforms();

        Vector3 solvedKnee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);
        Vector3 currentTip = GetSkeletonPoint(_skeleton.Nodes[leg.Tip].Global);
        ApplyAimRotation(leg.Knee, leg.KneeRestLocal, currentTip - solvedKnee, target - solvedKnee);
        _skeleton.ComputeGlobalTransforms();
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

    private SurfacePoint FindSupportSurface(
        Vector3 hipWorld,
        Vector3 idealFootWorld,
        Vector3 bodyUp,
        Vector3 outwardDirWorld,
        float minReach,
        float maxReach,
        BodyID selfBody,
        out Vector3 rayStart,
        out Vector3 rayEnd)
    {
        bodyUp = NormalizeOrFallback(bodyUp, Vector3.UnitY);
        outwardDirWorld = NormalizeOrFallback(outwardDirWorld, Vector3.UnitX);

        SurfacePoint best = new(Vector3.Zero, bodyUp, false);
        float bestScore = float.MaxValue;
        rayStart = idealFootWorld;
        rayEnd = idealFootWorld;

        // On normal locomotion, a floor/slope under the desired foot always
        // wins. The visual body can lean toward a wall, but that must not pull
        // ground legs into the wall or leave them hovering.
        TrySelectSurfaceRay(
            idealFootWorld + Vector3.UnitY * SurfaceProbeOffset,
            -Vector3.UnitY,
            RaycastDistance,
            hipWorld,
            idealFootWorld,
            bodyUp,
            minReach,
            maxReach,
            selfBody,
            ref best,
            ref bestScore,
            ref rayStart,
            ref rayEnd);

        if (best.IsValid)
            return best;

        // If there is no ground inside the leg's reach, look along the current
        // surface normal and then sideways. These are the wall/corner fallbacks.
        TrySelectSurfaceRay(
            idealFootWorld + bodyUp * SurfaceProbeOffset,
            -bodyUp,
            RaycastDistance,
            hipWorld,
            idealFootWorld,
            bodyUp,
            minReach,
            maxReach,
            selfBody,
            ref best,
            ref bestScore,
            ref rayStart,
            ref rayEnd);

        TrySelectSurfaceRay(
            idealFootWorld - outwardDirWorld * SurfaceProbeOffset,
            outwardDirWorld,
            SurfaceProbeOffset * 2f + 0.5f,
            hipWorld,
            idealFootWorld,
            bodyUp,
            minReach,
            maxReach,
            selfBody,
            ref best,
            ref bestScore,
            ref rayStart,
            ref rayEnd);

        Vector3 toIdeal = idealFootWorld - hipWorld;
        if (toIdeal.LengthSquared() > Epsilon * Epsilon)
        {
            TrySelectSurfaceRay(
                hipWorld + bodyUp * 0.08f,
                Vector3.Normalize(toIdeal),
                MathF.Min(toIdeal.Length() + SurfaceProbeOffset, maxReach + SurfaceProbeOffset),
                hipWorld,
                idealFootWorld,
                bodyUp,
                minReach,
                maxReach,
                selfBody,
                ref best,
                ref bestScore,
                ref rayStart,
                ref rayEnd);
        }

        return best;
    }

    private void TrySelectSurfaceRay(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        Vector3 hipWorld,
        Vector3 idealFootWorld,
        Vector3 bodyUp,
        float minReach,
        float maxReach,
        BodyID selfBody,
        ref SurfacePoint best,
        ref float bestScore,
        ref Vector3 debugStart,
        ref Vector3 debugEnd)
    {
        if (direction.LengthSquared() <= Epsilon * Epsilon || maxDistance <= Epsilon ||
            !_sceneManager.Raycast(origin, direction, maxDistance, out var hit, selfBody))
            return;

        float reach = Vector3.Distance(hipWorld, hit.Position);
        if (reach < minReach || reach > maxReach)
            return;

        Vector3 normal = NormalizeOrFallback(hit.Normal, bodyUp);
        float normalPenalty = 1f - MathF.Max(0f, Vector3.Dot(normal, bodyUp));
        float score = Vector3.DistanceSquared(hit.Position, idealFootWorld) +
                      normalPenalty * maxReach * maxReach * 0.18f;
        if (score >= bestScore)
            return;

        bestScore = score;
        best = new SurfacePoint(hit.Position, normal, true);
        debugStart = origin;
        debugEnd = hit.Position;
    }

    private bool IsValidNode(int nodeIndex) => nodeIndex >= 0 && nodeIndex < _skeleton.Nodes.Length;

    private bool IsValidLeg(in LegState leg) =>
        IsValidNode(leg.Hip) && IsValidNode(leg.Knee) && IsValidNode(leg.Tip) &&
        (leg.Length1 + leg.Length2) > 0.001f;

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
            drawer.PushLine(kneePos, footCurrent, groupColor);

            drawer.DrawSphere(footCurrent, Quaternion.Identity, 0.05f, new Vector3(0, 1, 0));

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
                    Vector3 p0 = Vector3.Lerp(arcStart, arcEnd, t0) + n0 * MathF.Sin(t0 * MathF.PI) * StepHeight;
                    Vector3 p1 = Vector3.Lerp(arcStart, arcEnd, t1) + n1 * MathF.Sin(t1 * MathF.PI) * StepHeight;
                    drawer.PushLine(p0, p1, new Vector3(1, 0, 1));
                }
            }
        }
    }
}
