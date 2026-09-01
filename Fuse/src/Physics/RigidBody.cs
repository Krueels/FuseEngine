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

    public RigidBody SetBox(Vector3 halfExtents)
    {
        _shapeType = ShapeType.Box;
        _boxHalfExtents = halfExtents;
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
        return this;
    }

    public RigidBody SetCapsule(float radius, float height)
    {
        _shapeType = ShapeType.Capsule;
        _capsuleRadius = radius;
        _capsuleHeight = height;
        return this;
    }

    public RigidBody SetTrimesh(Vector3[] vertices, uint[] indices, Vector3 scale = default)
    {
        _shapeType = ShapeType.Trimesh;
        _trimeshVerts = vertices;
        _trimeshIndices = indices;
        _trimeshScale = scale == default ? Vector3.One : scale;
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
        return this;
    }

    public RigidBody SetConvexHull(Vector3[] vertices, Vector3 scale = default)
    {
        _shapeType = ShapeType.ConvexHull;
        _trimeshScale = scale == default ? Vector3.One : scale;
        _trimeshVerts = vertices;
        _trimeshIndices = null;

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
            }
        }
        catch
        {
            // Fallback to original vertices without indices (won't be drawn)
            _trimeshVerts = vertices;
        }

        return this;
    }

    public RigidBody SetPosition(Vector3 pos) { _position = pos; return this; }
    public RigidBody SetRotation(Quaternion rot) { _rotation = rot; return this; }
    public RigidBody SetMass(float mass) { _mass = mass; return this; }
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
            settings.OverrideMassProperties = OverrideMassProperties.CalculateMassAndInertia;
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

    public BodyID Native => _bodyID;
    public bool IsBuilt => _built;

    public ShapeType Type => _shapeType;
    public Vector3 GetPosition() => _position;
    public Quaternion GetRotation() => _rotation;
    public float Mass => _mass;
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
