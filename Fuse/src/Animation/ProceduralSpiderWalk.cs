using System.Numerics;
using Fuse.Core;
using Fuse.Debug;
using Fuse.Enemy;
using Fuse.Scene;
using static Fuse.Animation.SpiderLocomotionMath;

namespace Fuse.Animation;

/// <summary>Contact-driven crawl with constrained IK and collision-checked body/leg motion.</summary>
public sealed class ProceduralSpiderWalk : IGizmoDrawable
{
    private sealed class Leg
    {
        public required int Index, Hip, Knee, Ankle, Tip, Group;
        public required SpiderLegKinematics Rig;
        public SpiderLegPose Pose, WorldPose;
        public SpiderSurfaceContact Planted, Target, Pending;
        public Vector3 Foot, Normal, Start, StartNormal, StepHeading, RecoveryDirection, SoleOffset;
        public float Radius, Progress, Lift, Duration, Urgency, RetryTime, BlockedTime;
        public int PendingFrames, PendingFrame = -1;
        public bool Initialized, Stepping, PoseValid, NeedsRecovery;
        public string Failure = "";
    }

    private readonly SpiderSurfaceSolver _solver;
    private readonly SpiderLocomotionProfile _profile;
    private Skeleton _skeleton = null!;
    private readonly List<Leg> _legs = new(8);
    private readonly HashSet<int> _ownedNodes = new();
    private SpiderLocomotionPose _body;
    private SpiderLocomotionPose _acceptedBody;
    private SpiderLegPose[] _acceptedPoses = [];
    private bool _hasAcceptedBody;
    private bool _hasBody;
    private int _frame, _preferredGroup;
    private float _dt;
    public Matrix4x4[]? FinalBoneMatrices { get; private set; }
    public IEnumerable<int> OwnedNodes => _ownedNodes;
    public int PlantedLegCount => _legs.Count(l => l.Planted.IsValid && !l.Stepping && l.PoseValid);
    public int SwingLegCount => _legs.Count(l => l.Stepping);
    public int BlockedLegCount => _legs.Count(l => !l.PoseValid || l.BlockedTime > 0f);
    public float MaximumContactError { get; private set; }
    public string LastBodyBlockReason { get; private set; } = "";
    public event Action<int, Vector3, Vector3>? FootLanded;

    public ProceduralSpiderWalk(SceneManager scene, SpiderSurfaceSolver? surfaceSolver = null, SpiderLocomotionProfile? profile = null)
    {
        _profile = profile ?? SpiderLocomotionProfile.Default;
        _solver = surfaceSolver ?? new SpiderSurfaceSolver(scene, _profile);
    }

    public void SetFinalBoneMatrices(Matrix4x4[] matrices) => FinalBoneMatrices = matrices;

    internal void Initialize(Skeleton skeleton, SpiderEnemy.LegData[] data)
    {
        _skeleton = skeleton;
        _legs.Clear();
        _ownedNodes.Clear();
        FinalBoneMatrices = new Matrix4x4[skeleton.Bones.Length];
        bool Valid(int index) => (uint)index < (uint)skeleton.Nodes.Length;
        for (int i = 0; i < data.Length; i++)
        {
            int hip = data[i].ThighNodeIndex, knee = data[i].SegmentNodeIndices[0];
            int ankle = data[i].SegmentNodeIndices[1], tip = data[i].SegmentNodeIndices[2];
            if (!Valid(tip)) tip = ankle;
            if (!Valid(hip) || !Valid(knee) || !Valid(ankle) ||
                !Descendant(knee, hip) || !Descendant(ankle, knee) || !Descendant(tip, ankle))
            {
                Logger.Warn($"[SpiderWalk] Ignoring incomplete leg {i}.");
                continue;
            }
            var rest = new SpiderLegPose(Point(skeleton.Nodes[hip].RestGlobal), Point(skeleton.Nodes[knee].RestGlobal),
                Point(skeleton.Nodes[ankle].RestGlobal), Point(skeleton.Nodes[tip].RestGlobal));
            var limits = _profile.LegJointLimits.GetValueOrDefault(skeleton.Nodes[hip].Name, _profile.JointLimits);
            var rig = new SpiderLegKinematics(rest, limits);
            if (rig.L1 <= Epsilon || rig.L2 <= Epsilon) continue;
            Vector3 soleOffset = Vector3.Zero;
            for (int n = tip + 1; n < skeleton.Nodes.Length; n++)
                if (Descendant(n, tip))
                {
                    Vector3 offset = Point(skeleton.Nodes[n].RestGlobal) - rest.Tip;
                    if (offset.LengthSquared() > soleOffset.LengthSquared()) soleOffset = offset;
                }
            bool left = rest.Tip.X < 0f;
            _legs.Add(new Leg
            {
                Index = i, Hip = hip, Knee = knee, Ankle = ankle, Tip = tip,
                Rig = rig, Pose = rest, SoleOffset = soleOffset, Group = ((i % 4) + (left ? 0 : 1)) % 2
            });
            // Own the whole leg subtree, including toes/twist helpers, and its
            // ancestors. Unrelated appendages can still receive clip animation.
            for (int n = 0; n < skeleton.Nodes.Length; n++)
                if (Descendant(n, hip)) _ownedNodes.Add(n);
            for (int n = hip; n >= 0; n = skeleton.Nodes[n].Parent) _ownedNodes.Add(n);
        }
        ResetContacts();
        _acceptedPoses = new SpiderLegPose[_legs.Count];
        DebugDrawer.Register(this);
    }

