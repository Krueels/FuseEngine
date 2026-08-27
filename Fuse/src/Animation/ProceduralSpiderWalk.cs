using System;
using System.Numerics;
using Fuse.Core;
using Fuse.Debug;
using Fuse.Enemy;
using Fuse.Scene;

namespace Fuse.Animation;

/// <summary>
/// Keeps the spider's feet planted in world space and solves the thigh/leg chain
/// towards those planted positions. The skeleton imported by Assimp uses column
/// convention (translation in M14/M24/M34), so its bone-space math is kept
/// separate from System.Numerics' regular world-space helpers.
/// </summary>
public sealed class ProceduralSpiderWalk : IGizmoDrawable
{
    // The bind pose is already close to the leg's maximum reach. Start moving a
    // trailing foot early, but place it far ahead so this large spider takes long,
    // deliberate strides instead of continuously shuffling in tiny increments.
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
        Quaternion modelWorldRotation,
        Vector3 modelScale,
        Matrix4x4 modelMatrix)
    {
        if (!_initialized)
            return;

        _lastModelMatrix = modelMatrix;
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

            Vector3 desiredFootWorld = ModelToWorld(leg.RestFootModel, bodyPosition, modelWorldRotation, modelScale);
            leg.DebugIdealBeforeRaycast = desiredFootWorld;
            leg.DebugRayStart = desiredFootWorld + Vector3.UnitY * (RaycastDistance * 0.5f);
            leg.DebugRayEnd = desiredFootWorld - Vector3.UnitY * (RaycastDistance * 0.5f);
            leg.DebugRaycastHit = _sceneManager.Raycast(leg.DebugRayStart, -Vector3.UnitY, RaycastDistance, out var groundHit);
            if (leg.DebugRaycastHit)
                leg.DebugRayEnd = groundHit.Position;
            desiredFootWorld = leg.DebugRaycastHit ? groundHit.Position : desiredFootWorld;

            // The first valid frame starts each foot on the floor instead of letting
            // the bind pose decide where its contact point should be.
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

                    // The resting point determines when the leg is late. The
                    // landing point is deliberately ahead of it, giving the body
                    // room to travel before this same leg needs another step.
                    Vector3 landingPoint = desiredFootWorld + forward * StepForwardPlacement;
                    leg.TargetFootWorld = ProjectToGround(landingPoint);
                    leg.DebugLandingPoint = leg.TargetFootWorld;

                    if (leg.GaitGroup == 0) group0IsStepping = true;
                    else group1IsStepping = true;
                }

                UpdateFootStep(ref leg, dt);
            }

            Vector3 targetInModel = WorldToModel(leg.CurrentFootWorld, bodyPosition, modelWorldRotation, modelScale);
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

            // Skeleton: world-space
            Vector3 hipPos = ModelToWorld(GetSkeletonPoint(_skeleton.Nodes[leg.Hip].Global));
            Vector3 kneePos = ModelToWorld(GetSkeletonPoint(_skeleton.Nodes[leg.Knee].Global));

            // Feet: already world-space
            Vector3 footCurrent = leg.CurrentFootWorld;
            Vector3 footTarget = leg.IsStepping ? leg.TargetFootWorld : leg.CurrentFootWorld;

            Vector3 groupColor = leg.GaitGroup == 0
                ? new Vector3(1, 0.5f, 0)
                : new Vector3(0, 0.5f, 1);

            // --- RAYCAST DEBUG ---
            // Linha do raycast (ciano se acertou, vermelho se errou)
            Vector3 rayColor = leg.DebugRaycastHit ? new Vector3(0, 1, 1) : new Vector3(1, 0, 0);
            drawer.PushLine(leg.DebugRayStart, leg.DebugRayEnd, rayColor);

            // Esfera no início do raycast (pequena, branca)
            drawer.DrawSphere(leg.DebugRayStart, Quaternion.Identity, 0.04f, new Vector3(1, 1, 1));

            // Esfera no ponto de hit (ciano)
            if (leg.DebugRaycastHit)
            {
                drawer.DrawSphere(leg.DebugRayEnd, Quaternion.Identity, 0.06f, new Vector3(0, 1, 1));
            }

            // Posição ideal ANTES do raycast (vermelha, translúcida)
            drawer.DrawSphere(leg.DebugIdealBeforeRaycast, Quaternion.Identity, 0.05f, new Vector3(1, 0.3f, 0.3f));

            // Linha da posição ideal até o hit (mostra offset do terreno)
            if (leg.DebugRaycastHit)
            {
                drawer.PushLine(leg.DebugIdealBeforeRaycast, leg.DebugRayEnd, new Vector3(1, 0.3f, 0.3f));
            }

            // --- SKELETON DEBUG ---
            drawer.PushLine(hipPos, kneePos, groupColor);
            drawer.PushLine(kneePos, footCurrent, groupColor);

            // --- FOOT DEBUG ---
            // Pé atual (verde)
            drawer.DrawSphere(footCurrent, Quaternion.Identity, 0.05f, new Vector3(0, 1, 0));

            // Target (amarelo se stance, magenta se swing)
            drawer.DrawSphere(footTarget, Quaternion.Identity, 0.08f,
                leg.IsStepping ? new Vector3(1, 0, 1) : new Vector3(0, 1, 0));

            // Linha quadril -> target
            drawer.PushLine(hipPos, footTarget, leg.IsStepping ? new Vector3(1, 0, 1) : new Vector3(1, 1, 1));

            // --- LANDING POINT ---
            if (leg.IsStepping)
            {
                // Landing point (onde o pé vai pousar)
                drawer.DrawSphere(leg.DebugLandingPoint, Quaternion.Identity, 0.07f, new Vector3(1, 1, 0));

                // Linha do target até o landing point
                drawer.PushLine(footTarget, leg.DebugLandingPoint, new Vector3(1, 1, 0));

                // Arco do step
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
