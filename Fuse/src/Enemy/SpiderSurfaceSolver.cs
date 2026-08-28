using System;
using System.Collections.Generic;
using System.Numerics;
using Fuse.Debug;
using Fuse.Physics;
using Fuse.Scene;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>
/// Builds stable, surface-relative contacts for the spider body and feet.
/// A contact is deliberately more than a raycast hit: it keeps the supporting
/// body/local anchor, a filtered normal and a confidence value.
/// </summary>
public sealed class SpiderSurfaceSolver : IGizmoDrawable
{
    private const float Epsilon = 0.0001f;
    private const float BodyProbeHeight = 0.85f;
    private const float BodyProbeDistance = 3.0f;
    private const float BodyProbeRadius = 0.48f;
    private const float FootProbeHeight = 1.35f;
    private const float FootProbeDistance = 4.5f;
    private const float ContactNormalSimilarity = 0.55f;

    private readonly SceneManager _scene;
    private readonly List<DebugProbe> _debugProbes = new();
    private readonly List<SpiderSurfaceContact> _debugCandidates = new();
    private SpiderSurfaceContact _lastBodyContact;

    public SpiderSurfaceContact LastBodyContact => _lastBodyContact;

    public SpiderSurfaceSolver(SceneManager scene)
    {
        _scene = scene;
        Debug.DebugDrawer.Register(this);
    }

    /// <summary>Clears transient probe debug once per animation frame.</summary>
    public void BeginFrame()
    {
        _debugProbes.Clear();
        _debugCandidates.Clear();
    }

    public SpiderSurfaceContact Refresh(in SpiderSurfaceContact contact)
    {
        if (!contact.IsValid || contact.SupportBody == null)
            return contact;

        Vector3 bodyPosition = contact.SupportBody.Position(_scene.Physics);
        Quaternion bodyRotation = contact.SupportBody.Rotation(_scene.Physics);
        return contact.WithWorldPose(
            bodyPosition + Vector3.Transform(contact.LocalPoint, bodyRotation),
            NormalizeOrFallback(Vector3.Transform(contact.LocalNormal, bodyRotation), contact.Normal));
    }

    /// <summary>
    /// Samples a small support patch below/around a proposed body position.
    /// The fan catches corners without switching normal from a single triangle.
    /// </summary>
    public bool TryFindBodyContact(
        Vector3 bodyPosition,
        Vector3 preferredUp,
        Vector3 preferredForward,
        BodyID selfBody,
        out SpiderSurfaceContact contact)
    {
        BeginFrame();

        // The physical controller currently walks on floors and slopes only.
        // Acquire that support with one unambiguous world-down probe before
        // considering any directional probes. This prevents a nearby wall from
        // being mistaken for ground and pulling the body through the terrain.
        Vector3 floorStart = bodyPosition + Vector3.UnitY * (BodyProbeHeight + 0.45f);
        float floorDistance = BodyProbeDistance + BodyProbeHeight + 1.0f;
        bool floorHit = _scene.Raycast(floorStart, -Vector3.UnitY, floorDistance, out SceneRaycastHit floorRay, selfBody);
        _debugProbes.Add(new DebugProbe(floorStart, floorHit ? floorRay.Position : floorStart - Vector3.UnitY * floorDistance, floorHit));
        if (floorHit && floorRay.Normal.Y >= 0.20f)
        {
            contact = CreateContact(floorRay, 1f);
            _debugCandidates.Add(contact);
            _lastBodyContact = contact;
            return true;
        }

        // Wall/ceiling support: if no floor was found, probe along the
        // preferred up direction (which may be a wall normal from last frame).
        Vector3 wallStart = bodyPosition + preferredUp * BodyProbeHeight;
        bool wallHit = _scene.Raycast(wallStart, -preferredUp, BodyProbeDistance, out SceneRaycastHit wallRay, selfBody);
        _debugProbes.Add(new DebugProbe(wallStart, wallHit ? wallRay.Position : wallStart + (-preferredUp) * BodyProbeDistance, wallHit));
        if (wallHit && Vector3.Dot(wallRay.Normal, -preferredUp) > 0.30f)
        {
            contact = CreateContact(wallRay, 0.8f);
            _debugCandidates.Add(contact);
            _lastBodyContact = contact;
            return true;
        }

        // Last resort: cast in all 6 cardinal directions
        Vector3[] directions = { -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitY };
        foreach (var dir in directions)
        {
            bool hit = _scene.Raycast(bodyPosition, dir, BodyProbeDistance, out SceneRaycastHit dirRay, selfBody);
            _debugProbes.Add(new DebugProbe(bodyPosition, hit ? dirRay.Position : bodyPosition + dir * BodyProbeDistance, hit));
            if (hit && Vector3.Dot(dirRay.Normal, -dir) > 0.30f)
            {
                contact = CreateContact(dirRay, 0.6f);
                _debugCandidates.Add(contact);
                _lastBodyContact = contact;
                return true;
            }
        }

        contact = default;
        return false;

    }