    public void ResetContacts()
    {
        foreach (Leg leg in _legs)
        {
            leg.Initialized = leg.Stepping = leg.PoseValid = false;
            leg.Planted = leg.Target = leg.Pending = default;
            leg.Progress = leg.RetryTime = leg.BlockedTime = 0f;
            leg.NeedsRecovery = false;
            leg.PendingFrames = 0;
            leg.PendingFrame = -1;
            leg.Pose = leg.Rig.Rest;
        }
        _hasBody = false;
        _hasAcceptedBody = false;
    }

    public void Update(float dt, in SpiderLocomotionPose body)
    {
        if (_skeleton == null || dt <= 0f) return;
        if (!Finite(body.Position) || !float.IsFinite(body.Rotation.LengthSquared()) ||
            body.Rotation.LengthSquared() < Epsilon || !float.IsFinite(body.Scale) || body.Scale <= Epsilon)
            throw new ArgumentException("Spider locomotion requires a finite pose and positive uniform scale.", nameof(body));
        if (_hasBody && (Vector3.Distance(_body.Position, body.Position) > _profile.TeleportResetDistance ||
            MathF.Abs(_body.Scale - body.Scale) > Epsilon)) ResetContacts();
        _body = body;
        _hasBody = true;
        _dt = MathF.Min(dt, 0.05f);
        _frame++;
        MaximumContactError = 0f;
        foreach (int index in _ownedNodes) _skeleton.Nodes[index].Local = _skeleton.Nodes[index].RestLocal;
        _skeleton.ComputeGlobalTransforms();

        foreach (Leg leg in _legs)
        {
            leg.Radius = System.Math.Clamp(leg.Rig.Reach * body.Scale * _profile.FootRadiusFractionOfLeg,
                _profile.MinimumFootRadiusWorld, _profile.MaximumFootRadiusWorld);
            leg.RetryTime = MathF.Max(0f, leg.RetryTime - _dt);
            leg.Planted = _solver.Refresh(leg.Planted);
            if (!leg.Initialized)
            {
                leg.Foot = body.ToWorld(leg.Rig.Rest.Tip);
                leg.Normal = body.Up;
                leg.WorldPose = ToWorld(leg.Rig.Rest, body);
                if (FindLanding(leg, body.ToWorld(Stance(leg)), out var initial, out var initialPose))
                {
                    leg.Planted = initial;
                    leg.Foot = ContactPoint(leg, initial);
                    leg.Normal = initial.Normal;
                    leg.Pose = initialPose;
                    leg.PoseValid = true;
                }
                leg.Initialized = true;
            }
            if (!leg.Stepping && leg.Planted.IsValid)
            {
                leg.Foot = ContactPoint(leg, leg.Planted);
                leg.Normal = leg.Planted.Normal;
            }
            int legSlot = _legs.IndexOf(leg);
            SpiderLegPose solved;
            bool accepted = _hasAcceptedBody && _acceptedBody.Position == body.Position && _acceptedBody.Rotation == body.Rotation &&
                Vector3.DistanceSquared(body.ToWorld(_acceptedPoses[legSlot].Tip), leg.Foot) < 0.0000001f;
            bool unchanged = Vector3.DistanceSquared(body.ToWorld(leg.Pose.Hip), leg.WorldPose.Hip) < 0.0000001f &&
                Vector3.DistanceSquared(body.ToWorld(leg.Pose.Tip), leg.Foot) < 0.0000001f;
            if (accepted)
            {
                solved = _acceptedPoses[legSlot];
                leg.PoseValid = true;
            }
            else if (unchanged && PoseClear(leg, ToWorld(leg.Pose, body), body, leg.Normal, false))
            {
                solved = leg.Pose;
                leg.PoseValid = true;
            }
            else leg.PoseValid = Solve(leg, leg.Foot, leg.Normal, _body, out solved, checkOtherLegs: false);
            leg.Failure = leg.PoseValid ? "" : "contact pose is obstructed or unreachable";
            if (leg.PoseValid)
            {
                leg.Pose = solved;
                leg.WorldPose = ToWorld(solved, body);
            }
            else leg.BlockedTime += _dt;

            Vector3 ideal = body.ToWorld(Stance(leg));
            float threshold = leg.Rig.Reach * body.Scale * _profile.StepTriggerFractionOfReach;
            // Absolute stance error works for reverse travel and turn-in-place.
            leg.Urgency = Project(ideal - leg.Foot, body.Up).Length() / MathF.Max(threshold, 0.02f);
            if (!leg.Planted.IsValid || !leg.PoseValid) leg.Urgency += 10f;
            if (leg.NeedsRecovery) leg.Urgency += 5f;
        }

        foreach (Leg leg in _legs.Where(l => l.Stepping)) AdvanceStep(leg);
        int available = System.Math.Max(0, _profile.MaximumSwingLegs - SwingLegCount);
        foreach (Leg leg in _legs.Where(l => !l.Stepping && l.Urgency >= 1f && l.RetryTime <= 0f)
                     .OrderByDescending(l => l.Urgency + (l.Group == _preferredGroup ? 0.1f : 0f)))
        {
            if (available <= 0) break;
            if (!CanLift(leg)) continue;
            if (!PlanStep(leg)) { leg.RetryTime = _profile.ReplanIntervalSeconds; continue; }
            available--;
            _preferredGroup = 1 - leg.Group;
        }

        foreach (Leg leg in _legs)
        {
            ApplyPose(leg);
            Vector3 actual = body.ToWorld(Point(_skeleton.Nodes[leg.Tip].Global));
            float error = Vector3.Distance(actual, leg.Foot);
            if (!float.IsFinite(error) || error > _profile.ContactToleranceWorld)
            {
                leg.PoseValid = false;
                leg.Planted = default;
                leg.Failure = $"skin pose residual {error}";
            }
            if (leg.Planted.IsValid && !leg.Stepping)
                MaximumContactError = MathF.Max(MaximumContactError, error);
        }
        if (FinalBoneMatrices != null) _skeleton.ComputeFinalBoneMatrices(FinalBoneMatrices);
    }

