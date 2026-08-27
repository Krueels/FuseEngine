using System;
using System.Numerics;
using Fuse.Core;
using Fuse.Enemy;
using Fuse.Scene;

namespace Fuse.Animation;

/// <summary>
/// Keeps the spider's feet planted in world space and solves the thigh/leg chain
/// towards those planted positions. The skeleton imported by Assimp uses column
/// convention (translation in M14/M24/M34), so its bone-space math is kept
/// separate from System.Numerics' regular world-space helpers.
/// </summary>
public sealed class ProceduralSpiderWalk
{
    private const float StepHeight = 0.4f;
    private const float StepDistanceThreshold = 0.6f;
    private const float StepSpeed = 8.0f;
    private const float RaycastDistance = 3.0f;
    private const float Epsilon = 0.0001f;

    private Skeleton _skeleton = null!;
    private readonly SceneManager _sceneManager;
    private LegState[] _legs = Array.Empty<LegState>();
    private bool _initialized;

    public Matrix4x4[]? FinalBoneMatrices { get; private set; }

    private struct LegState
    {
        public int Hip;
        public int Knee;
        public int Tip;
        public int GaitGroup;

        // Measured in the model's unscaled skeleton space.
        public float Length1;
        public float Length2;
        public Vector3 RestFootModel;
        public Matrix4x4 HipRestLocal;
        public Matrix4x4 KneeRestLocal;

        // These positions deliberately stay in world space while a foot is planted.
        public Vector3 CurrentFootWorld;
        public Vector3 TargetFootWorld;
        public Vector3 StepStartWorld;
        public float StepProgress;
        public bool IsStepping;
        public bool HasPlantedFoot;
    }

    public ProceduralSpiderWalk(SceneManager scene) => _sceneManager = scene;

    public void SetFinalBoneMatrices(Matrix4x4[] matrices) => FinalBoneMatrices = matrices;

    internal void Initialize(Skeleton skeleton, SpiderEnemy.LegData[] data)
    {
        _skeleton = skeleton;
        FinalBoneMatrices = new Matrix4x4[_skeleton.Bones.Length];

        _skeleton.ComputeGlobalTransforms();

        int count = System.Math.Min(data.Length, 8);
        _legs = new LegState[count];

        for (int i = 0; i < count; i++)
        {
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

            // L0/L2/R1/R3 move together, alternating with the other four legs.
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
        Quaternion bodyRotation,
        Vector3 modelScale)
    {
        if (!_initialized)
            return;

        modelScale = SanitizeScale(modelScale);
        _skeleton.ComputeGlobalTransforms();

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

            Vector3 idealFootWorld = ModelToWorld(leg.RestFootModel, bodyPosition, bodyRotation, modelScale);
            idealFootWorld += forward * MathF.Min(speed * 0.2f, 0.8f);
            idealFootWorld = ProjectToGround(idealFootWorld);

            // The first valid frame starts each foot on the floor instead of letting
            // the bind pose decide where its contact point should be.
            if (!leg.HasPlantedFoot)
            {
                leg.CurrentFootWorld = idealFootWorld;
                leg.TargetFootWorld = idealFootWorld;
                leg.StepStartWorld = idealFootWorld;
                leg.HasPlantedFoot = true;
            }
            else
            {
                bool oppositeGroupIsStepping = leg.GaitGroup == 0 ? group1IsStepping : group0IsStepping;
                float distanceToIdeal = Vector3.Distance(leg.CurrentFootWorld, idealFootWorld);

                if (!leg.IsStepping && !oppositeGroupIsStepping && distanceToIdeal > StepDistanceThreshold)
                {
                    leg.IsStepping = true;
                    leg.StepProgress = 0.0f;
                    leg.StepStartWorld = leg.CurrentFootWorld;
                    leg.TargetFootWorld = idealFootWorld;

                    if (leg.GaitGroup == 0) group0IsStepping = true;
                    else group1IsStepping = true;
                }

                UpdateFootStep(ref leg, dt);
            }

            Vector3 targetInModel = WorldToModel(leg.CurrentFootWorld, bodyPosition, bodyRotation, modelScale);
            SolveTwoBoneIK(ref leg, targetInModel);
        }

        // This also rebuilds the global hierarchy before the matrices are uploaded.
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
        // The animator has restored the bind pose earlier in the frame. Start this
        // leg from that pose, so rotations never accumulate between frames.
        _skeleton.Nodes[leg.Hip].Local = leg.HipRestLocal;
        _skeleton.Nodes[leg.Knee].Local = leg.KneeRestLocal;
        _skeleton.ComputeGlobalTransforms();

        Vector3 hipPosition = GetSkeletonPoint(_skeleton.Nodes[leg.Hip].Global);
        Vector3 toTarget = requestedTarget - hipPosition;
        float requestedDistance = toTarget.Length();
        if (requestedDistance <= Epsilon)
            return;

        Vector3 targetDirection = toTarget / requestedDistance;
        float minReach = MathF.Abs(leg.Length1 - leg.Length2) + 0.0005f;
        float maxReach = leg.Length1 + leg.Length2 - 0.0005f;
        float distance = System.Math.Clamp(requestedDistance, minReach, maxReach);
        Vector3 target = hipPosition + targetDirection * distance;

        // Pick the bend side from the current base pose. This prevents left/right
        // legs from flipping their knees when the target is directly in front of
        // the hip, while still allowing an animation to move the body.
        Vector3 currentKnee = GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global);
        Vector3 restBend = currentKnee - hipPosition;
        Vector3 pole = restBend - targetDirection * Vector3.Dot(restBend, targetDirection);
        if (pole.LengthSquared() <= Epsilon * Epsilon)
        {
            pole = Vector3.Cross(targetDirection, Vector3.UnitY);
            if (pole.LengthSquared() <= Epsilon * Epsilon)
                pole = Vector3.Cross(targetDirection, Vector3.UnitX);
        }
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

        // Imported locals are T * R in column convention. Inserting the delta after
        // T rotates the bone around its own joint instead of moving that joint.
        Matrix4x4 translation = CreateSkeletonTranslation(GetSkeletonTranslation(restLocal));
        Matrix4x4 orientationAndScale = ClearSkeletonTranslation(restLocal);
        _skeleton.Nodes[nodeIndex].Local = translation * deltaRotation * orientationAndScale;
    }

    private Vector3 ProjectToGround(Vector3 idealFootWorld)
    {
        Vector3 rayStart = idealFootWorld + Vector3.UnitY * (RaycastDistance * 0.5f);
        if (_sceneManager.Raycast(rayStart, -Vector3.UnitY, RaycastDistance, out var hit))
            return hit.Position;

        return idealFootWorld;
    }

    private bool IsValidNode(int nodeIndex) => (uint)nodeIndex < (uint)_skeleton.Nodes.Length;

    private bool IsValidLeg(in LegState leg) =>
        IsValidNode(leg.Hip) && IsValidNode(leg.Knee) && IsValidNode(leg.Tip) &&
        leg.Length1 > Epsilon && leg.Length2 > Epsilon;

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

    // Assimp matrices are used as column-vector matrices throughout the skeleton.
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
        // System.Numerics creates a row-vector matrix; transpose it for Assimp's
        // column-vector skeleton matrices.
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
}