    /// <summary>
    /// Finds a foot target from a local, surface-relative fan. A candidate must
    /// be inside the chain reach band and is scored against the desired stride.
    /// </summary>
    public bool TryFindFootContact(
        Vector3 hipPosition,
        Vector3 desiredPosition,
        Vector3 expectedNormal,
        Vector3 strideDirection,
        float minReach,
        float maxReach,
        BodyID selfBody,
        out SpiderSurfaceContact contact)
    {
        expectedNormal = NormalizeOrFallback(expectedNormal, Vector3.UnitY);
        BuildTangentBasis(expectedNormal, strideDirection, out Vector3 forward, out Vector3 right);

        // Ground-first is the conservative and reliable path for the current
        // physical enemy controller. Do not make a side/corner probe compete
        // with a perfectly valid floor contact: that was the source of feet
        // being pulled into nearby walls and of most red debug noise.
        Vector3 floorStart = desiredPosition + Vector3.UnitY * FootProbeHeight;
        float floorDistance = MathF.Max(10f, maxReach + FootProbeHeight);
        bool floorHit = _scene.Raycast(floorStart, -Vector3.UnitY, floorDistance, out SceneRaycastHit floorRay, selfBody);
        _debugProbes.Add(new DebugProbe(floorStart, floorHit ? floorRay.Position : floorStart - Vector3.UnitY * floorDistance, floorHit));
        if (floorHit && floorRay.Normal.Y >= 0.15f)
        {
            float floorReach = Vector3.Distance(hipPosition, floorRay.Position);
            if (floorReach >= minReach && floorReach <= maxReach)
            {
                contact = CreateContact(floorRay, 1f);
                _debugCandidates.Add(contact);
                return true;
            }
        }

        var hits = new List<ProbeHit>(16);
        Vector3[] offsets =
        {
            Vector3.Zero,
            forward * 0.32f,
            -forward * 0.20f,
            right * 0.28f,
            -right * 0.28f,
            (forward + right) * 0.22f,
            (forward - right) * 0.22f
        };

        foreach (Vector3 offset in offsets)
        {
            AddProbe(
                hits,
                desiredPosition + offset + expectedNormal * FootProbeHeight,
                -expectedNormal,
                FootProbeDistance,
                selfBody,
                expectedNormal,
                0f);
        }

        // Two diagonal probes are needed at a convex edge, where the next face
        // is neither directly below the foot nor directly in front of the hip.
        AddProbe(hits, desiredPosition + expectedNormal * 0.30f, NormalizeOrFallback(-expectedNormal + forward * 0.95f, -expectedNormal), FootProbeDistance, selfBody, expectedNormal, 0.15f);
        AddProbe(hits, desiredPosition + expectedNormal * 0.30f, NormalizeOrFallback(-expectedNormal - forward * 0.70f, -expectedNormal), FootProbeDistance, selfBody, expectedNormal, 0.30f);

        // Clearance probes are especially important in a narrow corridor. A
        // ground candidate remains preferred when it is available, but a foot
        // blocked by a nearby side wall can now acquire that wall as support
        // instead of forcing the IK chain through it.
        AddProbe(hits, desiredPosition + expectedNormal * 0.22f, forward, 1.20f, selfBody, expectedNormal, 0.40f);
        AddProbe(hits, desiredPosition + expectedNormal * 0.22f, -forward, 1.00f, selfBody, expectedNormal, 0.55f);
        AddProbe(hits, desiredPosition + expectedNormal * 0.22f, right, 0.95f, selfBody, expectedNormal, 0.60f);
        AddProbe(hits, desiredPosition + expectedNormal * 0.22f, -right, 0.95f, selfBody, expectedNormal, 0.60f);

        ProbeHit? best = null;
        float bestScore = float.MaxValue;
        foreach (ProbeHit hit in hits)
        {
            float reach = Vector3.Distance(hipPosition, hit.Hit.Position);
            if (reach < minReach || reach > maxReach)
                continue;

            float normalPenalty = 1f - MathF.Max(0f, Vector3.Dot(hit.Hit.Normal, expectedNormal));
            // Keep this penalty in world units. Scaling it by maxReach² made a
            // large spider categorically ignore a valid nearby wall, even when
            // that was the only usable support in a cramped space.
            float normalWeight = MathF.Max(0.50f, Vector3.Distance(hipPosition, desiredPosition) * 0.35f);
            float score = Vector3.DistanceSquared(hit.Hit.Position, desiredPosition) +
                          normalPenalty * normalWeight +
                          hit.Priority * 0.20f;
            if (score < bestScore)
            {
                best = hit;
                bestScore = score;
            }
        }

        if (best == null)
        {
            contact = default;
            return false;
        }

        ProbeHit selected = best.Value;
        contact = CreateContact(selected.Hit, 1f / (1f + selected.Priority));
        _debugCandidates.Add(contact);
        return true;
    }