    private bool CanLift(Leg lifting)
    {
        if (!lifting.Planted.IsValid || !lifting.PoseValid) return true;
        Span<Vector3> supports = stackalloc Vector3[_legs.Count];
        int count = 0;
        foreach (Leg leg in _legs)
            if (leg != lifting && !leg.Stepping && leg.Planted.IsValid && leg.PoseValid)
                supports[count++] = leg.Foot;
        int minimum = System.Math.Min(_profile.MinimumSupportLegs, System.Math.Max(3, _legs.Count - 1));
        return count >= minimum && ContainsSupport(supports[..count], _body.Position, SupportNormal(), _profile.SupportPolygonMargin);
    }

    private Vector3 SupportNormal()
    {
        Vector3 sum = Vector3.Zero;
        foreach (Leg leg in _legs)
            if (!leg.Stepping && leg.Planted.IsValid && leg.PoseValid) sum += leg.Planted.Normal;
        return Normal(sum, _body.Up);
    }

    private Vector3 PredictLanding(Leg leg)
    {
        Vector3 restOffset = _body.ToWorld(Stance(leg)) - _body.Position;
        Vector3 velocity = _body.RelativeVelocity + Vector3.Cross(_body.RelativeAngularVelocity, restOffset);
        Vector3 advance = Project(velocity, _body.Up) * _profile.StepPredictionSeconds;
        if (leg.NeedsRecovery)
            advance += leg.RecoveryDirection * (leg.Rig.Reach * _body.Scale * 0.14f);
        float maxAdvance = leg.Rig.Reach * _body.Scale * 0.22f;
        if (advance.Length() > maxAdvance) advance = Normal(advance, _body.Forward) * maxAdvance;
        return _body.Position + restOffset + advance;
    }

