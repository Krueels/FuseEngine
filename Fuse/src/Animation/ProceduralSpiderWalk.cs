using System;
using System.Numerics;
using Fuse.Core;
using Fuse.Debug;
using Fuse.Enemy;
using Fuse.Scene;

namespace Fuse.Animation;

/// <summary>
/// Keeps the spider's feet planted in world space and solves the thigh/leg chain
/// towards those planted positions.
/// </summary>
public sealed class ProceduralSpiderWalk : IGizmoDrawable
{
    private const float StepHeight = 0.55f;
    private const float StepDistanceThreshold = 0.18f;
    private const float LateralStepThreshold = 0.75f;
    private const float StepForwardPlacement = 1.5f;
    private const float StepSpeed = 7.0f;
    private const float RaycastDistance = 10.0f;
    private const float Epsilon = 0.0001f;

    private Skeleton _skeleton = null!;
    private readonly SceneManager _sceneManager;
    private LegState[] _legs = Array.Empty<LegState>();
    private bool _initialized;
    private Matrix4x4 _lastModelMatrix = Matrix4x4.Identity;

    public Matrix4x4[]? FinalBoneMatrices { get; private set; }

    private struct LegState
    {
        public int Hip;
        public int Knee;
        public int Tip;
        public int GaitGroup;

        public float Length1;
        public float Length2;
        public Vector3 RestFootModel;
        public Matrix4x4 HipRestLocal;
        public Matrix4x4 KneeRestLocal;

        public Vector3 CurrentFootWorld;
        public Vector3 TargetFootWorld;
        public Vector3 StepStartWorld;
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

            _legs[i] = new LegState
            {
                Hip = hip,
                Knee = knee,
                Tip = tip,
                GaitGroup = gaitGroup,
                Length1 = length1,
                Length2 = length2,
                RestFootModel = tipPosition,
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
        Matrix4x4 modelMatrix)
    {
        if (!_initialized)
            return;

        _lastModelMatrix = modelMatrix;
        modelScale = SanitizeScale(modelScale);
        _skeleton.ComputeGlobalTransforms();

        Vector3 bodyUp = Vector3.Transform(Vector3.UnitY, modelWorldRotation);

        bool group0IsStepping = false;
        bool group1IsStepping = false;
        foreach (var leg in _legs)
        {
            if (!leg.IsStepping)
                continue;

            if (leg.GaitGroup == 0) group0IsStepping = true;
            else group1IsStepping = true;
        }

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

            desiredFootWorld = ProjectToGround(
                hipWorld,
                desiredFootWorld,
                bodyUp,
                outwardDirWorld,
                out leg.DebugRaycastHit,
                out leg.DebugRayStart,
                out leg.DebugRayEnd,
                out _);

            if (!leg.HasPlantedFoot)
            {
                leg.CurrentFootWorld = desiredFootWorld;
                leg.TargetFootWorld = desiredFootWorld;
                leg.StepStartWorld = desiredFootWorld;
                leg.HasPlantedFoot = true;
            }
            else
            {
                bool oppositeGroupIsStepping = leg.GaitGroup == 0 ? group1IsStepping : group0IsStepping;
                Vector3 footError = desiredFootWorld - leg.CurrentFootWorld;
                float forwardError = Vector3.Dot(footError, forward);
                Vector3 lateralError = footError - forward * forwardError;
                bool footIsTrailing = forwardError > StepDistanceThreshold;
                bool footIsOutOfPosition = lateralError.LengthSquared() > LateralStepThreshold * LateralStepThreshold;

                if (!leg.IsStepping && !oppositeGroupIsStepping && (footIsTrailing || footIsOutOfPosition))
                {
                    leg.IsStepping = true;
                    leg.StepProgress = 0.0f;
                    leg.StepStartWorld = leg.CurrentFootWorld;

                    Vector3 landingPoint = desiredFootWorld + forward * StepForwardPlacement;
                    leg.TargetFootWorld = ProjectToGround(hipWorld, landingPoint, bodyUp, outwardDirWorld, out _, out _, out _, out _);
                    leg.DebugLandingPoint = leg.TargetFootWorld;

                    if (leg.GaitGroup == 0) group0IsStepping = true;
                    else group1IsStepping = true;
                }

                UpdateFootStep(ref leg, dt);
            }

            Vector3 targetInModel = WorldToModel(leg.CurrentFootWorld, bodyPosition, modelWorldRotation, modelScale);
            SolveTwoBoneIK(ref leg, targetInModel);
        }

        if (FinalBoneMatrices != null)
            _skeleton.ComputeFinalBoneMatrices(FinalBoneMatrices);
    }

