using System.Numerics;
using Fuse.Debug;
using Fuse.Renderer;
using Fuse.Scene.Model;

namespace Fuse.Physics;

/// <summary>
/// Water state sampled for the CharacterVirtual player controller. The
/// character is not a Jolt dynamic body, so it needs this small explicit
/// contract to apply swimming and sinking behaviour.
/// </summary>
public readonly struct OceanPlayerWaterState
{
    public OceanPlayerWaterState(
        bool isInWater,
        float surfaceHeight,
        float submersion,
        Vector3 surfaceNormal,
        Vector3 surfaceVelocity)
    {
        IsInWater = isInWater;
        SurfaceHeight = surfaceHeight;
        Submersion = submersion;
        SurfaceNormal = surfaceNormal;
        SurfaceVelocity = surfaceVelocity;
    }

    public bool IsInWater { get; }
    public float SurfaceHeight { get; }
    public float Submersion { get; }
    public Vector3 SurfaceNormal { get; }
    public Vector3 SurfaceVelocity { get; }
}

/// <summary>
/// Applies Archimedean buoyancy to dynamic scene bodies and supplies the
/// player water state. A dynamic collider is clipped against the local tangent
/// plane of the shared ocean height field. This gives a continuous submerged
/// volume and a real center of buoyancy instead of a collection of independent
/// point/sphere probes.
/// </summary>
public sealed class OceanPhysicsSystem
{
    private const float GeometryEpsilon = 0.00001f;
    private const float MinimumVolume = 0.0001f;

    private readonly PhysicsWorld _physics;
    private readonly Renderer.Scene _scene;
    private readonly MasterRenderer _renderer;

    private readonly Dictionary<RigidBody, CachedConvexGeometry> _geometryCache = [];
    private readonly List<BuoyancyDebugState> _debugBodies = [];
    private readonly List<Vector3> _clippedFace = new(8);
    private readonly List<Vector3> _capPoints = new(64);
    private readonly List<CapPoint> _orderedCapPoints = new(64);
    private Vector3[] _worldVerticesScratch = [];
    private int _cachePruneCountdown;

    /// <summary>
    /// Enables collection of submerged-volume data for the F9 diagnostic
    /// drawer. It is disabled during normal play so debug geometry has no cost.
    /// </summary>
    public bool DebugEnabled { get; set; }

    private sealed class CachedConvexGeometry
    {
        public CachedConvexGeometry(
            RigidBody body,
            Vector3[] vertices,
            uint[] indices,
            float volume,
            Vector3 localCentroid)
        {
            ShapeType = body.Type;
            BoxHalfExtents = body.BoxHalfExtents;
            SphereRadius = body.SphereRadius;
            CapsuleRadius = body.CapsuleRadius;
            CapsuleHeight = body.CapsuleHeight;
            SourceVertices = body.TrimeshVertices;
            SourceIndices = body.TrimeshIndices;
            SourceScale = body.TrimeshScale;
            Vertices = vertices;
            Indices = indices;
            Volume = volume;
            LocalCentroid = localCentroid;
        }

        public RigidBody.ShapeType ShapeType { get; }
        public Vector3 BoxHalfExtents { get; }
        public float SphereRadius { get; }
        public float CapsuleRadius { get; }
        public float CapsuleHeight { get; }
        public Vector3[]? SourceVertices { get; }
        public uint[]? SourceIndices { get; }
        public Vector3 SourceScale { get; }
        public Vector3[] Vertices { get; }
        public uint[] Indices { get; }
        public float Volume { get; }
        public Vector3 LocalCentroid { get; }

        public bool Matches(RigidBody body)
        {
            if (ShapeType != body.Type)
                return false;

            switch (ShapeType)
            {
                case RigidBody.ShapeType.Box:
                    return VectorNearlyEqual(BoxHalfExtents, body.BoxHalfExtents);

                case RigidBody.ShapeType.Capsule:
                    return NearlyEqual(CapsuleRadius, body.CapsuleRadius) &&
                           NearlyEqual(CapsuleHeight, body.CapsuleHeight);

                case RigidBody.ShapeType.Trimesh:
                case RigidBody.ShapeType.ConvexHull:
                    return ReferenceEquals(SourceVertices, body.TrimeshVertices) &&
                           ReferenceEquals(SourceIndices, body.TrimeshIndices) &&
                           VectorNearlyEqual(SourceScale, body.TrimeshScale);

                default:
                    return false;
            }
        }
    }

    private readonly struct SubmergedVolumeResult
    {
        public SubmergedVolumeResult(
            float geometricVolume,
            float fraction,
            Vector3 centerOfBuoyancy,
            float projectedArea,
            bool isSubmerged)
        {
            GeometricVolume = geometricVolume;
            Fraction = fraction;
            CenterOfBuoyancy = centerOfBuoyancy;
            ProjectedArea = projectedArea;
            IsSubmerged = isSubmerged;
        }

        public float GeometricVolume { get; }
        public float Fraction { get; }
        public Vector3 CenterOfBuoyancy { get; }
        public float ProjectedArea { get; }
        public bool IsSubmerged { get; }
    }

    private readonly struct WaterForceSample
    {
        public WaterForceSample(
            Vector3 centerOfBuoyancy,
            Vector3 surfacePoint,
            Vector3 surfaceNormal,
            Vector3 buoyancyForce,
            Vector3 linearDragForce,
            Vector3 angularDrag,
            float displacedVolume,
            float fraction,
            bool hasSurface)
        {
            CenterOfBuoyancy = centerOfBuoyancy;
            SurfacePoint = surfacePoint;
            SurfaceNormal = surfaceNormal;
            BuoyancyForce = buoyancyForce;
            LinearDragForce = linearDragForce;
            AngularDrag = angularDrag;
            DisplacedVolume = displacedVolume;
            Fraction = fraction;
            HasSurface = hasSurface;
        }

        public Vector3 CenterOfBuoyancy { get; }
        public Vector3 SurfacePoint { get; }
        public Vector3 SurfaceNormal { get; }
        public Vector3 BuoyancyForce { get; }
        public Vector3 LinearDragForce { get; }
        public Vector3 TotalForce => BuoyancyForce + LinearDragForce;
        public Vector3 AngularDrag { get; }
        public float DisplacedVolume { get; }
        public float Fraction { get; }
        public bool HasSurface { get; }
    }

    private readonly struct BuoyancyDebugState
    {
        public BuoyancyDebugState(
            Vector3 centerOfMass,
            Vector3 centerOfBuoyancy,
            Vector3 surfacePoint,
            Vector3 surfaceNormal,
            Vector3 buoyancyForce,
            Vector3 linearDragForce,
            float totalVolume,
            float submergedVolume,
            float fraction,
            float mass,
            bool hasSurface)
        {
            CenterOfMass = centerOfMass;
            CenterOfBuoyancy = centerOfBuoyancy;
            SurfacePoint = surfacePoint;
            SurfaceNormal = surfaceNormal;
            BuoyancyForce = buoyancyForce;
            LinearDragForce = linearDragForce;
            TotalVolume = totalVolume;
            SubmergedVolume = submergedVolume;
            Fraction = fraction;
            Mass = mass;
            HasSurface = hasSurface;
        }

        public Vector3 CenterOfMass { get; }
        public Vector3 CenterOfBuoyancy { get; }
        public Vector3 SurfacePoint { get; }
        public Vector3 SurfaceNormal { get; }
        public Vector3 BuoyancyForce { get; }
        public Vector3 LinearDragForce { get; }
        public float TotalVolume { get; }
        public float SubmergedVolume { get; }
        public float Fraction { get; }
        public float Mass { get; }
        public bool HasSurface { get; }
    }

    private struct CapPoint
    {
        public Vector3 Position;
        public float Angle;
    }