    private bool PlanStep(Leg leg)
    {
        Vector3 predicted = PredictLanding(leg);
        if (!FindLanding(leg, predicted, out var contact, out _) &&
            !FindLanding(leg, _body.ToWorld(Stance(leg)), out contact, out _) &&
            !FindLanding(leg, Vector3.Lerp(leg.Foot, predicted, 0.5f), out contact, out _)) return false;
        if (!ConfirmSurface(leg, contact)) return false;
        Vector3 target = ContactPoint(leg, contact);
        float reach = leg.Rig.Reach * _body.Scale;
        float maxLift = MathF.Min(_profile.MaximumStepLiftWorld, reach * _profile.MaximumStepLiftFractionOfReach);
        float lift = System.Math.Clamp(reach * _profile.MinimumStepLiftFractionOfReach + Vector3.Distance(leg.Foot, target) * 0.16f,
            MathF.Min(_profile.MinimumStepLiftWorld, maxLift), maxLift);
        if (!StepPathClear(leg, target, contact.Normal, lift))
        {
            lift = maxLift;
            if (!StepPathClear(leg, target, contact.Normal, lift)) return false;
        }
        leg.Start = leg.Foot; leg.StartNormal = leg.Normal;
        leg.Target = contact; leg.Progress = 0f; leg.Lift = lift;
        leg.Duration = MathF.Max(_profile.MinimumStepDurationSeconds,
            _profile.StepDurationSeconds / (1f + _body.RelativeVelocity.Length() * 0.035f));
        leg.StepHeading = _body.Forward;
        leg.Stepping = true;
        leg.NeedsRecovery = false;
        leg.BlockedTime = 0f;
        return true;
    }

    private bool ConfirmSurface(Leg leg, SpiderSurfaceContact candidate)
    {
        var old = leg.Planted;
        if (!old.IsValid || old.BodyId == candidate.BodyId && Vector3.Dot(old.Normal, candidate.Normal) >= _profile.SurfaceNormalContinuity)
        {
            leg.PendingFrames = 0;
            return true;
        }
        bool same = leg.Pending.IsValid && leg.Pending.BodyId == candidate.BodyId &&
            Vector3.Dot(leg.Pending.Normal, candidate.Normal) >= _profile.SurfaceNormalContinuity;
        if (!same) leg.PendingFrames = 0;
        if (leg.PendingFrame != _frame) { leg.PendingFrames++; leg.PendingFrame = _frame; }
        leg.Pending = candidate;
        return leg.PendingFrames >= _profile.SurfaceSwitchConfirmationFrames;
    }

    private bool StepPathClear(Leg leg, Vector3 target, Vector3 normal, float lift)
    {
        SpiderLegPose previous = leg.WorldPose;
        const int samples = 10;
        for (int i = 1; i <= samples; i++)
        {
            float t = (float)i / samples;
            Vector3 point = StepPoint(leg.Foot, target, leg.Normal, normal, lift, t);
            Vector3 n = Normal(Vector3.Lerp(leg.Normal, normal, t), normal);
            if (!Solve(leg, point, n, _body, out var pose)) return false;
            var next = ToWorld(pose, _body);
            if (!MotionClear(leg, previous, next)) return false;
            previous = next;
        }
        return true;
    }