    private void UpdateFootStep(ref LegState leg, float dt)
    {
        if (!leg.IsStepping)
            return;

        leg.StepProgress = MathF.Min(leg.StepProgress + dt * StepSpeed, 1.0f);
        Vector3 linearPosition = Vector3.Lerp(leg.StepStartWorld, leg.TargetFootWorld, leg.StepProgress);
        float arcHeight = MathF.Sin(leg.StepProgress * MathF.PI) * StepHeight;
        leg.CurrentFootWorld = linearPosition + Vector3.UnitY * arcHeight;

        if (leg.StepProgress >= 1.0f)
        {
            leg.CurrentFootWorld = leg.TargetFootWorld;
            leg.IsStepping = false;
        }
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
        float minReach = MathF.Abs(leg.Length1 - leg.Length2) + 0.0005f;
        float maxReach = MathF.Max(minReach, leg.Length1 + leg.Length2 - 0.0005f);
        float distance = System.Math.Clamp(requestedDistance, minReach, maxReach);
        Vector3 target = hipPosition + targetDirection * distance;

        Vector3 currentKnee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);

        // --- CÁLCULO DE POLE VECTOR COM VIÉS ARACNÍDEO VERTICAL (+Y) ---
        Vector3 restKneeDir = currentKnee - hipPosition;
        float outwardX = restKneeDir.X;
        float outwardZ = restKneeDir.Z;

        if (MathF.Abs(outwardX) < Epsilon && MathF.Abs(outwardZ) < Epsilon)
        {
            Vector3 restFootDir = leg.RestFootModel - hipPosition;
            outwardX = restFootDir.X;
            outwardZ = restFootDir.Z;
        }

        // Garante viés vertical positivo em relação ao dorso do modelo
        float outwardMag = MathF.Sqrt(outwardX * outwardX + outwardZ * outwardZ);
        Vector3 preferredUp = new Vector3(
            outwardX,
            outwardMag * 1.2f + 1.5f,
            outwardZ
        );

        if (preferredUp.LengthSquared() > Epsilon)
            preferredUp = Vector3.Normalize(preferredUp);
        else
            preferredUp = Vector3.UnitY;

        Vector3 pole = preferredUp - targetDirection * Vector3.Dot(preferredUp, targetDirection);

        if (pole.LengthSquared() <= Epsilon * Epsilon)
        {
            Vector3 outwardOnly = Vector3.Normalize(new Vector3(outwardX, 0f, outwardZ));
            pole = outwardOnly - targetDirection * Vector3.Dot(outwardOnly, targetDirection);
            if (pole.LengthSquared() <= Epsilon * Epsilon)
            {
                pole = Vector3.Cross(targetDirection, Vector3.UnitZ);
            }
        }

        pole = Vector3.Normalize(pole);

        // Impede que o joelho aponte para baixo/barriga da aranha
        if (Vector3.Dot(pole, preferredUp) < 0f)
        {
            pole = -pole;
        }

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

    private Vector3 ProjectToGround(
        Vector3 hipWorld,
        Vector3 idealFootWorld,
        Vector3 bodyUp,
        Vector3 outwardDirWorld,
        out bool rayHit,
        out Vector3 rayStart,
        out Vector3 rayEnd,
        out Vector3 hitNormal)
    {
        rayStart = hipWorld;
        hitNormal = Vector3.UnitY;

        Vector3 toTarget = idealFootWorld - hipWorld;
        float dist = toTarget.Length();
        if (dist > Epsilon)
        {
            Vector3 dir = toTarget / dist;
            float castDistance = dist + 1.2f;
            if (_sceneManager.Raycast(hipWorld, dir, castDistance, out var hit))
            {
                rayHit = true;
                rayEnd = hit.Position;
                hitNormal = hit.Normal;
                return hit.Position;
            }
        }

        Vector3 downStart = idealFootWorld + bodyUp * 0.8f;
        if (_sceneManager.Raycast(downStart, -bodyUp, RaycastDistance, out var downHit))
        {
            rayHit = true;
            rayEnd = downHit.Position;
            hitNormal = downHit.Normal;
            return downHit.Position;
        }

        Vector3 diagDir = Vector3.Normalize(outwardDirWorld - bodyUp * 0.6f);
        if (_sceneManager.Raycast(hipWorld, diagDir, dist + 1.5f, out var diagHit))
        {
            rayHit = true;
            rayEnd = diagHit.Position;
            hitNormal = diagHit.Normal;
            return diagHit.Position;
        }

        rayHit = false;
        rayEnd = idealFootWorld;
        return idealFootWorld;
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
                    Vector3 p0 = Vector3.Lerp(arcStart, arcEnd, t0) + Vector3.UnitY * MathF.Sin(t0 * MathF.PI) * StepHeight;
                    Vector3 p1 = Vector3.Lerp(arcStart, arcEnd, t1) + Vector3.UnitY * MathF.Sin(t1 * MathF.PI) * StepHeight;
                    drawer.PushLine(p0, p1, new Vector3(1, 0, 1));
                }
            }
        }
    }
}