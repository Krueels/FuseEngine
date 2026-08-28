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
    private const float ActiveContactSwitchAdvantage = 0.20f;

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
    /// Finds the body support from a surface-relative probe fan. There is no
    /// world-down or cardinal-axis priority: an existing contact is deliberately
    /// favoured until another surface is demonstrably better.
    /// </summary>
    public bool TryFindBodyContact(
        Vector3 bodyPosition,
        Vector3 preferredUp,
        Vector3 preferredForward,
        in SpiderSurfaceContact activeContact,
        BodyID selfBody,
        out SpiderSurfaceContact contact)
    {
        BeginFrame();
        preferredUp = NormalizeOrFallback(preferredUp, Vector3.UnitY);
        BuildTangentBasis(preferredUp, preferredForward, out Vector3 forward, out Vector3 right);

        var hits = new List<ProbeHit>(14);
        Vector3 probeStart = bodyPosition + preferredUp * (BodyProbeHeight + 0.45f);
        AddProbe(hits, probeStart, -preferredUp, BodyProbeDistance + BodyProbeHeight + 0.45f, selfBody, preferredUp, 0f);

        // Sample a small patch around the contact axis. These probes make an
        // edge robust without changing preference from one world axis to another.
        Vector3[] patchOffsets =
        {
            forward * BodyProbeRadius,
            -forward * BodyProbeRadius,
            right * BodyProbeRadius,
            -right * BodyProbeRadius,
            (forward + right) * (BodyProbeRadius * 0.70f),
            (forward - right) * (BodyProbeRadius * 0.70f)
        };
        foreach (Vector3 offset in patchOffsets)
        {
            AddProbe(hits, probeStart + offset, -preferredUp, BodyProbeDistance + BodyProbeHeight + 0.45f, selfBody, preferredUp, 0.08f);
        }

        // Directional fallbacks are only candidates, never a fixed priority.
        // They allow an initial attachment or a convex-edge transition when the
        // surface-relative patch has legitimately disappeared.
        Vector3[] directions =
        {
            Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY,
            -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ
        };
        foreach (Vector3 direction in directions)
            AddProbe(hits, bodyPosition, direction, BodyProbeDistance, selfBody, preferredUp, 0.80f);

        if (!TrySelectBodyContact(hits, preferredUp, activeContact, out contact))
            return false;

        _lastBodyContact = contact;
        return true;
    }

    /// <summary>
    /// Projects a point onto the currently active surface. Patrol uses this for
    /// destinations, so walking on a wall never falls back to a world-down scan.
    /// </summary>
    public bool TryProjectToSurface(
        Vector3 bodyCenter,
        Vector3 expectedNormal,
        BodyID selfBody,
        out SpiderSurfaceContact contact)
    {
        expectedNormal = NormalizeOrFallback(expectedNormal, Vector3.UnitY);
        Vector3 origin = bodyCenter + expectedNormal * (BodyProbeHeight + 0.45f);
        float distance = BodyProbeDistance + BodyProbeHeight + 0.45f;
        bool didHit = _scene.Raycast(
            origin,
            -expectedNormal,
            distance,
            out SceneRaycastHit hit,
            selfBody,
            collideWithBackFaces: true);
        _debugProbes.Add(new DebugProbe(origin, didHit ? hit.Position : origin - expectedNormal * distance, didHit));

        if (!didHit || Vector3.Dot(hit.Normal, expectedNormal) < ContactNormalSimilarity)
        {
            contact = default;
            return false;
        }

        contact = CreateContact(hit, 1f);
        _debugCandidates.Add(contact);
        return true;
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

            float normalPenalty = 1f - MathF.Max(-1f, Vector3.Dot(hit.Hit.Normal, expectedNormal));
            // Feet follow the same surface anchor as the body. A different
            // normal remains possible at an edge, but can no longer beat the
            // active surface merely because the world floor is nearby.
            float normalWeight = MathF.Max(1.50f, maxReach * 0.65f);
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

    private bool TrySelectBodyContact(
        List<ProbeHit> hits,
        Vector3 preferredUp,
        in SpiderSurfaceContact activeContact,
        out SpiderSurfaceContact contact)
    {
        ProbeHit? best = null;
        float bestScore = float.MaxValue;
        foreach (ProbeHit hit in hits)
        {
            float normalAlignment = MathF.Max(-1f, Vector3.Dot(hit.Hit.Normal, preferredUp));
            float score = hit.Priority +
                          (1f - normalAlignment) * 1.75f +
                          hit.Hit.Distance * 0.02f;

            if (activeContact.IsValid)
            {
                float activeAlignment = MathF.Max(-1f, Vector3.Dot(hit.Hit.Normal, activeContact.Normal));
                score += (1f - activeAlignment) * 1.60f;
                if (hit.Hit.BodyID == activeContact.BodyId)
                    score -= ActiveContactSwitchAdvantage;
            }

            _debugCandidates.Add(CreateContact(hit.Hit, 1f / (1f + MathF.Max(0f, score))));
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

        ProbeHit root = best.Value;
        Vector3 normalSum = Vector3.Zero;
        Vector3 pointSum = Vector3.Zero;
        float weightSum = 0f;
        int sampleCount = 0;

        foreach (ProbeHit hit in hits)
        {
            if (hit.Hit.BodyID != root.Hit.BodyID ||
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
        bool didHit = _scene.Raycast(
            origin,
            direction,
            distance,
            out SceneRaycastHit hit,
            selfBody,
            collideWithBackFaces: true);
        _debugProbes.Add(new DebugProbe(origin, didHit ? hit.Position : origin + direction * distance, didHit));
        if (!didHit)
            return;

        // The resolved normal must face the probe even when the underlying
        // triangle was reached through its back face.
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
            forward = Vector3.UnitZ - normal * Vector3.Dot(Vector3.UnitZ, normal);
            if (forward.LengthSquared() <= Epsilon * Epsilon)
                forward = Vector3.UnitX - normal * Vector3.Dot(Vector3.UnitX, normal);
        }

        forward = NormalizeOrFallback(forward, Vector3.UnitZ);
        right = NormalizeOrFallback(Vector3.Cross(normal, forward), Vector3.UnitX);
        forward = NormalizeOrFallback(Vector3.Cross(right, normal), forward);
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