    private void AdvanceStep(Leg leg)
    {
        leg.Target = _solver.Refresh(leg.Target);
        bool headingChanged = Vector3.Dot(leg.StepHeading, _body.Forward) < 0.5f && leg.Progress < 0.7f;
        if ((!leg.Target.IsValid || headingChanged || leg.BlockedTime > _profile.ReplanIntervalSeconds) && leg.RetryTime <= 0f)
        {
            if (PlanStep(leg)) return;
            leg.RetryTime = _profile.ReplanIntervalSeconds;
            var original = _solver.Refresh(leg.Planted);
            if (original.IsValid && StepPathClear(leg, ContactPoint(leg, original), original.Normal, leg.Lift))
            {
                leg.Target = original; leg.Start = leg.Foot; leg.StartNormal = leg.Normal; leg.Progress = 0f;
            }
        }
        if (!leg.Target.IsValid) { leg.BlockedTime += _dt; return; }
        Vector3 target = ContactPoint(leg, leg.Target);
        float progress = MathF.Min(1f, leg.Progress + _dt / leg.Duration);
        Vector3 next = StepPoint(leg.Start, target, leg.StartNormal, leg.Target.Normal, leg.Lift, progress);
        Vector3 normal = Normal(Vector3.Lerp(leg.StartNormal, leg.Target.Normal, progress), leg.Target.Normal);
        if (!Solve(leg, next, normal, _body, out var pose, previous: leg.WorldPose))
        {
            leg.BlockedTime += _dt;
            return;
        }
        leg.Foot = next; leg.Normal = normal; leg.Progress = progress;
        leg.Pose = pose; leg.WorldPose = ToWorld(pose, _body); leg.PoseValid = true;
        leg.BlockedTime = 0f;
        if (progress < 1f) return;
        if (!_solver.HasSupportPatch(leg.Target, leg.Radius * 0.6f, _body.BodyId))
        {
            leg.Target = default; leg.BlockedTime = _profile.ReplanIntervalSeconds; return;
        }
        leg.Stepping = false; leg.Planted = leg.Target; leg.Target = default;
        leg.NeedsRecovery = false;
        leg.PendingFrames = 0;
        FootLanded?.Invoke(leg.Index, leg.Foot, leg.Normal);
    }

    private bool FindLanding(Leg leg, Vector3 desired, out SpiderSurfaceContact contact, out SpiderLegPose pose)
    {
        SpiderLegPose accepted = default;
        bool found = _solver.TryFindFootContact(_body.ToWorld(leg.Rig.Rest.Hip), desired + SoleOffset(leg, _body.Up, _body), _body.Up,
            Normal(Project(desired - _body.ToWorld(leg.Rig.Rest.Hip), _body.Up), _body.Forward),
            0f, leg.Rig.Reach * _body.Scale, _body.BodyId, leg.Planted.IsValid ? leg.Planted : _body.Support,
            out contact, leg.Radius * 0.6f, candidate => Solve(leg, ContactPoint(leg, candidate), candidate.Normal, _body, out accepted));
        pose = accepted;
        return found;
    }

    private Vector3 ContactPoint(Leg leg, SpiderSurfaceContact contact, SpiderLocomotionPose? body = null) =>
        contact.Point + contact.Normal * (leg.Radius + _profile.CollisionSkinWorld) - SoleOffset(leg, contact.Normal, body ?? _body);

    private Vector3 Stance(Leg leg)
    {
        Vector3 offset = leg.Rig.Rest.Tip - leg.Rig.Rest.Hip;
        return leg.Rig.Rest.Hip + new Vector3(offset.X * _profile.StanceRadiusScale, offset.Y, offset.Z * _profile.StanceRadiusScale);
    }

    private static Vector3 SoleOffset(Leg leg, Vector3 normal, SpiderLocomotionPose body)
    {
        Quaternion tilt = RotationBetween(Vector3.UnitY, body.DirectionToModel(normal), leg.Rig.Rest.Tip - leg.Rig.Rest.Hip);
        return Vector3.Transform(Vector3.Transform(leg.SoleOffset, tilt) * body.Scale, body.Rotation);
    }