    private bool TryBuildStableContact(
        List<ProbeHit> hits,
        Vector3 bodyPosition,
        Vector3 preferredUp,
        out SpiderSurfaceContact contact)
    {
        ProbeHit? seed = null;
        float seedScore = float.MaxValue;
        foreach (ProbeHit hit in hits)
        {
            float score = hit.Hit.Distance + hit.Priority +
                          (1f - MathF.Max(-1f, Vector3.Dot(hit.Hit.Normal, preferredUp))) * 0.30f;
            if (score < seedScore)
            {
                seed = hit;
                seedScore = score;
            }
        }

        if (seed == null)
        {
            contact = default;
            return false;
        }

        ProbeHit root = seed.Value;
        Vector3 normalSum = Vector3.Zero;
        Vector3 pointSum = Vector3.Zero;
        float weightSum = 0f;
        int sampleCount = 0;

        foreach (ProbeHit hit in hits)
        {
            if (hit.Hit.RigidBody != root.Hit.RigidBody ||
                Vector3.Dot(hit.Hit.Normal, root.Hit.Normal) < ContactNormalSimilarity)
                continue;

            float weight = 1f / MathF.Max(0.05f, hit.Hit.Distance + hit.Priority + 0.05f);
            normalSum += hit.Hit.Normal * weight;
            pointSum += hit.Hit.Position * weight;
            weightSum += weight;
            sampleCount++;
        }

        if (weightSum <= Epsilon)
        {
            contact = default;
            return false;
        }

        Vector3 point = pointSum / weightSum;
        Vector3 normal = NormalizeOrFallback(normalSum, root.Hit.Normal);
        float confidence = MathF.Min(1f, sampleCount / 5f);
        contact = CreateContact(root.Hit, confidence, point, normal);
        _debugCandidates.Add(contact);
        return true;
    }

    private void AddProbe(
        List<ProbeHit> hits,
        Vector3 origin,
        Vector3 direction,
        float distance,
        BodyID selfBody,
        Vector3 expectedNormal,
        float priority)
    {
        direction = NormalizeOrFallback(direction, -expectedNormal);
        bool didHit = _scene.Raycast(origin, direction, distance, out SceneRaycastHit hit, selfBody);
        _debugProbes.Add(new DebugProbe(origin, didHit ? hit.Position : origin + direction * distance, didHit));
        if (!didHit)
            return;

        // Backfaces and ceilings relative to the ray are not usable contacts.
        if (Vector3.Dot(hit.Normal, -direction) < 0.10f)
            return;

        hits.Add(new ProbeHit(hit, priority));
    }