    public OceanPhysicsSystem(
        PhysicsWorld physics,
        Renderer.Scene scene,
        MasterRenderer renderer)
    {
        _physics = physics;
        _scene = scene;
        _renderer = renderer;
    }

    /// <summary>
    /// Runs deterministic, renderer-independent checks for the exact box
    /// clipping used by buoyancy. This is callable from diagnostics without a
    /// GL context and covers the density equilibria required by PLAN.md plus a
    /// rotated 4x1x1 hull.
    /// </summary>
    public static bool ValidateDeterministicHydrostatics(out string failure)
    {
        failure = string.Empty;
        var validator = new OceanPhysicsSystem(null!, null!, null!);
        var unitBoxBody = new RigidBody().SetBox(new Vector3(0.5f));
        CachedConvexGeometry unitBox = BuildBoxGeometry(unitBoxBody);
        validator.EnsureWorldVertexCapacity(unitBox.Vertices.Length);

        float[] expectedFractions = [0.25f, 0.5f, 0.9f, 1.0f];
        foreach (float expected in expectedFractions)
        {
            Vector3 position = new(0.0f, 0.5f - expected, 0.0f);
            TransformVertices(
                unitBox.Vertices,
                validator._worldVerticesScratch,
                position,
                Quaternion.Identity);
            SubmergedVolumeResult result =
                validator.CalculateConvexSubmergedVolume(
                    unitBox,
                    validator._worldVerticesScratch,
                    position,
                    Quaternion.Identity,
                    Vector3.Zero,
                    Vector3.UnitY,
                    Vector3.Zero);

            if (!result.IsSubmerged ||
                MathF.Abs(result.Fraction - expected) > 0.0005f)
            {
                failure = $"unit cube expected {expected:P0} submerged, got {result.Fraction:P3}";
                return false;
            }

            float mass = expected * 1000.0f;
            float weight = mass * 9.81f;
            float buoyancy = 1000.0f * 9.81f * result.GeometricVolume;
            if (MathF.Abs(buoyancy - weight) > MathF.Max(0.01f, weight * 0.001f))
            {
                failure = $"unit cube force mismatch at mass {mass:F0} kg";
                return false;
            }
        }

        const float heavyMass = 2000.0f;
        float maximumBuoyancy = 1000.0f * 9.81f * unitBox.Volume;
        if (maximumBuoyancy >= heavyMass * 9.81f)
        {
            failure = "2000 kg unit cube did not remain heavier than maximum buoyancy";
            return false;
        }

        var longBoxBody = new RigidBody().SetBox(new Vector3(2.0f, 0.5f, 0.5f));
        CachedConvexGeometry longBox = BuildBoxGeometry(longBoxBody);
        validator.EnsureWorldVertexCapacity(longBox.Vertices.Length);
        Quaternion tilted = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            float.DegreesToRadians(30.0f));
        TransformVertices(
            longBox.Vertices,
            validator._worldVerticesScratch,
            Vector3.Zero,
            tilted);
        SubmergedVolumeResult rotatedResult =
            validator.CalculateConvexSubmergedVolume(
                longBox,
                validator._worldVerticesScratch,
                Vector3.Zero,
                tilted,
                Vector3.Zero,
                Vector3.UnitY,
                Vector3.Zero);
        if (!rotatedResult.IsSubmerged ||
            MathF.Abs(rotatedResult.Fraction - 0.5f) > 0.0005f ||
            MathF.Abs(rotatedResult.CenterOfBuoyancy.X) <= 0.01f ||
            rotatedResult.CenterOfBuoyancy.Y >= -0.01f)
        {
            failure = "rotated 4x1x1 box produced an invalid displaced centroid";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies hydrostatic and hydrodynamic forces during one fixed physics
    /// step. Buoyancy is exactly -gravity * waterDensity * displacedVolume;
    /// BuoyancyStrength is intentionally not part of this equation. It is a
    /// legacy serialized setting kept for compatibility with old maps.
    /// </summary>
    public void ApplyBuoyancy(float deltaTime, float simulationTime)
    {
        _debugBodies.Clear();

        if (!float.IsFinite(deltaTime) || deltaTime <= 0.0f)
            return;

        OceanSettings settings = _renderer.Ocean;
        if (!settings.Enabled || !settings.PhysicsEnabled)
            return;

        Vector3 gravity = _physics.Gravity;
        float gravityMagnitude = gravity.Length();
        if (!IsFinite(gravity) || !float.IsFinite(gravityMagnitude) ||
            gravityMagnitude < GeometryEpsilon)
        {
            return;
        }

        float waterDensity = float.IsFinite(settings.WaterDensity)
            ? MathF.Max(settings.WaterDensity, 0.0f)
            : 0.0f;
        if (waterDensity <= 0.0f)
            return;

        float waveEnvelope = MathF.Abs(settings.WaveAmplitude) * 2.8f + 0.5f;
        bool collectDebug = DebugEnabled;
        PruneGeometryCache();

        foreach (Entity entity in _scene.Entities)
        {
            RigidBody? body = entity.Body;
            if (body == null ||
                !body.IsBuilt ||
                !body.IsDynamic ||
                body.IsKinematic ||
                body.IsTrigger ||
                body.Mass <= 0.0f)
            {
                continue;
            }

            float buoyancyVolume = body.BuoyancyVolume;
            if (!float.IsFinite(buoyancyVolume) || buoyancyVolume <= MinimumVolume)
                continue;

            Vector3 bodyPosition = body.Position(_physics);
            Quaternion bodyRotation = body.Rotation(_physics);
            Vector3 halfExtents = body.BuoyancyHalfExtents;
            if (!IsFinite(bodyPosition) ||
                !IsFinite(halfExtents) ||
                !IsFinite(bodyRotation))
            {
                continue;
            }

            float bodyRadius = MathF.Max(halfExtents.Length(), 0.05f);
            Vector3 linearVelocity = body.LinearVelocity(_physics);
            Vector3 angularVelocity = body.AngularVelocity(_physics);
            if (!IsFinite(linearVelocity) || !IsFinite(angularVelocity))
                continue;

            // Water is a fluid half-space, not a collision surface. Only skip
            // bodies that are certainly above the highest possible wave. A
            // body below the wave envelope must keep receiving buoyancy even
            // when it is deeply submerged.
            if (bodyPosition.Y - bodyRadius >
                settings.WaterLevel + waveEnvelope)
            {
                continue;
            }

            CachedConvexGeometry? geometry = body.Type == RigidBody.ShapeType.Sphere
                ? null
                : GetConvexGeometry(body);
            if (body.Type != RigidBody.ShapeType.Sphere &&
                (geometry == null || geometry.Volume <= MinimumVolume))
            {
                continue;
            }

            // Evaluate forces from the collider's current fixed-step pose.
            // Sampling predicted poses applies resistance before the body has
            // actually entered the water and makes the interface behave like
            // a solid constraint instead of allowing natural penetration.
            if (!TryCalculateWaterForceSample(
                    body,
                    geometry,
                    buoyancyVolume,
                    bodyPosition,
                    bodyRotation,
                    linearVelocity,
                    angularVelocity,
                    gravity,
                    waterDensity,
                    settings,
                    waveEnvelope,
                    simulationTime,
                    deltaTime,
                    out WaterForceSample waterSample))
            {
                continue;
            }

            if (!IsFinite(waterSample.TotalForce) ||
                !IsFinite(waterSample.AngularDrag))
            {
                continue;
            }

            // Hydrostatic force acts at the center of displaced volume and is
            // the sole source of righting torque. An aggregate drag sample has
            // no trustworthy center of pressure; applying it at the center of
            // buoyancy created a second, very stiff false righting torque.
            body.ApplyForceAtPosition(
                _physics,
                waterSample.BuoyancyForce,
                waterSample.CenterOfBuoyancy);
            if (waterSample.LinearDragForce.LengthSquared() >
                GeometryEpsilon * GeometryEpsilon)
            {
                body.ApplyCentralForce(_physics, waterSample.LinearDragForce);
            }
            if (waterSample.AngularDrag.LengthSquared() >
                GeometryEpsilon * GeometryEpsilon)
            {
                body.ApplyTorque(_physics, waterSample.AngularDrag);
            }

            if (collectDebug)
            {
                _debugBodies.Add(new BuoyancyDebugState(
                    body.CenterOfMassPosition(_physics),
                    waterSample.CenterOfBuoyancy,
                    waterSample.SurfacePoint,
                    NormalizeSurfaceNormal(waterSample.SurfaceNormal),
                    waterSample.BuoyancyForce,
                    waterSample.LinearDragForce,
                    buoyancyVolume,
                    waterSample.DisplacedVolume,
                    waterSample.Fraction,
                    body.Mass,
                    waterSample.HasSurface));
            }
        }
    }

    private bool TryCalculateWaterForceSample(
        RigidBody body,
        CachedConvexGeometry? geometry,
        float buoyancyVolume,
        Vector3 bodyPosition,
        Quaternion bodyRotation,
        Vector3 linearVelocity,
        Vector3 angularVelocity,
        Vector3 gravity,
        float waterDensity,
        OceanSettings settings,
        float waveEnvelope,
        float simulationTime,
        float deltaTime,
        out WaterForceSample sample)
    {
        sample = default;

        Vector3 halfExtents = body.BuoyancyHalfExtents;
        float bodyRadius = MathF.Max(halfExtents.Length(), 0.05f);
        if (!IsFinite(bodyPosition) || !IsFinite(bodyRotation) ||
            bodyPosition.Y - bodyRadius > settings.WaterLevel + waveEnvelope)
        {
            return false;
        }

        Vector3 waterVelocity = Vector3.Zero;
        Vector3 surfacePoint = bodyPosition;
        Vector3 surfaceNormal = Vector3.UnitY;
        bool hasSurface = false;
        SubmergedVolumeResult submerged;

        if (body.Type == RigidBody.ShapeType.Sphere)
        {
            float radius = MathF.Max(MathF.Abs(body.SphereRadius), 0.01f);
            bool fullySubmerged = bodyPosition.Y + radius <
                settings.WaterLevel - waveEnvelope;

            if (fullySubmerged)
            {
                submerged = CreateFullSphereResult(
                    bodyPosition,
                    radius,
                    linearVelocity);
            }
            else
            {
                if (!TrySampleRenderedSurface(
                        new Vector2(bodyPosition.X, bodyPosition.Z),
                        simulationTime,
                        out OceanSurfaceSample surface))
                {
                    return false;
                }

                waterVelocity = surface.Velocity;
                surfaceNormal = NormalizeSurfaceNormal(surface.Normal);
                surfacePoint = new Vector3(
                    bodyPosition.X,
                    surface.Height,
                    bodyPosition.Z);
                Vector3 relativeLinearVelocity = linearVelocity - waterVelocity;
                submerged = CalculateSphereSubmergedVolume(
                    bodyPosition,
                    radius,
                    surfacePoint,
                    surfaceNormal,
                    relativeLinearVelocity);
                hasSurface = true;
            }
        }
        else
        {
            if (geometry == null || geometry.Volume <= MinimumVolume)
                return false;

            EnsureWorldVertexCapacity(geometry.Vertices.Length);
            TransformVertices(
                geometry.Vertices,
                _worldVerticesScratch,
                bodyPosition,
                bodyRotation);

            bool fullySubmerged = bodyPosition.Y + bodyRadius <
                settings.WaterLevel - waveEnvelope;
            if (fullySubmerged)
            {
                submerged = CreateFullConvexResult(
                    geometry,
                    _worldVerticesScratch,
                    bodyPosition,
                    bodyRotation,
                    linearVelocity);
            }
            else
            {
                if (!TrySampleRenderedSurface(
                        new Vector2(bodyPosition.X, bodyPosition.Z),
                        simulationTime,
                        out OceanSurfaceSample surface))
                {
                    return false;
                }

                waterVelocity = surface.Velocity;
                surfaceNormal = NormalizeSurfaceNormal(surface.Normal);
                surfacePoint = new Vector3(
                    bodyPosition.X,
                    surface.Height,
                    bodyPosition.Z);
                Vector3 relativeLinearVelocity = linearVelocity - waterVelocity;
                submerged = CalculateConvexSubmergedVolume(
                    geometry,
                    _worldVerticesScratch,
                    bodyPosition,
                    bodyRotation,
                    surfacePoint,
                    surfaceNormal,
                    relativeLinearVelocity);
                hasSurface = true;
            }
        }

        if (!submerged.IsSubmerged ||
            !float.IsFinite(submerged.GeometricVolume) ||
            submerged.GeometricVolume <= GeometryEpsilon)
        {
            return false;
        }

        float fraction = System.Math.Clamp(
            submerged.Fraction,
            0.0f,
            1.0f);
        float displacedVolume = buoyancyVolume * fraction;
        Vector3 centerOfBuoyancy = submerged.CenterOfBuoyancy;
        if (!float.IsFinite(displacedVolume) ||
            displacedVolume <= GeometryEpsilon ||
            !IsFinite(centerOfBuoyancy))
        {
            return false;
        }

        // Archimedes' principle. No depth or arbitrary buoyancy multiplier
        // participates in this equation.
        Vector3 buoyancyForce = -gravity * waterDensity * displacedVolume;
        // The aggregate linear drag represents translation of the displaced
        // volume. Rotation is handled independently below using the body's
        // actual inertia; mixing point velocity into this force double-counted
        // angular motion and could alternate the torque sign each tick.
        Vector3 relativeVelocity = linearVelocity - waterVelocity;

        float characteristicLength = MathF.Max(bodyRadius, 0.05f);
        float projectedArea = submerged.ProjectedArea;
        if (geometry != null)
        {
            float fullProjectedArea = CalculateProjectedArea(
                geometry,
                _worldVerticesScratch,
                NormalizeOrZero(relativeVelocity));
            projectedArea = LimitFreeSurfaceDragArea(
                projectedArea,
                fullProjectedArea,
                fraction);
        }

        if (!float.IsFinite(projectedArea) || projectedArea <= GeometryEpsilon)
        {
            // A body rotating in place can have zero linear flow. The
            // volume/length estimate still gives angular drag a physical
            // cross-section without changing the hydrostatic force.
            projectedArea = displacedVolume / characteristicLength;
        }

        Vector3 dragForce = CalculateQuadraticDrag(
            relativeVelocity,
            projectedArea,
            waterDensity,
            settings.WaterLinearDrag,
            body.Mass,
            deltaTime,
            gravity + buoyancyForce / body.Mass);
        Vector3 angularDrag = CalculateAngularDrag(
            angularVelocity,
            waterDensity,
            settings.WaterAngularDrag,
            projectedArea,
            characteristicLength,
            fraction,
            body.InverseInertia(_physics),
            deltaTime);

        if (!IsFinite(buoyancyForce) ||
            !IsFinite(dragForce) ||
            !IsFinite(angularDrag))
        {
            return false;
        }

        sample = new WaterForceSample(
            centerOfBuoyancy,
            surfacePoint,
            surfaceNormal,
            buoyancyForce,
            dragForce,
            angularDrag,
            displacedVolume,
            fraction,
            hasSurface);
        return true;
    }

    /// <summary>
    /// Draws the volume/centroid data from the latest fixed physics tick.
    /// Red is the solver body position, cyan is the center of buoyancy, yellow
    /// is linear drag, blue is gravity and green is the water tangent.
    /// </summary>
    public void DrawDebug(DebugDrawer debugDrawer)
    {
        if (!debugDrawer.Enabled)
            return;

        foreach (BuoyancyDebugState body in _debugBodies)
        {
            debugDrawer.DrawSphere(
                body.CenterOfMass,
                Quaternion.Identity,
                0.06f,
                new Vector3(1.0f, 0.15f, 0.15f),
                rings: 6,
                sectors: 10);
            debugDrawer.DrawSphere(
                body.CenterOfBuoyancy,
                Quaternion.Identity,
                0.08f,
                new Vector3(0.1f, 0.9f, 1.0f),
                rings: 6,
                sectors: 10);

            debugDrawer.PushLine(
                body.CenterOfMass,
                body.CenterOfBuoyancy,
                new Vector3(1.0f, 0.45f, 0.05f));

            if (body.HasSurface)
            {
                debugDrawer.PushLine(
                    body.SurfacePoint,
                    body.SurfacePoint + body.SurfaceNormal * 1.5f,
                    new Vector3(0.1f, 1.0f, 0.35f));
            }

            DrawForceArrow(
                debugDrawer,
                body.CenterOfBuoyancy,
                body.BuoyancyForce,
                0.004f,
                new Vector3(0.25f, 0.95f, 1.0f));
            DrawForceArrow(
                debugDrawer,
                body.CenterOfMass,
                -_physics.Gravity * body.Mass,
                0.004f,
                new Vector3(0.25f, 0.45f, 1.0f));
            DrawForceArrow(
                debugDrawer,
                body.CenterOfMass,
                body.LinearDragForce,
                0.004f,
                new Vector3(1.0f, 0.25f, 0.9f));
        }
    }

    /// <summary>
    /// Samples the wave at the character's horizontal position and estimates
    /// how much of its capsule is below the same displaced surface used by
    /// buoyancy. The character controller remains intentionally separate from
    /// the dynamic-body solver.
    /// </summary>
    public OceanPlayerWaterState SamplePlayerWater(
        Vector3 characterPosition,
        float capsuleRadius,
        float capsuleCylinderHeight,
        float simulationTime)
    {
        OceanSettings settings = _renderer.Ocean;
        if (!settings.Enabled || !settings.PhysicsEnabled)
            return default;

        if (!TrySampleRenderedSurface(
                new Vector2(characterPosition.X, characterPosition.Z),
                simulationTime,
                out OceanSurfaceSample surface))
        {
            return default;
        }

        float halfHeight = MathF.Max(
            MathF.Abs(capsuleCylinderHeight) * 0.5f + MathF.Abs(capsuleRadius),
            0.05f);
        float bottom = characterPosition.Y - halfHeight;
        float totalHeight = halfHeight * 2.0f;
        float submersion = System.Math.Clamp(
            (surface.Height - bottom) / totalHeight,
            0.0f,
            1.0f);

        return new OceanPlayerWaterState(
            submersion > 0.0001f,
            surface.Height,
            submersion,
            surface.Normal,
            surface.Velocity);
    }

    private bool TrySampleRenderedSurface(
        Vector2 worldPosition,
        float simulationTime,
        out OceanSurfaceSample sample)
    {
        if (!_renderer.TrySampleOceanSurface(
                worldPosition,
                simulationTime,
                physicsQuality: true,
                out sample))
        {
            return false;
        }

        // The visible vertex is displaced horizontally by the Gerstner
        // component. One inverse-map iteration keeps physics/waterline on that
        // same surface instead of sampling the undisplaced parameter domain.
        Vector2 displacedPosition = worldPosition - new Vector2(
            sample.Displacement.X,
            sample.Displacement.Z);
        if (!IsFinite(displacedPosition) ||
            Vector2.DistanceSquared(displacedPosition, worldPosition) <=
            GeometryEpsilon * GeometryEpsilon)
        {
            return true;
        }

        return _renderer.TrySampleOceanSurface(
            displacedPosition,
            simulationTime,
            physicsQuality: true,
            out sample);
    }

    private CachedConvexGeometry? GetConvexGeometry(RigidBody body)
    {
        if (_geometryCache.TryGetValue(body, out CachedConvexGeometry? cached) &&
            cached.Matches(body))
        {
            return cached;
        }

        CachedConvexGeometry? geometry = BuildConvexGeometry(body);
        if (geometry == null)
            _geometryCache.Remove(body);
        else
            _geometryCache[body] = geometry;
        return geometry;
    }

    private void PruneGeometryCache()
    {
        if (_geometryCache.Count == 0 || ++_cachePruneCountdown < 120)
            return;

        _cachePruneCountdown = 0;
        var liveBodies = new HashSet<RigidBody>();
        foreach (Entity entity in _scene.Entities)
        {
            if (entity.Body != null)
                liveBodies.Add(entity.Body);
        }

        foreach (RigidBody body in _geometryCache.Keys.ToArray())
        {
            if (!liveBodies.Contains(body))
                _geometryCache.Remove(body);
        }
    }

    private static CachedConvexGeometry? BuildConvexGeometry(RigidBody body)
    {
        switch (body.Type)
        {
            case RigidBody.ShapeType.Box:
                return BuildBoxGeometry(body);

            case RigidBody.ShapeType.Capsule:
                return BuildCapsuleGeometry(body);

            case RigidBody.ShapeType.ConvexHull:
            case RigidBody.ShapeType.Trimesh:
                return BuildMeshGeometry(body);

            default:
                return null;
        }
    }

    private static CachedConvexGeometry BuildBoxGeometry(RigidBody body)
    {
        Vector3 extents = new(
            MathF.Max(MathF.Abs(body.BoxHalfExtents.X), 0.0001f),
            MathF.Max(MathF.Abs(body.BoxHalfExtents.Y), 0.0001f),
            MathF.Max(MathF.Abs(body.BoxHalfExtents.Z), 0.0001f));

        Vector3[] vertices =
        [
            new(-extents.X, -extents.Y, -extents.Z),
            new( extents.X, -extents.Y, -extents.Z),
            new( extents.X, -extents.Y,  extents.Z),
            new(-extents.X, -extents.Y,  extents.Z),
            new(-extents.X,  extents.Y, -extents.Z),
            new( extents.X,  extents.Y, -extents.Z),
            new( extents.X,  extents.Y,  extents.Z),
            new(-extents.X,  extents.Y,  extents.Z)
        ];

        uint[] indices =
        [
            // Bottom
            0, 1, 2, 0, 2, 3,
            // Top
            4, 6, 5, 4, 7, 6,
            // +X
            1, 5, 6, 1, 6, 2,
            // -X
            0, 3, 7, 0, 7, 4,
            // +Z
            2, 6, 7, 2, 7, 3,
            // -Z
            0, 4, 5, 0, 5, 1
        ];

        return FinalizeGeometry(body, vertices, indices);
    }

    private static CachedConvexGeometry BuildCapsuleGeometry(RigidBody body)
    {
        float radius = MathF.Max(MathF.Abs(body.CapsuleRadius), 0.01f);
        float halfCylinder = MathF.Max(MathF.Abs(body.CapsuleHeight) * 0.5f, 0.0f);
        const int slices = 24;
        const int hemisphereSegments = 8;

        var vertices = new List<Vector3>(slices * (hemisphereSegments * 2 + 4));
        var rings = new List<int>(hemisphereSegments * 2 + 2);

        int bottomPole = vertices.Count;
        vertices.Add(new Vector3(0.0f, -halfCylinder - radius, 0.0f));

        for (int i = 1; i <= hemisphereSegments; i++)
        {
            float angle = -MathF.PI * 0.5f +
                          MathF.PI * 0.5f * i / hemisphereSegments;
            rings.Add(AddRing(
                vertices,
                slices,
                -halfCylinder + MathF.Sin(angle) * radius,
                MathF.Cos(angle) * radius));
        }

        rings.Add(AddRing(vertices, slices, halfCylinder, radius));

        for (int i = 1; i < hemisphereSegments; i++)
        {
            float angle = MathF.PI * 0.5f * i / hemisphereSegments;
            rings.Add(AddRing(
                vertices,
                slices,
                halfCylinder + MathF.Sin(angle) * radius,
                MathF.Cos(angle) * radius));
        }

        int topPole = vertices.Count;
        vertices.Add(new Vector3(0.0f, halfCylinder + radius, 0.0f));

        var indices = new List<uint>(slices * (rings.Count + 1) * 6);
        AddPoleRingTriangles(indices, bottomPole, rings[0], slices);
        for (int i = 0; i + 1 < rings.Count; i++)
            AddRingTriangles(indices, rings[i], rings[i + 1], slices);
        AddPoleRingTriangles(indices, topPole, rings[^1], slices);

        return FinalizeGeometry(body, vertices.ToArray(), indices.ToArray());
    }

    private static int AddRing(
        List<Vector3> vertices,
        int slices,
        float y,
        float radius)
    {
        int start = vertices.Count;
        for (int i = 0; i < slices; i++)
        {
            float angle = MathF.Tau * i / slices;
            vertices.Add(new Vector3(
                MathF.Cos(angle) * radius,
                y,
                MathF.Sin(angle) * radius));
        }
        return start;
    }

    private static void AddPoleRingTriangles(
        List<uint> indices,
        int pole,
        int ring,
        int slices)
    {
        for (int i = 0; i < slices; i++)
        {
            int next = (i + 1) % slices;
            indices.Add((uint)pole);
            indices.Add((uint)(ring + i));
            indices.Add((uint)(ring + next));
        }
    }

    private static void AddRingTriangles(
        List<uint> indices,
        int lower,
        int upper,
        int slices)
    {
        for (int i = 0; i < slices; i++)
        {
            int next = (i + 1) % slices;
            uint lowerA = (uint)(lower + i);
            uint lowerB = (uint)(lower + next);
            uint upperA = (uint)(upper + i);
            uint upperB = (uint)(upper + next);
            indices.Add(lowerA);
            indices.Add(upperA);
            indices.Add(upperB);
            indices.Add(lowerA);
            indices.Add(upperB);
            indices.Add(lowerB);
        }
    }

    private static CachedConvexGeometry? BuildMeshGeometry(RigidBody body)
    {
        Vector3[]? source = body.TrimeshVertices;
        if (source == null || source.Length < 4)
            return null;

        Vector3 scale = body.TrimeshScale;
        var scaledVertices = new Vector3[source.Length];
        for (int i = 0; i < source.Length; i++)
            scaledVertices[i] = source[i] * scale;

        // Dynamic trimesh bodies are converted to a Jolt convex hull in
        // RigidBody.Build. Rebuild the same hull topology here so buoyancy
        // clips the collider that the solver actually uses.
        if (body.Type == RigidBody.ShapeType.Trimesh &&
            TryBuildConvexHull(scaledVertices, out Vector3[] hullVertices, out uint[] hullIndices))
        {
            return FinalizeGeometry(body, hullVertices, hullIndices);
        }

        uint[]? sourceIndices = body.TrimeshIndices;
        if (sourceIndices == null || sourceIndices.Length < 3)
            return null;

        return FinalizeGeometry(
            body,
            scaledVertices,
            (uint[])sourceIndices.Clone());
    }

    private static bool TryBuildConvexHull(
        Vector3[] vertices,
        out Vector3[] hullVertices,
        out uint[] hullIndices)
    {
        hullVertices = [];
        hullIndices = [];
        try
        {
            var hullInput = vertices
                .Select(v => new Fuse.Renderer.HullVertex
                {
                    Position = new double[] { v.X, v.Y, v.Z }
                })
                .ToArray();
            var hull = MIConvexHull.ConvexHull.Create(hullInput);
            var resultPoints = hull.Result.Points.ToArray();
            var resultFaces = hull.Result.Faces.ToArray();
            if (resultPoints.Length < 4 || resultFaces.Length == 0)
                return false;

            var pointToIndex = resultPoints
                .Select((point, index) => new { point, index })
                .ToDictionary(value => value.point, value => (uint)value.index);
            var indices = new List<uint>(resultFaces.Length * 3);
            foreach (var face in resultFaces)
            {
                if (face.Vertices.Length < 3 ||
                    !pointToIndex.TryGetValue(face.Vertices[0], out uint a) ||
                    !pointToIndex.TryGetValue(face.Vertices[1], out uint b) ||
                    !pointToIndex.TryGetValue(face.Vertices[2], out uint c))
                {
                    continue;
                }

                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
            }

            hullVertices = resultPoints
                .Select(point => new Vector3(
                    (float)point.Position[0],
                    (float)point.Position[1],
                    (float)point.Position[2]))
                .ToArray();
            hullIndices = indices.ToArray();
            return hullIndices.Length >= 12;
        }
        catch
        {
            hullVertices = [];
            hullIndices = [];
            return false;
        }
    }

    private static CachedConvexGeometry FinalizeGeometry(
        RigidBody body,
        Vector3[] vertices,
        uint[] indices)
    {
        Vector3 average = Vector3.Zero;
        for (int i = 0; i < vertices.Length; i++)
            average += vertices[i];
        average /= MathF.Max(vertices.Length, 1);

        var orientedIndices = (uint[])indices.Clone();
        for (int i = 0; i + 2 < orientedIndices.Length; i += 3)
        {
            uint ia = orientedIndices[i];
            uint ib = orientedIndices[i + 1];
            uint ic = orientedIndices[i + 2];
            if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                continue;

            Vector3 a = vertices[ia];
            Vector3 b = vertices[ib];
            Vector3 c = vertices[ic];
            Vector3 faceNormal = Vector3.Cross(b - a, c - a);
            Vector3 faceCenter = (a + b + c) / 3.0f;
            if (Vector3.Dot(faceNormal, faceCenter - average) < 0.0f)
            {
                orientedIndices[i + 1] = ic;
                orientedIndices[i + 2] = ib;
            }
        }

        float signedVolume = 0.0f;
        Vector3 weightedCentroid = Vector3.Zero;
        for (int i = 0; i + 2 < orientedIndices.Length; i += 3)
        {
            uint ia = orientedIndices[i];
            uint ib = orientedIndices[i + 1];
            uint ic = orientedIndices[i + 2];
            if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                continue;

            Vector3 a = vertices[ia] - average;
            Vector3 b = vertices[ib] - average;
            Vector3 c = vertices[ic] - average;
            float tetraVolume = Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0f;
            if (!float.IsFinite(tetraVolume))
                continue;

            signedVolume += tetraVolume;
            weightedCentroid += (a + b + c) * (0.25f * tetraVolume);
        }

        float volume = MathF.Abs(signedVolume);
        Vector3 centroid = MathF.Abs(signedVolume) > GeometryEpsilon
            ? average + weightedCentroid / signedVolume
            : average;
        if (!float.IsFinite(volume) || volume <= MinimumVolume)
            volume = MinimumVolume;
        if (!IsFinite(centroid))
            centroid = average;

        return new CachedConvexGeometry(
            body,
            vertices,
            orientedIndices,
            volume,
            centroid);
    }

    private SubmergedVolumeResult CalculateConvexSubmergedVolume(
        CachedConvexGeometry geometry,
        Vector3[] worldVertices,
        Vector3 bodyPosition,
        Quaternion bodyRotation,
        Vector3 surfacePoint,
        Vector3 surfaceNormal,
        Vector3 relativeLinearVelocity)
    {
        bool hasUnderwaterVertex = false;
        bool hasAboveWaterVertex = false;
        for (int i = 0; i < geometry.Vertices.Length; i++)
        {
            float distance = PlaneDistance(
                worldVertices[i],
                surfacePoint,
                surfaceNormal);
            if (distance < -GeometryEpsilon)
                hasUnderwaterVertex = true;
            else if (distance > GeometryEpsilon)
                hasAboveWaterVertex = true;
        }

        if (!hasUnderwaterVertex)
            return default;

        Vector3 worldCentroid = bodyPosition + Vector3.Transform(
            geometry.LocalCentroid,
            bodyRotation);
        Vector3 flowDirection = NormalizeOrZero(relativeLinearVelocity);

        if (!hasAboveWaterVertex)
        {
            float fullArea = CalculateProjectedArea(
                geometry,
                worldVertices,
                flowDirection);
            return new SubmergedVolumeResult(
                geometry.Volume,
                1.0f,
                worldCentroid,
                fullArea,
                true);
        }

        _capPoints.Clear();
        float signedVolume = 0.0f;
        Vector3 weightedCentroid = Vector3.Zero;
        float projectedArea = 0.0f;

        for (int i = 0; i + 2 < geometry.Indices.Length; i += 3)
        {
            uint ia = geometry.Indices[i];
            uint ib = geometry.Indices[i + 1];
            uint ic = geometry.Indices[i + 2];
            if (ia >= worldVertices.Length ||
                ib >= worldVertices.Length ||
                ic >= worldVertices.Length)
            {
                continue;
            }

            Vector3 a = worldVertices[ia];
            Vector3 b = worldVertices[ib];
            Vector3 c = worldVertices[ic];
            float da = PlaneDistance(a, surfacePoint, surfaceNormal);
            float db = PlaneDistance(b, surfacePoint, surfaceNormal);
            float dc = PlaneDistance(c, surfacePoint, surfaceNormal);

            CollectPlaneEdge(a, da, b, db);
            CollectPlaneEdge(b, db, c, dc);
            CollectPlaneEdge(c, dc, a, da);

            _clippedFace.Clear();
            ClipEdge(a, da, b, db);
            ClipEdge(b, db, c, dc);
            ClipEdge(c, dc, a, da);
            RemoveNearDuplicateVertices(_clippedFace);
            if (_clippedFace.Count < 3)
                continue;

            Vector3 first = _clippedFace[0];
            for (int vertex = 1; vertex + 1 < _clippedFace.Count; vertex++)
            {
                Vector3 second = _clippedFace[vertex];
                Vector3 third = _clippedFace[vertex + 1];
                AccumulateTriangle(
                    first,
                    second,
                    third,
                    worldCentroid,
                    ref signedVolume,
                    ref weightedCentroid);
                projectedArea += CalculateProjectedTriangleArea(
                    first,
                    second,
                    third,
                    flowDirection);
            }
        }

        AddWaterlineCap(
            surfacePoint,
            surfaceNormal,
            worldCentroid,
            ref signedVolume,
            ref weightedCentroid);

        if (!float.IsFinite(signedVolume) ||
            MathF.Abs(signedVolume) <= GeometryEpsilon)
        {
            return default;
        }

        float volume = MathF.Abs(signedVolume);
        Vector3 center = worldCentroid + weightedCentroid / signedVolume;
        if (!float.IsFinite(volume) || !IsFinite(center))
            return default;

        float fraction = System.Math.Clamp(
            volume / MathF.Max(geometry.Volume, MinimumVolume),
            0.0f,
            1.0f);
        return new SubmergedVolumeResult(
            volume,
            fraction,
            center,
            projectedArea,
            true);
    }

    private static SubmergedVolumeResult CreateFullConvexResult(
        CachedConvexGeometry geometry,
        Vector3[] worldVertices,
        Vector3 bodyPosition,
        Quaternion bodyRotation,
        Vector3 relativeLinearVelocity)
    {
        Vector3 center = bodyPosition + Vector3.Transform(
            geometry.LocalCentroid,
            bodyRotation);
        float projectedArea = CalculateProjectedArea(
            geometry,
            worldVertices,
            NormalizeOrZero(relativeLinearVelocity));
        return new SubmergedVolumeResult(
            geometry.Volume,
            1.0f,
            center,
            projectedArea,
            true);
    }

    private static SubmergedVolumeResult CalculateSphereSubmergedVolume(
        Vector3 sphereCenter,
        float radius,
        Vector3 surfacePoint,
        Vector3 surfaceNormal,
        Vector3 relativeVelocity)
    {
        float signedDistance = Vector3.Dot(
            surfaceNormal,
            sphereCenter - surfacePoint);
        if (signedDistance >= radius - GeometryEpsilon)
            return default;

        float sphereVolume = 4.0f / 3.0f * MathF.PI * radius * radius * radius;
        if (signedDistance <= -radius + GeometryEpsilon)
        {
            float fullArea = MathF.PI * radius * radius;
            return new SubmergedVolumeResult(
                sphereVolume,
                1.0f,
                sphereCenter,
                fullArea,
                true);
        }

        float capHeight = System.Math.Clamp(
            radius - signedDistance,
            0.0f,
            radius * 2.0f);
        float capVolume = MathF.PI * capHeight * capHeight *
            (radius - capHeight / 3.0f);
        float fraction = System.Math.Clamp(
            capVolume / MathF.Max(sphereVolume, MinimumVolume),
            0.0f,
            1.0f);

        float denominator = MathF.Max(
            4.0f * (radius - capHeight / 3.0f),
            GeometryEpsilon);
        float centroidDistance =
            (2.0f * radius - capHeight) *
            (2.0f * radius - capHeight) /
            denominator;
        Vector3 center = sphereCenter - surfaceNormal * centroidDistance;

        // Steady fully-wetted drag is not valid at the instant a free surface
        // first touches the body. Grow the effective area with the developed
        // immersed flow, while leaving the analytic hydrostatic volume exact.
        float projectedArea = MathF.PI * radius * radius *
            SmoothStep01(fraction);
        if (relativeVelocity.LengthSquared() <= GeometryEpsilon * GeometryEpsilon)
            projectedArea = 0.0f;

        return new SubmergedVolumeResult(
            capVolume,
            fraction,
            center,
            projectedArea,
            true);
    }

    private void AddWaterlineCap(
        Vector3 surfacePoint,
        Vector3 surfaceNormal,
        Vector3 reference,
        ref float signedVolume,
        ref Vector3 weightedCentroid)
    {
        if (_capPoints.Count < 3)
            return;

        Vector3 capCenter = Vector3.Zero;
        for (int i = 0; i < _capPoints.Count; i++)
            capCenter += _capPoints[i];
        capCenter /= _capPoints.Count;
        capCenter -= surfaceNormal * Vector3.Dot(
            surfaceNormal,
            capCenter - surfacePoint);

        Vector3 referenceAxis = MathF.Abs(surfaceNormal.Y) < 0.9f
            ? Vector3.UnitY
            : Vector3.UnitZ;
        Vector3 axisU = Vector3.Normalize(Vector3.Cross(
            referenceAxis,
            surfaceNormal));
        Vector3 axisV = Vector3.Cross(surfaceNormal, axisU);
        if (!IsFinite(axisU) || !IsFinite(axisV))
            return;

        _orderedCapPoints.Clear();
        for (int i = 0; i < _capPoints.Count; i++)
        {
            Vector3 projected = _capPoints[i] - surfaceNormal * Vector3.Dot(
                surfaceNormal,
                _capPoints[i] - surfacePoint);
            Vector3 fromCenter = projected - capCenter;
            float angle = MathF.Atan2(
                Vector3.Dot(fromCenter, axisV),
                Vector3.Dot(fromCenter, axisU));
            InsertCapPoint(projected, angle);
        }

        for (int i = 0; i < _orderedCapPoints.Count; i++)
        {
            Vector3 next = _orderedCapPoints[(i + 1) % _orderedCapPoints.Count].Position;
            AccumulateTriangle(
                capCenter,
                _orderedCapPoints[i].Position,
                next,
                reference,
                ref signedVolume,
                ref weightedCentroid);
        }
    }

    private void InsertCapPoint(Vector3 position, float angle)
    {
        int index = _orderedCapPoints.Count;
        while (index > 0 && _orderedCapPoints[index - 1].Angle > angle)
            index--;
        _orderedCapPoints.Insert(index, new CapPoint
        {
            Position = position,
            Angle = angle
        });
    }

    private void CollectPlaneEdge(
        Vector3 first,
        float firstDistance,
        Vector3 second,
        float secondDistance)
    {
        if (MathF.Abs(firstDistance) <= GeometryEpsilon)
            AddUniqueCapPoint(first);
        if (MathF.Abs(secondDistance) <= GeometryEpsilon)
            AddUniqueCapPoint(second);

        bool crosses = (firstDistance < -GeometryEpsilon &&
                        secondDistance > GeometryEpsilon) ||
                       (firstDistance > GeometryEpsilon &&
                        secondDistance < -GeometryEpsilon);
        if (!crosses)
            return;

        float denominator = firstDistance - secondDistance;
        if (MathF.Abs(denominator) <= GeometryEpsilon)
            return;
        float t = System.Math.Clamp(
            firstDistance / denominator,
            0.0f,
            1.0f);
        AddUniqueCapPoint(Vector3.Lerp(first, second, t));
    }

    private void AddUniqueCapPoint(Vector3 point)
    {
        if (!IsFinite(point))
            return;

        const float duplicateDistanceSquared = 0.00000001f;
        for (int i = 0; i < _capPoints.Count; i++)
        {
            if (Vector3.DistanceSquared(_capPoints[i], point) <=
                duplicateDistanceSquared)
            {
                return;
            }
        }
        _capPoints.Add(point);
    }

    private void ClipEdge(
        Vector3 current,
        float currentDistance,
        Vector3 next,
        float nextDistance)
    {
        bool currentInside = currentDistance <= GeometryEpsilon;
        bool nextInside = nextDistance <= GeometryEpsilon;
        if (currentInside && nextInside)
        {
            _clippedFace.Add(next);
            return;
        }

        if (currentInside && !nextInside)
        {
            AddClippedIntersection(current, currentDistance, next, nextDistance);
            return;
        }

        if (!currentInside && nextInside)
        {
            AddClippedIntersection(current, currentDistance, next, nextDistance);
            _clippedFace.Add(next);
        }
    }

    private void AddClippedIntersection(
        Vector3 current,
        float currentDistance,
        Vector3 next,
        float nextDistance)
    {
        float denominator = currentDistance - nextDistance;
        if (MathF.Abs(denominator) <= GeometryEpsilon)
            return;
        float t = System.Math.Clamp(
            currentDistance / denominator,
            0.0f,
            1.0f);
        _clippedFace.Add(Vector3.Lerp(current, next, t));
    }

    private static void RemoveNearDuplicateVertices(List<Vector3> polygon)
    {
        for (int i = polygon.Count - 1; i > 0; i--)
        {
            if (Vector3.DistanceSquared(polygon[i], polygon[i - 1]) <=
                GeometryEpsilon * GeometryEpsilon)
            {
                polygon.RemoveAt(i);
            }
        }

        if (polygon.Count > 1 &&
            Vector3.DistanceSquared(polygon[0], polygon[^1]) <=
            GeometryEpsilon * GeometryEpsilon)
        {
            polygon.RemoveAt(polygon.Count - 1);
        }
    }

    private static void AccumulateTriangle(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 reference,
        ref float signedVolume,
        ref Vector3 weightedCentroid)
    {
        Vector3 relativeA = a - reference;
        Vector3 relativeB = b - reference;
        Vector3 relativeC = c - reference;
        float tetraVolume = Vector3.Dot(
            relativeA,
            Vector3.Cross(relativeB, relativeC)) / 6.0f;
        if (!float.IsFinite(tetraVolume))
            return;

        signedVolume += tetraVolume;
        weightedCentroid += (
            relativeA + relativeB + relativeC) * (0.25f * tetraVolume);
    }

    private static float CalculateProjectedArea(
        CachedConvexGeometry geometry,
        Vector3[] worldVertices,
        Vector3 flowDirection)
    {
        if (flowDirection.LengthSquared() <= GeometryEpsilon * GeometryEpsilon)
            return 0.0f;

        float area = 0.0f;
        for (int i = 0; i + 2 < geometry.Indices.Length; i += 3)
        {
            uint ia = geometry.Indices[i];
            uint ib = geometry.Indices[i + 1];
            uint ic = geometry.Indices[i + 2];
            if (ia >= worldVertices.Length ||
                ib >= worldVertices.Length ||
                ic >= worldVertices.Length)
            {
                continue;
            }

            area += CalculateProjectedTriangleArea(
                worldVertices[ia],
                worldVertices[ib],
                worldVertices[ic],
                flowDirection);
        }
        return area;
    }

    private static float CalculateProjectedTriangleArea(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 flowDirection)
    {
        Vector3 cross = Vector3.Cross(b - a, c - a);
        float doubleArea = cross.Length();
        if (!float.IsFinite(doubleArea) || doubleArea <= GeometryEpsilon)
            return 0.0f;

        float facing = MathF.Max(
            Vector3.Dot(cross / doubleArea, flowDirection),
            0.0f);
        return doubleArea * 0.5f * facing;
    }

    private static float LimitFreeSurfaceDragArea(
        float submergedProjectedArea,
        float fullProjectedArea,
        float submergedFraction)
    {
        if (!float.IsFinite(fullProjectedArea) ||
            fullProjectedArea <= GeometryEpsilon)
        {
            return submergedProjectedArea;
        }

        float fraction = System.Math.Clamp(submergedFraction, 0.0f, 1.0f);
        if (fraction <= GeometryEpsilon)
            return 0.0f;

        // A steady-flow projected area jumps to an entire flat lower face at
        // first contact. That formula is outside its regime during water entry
        // and acts like a collision impulse. Smoothly develop the effective
        // wetted flow from zero to the exact fully submerged projected area.
        // Buoyancy still uses the unmodified clipped volume.
        float immersionLimitedArea = fullProjectedArea *
            SmoothStep01(fraction);
        if (!float.IsFinite(immersionLimitedArea) ||
            immersionLimitedArea <= GeometryEpsilon)
        {
            return 0.0f;
        }

        if (!float.IsFinite(submergedProjectedArea) ||
            submergedProjectedArea <= GeometryEpsilon)
        {
            // The generated waterline cap is part of the displaced volume but
            // is not one of the collider's original faces. Use the continuous
            // immersion cross-section when flow points out through that cap.
            return immersionLimitedArea;
        }

        return MathF.Min(submergedProjectedArea, immersionLimitedArea);
    }

    private static Vector3 CalculateQuadraticDrag(
        Vector3 relativeVelocity,
        float projectedArea,
        float waterDensity,
        float dragCoefficient,
        float bodyMass,
        float deltaTime,
        Vector3 nonDragAcceleration)
    {
        if (!IsFinite(relativeVelocity) ||
            !IsFinite(nonDragAcceleration) ||
            !float.IsFinite(projectedArea) ||
            !float.IsFinite(waterDensity) ||
            !float.IsFinite(dragCoefficient) ||
            !float.IsFinite(bodyMass) ||
            !float.IsFinite(deltaTime) ||
            projectedArea <= 0.0f ||
            waterDensity <= 0.0f ||
            dragCoefficient <= 0.0f ||
            bodyMass <= 0.0f ||
            deltaTime <= 0.0f)
        {
            return Vector3.Zero;
        }

        // Apply gravity and buoyancy first in this local integration estimate,
        // then solve quadratic drag over the same fixed interval. If buoyancy
        // reverses a falling body during the step, drag therefore changes to
        // oppose the emerging upward motion instead of continuing to add an
        // upward force based on stale entry velocity.
        Vector3 velocityAfterExternalForces = relativeVelocity +
            nonDragAcceleration * deltaTime;
        if (!IsFinite(velocityAfterExternalForces))
            return Vector3.Zero;

        float speedSquared = velocityAfterExternalForces.LengthSquared();
        if (!float.IsFinite(speedSquared) || speedSquared <= GeometryEpsilon)
            return Vector3.Zero;

        float speed = MathF.Sqrt(speedSquared);
        float dragConstant = 0.5f * waterDensity * dragCoefficient * projectedArea;
        if (!float.IsFinite(dragConstant) || dragConstant <= 0.0f)
            return Vector3.Zero;

        // Integrate dv/dt = -(K / mass) * |v| * v analytically over this
        // fixed step. A raw explicit Euler force becomes unstable when a body
        // enters water quickly; the old one-step clamp stopped the velocity
        // exactly at zero and made the surface feel like concrete. The
        // expression below is the average force of the same quadratic-drag
        // equation over the interval, so it remains dissipative without
        // injecting an upward impulse or directly modifying velocity.
        float denominator = 1.0f + dragConstant * speed * deltaTime / bodyMass;
        if (!float.IsFinite(denominator) || denominator <= 1.0f)
            return Vector3.Zero;

        Vector3 velocityAfterDrag = velocityAfterExternalForces / denominator;
        Vector3 force = (velocityAfterDrag - velocityAfterExternalForces) *
            (bodyMass / deltaTime);
        return IsFinite(force) ? force : Vector3.Zero;
    }

    private static Vector3 CalculateAngularDrag(
        Vector3 angularVelocity,
        float waterDensity,
        float dragCoefficient,
        float projectedArea,
        float characteristicLength,
        float submersion,
        Matrix4x4 inverseInertia,
        float deltaTime)
    {
        if (!IsFinite(angularVelocity) ||
            !float.IsFinite(waterDensity) ||
            !float.IsFinite(dragCoefficient) ||
            !float.IsFinite(projectedArea) ||
            !float.IsFinite(characteristicLength) ||
            !float.IsFinite(submersion) ||
            !IsFinite(inverseInertia) ||
            !float.IsFinite(deltaTime) ||
            waterDensity <= 0.0f ||
            dragCoefficient <= 0.0f ||
            projectedArea <= 0.0f ||
            characteristicLength <= 0.0f ||
            submersion <= 0.0f ||
            deltaTime <= 0.0f)
        {
            return Vector3.Zero;
        }

        float angularSpeedSquared = angularVelocity.LengthSquared();
        if (!float.IsFinite(angularSpeedSquared) ||
            angularSpeedSquared <= GeometryEpsilon * GeometryEpsilon)
        {
            return Vector3.Zero;
        }

        float angularSpeed = MathF.Sqrt(angularSpeedSquared);
        Vector3 axis = angularVelocity / angularSpeed;
        Vector3 inverseInertiaAlongAxis = Vector3.TransformNormal(
            axis,
            inverseInertia);
        float inverseEffectiveInertia = Vector3.Dot(
            axis,
            inverseInertiaAlongAxis);
        if (!float.IsFinite(inverseEffectiveInertia) ||
            inverseEffectiveInertia <= GeometryEpsilon)
        {
            return Vector3.Zero;
        }

        // Rotational quadratic drag follows the same pressure scale as linear
        // drag: F~rho*A*(omega*L)^2 and torque~F*L. Integrate the scalar decay
        // analytically with the body's world-space effective inertia so the
        // torque cannot reverse angular velocity in one fixed step.
        float dragConstant = 0.5f *
            waterDensity *
            dragCoefficient *
            projectedArea *
            characteristicLength *
            characteristicLength *
            characteristicLength *
            System.Math.Clamp(submersion, 0.0f, 1.0f);
        if (!float.IsFinite(dragConstant) || dragConstant <= 0.0f)
            return Vector3.Zero;

        float denominator = 1.0f +
            dragConstant * angularSpeed * deltaTime * inverseEffectiveInertia;
        if (!float.IsFinite(denominator) || denominator <= 1.0f)
            return Vector3.Zero;

        float speedAfterDrag = angularSpeed / denominator;
        float effectiveInertia = 1.0f / inverseEffectiveInertia;
        float torqueMagnitude =
            (speedAfterDrag - angularSpeed) * effectiveInertia / deltaTime;
        Vector3 torque = axis * torqueMagnitude;
        return IsFinite(torque) ? torque : Vector3.Zero;
    }

    private static SubmergedVolumeResult CreateFullSphereResult(
        Vector3 center,
        float radius,
        Vector3 relativeVelocity)
    {
        float volume = 4.0f / 3.0f * MathF.PI * radius * radius * radius;
        float area = relativeVelocity.LengthSquared() >
                     GeometryEpsilon * GeometryEpsilon
            ? MathF.PI * radius * radius
            : 0.0f;
        return new SubmergedVolumeResult(
            volume,
            1.0f,
            center,
            area,
            true);
    }

    private void EnsureWorldVertexCapacity(int required)
    {
        if (_worldVerticesScratch.Length < required)
            Array.Resize(ref _worldVerticesScratch, required);
    }

    private static void TransformVertices(
        Vector3[] localVertices,
        Vector3[] worldVertices,
        Vector3 position,
        Quaternion rotation)
    {
        for (int i = 0; i < localVertices.Length; i++)
            worldVertices[i] = position + Vector3.Transform(
                localVertices[i],
                rotation);
    }

    private static float PlaneDistance(
        Vector3 point,
        Vector3 planePoint,
        Vector3 planeNormal) => Vector3.Dot(
        planeNormal,
        point - planePoint);

    private static Vector3 NormalizeSurfaceNormal(Vector3 normal)
    {
        if (!IsFinite(normal) || normal.LengthSquared() <= GeometryEpsilon)
            return Vector3.UnitY;

        normal = Vector3.Normalize(normal);
        return normal.Y >= 0.05f ? normal : Vector3.UnitY;
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        if (!IsFinite(value) ||
            !float.IsFinite(lengthSquared) ||
            lengthSquared <= GeometryEpsilon * GeometryEpsilon)
        {
            return Vector3.Zero;
        }
        return value / MathF.Sqrt(lengthSquared);
    }

    private static float SmoothStep01(float value)
    {
        float t = System.Math.Clamp(value, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static void DrawForceArrow(
        DebugDrawer debugDrawer,
        Vector3 origin,
        Vector3 force,
        float scale,
        Vector3 color)
    {
        if (!IsFinite(force) || force.LengthSquared() <= GeometryEpsilon * GeometryEpsilon)
            return;

        Vector3 direction = Vector3.Normalize(force);
        float length = MathF.Min(
            3.0f,
            MathF.Max(0.05f, force.Length() * scale));
        debugDrawer.PushLine(origin, origin + direction * length, color);
    }

    private static bool NearlyEqual(float left, float right) =>
        float.IsFinite(left) &&
        float.IsFinite(right) &&
        MathF.Abs(left - right) <= 0.00001f;

    private static bool VectorNearlyEqual(Vector3 left, Vector3 right) =>
        IsFinite(left) &&
        IsFinite(right) &&
        Vector3.DistanceSquared(left, right) <= 0.000001f;

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