    private bool Solve(Leg leg, Vector3 foot, Vector3 normal, SpiderLocomotionPose body, out SpiderLegPose pose,
        bool checkOtherLegs = true, SpiderLegPose? previous = null) =>
        leg.Rig.TrySolve(body.ToModel(foot), Normal(body.DirectionToModel(normal), Vector3.UnitY), leg.Pose,
            out pose, candidate => PoseClear(leg, ToWorld(candidate, body), body, normal, checkOtherLegs) &&
                (!previous.HasValue || MotionClear(leg, previous.Value, ToWorld(candidate, body), body, normal)));

    private bool PoseClear(Leg leg, SpiderLegPose pose, SpiderLocomotionPose body, Vector3 normal, bool checkOtherLegs)
    {
        float radius = leg.Radius * _profile.LegRadiusFractionOfFoot;
        int segments = leg.Rig.L3 > Epsilon ? 3 : 2;
        for (int i = 0; i < segments; i++)
            if (!_solver.IsSegmentClear(pose[i], pose[i + 1], radius, body.BodyId, _profile.CollisionSkinWorld)) return false;
        if (!_solver.IsSegmentClear(pose.Tip, pose.Tip, leg.Radius, body.BodyId, _profile.CollisionSkinWorld)) return false;
        Vector3 sole = pose.Tip + SoleOffset(leg, normal, body);
        if (!_solver.IsSegmentClear(pose.Tip, sole, radius, body.BodyId, _profile.CollisionSkinWorld) ||
            !_solver.IsSegmentClear(sole, sole, leg.Radius, body.BodyId, _profile.CollisionSkinWorld)) return false;

        Vector3 bodyA = body.Position - body.Up * (_profile.BodyCylinderHeight * 0.5f);
        Vector3 bodyB = body.Position + body.Up * (_profile.BodyCylinderHeight * 0.5f);
        for (int i = 1; i < segments; i++)
            if (SegmentDistanceSquared(pose[i], pose[i + 1], bodyA, bodyB) < MathF.Pow(_profile.BodyRadius + radius, 2f)) return false;
        if (!checkOtherLegs) return true;
        foreach (Leg other in _legs)
        {
            if (other == leg || !other.Initialized || !other.PoseValid) continue;
            float separation = radius + other.Radius * _profile.LegRadiusFractionOfFoot;
            for (int i = 1; i < segments; i++)
                for (int j = 1; j < (other.Rig.L3 > Epsilon ? 3 : 2); j++)
                    if (SegmentDistanceSquared(pose[i], pose[i + 1], other.WorldPose[j], other.WorldPose[j + 1]) < separation * separation) return false;
        }
        return true;
    }

    private bool MotionClear(Leg leg, SpiderLegPose previous, SpiderLegPose next,
        SpiderLocomotionPose? nextBody = null, Vector3? nextNormal = null)
    {
        for (int i = 0; i < (leg.Rig.L3 > Epsilon ? 3 : 2); i++)
            if (!_solver.IsSegmentMotionClear(previous[i], previous[i + 1], next[i], next[i + 1],
                    leg.Radius * _profile.LegRadiusFractionOfFoot, _body.BodyId, _profile.CollisionSkinWorld)) return false;
        if (!_solver.IsSegmentMotionClear(previous.Tip, previous.Tip, next.Tip, next.Tip,
                leg.Radius, _body.BodyId, _profile.CollisionSkinWorld)) return false;
        Vector3 oldSole = previous.Tip + SoleOffset(leg, leg.Normal, _body);
        Vector3 newSole = next.Tip + SoleOffset(leg, nextNormal ?? leg.Normal, nextBody ?? _body);
        return _solver.IsSegmentMotionClear(previous.Tip, oldSole, next.Tip, newSole,
                   leg.Radius * _profile.LegRadiusFractionOfFoot, _body.BodyId, _profile.CollisionSkinWorld) &&
               _solver.IsSegmentMotionClear(oldSole, oldSole, newSole, newSole, leg.Radius, _body.BodyId, _profile.CollisionSkinWorld);
    }