    private SpiderSurfaceContact CreateContact(
        in SceneRaycastHit hit,
        float confidence,
        Vector3? pointOverride = null,
        Vector3? normalOverride = null)
    {
        Vector3 point = pointOverride ?? hit.Position;
        Vector3 normal = NormalizeOrFallback(normalOverride ?? hit.Normal, Vector3.UnitY);
        RigidBody? supportBody = hit.RigidBody;
        Vector3 localPoint = point;
        Vector3 localNormal = normal;
        if (supportBody != null)
        {
            Quaternion inverse = Quaternion.Inverse(supportBody.Rotation(_scene.Physics));
            localPoint = Vector3.Transform(point - supportBody.Position(_scene.Physics), inverse);
            localNormal = Vector3.Transform(normal, inverse);
        }

        return new SpiderSurfaceContact(
            true,
            point,
            normal,
            hit.BodyID,
            supportBody,
            localPoint,
            localNormal,
            confidence);
    }

    private static void BuildTangentBasis(Vector3 normal, Vector3 desiredForward, out Vector3 forward, out Vector3 right)
    {
        normal = NormalizeOrFallback(normal, Vector3.UnitY);
        forward = desiredForward - normal * Vector3.Dot(desiredForward, normal);
        if (forward.LengthSquared() <= Epsilon * Epsilon)
        {
            Vector3 fallback = MathF.Abs(normal.Y) < 0.90f ? Vector3.UnitY : Vector3.UnitZ;
            forward = Vector3.Cross(fallback, normal);
        }

        forward = NormalizeOrFallback(forward, Vector3.UnitZ);
        right = NormalizeOrFallback(Vector3.Cross(forward, normal), Vector3.UnitX);
        forward = NormalizeOrFallback(Vector3.Cross(normal, right), forward);
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > Epsilon * Epsilon)
            return Vector3.Normalize(value);
        return Vector3.Normalize(fallback);
    }

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        foreach (DebugProbe probe in _debugProbes)
        {
            Vector3 color = probe.Hit ? new Vector3(0.15f, 0.85f, 1f) : new Vector3(0.9f, 0.15f, 0.15f);
            drawer.PushLine(probe.Start, probe.End, color);
        }

        foreach (SpiderSurfaceContact candidate in _debugCandidates)
        {
            if (!candidate.IsValid)
                continue;
            drawer.DrawSphere(candidate.Point, Quaternion.Identity, 0.05f, new Vector3(1f, 0.85f, 0.1f));
            drawer.PushLine(candidate.Point, candidate.Point + candidate.Normal * 0.35f, new Vector3(1f, 0.85f, 0.1f));
        }

        if (_lastBodyContact.IsValid)
        {
            drawer.DrawSphere(_lastBodyContact.Point, Quaternion.Identity, 0.10f, new Vector3(0.1f, 1f, 0.3f));
            drawer.PushLine(_lastBodyContact.Point, _lastBodyContact.Point + _lastBodyContact.Normal * 0.75f, new Vector3(0.1f, 1f, 0.3f));
        }
    }

    private readonly struct ProbeHit
    {
        public ProbeHit(SceneRaycastHit hit, float priority)
        {
            Hit = hit;
            Priority = priority;
        }

        public SceneRaycastHit Hit { get; }
        public float Priority { get; }
    }

    private readonly struct DebugProbe
    {
        public DebugProbe(Vector3 start, Vector3 end, bool hit)
        {
            Start = start;
            End = end;
            Hit = hit;
        }

        public Vector3 Start { get; }
        public Vector3 End { get; }
        public bool Hit { get; }
    }
}

public readonly struct SpiderSurfaceContact
{
    public SpiderSurfaceContact(
        bool isValid,
        Vector3 point,
        Vector3 normal,
        BodyID bodyId,
        RigidBody? supportBody,
        Vector3 localPoint,
        Vector3 localNormal,
        float confidence)
    {
        IsValid = isValid;
        Point = point;
        Normal = normal;
        BodyId = bodyId;
        SupportBody = supportBody;
        LocalPoint = localPoint;
        LocalNormal = localNormal;
        Confidence = confidence;
    }

    public bool IsValid { get; }
    public Vector3 Point { get; }
    public Vector3 Normal { get; }
    public BodyID BodyId { get; }
    public RigidBody? SupportBody { get; }
    public Vector3 LocalPoint { get; }
    public Vector3 LocalNormal { get; }
    public float Confidence { get; }

    public SpiderSurfaceContact WithWorldPose(Vector3 point, Vector3 normal) => new(
        IsValid,
        point,
        normal,
        BodyId,
        SupportBody,
        LocalPoint,
        LocalNormal,
        Confidence);
}
