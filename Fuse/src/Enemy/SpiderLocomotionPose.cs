using System.Numerics;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>The final fixed-step body frame consumed by gait, skinning and hitboxes.</summary>
public readonly record struct SpiderLocomotionPose(
    Vector3 Position, Quaternion Rotation, Vector3 Velocity, Vector3 AngularVelocity,
    float Scale, BodyID BodyId, SpiderSurfaceContact Support,
    Vector3 SupportVelocity = default, Vector3 SupportAngularVelocity = default)
{
    public Vector3 RelativeVelocity => Velocity - SupportVelocity;
    public Vector3 RelativeAngularVelocity => AngularVelocity - SupportAngularVelocity;
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);
    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Rotation);
    public Vector3 ToWorld(Vector3 point) => Position + Vector3.Transform(point * Scale, Rotation);
    public Vector3 ToModel(Vector3 point) => Vector3.Transform(point - Position, Quaternion.Inverse(Rotation)) / Scale;
    public Vector3 DirectionToModel(Vector3 direction) => Vector3.Transform(direction, Quaternion.Inverse(Rotation));
}