    public bool CanOccupyPose(Vector3 position, Quaternion rotation)
    {
        if (!_hasBody) return true;
        LastBodyBlockReason = "";
        Span<Vector3> supportPoints = stackalloc Vector3[_legs.Count];
        int supportCount = 0;
        foreach (Leg supporting in _legs)
            if (!supporting.Stepping && supporting.Planted.IsValid && supporting.PoseValid)
                supportPoints[supportCount++] = _solver.Refresh(supporting.Planted).Point;
        if (supportCount >= 3 && !ContainsSupport(supportPoints[..supportCount], position, SupportNormal(), _profile.SupportPolygonMargin))
        {
            LastBodyBlockReason = "body would leave its support polygon";
            return false;
        }
        var nextBody = _body with { Position = position, Rotation = rotation };
        Span<SpiderLegPose> poses = stackalloc SpiderLegPose[_legs.Count];
        Span<SpiderLegPose> modelPoses = stackalloc SpiderLegPose[_legs.Count];
        int index = 0;
        foreach (Leg leg in _legs)
        {
            if (!leg.Initialized) { index++; continue; }
            if (!leg.PoseValid) { leg.NeedsRecovery = true; LastBodyBlockReason = $"leg {leg.Index}: no valid pose"; return false; }
            if (leg.Stepping)
            {
                var landing = _solver.Refresh(leg.Target);
                if (landing.IsValid && !Solve(leg, ContactPoint(leg, landing, nextBody), landing.Normal,
                        nextBody, out _, checkOtherLegs: false))
                {
                    LastBodyBlockReason = $"leg {leg.Index}: body motion would invalidate landing";
                    return false;
                }
            }
            var support = _solver.Refresh(leg.Planted);
            Vector3 foot = !leg.Stepping && support.IsValid ? ContactPoint(leg, support, nextBody) : leg.Foot;
            Vector3 normal = !leg.Stepping && support.IsValid ? support.Normal : leg.Normal;
            if (!Solve(leg, foot, normal, nextBody, out var pose, checkOtherLegs: false, previous: leg.WorldPose))
            {
                LastBodyBlockReason = $"leg {leg.Index}: " +
                    (Solve(leg, foot, normal, nextBody, out _, checkOtherLegs: false) ? "swept clearance" : "reach/static clearance");
                Vector3 translation = Project(position - _body.Position, _body.Up);
                if (!leg.Stepping && translation.LengthSquared() > 0.000001f)
                {
                    leg.NeedsRecovery = true;
                    leg.RecoveryDirection = Normal(translation, _body.Forward);
                }
                return false;
            }
            modelPoses[index] = pose;
            poses[index++] = ToWorld(pose, nextBody);
        }
        for (int a = 0; a < _legs.Count; a++)
            for (int b = a + 1; b < _legs.Count; b++)
                for (int i = 1; i < (_legs[a].Rig.L3 > Epsilon ? 3 : 2); i++)
                    for (int j = 1; j < (_legs[b].Rig.L3 > Epsilon ? 3 : 2); j++)
                    {
                        float clearance = (_legs[a].Radius + _legs[b].Radius) * _profile.LegRadiusFractionOfFoot;
                        if (SegmentDistanceSquared(poses[a][i], poses[a][i + 1], poses[b][j], poses[b][j + 1]) < clearance * clearance)
                        {
                            LastBodyBlockReason = $"legs {a}/{b}: self intersection";
                            if (!_legs[a].Stepping) _legs[a].NeedsRecovery = true;
                            if (!_legs[b].Stepping) _legs[b].NeedsRecovery = true;
                            return false;
                        }
                    }
        modelPoses.CopyTo(_acceptedPoses);
        _acceptedBody = nextBody;
        _hasAcceptedBody = true;
        return true;
    }

