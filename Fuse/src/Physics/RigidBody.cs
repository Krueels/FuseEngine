using System.Numerics;
using JoltPhysicsSharp;
using Fuse.Core;

namespace Fuse.Physics;

public class RigidBody
{
    public enum ShapeType
    {
        None = 0,
        Box,
        Plane,
        Sphere,
        Capsule,
        Trimesh,
        ConvexHull,
        HeightField
    }

    private Shape? _shape;
    private BodyID _bodyID;
    private bool _built;

    private Vector3 _position;
    private Quaternion _rotation = Quaternion.Identity;
    private float _mass;
    private float _buoyancyVolumeOverride;
    private bool _isKinematic;
    private float _friction = 0.5f;
    private float _restitution = 0.3f;
    private bool _isTrigger;
    private AllowedDOFs _allowedDOFs = AllowedDOFs.All;

    private ShapeType _shapeType;
    private Vector3 _boxHalfExtents = new(0.5f);
    private Vector3 _planeNormal = new(0, 1, 0);
    private float _planeDistance;
    private float _sphereRadius = 0.5f;
    private float _capsuleRadius = 0.4f;
    private float _capsuleHeight = 1.8f;
    private Vector3[]? _trimeshVerts;
    private uint[]? _trimeshIndices;
    private Vector3 _trimeshScale = Vector3.One;
    private float[]? _heightFieldSamples;
    private Vector3 _heightFieldOffset;
    private Vector3 _heightFieldScale = Vector3.One;
    private uint _heightFieldSampleCount;
    private float _cachedBuoyancyVolume = float.NaN;
    private Vector3 _cachedBuoyancyHalfExtents = new(float.NaN);

    public RigidBody SetBox(Vector3 halfExtents)
    {
        _shapeType = ShapeType.Box;
        _boxHalfExtents = halfExtents;
        InvalidateBuoyancyGeometryCache();
        return this;
    }

    public RigidBody SetPlane(Vector3 normal, float distance)
    {
        _shapeType = ShapeType.Plane;
        _planeNormal = normal;
        _planeDistance = distance;
        return this;
    }

    public RigidBody SetSphere(float radius)
    {
        _shapeType = ShapeType.Sphere;
        _sphereRadius = radius;
        InvalidateBuoyancyGeometryCache();
        return this;
    }

    public RigidBody SetCapsule(float radius, float height)
    {
        _shapeType = ShapeType.Capsule;
        _capsuleRadius = radius;
        _capsuleHeight = height;
        InvalidateBuoyancyGeometryCache();
        return this;
    }

    public RigidBody SetTrimesh(Vector3[] vertices, uint[] indices, Vector3 scale = default)
    {
        _shapeType = ShapeType.Trimesh;
        _trimeshVerts = vertices;
        _trimeshIndices = indices;
        _trimeshScale = scale == default ? Vector3.One : scale;
        InvalidateBuoyancyGeometryCache();
        return this;
    }

    /// <summary>
    /// Configures a static heightfield using one sample per terrain heightmap
    /// point. Samples are row-major (z * sampleCount + x) and are multiplied
    /// by <paramref name="scale"/> after <paramref name="offset"/> is applied.
    /// </summary>
    public RigidBody SetHeightField(
        float[] samples,
        Vector3 offset,
        Vector3 scale,
        uint sampleCount)
    {
        if (sampleCount < 2)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "A heightfield needs at least two samples per side.");

        ulong expectedSampleCount = (ulong)sampleCount * sampleCount;
        if (expectedSampleCount != (ulong)samples.Length)
            throw new ArgumentException("Heightfield samples must contain sampleCount squared values.", nameof(samples));

        if (scale.X <= 0.0f || scale.Z <= 0.0f)
            throw new ArgumentException("Heightfield X and Z scales must be positive.", nameof(scale));

