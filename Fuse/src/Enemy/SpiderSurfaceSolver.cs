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
    private const float BodyProbeRadius = 0.48f;
    private const float FootProbeHeight = 1.35f;
    private const float FootProbeDistance = 4.5f;

    private readonly SceneManager _scene;
    private readonly List<DebugProbe> _debugProbes = new();
    private readonly List<SpiderSurfaceContact> _debugCandidates = new();
    private readonly HashSet<BodyID> _ignoredBodies = new();
    private SpiderSurfaceContact _lastBodyContact;

    public SpiderSurfaceContact LastBodyContact => _lastBodyContact;

    public SpiderSurfaceSolver(SceneManager scene)
    {
        _scene = scene;
        Debug.DebugDrawer.Register(this);
    }

    /// <summary>
    /// Registra corpos auxiliares da própria aranha que não podem ser
    /// interpretados como superfícies de suporte. As hitboxes articuladas de
    /// dano são corpos físicos separados da cápsula de locomoção.
    /// </summary>
    public void SetIgnoredBodies(IEnumerable<BodyID>? bodyIds)
    {
        _ignoredBodies.Clear();

        if (bodyIds == null)
            return;

        foreach (BodyID bodyId in bodyIds)
        {
            if (bodyId.IsValid)
                _ignoredBodies.Add(bodyId);
        }
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
    /// Samples only the currently expected support plane. Unlike the legacy
    /// six-axis fallback this query is expressed entirely in the spider's local
    /// surface frame, so +X/-X and +Z/-Z are genuinely equivalent.
    /// </summary>
    public bool TryFindSupportContact(
        Vector3 bodyCenter,
        Vector3 expectedNormal,
        Vector3 preferredForward,
        float clearance,
        BodyID selfBody,
        out SpiderSurfaceContact contact)
    {
        expectedNormal = NormalizeOrZero(expectedNormal);
        if (expectedNormal.LengthSquared() <= Epsilon * Epsilon ||
            !BuildTangentBasis(expectedNormal, preferredForward, out Vector3 forward, out Vector3 right))
        {
            contact = default;
            return false;
        }

        float patchRadius = MathF.Min(BodyProbeRadius, MathF.Max(0.18f, clearance * 0.42f));
        Vector3[] offsets =
        {
            Vector3.Zero,
            forward * patchRadius,
            -forward * patchRadius,
            right * patchRadius,
            -right * patchRadius
        };

        SpiderSurfaceContact best = default;
        float bestScore = float.MaxValue;
        foreach (Vector3 offset in offsets)
        {
            Vector3 origin = bodyCenter + expectedNormal * 0.18f + offset;
            if (!TryProbeContact(
                    origin,
                    -expectedNormal,
                    clearance + 0.85f,
                    selfBody,
                    out SpiderSurfaceContact candidate,
                    out float distance))
            {
                continue;
            }

            float alignment = Vector3.Dot(candidate.Normal, expectedNormal);
            if (alignment < 0.62f)
                continue;

            float score = distance + (1f - alignment) * 2f + offset.LengthSquared() * 0.08f;
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        contact = best;
        if (!contact.IsValid)
            return false;

        _lastBodyContact = contact;
        return true;
    }

    /// <summary>
    /// Searches for an adjacent face in the intended movement direction.
    /// A forward probe handles concave corners; a quarter-circle probe fan
    /// handles convex edges where the next wall is below and behind the body.
    /// </summary>
    public bool TryFindTransitionContact(
        Vector3 bodyCenter,
        Vector3 currentNormal,
        Vector3 movementDirection,
        float clearance,
        float lookAhead,
        BodyID selfBody,
        out SpiderSurfaceContact contact)
    {
        currentNormal = NormalizeOrZero(currentNormal);
        if (currentNormal.LengthSquared() <= Epsilon * Epsilon)
        {
            contact = default;
            return false;
        }

        movementDirection -= currentNormal * Vector3.Dot(movementDirection, currentNormal);
        if (movementDirection.LengthSquared() <= Epsilon * Epsilon)
        {
            contact = default;
            return false;
        }
        movementDirection = Vector3.Normalize(movementDirection);

        // Concave transition: the spider is walking directly into the next face.
        Vector3 forwardOrigin = bodyCenter + currentNormal * 0.10f;
        if (TryProbeContact(
                forwardOrigin,
                movementDirection,
                clearance + MathF.Max(0.12f, lookAhead),
                selfBody,
                out SpiderSurfaceContact forwardContact,
                out _) &&
            IsUsableTransition(forwardContact.Normal, currentNormal) &&
            Vector3.Dot(forwardContact.Normal, movementDirection) < -0.28f)
        {
            contact = forwardContact;
            _lastBodyContact = contact;
            return true;
        }

        // Convex transition: rotate the support ray from -up toward -forward.
        // This detects the outer face before the old support disappears fully.
        Vector3 fanOrigin = bodyCenter + currentNormal * 0.10f +
                            movementDirection * MathF.Max(0.08f, lookAhead * 0.45f);
        SpiderSurfaceContact best = default;
        float bestScore = float.MaxValue;
        for (int i = 0; i < 6; i++)
        {
            float angle = (20f + i * 13f) * (MathF.PI / 180f);
            Vector3 direction = NormalizeOrFallback(
                -currentNormal * MathF.Cos(angle) - movementDirection * MathF.Sin(angle),
                -currentNormal);

            if (!TryProbeContact(
                    fanOrigin,
                    direction,
                    clearance * 2.65f + MathF.Max(0.15f, lookAhead),
                    selfBody,
                    out SpiderSurfaceContact candidate,
                    out float distance))
            {
                continue;
            }

            if (!IsUsableTransition(candidate.Normal, currentNormal))
                continue;

            float forwardAlignment = Vector3.Dot(candidate.Normal, movementDirection);
            if (forwardAlignment < 0.20f)
                continue;

            float score = distance + (1f - forwardAlignment) * clearance;
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        contact = best;
        if (!contact.IsValid)
            return false;

        _lastBodyContact = contact;
        return true;
    }

    /// <summary>
    /// Acquires a nearby surface using a local frame. This is used only for
    /// spawn/recovery; normal walking stays locked to the current support or an
    /// explicitly detected adjacent face.
    /// </summary>
    public bool TryAcquireContact(
        Vector3 bodyCenter,
        Vector3 preferredNormal,
        Vector3 preferredForward,
        float searchDistance,
        BodyID selfBody,
        out SpiderSurfaceContact contact)
    {
        preferredNormal = NormalizeOrZero(preferredNormal);
        if (preferredNormal.LengthSquared() <= Epsilon * Epsilon ||
            !BuildTangentBasis(preferredNormal, preferredForward, out Vector3 forward, out Vector3 right))
        {
            contact = default;
            return false;
        }

        Vector3[] directions =
        {
            -preferredNormal,
            forward,
            -forward,
            right,
            -right,
            preferredNormal,
            NormalizeOrFallback(-preferredNormal + forward, -preferredNormal),
            NormalizeOrFallback(-preferredNormal - forward, -preferredNormal),
            NormalizeOrFallback(-preferredNormal + right, -preferredNormal),
            NormalizeOrFallback(-preferredNormal - right, -preferredNormal)
        };

        SpiderSurfaceContact best = default;
        float bestScore = float.MaxValue;
        foreach (Vector3 direction in directions)
        {
            if (!TryProbeContact(
                    bodyCenter,
                    direction,
                    searchDistance,
                    selfBody,
                    out SpiderSurfaceContact candidate,
                    out float distance))
            {
                continue;
            }

            float continuity = Vector3.Dot(candidate.Normal, preferredNormal);
            float score = distance + (1f - continuity) * 0.35f;
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        contact = best;
        if (!contact.IsValid)
            return false;

        _lastBodyContact = contact;
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
        expectedNormal = NormalizeOrZero(expectedNormal);
        if (expectedNormal.LengthSquared() <= Epsilon * Epsilon ||
            !BuildTangentBasis(expectedNormal, strideDirection, out Vector3 forward, out Vector3 right))
        {
            contact = default;
            return false;
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
        AddProbe(hits, desiredPosition + expectedNormal * 0.30f, NormalizeOrFallback(-expectedNormal - forward * 1.25f, -expectedNormal), FootProbeDistance, selfBody, expectedNormal, 0.30f);

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

    private bool TryProbeContact(
        Vector3 origin,
        Vector3 direction,
        float distance,
        BodyID selfBody,
        out SpiderSurfaceContact contact,
        out float hitDistance)
    {
        direction = NormalizeOrZero(direction);
        if (direction.LengthSquared() <= Epsilon * Epsilon)
        {
            contact = default;
            hitDistance = float.MaxValue;
            return false;
        }

        bool didHit = _scene.Raycast(
            origin,
            direction,
            distance,
            out SceneRaycastHit hit,
            selfBody,
            collideWithBackFaces: true,
            excludedBodies: _ignoredBodies);

        _debugProbes.Add(new DebugProbe(
            origin,
            didHit ? hit.Position : origin + direction * distance,
            didHit));

        if (!didHit || Vector3.Dot(hit.Normal, -direction) < 0.08f)
        {
            contact = default;
            hitDistance = float.MaxValue;
            return false;
        }

        contact = CreateContact(hit, 1f);
        hitDistance = hit.Distance;
        _debugCandidates.Add(contact);
        return true;
    }

    private static bool IsUsableTransition(Vector3 candidateNormal, Vector3 currentNormal)
    {
        candidateNormal = NormalizeOrZero(candidateNormal);
        currentNormal = NormalizeOrZero(currentNormal);
        if (candidateNormal.LengthSquared() <= Epsilon * Epsilon ||
            currentNormal.LengthSquared() <= Epsilon * Epsilon)
        {
            return false;
        }

        float alignment = Vector3.Dot(candidateNormal, currentNormal);

        // Ignore the same face and the exact opposite face. Adjacent faces and
        // smooth slopes remain valid, including transitions slightly over 90°.
        return alignment < 0.82f && alignment > -0.42f;
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
        direction = NormalizeOrZero(direction);
        if (direction.LengthSquared() <= Epsilon * Epsilon)
            return;

        bool didHit = _scene.Raycast(
            origin,
            direction,
            distance,
            out SceneRaycastHit hit,
            selfBody,
            collideWithBackFaces: true,
            excludedBodies: _ignoredBodies);
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
        Vector3 normal = NormalizeOrZero(normalOverride ?? hit.Normal);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
            return default;

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

    private static bool BuildTangentBasis(Vector3 normal, Vector3 desiredForward, out Vector3 forward, out Vector3 right)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
        {
            forward = Vector3.Zero;
            right = Vector3.Zero;
            return false;
        }

        forward = desiredForward - normal * Vector3.Dot(desiredForward, normal);
        forward = NormalizeOrZero(forward);
        if (forward.LengthSquared() <= Epsilon * Epsilon)
            forward = BuildFallbackTangent(normal);
        if (forward.LengthSquared() <= Epsilon * Epsilon)
        {
            right = Vector3.Zero;
            return false;
        }

        right = NormalizeOrZero(Vector3.Cross(forward, normal));
        if (right.LengthSquared() <= Epsilon * Epsilon)
            return false;

        forward = NormalizeOrZero(Vector3.Cross(normal, right));
        return forward.LengthSquared() > Epsilon * Epsilon;
    }

    private static Vector3 BuildFallbackTangent(Vector3 normal)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
            return Vector3.Zero;

        Vector3 reference = Vector3.UnitX;
        float smallestAlignment = MathF.Abs(Vector3.Dot(normal, reference));
        Vector3[] basisCandidates = { Vector3.UnitY, Vector3.UnitZ };
        foreach (Vector3 candidate in basisCandidates)
        {
            float alignment = MathF.Abs(Vector3.Dot(normal, candidate));
            if (alignment >= smallestAlignment)
                continue;

            reference = candidate;
            smallestAlignment = alignment;
        }

        return NormalizeOrZero(Vector3.Cross(normal, reference));
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        Vector3 normalizedValue = NormalizeOrZero(value);
        if (normalizedValue.LengthSquared() > Epsilon * Epsilon)
            return normalizedValue;
        return NormalizeOrZero(fallback);
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        return value.LengthSquared() > Epsilon * Epsilon
            ? Vector3.Normalize(value)
            : Vector3.Zero;
    }

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        //foreach (DebugProbe probe in _debugProbes)
        //{
        //    Vector3 color = probe.Hit ? new Vector3(0.15f, 0.85f, 1f) : new Vector3(0.9f, 0.15f, 0.15f);
        //    drawer.PushLine(probe.Start, probe.End, color);
        //}

        //foreach (SpiderSurfaceContact candidate in _debugCandidates)
        //{
        //    if (!candidate.IsValid)
        //        continue;
        //    drawer.DrawSphere(candidate.Point, Quaternion.Identity, 0.05f, new Vector3(1f, 0.85f, 0.1f));
        //    drawer.PushLine(candidate.Point, candidate.Point + candidate.Normal * 0.35f, new Vector3(1f, 0.85f, 0.1f));
        //}

        //if (_lastBodyContact.IsValid)
        //{
        //    drawer.DrawSphere(_lastBodyContact.Point, Quaternion.Identity, 0.10f, new Vector3(0.1f, 1f, 0.3f));
        //    drawer.PushLine(_lastBodyContact.Point, _lastBodyContact.Point + _lastBodyContact.Normal * 0.75f, new Vector3(0.1f, 1f, 0.3f));
        //}
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