    private void ApplyPose(Leg leg)
    {
        Aim(leg.Hip, leg.Knee, leg.Pose.Knee - leg.Pose.Hip);
        Aim(leg.Knee, leg.Ankle, leg.Pose.Ankle - leg.Pose.Knee);
        if (leg.Tip != leg.Ankle) Aim(leg.Ankle, leg.Tip, leg.Pose.Tip - leg.Pose.Ankle);
        var node = _skeleton.Nodes[leg.Tip];
        if (Matrix4x4.Decompose(Matrix4x4.Transpose(node.RestGlobal), out _, out Quaternion restRotation, out _) &&
            Matrix4x4.Decompose(Matrix4x4.Transpose(node.Global), out Vector3 scale, out _, out Vector3 position))
        {
            Vector3 up = Normal(_body.DirectionToModel(leg.Normal), Vector3.UnitY);
            Quaternion tilt = RotationBetween(Vector3.UnitY, up, leg.Rig.Rest.Tip - leg.Rig.Rest.Hip);
            Matrix4x4 global = Matrix4x4.Transpose(Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(tilt * restRotation)) * Matrix4x4.CreateTranslation(position));
            int parent = node.Parent;
            if (parent < 0) node.Local = global;
            else if (Matrix4x4.Invert(_skeleton.Nodes[parent].Global, out var inverse)) node.Local = inverse * global;
            _skeleton.ComputeGlobalTransforms();
        }
    }

    private void Aim(int nodeIndex, int childIndex, Vector3 desired)
    {
        var node = _skeleton.Nodes[nodeIndex];
        Vector3 current = Point(_skeleton.Nodes[childIndex].Global) - Point(node.Global);
        int parent = node.Parent;
        Matrix4x4 parentGlobal = parent < 0 ? Matrix4x4.Identity : _skeleton.Nodes[parent].Global;
        if (!Matrix4x4.Invert(parentGlobal, out Matrix4x4 inverse)) return;
        Vector3 Direction(Vector3 v) => Vector3.TransformNormal(v, Matrix4x4.Transpose(inverse));
        Quaternion delta = RotationBetween(Direction(current), Direction(desired), Direction(Vector3.UnitY));
        Matrix4x4 local = node.Local;
        Vector3 translation = Point(local);
        local.M14 = local.M24 = local.M34 = 0f;
        local = Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(delta)) * local;
        local.M14 = translation.X; local.M24 = translation.Y; local.M34 = translation.Z;
        node.Local = local;
        _skeleton.ComputeGlobalTransforms();
    }

    private bool Descendant(int node, int ancestor)
    {
        for (int guard = _skeleton.Nodes.Length; node >= 0 && node < _skeleton.Nodes.Length && guard-- > 0; node = _skeleton.Nodes[node].Parent)
            if (node == ancestor) return true;
        return false;
    }

    private static SpiderLegPose ToWorld(in SpiderLegPose pose, in SpiderLocomotionPose body) =>
        new(body.ToWorld(pose.Hip), body.ToWorld(pose.Knee), body.ToWorld(pose.Ankle), body.ToWorld(pose.Tip));

    internal (int Index, bool Planted, bool Swinging, bool Valid, Vector3 Foot, SpiderLegPose Pose, string Failure, Vector3? Target)[] GetDiagnostics() =>
        _legs.Select(l => (l.Index, l.Planted.IsValid && !l.Stepping, l.Stepping, l.PoseValid, l.Foot, l.WorldPose, l.Failure,
            l.Target.IsValid ? (Vector3?)ContactPoint(l, l.Target) : null)).ToArray();

    internal string DescribeGait() => string.Join("; ", _legs.Select(l =>
        $"{l.Index}: step={l.Stepping}/{l.Progress:F2}, urge={l.Urgency:F1}, recover={l.NeedsRecovery}, foot={l.Foot}, normal={l.Normal}"));

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (!_hasBody) return;
        foreach (Leg leg in _legs)
        {
            Vector3 color = !leg.PoseValid ? new(1f, 0.1f, 0.1f) : leg.Stepping ? new(1f, 0f, 1f) : new(0.1f, 1f, 0.2f);
            for (int i = 0; i < (leg.Rig.L3 > Epsilon ? 3 : 2); i++) drawer.PushLine(leg.WorldPose[i], leg.WorldPose[i + 1], color);
            drawer.DrawSphere(leg.Foot, Quaternion.Identity, leg.Radius, color);
            drawer.PushLine(leg.Foot, leg.Foot + leg.Normal * 0.25f, color);
            if (leg.Target.IsValid) drawer.DrawSphere(ContactPoint(leg, leg.Target), Quaternion.Identity, leg.Radius, new(1f, 1f, 0f));
        }
    }
}