        _shapeType = ShapeType.HeightField;
        _heightFieldSamples = (float[])samples.Clone();
        _heightFieldOffset = offset;
        _heightFieldScale = scale;
        _heightFieldSampleCount = sampleCount;
        InvalidateBuoyancyGeometryCache();
        return this;
    }

    public RigidBody SetConvexHull(Vector3[] vertices, Vector3 scale = default)
    {
        _shapeType = ShapeType.ConvexHull;
        _trimeshScale = scale == default ? Vector3.One : scale;
        _trimeshVerts = vertices;
        _trimeshIndices = null;
        InvalidateBuoyancyGeometryCache();

        try
        {
            if (vertices.Length >= 4)
            {
                var hullVerts = vertices.Select(v => new Fuse.Renderer.HullVertex { Position = new double[] { v.X, v.Y, v.Z } }).ToList();
                var hull = MIConvexHull.ConvexHull.Create(hullVerts);
                var pointToIndex = hull.Result.Points.Select((p, i) => new { p, i }).ToDictionary(x => x.p, x => (uint)x.i);
                
                var cvxTriIndices = new System.Collections.Generic.List<uint>();
                foreach (var face in hull.Result.Faces)
                {
                    cvxTriIndices.Add(pointToIndex[face.Vertices[0]]);
                    cvxTriIndices.Add(pointToIndex[face.Vertices[1]]);
                    cvxTriIndices.Add(pointToIndex[face.Vertices[2]]);
                }
                
                _trimeshVerts = hull.Result.Points.Select(p => new Vector3((float)p.Position[0], (float)p.Position[1], (float)p.Position[2])).ToArray();
                _trimeshIndices = cvxTriIndices.ToArray();
                InvalidateBuoyancyGeometryCache();
            }
        }
        catch
        {
            // Fallback to original vertices without indices (won't be drawn)
            _trimeshVerts = vertices;
            InvalidateBuoyancyGeometryCache();
        }

        return this;
    }

    public RigidBody SetPosition(Vector3 pos) { _position = pos; return this; }
    public RigidBody SetRotation(Quaternion rot) { _rotation = rot; return this; }
    public RigidBody SetMass(float mass) { _mass = mass; return this; }
    public RigidBody SetBuoyancyVolumeOverride(float volume)
    {
        _buoyancyVolumeOverride = float.IsFinite(volume) && volume > 0.0001f
            ? volume
            : 0.0f;
        return this;
    }
    public RigidBody SetKinematic(bool kinematic) { _isKinematic = kinematic; return this; }
    public RigidBody SetTrigger(bool trigger) { _isTrigger = trigger; return this; }
    public RigidBody SetFriction(float f) { _friction = f; return this; }
    public RigidBody SetRestitution(float r) { _restitution = r; return this; }
    public RigidBody SetAllowedDOFs(AllowedDOFs dofs) { _allowedDOFs = dofs; return this; }

    public void Build(PhysicsWorld world)
    {
        if (_built)
            Destroy();

        switch (_shapeType)
        {
            case ShapeType.Box:
                _shape = new BoxShape(_boxHalfExtents);
                break;

            case ShapeType.Plane:
            {
                var plane = new System.Numerics.Plane(_planeNormal, _planeDistance);
                _shape = new PlaneShape(plane, null, 500.0f);
                break;
            }

            case ShapeType.Sphere:
                _shape = new SphereShape(_sphereRadius);
                break;

            case ShapeType.Capsule:
                _shape = new CapsuleShape(_capsuleHeight * 0.5f, _capsuleRadius);
                break;

            case ShapeType.HeightField:
            {
                if (_heightFieldSamples == null || _heightFieldSampleCount < 2)
                {
                    Logger.Error("RigidBody.Build HEIGHTFIELD with no data, skipping");
                    return;
                }

                var heightFieldSettings = new HeightFieldShapeSettings(
                    new Span<float>(_heightFieldSamples),
                    _heightFieldOffset,
                    _heightFieldScale,
                    _heightFieldSampleCount);

                // These are Jolt's native heightfield defaults. Keeping the
                // block structure explicit makes the representation stable as
                // terrain sizes change and avoids mesh-style seam heuristics.
                heightFieldSettings.BlockSize = 2;
                heightFieldSettings.BitsPerSample = 8;
                _shape = heightFieldSettings.Create();
                if (_shape == null)
                {
                    Logger.Error("RigidBody.Build HeightFieldShape creation failed");
                    return;
                }

                break;
            }

            case ShapeType.Trimesh:
            {
                if (_trimeshVerts == null || _trimeshVerts.Length == 0 || _trimeshIndices == null || _trimeshIndices.Length < 3)
                {
                    Logger.Error("RigidBody.Build TRIMESH with no data, skipping");
                    return;
                }

                Vector3[] finalVerts = _trimeshVerts;
                if (_trimeshScale != Vector3.One)
                {
                    finalVerts = new Vector3[_trimeshVerts.Length];
                    for (int i = 0; i < _trimeshVerts.Length; i++)
                    {
                        finalVerts[i] = _trimeshVerts[i] * _trimeshScale;
                    }
                }

                if (_mass > 0)
                    {
                        var hullSettings = new ConvexHullShapeSettings(new Span<Vector3>(finalVerts));
                        _shape = hullSettings.Create();
                        if (_shape == null)
                        {
                            Logger.Error("RigidBody.Build ConvexHull creation failed");
                            return;
                        }
                    }
                    else
                    {
                        int triCount = _trimeshIndices.Length / 3;
                        var triangles = new IndexedTriangle[triCount];
                        for (int i = 0; i < triCount; i++)
                        {
                            uint a = _trimeshIndices[i * 3];
                            uint b = _trimeshIndices[i * 3 + 1];
                            uint c = _trimeshIndices[i * 3 + 2];
                            triangles[i] = new IndexedTriangle(a, b, c, 0, 0);
                        }

                        var meshSettings = new MeshShapeSettings(
                            new Span<Vector3>(finalVerts),
                            new Span<IndexedTriangle>(triangles));

                        // Fix (PLEASE GOD) "Ghost Collisions" / Internal Edges.
                        // We use Cos(50 degrees) as a general threshold so any internal
                        // edges less steep than 50 degrees difference are ignored.
                        // Trimeshes are bullshit.
                        meshSettings.ActiveEdgeCosThresholdAngle = MathF.Cos(50.0f * MathF.PI / 180.0f);

                        _shape = meshSettings.Create();
                        if (_shape == null)
                        {
                            Logger.Error("RigidBody.Build MeshShape creation failed");
                            return;
                        }
                    }
                break;
            }

            case ShapeType.ConvexHull:
            {
                if (_trimeshVerts == null || _trimeshVerts.Length == 0)
                {
                    Logger.Error("RigidBody.Build CONVEXHULL with no data, skipping");
                    return;
                }

                Vector3[] finalVerts = _trimeshVerts;
                if (_trimeshScale != Vector3.One)
                {
                    finalVerts = new Vector3[_trimeshVerts.Length];
                    for (int i = 0; i < _trimeshVerts.Length; i++)
                    {
                        finalVerts[i] = _trimeshVerts[i] * _trimeshScale;
                    }
                }

                var hullSettings = new ConvexHullShapeSettings(new Span<Vector3>(finalVerts));
                _shape = hullSettings.Create();
                if (_shape == null)
                {
                    Logger.Error("RigidBody.Build ConvexHull creation failed");
                    return;
                }
                break;
            }

            default:
                return;
        }

        var motionType = _isKinematic ? MotionType.Kinematic : _mass > 0 ? MotionType.Dynamic : MotionType.Static;

        var settings = new BodyCreationSettings(
            _shape!,
            _position,
            _rotation,
            motionType,
            0);

        settings.Friction = _friction;
        settings.Restitution = _restitution;
        settings.AllowSleeping = true;
        settings.MotionQuality = MotionQuality.Discrete;

        if (_isTrigger)
            settings.IsSensor = true;

        settings.AllowedDOFs = _allowedDOFs;

        if (motionType == MotionType.Dynamic)
        {
            // MapBody.Mass is the authored mass, not merely a flag that turns
            // a body dynamic. Ask Jolt to calculate the inertia for the shape
            // while scaling it to this explicit mass. This is also what makes
            // the ocean's Archimedean force distinguish light and heavy bodies.
            settings.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
            MassProperties massProperties = settings.MassPropertiesOverride;
            massProperties.Mass = _mass;
            settings.MassPropertiesOverride = massProperties;
        }

        _bodyID = world.CreateAndAddBody(settings);
        _built = true;
    }

    public void Destroy()
    {
        _shape?.Dispose();
        _shape = null;
        _bodyID = BodyID.Invalid;
        _built = false;
    }

    public Vector3 Position(PhysicsWorld world)
    {
        if (!_built || Type == ShapeType.None) return _position;
        return world.GetBodyPosition(_bodyID);
    }

    public Quaternion Rotation(PhysicsWorld world)
    {
        if (!_built || Type == ShapeType.None) return _rotation;
        return world.GetBodyRotation(_bodyID);
    }

    public Matrix4x4 ModelMatrix(PhysicsWorld world)
    {
        Vector3 p = Position(world);
        Quaternion r = Rotation(world);
        return Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(p);
    }

    public void ApplyCentralForce(PhysicsWorld world, Vector3 force)
    {
        if (_built)
            world.BodyInterface.AddForce(_bodyID, force);
    }

    /// <summary>
    /// Applies a force in world space at a world-space point. Buoyancy uses
    /// this instead of a central force so uneven submersion can create the
    /// restoring torque that makes an object settle on the water.
    /// </summary>
    public void ApplyForceAtPosition(
        PhysicsWorld world,
        Vector3 force,
        Vector3 worldPosition)
    {
        if (_built)
            world.BodyInterface.AddForce(_bodyID, force, worldPosition);
    }

    public void ApplyTorque(PhysicsWorld world, Vector3 torque)
    {
        if (_built)
            world.BodyInterface.AddTorque(_bodyID, torque);
    }

    public void ApplyCentralImpulse(PhysicsWorld world, Vector3 impulse)
    {
        if (_built)
            world.BodyInterface.AddImpulse(_bodyID, impulse);
    }

    public void SetLinearVelocity(PhysicsWorld world, Vector3 velocity)
    {
        if (_built)
            world.BodyInterface.SetLinearVelocity(_bodyID, velocity);
    }

    public Vector3 LinearVelocity(PhysicsWorld world)
    {
        if (!_built) return Vector3.Zero;
        return world.BodyInterface.GetLinearVelocity(_bodyID);
    }

    public Vector3 AngularVelocity(PhysicsWorld world)
    {
        if (!_built) return Vector3.Zero;
        return world.BodyInterface.GetAngularVelocity(_bodyID);
    }

    public Vector3 PointVelocity(PhysicsWorld world, Vector3 worldPosition)
    {
        if (!_built) return Vector3.Zero;
        return world.BodyInterface.GetPointVelocity(_bodyID, worldPosition);
    }

    public Vector3 CenterOfMassPosition(PhysicsWorld world)
    {
        if (!_built) return _position;
        return world.BodyInterface.GetCenterOfMassPosition(_bodyID);
    }

    public Matrix4x4 InverseInertia(PhysicsWorld world)
    {
        if (!_built) return new Matrix4x4();
        return world.BodyInterface.GetInverseInertia(_bodyID);
    }

    public BodyID Native => _bodyID;
    public bool IsBuilt => _built;

    public ShapeType Type => _shapeType;
    public Vector3 GetPosition() => _position;
    public Quaternion GetRotation() => _rotation;
    public float Mass => _mass;
    public float BuoyancyVolumeOverride => _buoyancyVolumeOverride;
    public bool IsKinematic => _isKinematic;
    public bool IsDynamic => _isKinematic || _mass > 0.0f;
    public float Friction => _friction;
    public float Restitution => _restitution;
    public Vector3 BoxHalfExtents => _boxHalfExtents;
    public float SphereRadius => _sphereRadius;
    public float CapsuleRadius => _capsuleRadius;
    public float CapsuleHeight => _capsuleHeight;
    public Vector3 PlaneNormal => _planeNormal;
    public float PlaneDistance => _planeDistance;
    public Vector3[]? TrimeshVertices => _trimeshVerts;
    public uint[]? TrimeshIndices => _trimeshIndices;
    public Vector3 TrimeshScale => _trimeshScale;
    public float[]? HeightFieldSamples => _heightFieldSamples;
    public Vector3 HeightFieldOffset => _heightFieldOffset;
    public Vector3 HeightFieldScale => _heightFieldScale;
    public uint HeightFieldSampleCount => _heightFieldSampleCount;
    public bool IsTrigger => _isTrigger;

    /// <summary>
    /// Returns the collider volume in world units. Dynamic model hulls keep
    /// their import scale in <see cref="_trimeshScale"/>, so it is included
    /// when calculating density and buoyancy.
    /// </summary>
    public float BuoyancyVolume
    {
        get
        {
            const float MinimumVolume = 0.0001f;
            if (_buoyancyVolumeOverride > MinimumVolume &&
                float.IsFinite(_buoyancyVolumeOverride))
            {
                return _buoyancyVolumeOverride;
            }

            if (float.IsFinite(_cachedBuoyancyVolume))
                return _cachedBuoyancyVolume;

            float volume = _shapeType switch
            {
                ShapeType.Box => 8.0f *
                    MathF.Abs(_boxHalfExtents.X * _boxHalfExtents.Y * _boxHalfExtents.Z),
                ShapeType.Sphere => 4.0f / 3.0f * MathF.PI *
                    MathF.Pow(MathF.Abs(_sphereRadius), 3.0f),
                ShapeType.Capsule => MathF.PI *
                    MathF.Pow(MathF.Abs(_capsuleRadius), 2.0f) * MathF.Abs(_capsuleHeight) +
                    4.0f / 3.0f * MathF.PI * MathF.Pow(MathF.Abs(_capsuleRadius), 3.0f),
                ShapeType.Trimesh or ShapeType.ConvexHull => ComputeMeshVolume(),
                _ => 0.0f
            };

            _cachedBuoyancyVolume = float.IsFinite(volume) &&
                                     volume > MinimumVolume
                ? volume
                : 0.0f;
            return _cachedBuoyancyVolume;
        }
    }

    /// <summary>
    /// Returns conservative local half extents for buoyancy probes. The
    /// extents are in the same local space as the body pose.
    /// </summary>
    public Vector3 BuoyancyHalfExtents
    {
        get
        {
            if (float.IsFinite(_cachedBuoyancyHalfExtents.X) &&
                float.IsFinite(_cachedBuoyancyHalfExtents.Y) &&
                float.IsFinite(_cachedBuoyancyHalfExtents.Z))
            {
                return _cachedBuoyancyHalfExtents;
            }

            Vector3 extents = _shapeType switch
            {
                ShapeType.Box => _boxHalfExtents,
                ShapeType.Sphere => new Vector3(MathF.Abs(_sphereRadius)),
                ShapeType.Capsule => new Vector3(
                    MathF.Abs(_capsuleRadius),
                    MathF.Abs(_capsuleHeight) * 0.5f + MathF.Abs(_capsuleRadius),
                    MathF.Abs(_capsuleRadius)),
                ShapeType.Trimesh or ShapeType.ConvexHull => ComputeMeshHalfExtents(),
                _ => Vector3.Zero
            };

            _cachedBuoyancyHalfExtents = new Vector3(
                MathF.Max(MathF.Abs(extents.X), 0.025f),
                MathF.Max(MathF.Abs(extents.Y), 0.025f),
                MathF.Max(MathF.Abs(extents.Z), 0.025f));
            return _cachedBuoyancyHalfExtents;
        }
    }

    private void InvalidateBuoyancyGeometryCache()
    {
        _cachedBuoyancyVolume = float.NaN;
        _cachedBuoyancyHalfExtents = new Vector3(float.NaN);
    }

    private float ComputeMeshVolume()
    {
        if (_trimeshVerts == null || _trimeshVerts.Length == 0)
            return 0.0f;

        Vector3[] vertices = GetScaledMeshVertices();
        float volume = 0.0f;
        if (_trimeshIndices != null && _trimeshIndices.Length >= 3)
        {
            for (int i = 0; i + 2 < _trimeshIndices.Length; i += 3)
            {
                uint ia = _trimeshIndices[i];
                uint ib = _trimeshIndices[i + 1];
                uint ic = _trimeshIndices[i + 2];
                if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                    continue;

                Vector3 a = vertices[ia];
                Vector3 b = vertices[ib];
                Vector3 c = vertices[ic];
                volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0f;
            }

            volume = MathF.Abs(volume);
        }

        if (volume > 0.0001f && float.IsFinite(volume))
            return volume;

        Vector3 halfExtents = ComputeMeshHalfExtents(vertices);
        return 8.0f * halfExtents.X * halfExtents.Y * halfExtents.Z;
    }

    private Vector3 ComputeMeshHalfExtents()
    {
        return ComputeMeshHalfExtents(GetScaledMeshVertices());
    }

    private static Vector3 ComputeMeshHalfExtents(Vector3[] vertices)
    {
        if (vertices.Length == 0)
            return Vector3.Zero;

        Vector3 min = vertices[0];
        Vector3 max = vertices[0];
        for (int i = 1; i < vertices.Length; i++)
        {
            min = Vector3.Min(min, vertices[i]);
            max = Vector3.Max(max, vertices[i]);
        }

        return (max - min) * 0.5f;
    }

    private Vector3[] GetScaledMeshVertices()
    {
        if (_trimeshVerts == null || _trimeshVerts.Length == 0)
            return Array.Empty<Vector3>();

        Vector3[] vertices = new Vector3[_trimeshVerts.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = _trimeshVerts[i] * _trimeshScale;
        return vertices;
    }

    /// <summary>
    /// Returns a terrain surface normal for editor/runtime raycasts. Jolt is
    /// still responsible for the actual contact and collision resolution; this
    /// keeps the custom scene raycast consistent with the native shape.
    /// </summary>
    public Vector3 GetHeightFieldSurfaceNormal(Vector3 localPoint)
    {
        if (_heightFieldSamples == null || _heightFieldSampleCount < 3)
            return Vector3.UnitY;

        int sampleCount = (int)_heightFieldSampleCount;
        float sampleX = (localPoint.X - _heightFieldOffset.X) / _heightFieldScale.X;
        float sampleZ = (localPoint.Z - _heightFieldOffset.Z) / _heightFieldScale.Z;
        int x = System.Math.Clamp((int)MathF.Round(sampleX), 1, sampleCount - 2);
        int z = System.Math.Clamp((int)MathF.Round(sampleZ), 1, sampleCount - 2);

        float left = GetHeightFieldSampleHeight(x - 1, z);
        float right = GetHeightFieldSampleHeight(x + 1, z);
        float back = GetHeightFieldSampleHeight(x, z - 1);
        float forward = GetHeightFieldSampleHeight(x, z + 1);

        float dx = MathF.Max(MathF.Abs(_heightFieldScale.X), 0.0001f);
        float dz = MathF.Max(MathF.Abs(_heightFieldScale.Z), 0.0001f);
        Vector3 normal = new(
            (left - right) / (2.0f * dx),
            1.0f,
            (back - forward) / (2.0f * dz));

        return normal.LengthSquared() > 0.000001f
            ? Vector3.Normalize(normal)
            : Vector3.UnitY;
    }

    private float GetHeightFieldSampleHeight(int x, int z)
    {
        int sampleCount = (int)_heightFieldSampleCount;
        x = System.Math.Clamp(x, 0, sampleCount - 1);
        z = System.Math.Clamp(z, 0, sampleCount - 1);
        return _heightFieldOffset.Y +
            _heightFieldSamples![z * sampleCount + x] * _heightFieldScale.Y;
    }
}
