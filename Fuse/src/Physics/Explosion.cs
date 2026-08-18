using System.Numerics;
using JoltPhysicsSharp;

namespace Fuse.Physics;

public static class Explosion
{
    public static void Apply(PhysicsWorld world, Vector3 origin, float radius, float force, Audio.AudioSystem? audio = null)
    {
        audio?.Play3D("Audio/explosion.wav", origin, 1.0f, maxDist: radius * 2f);

        var bi = world.BodyInterface;
        var narrow = world.NarrowPhaseQuery;

        using var sphere = new SphereShape(radius);
        Vector3 scale = Vector3.One;
        Matrix4x4 transform = Matrix4x4.CreateTranslation(origin);
        Vector3 baseOffset = Vector3.Zero;

        var results = new List<CollideShapeResult>();
        using var bpFilter = new DefaultBroadPhaseLayerFilter();
        using var olFilter = new DefaultObjectLayerFilter();
        using var bodyFilter = new DefaultBodyFilter();
        using var shapeFilter = new DefaultShapeFilter();

        narrow.CollideShape(sphere, ref scale, ref transform, ref baseOffset,
            CollisionCollectorType.AllHit, results,
            bpFilter, olFilter, bodyFilter, shapeFilter);

        var processed = new HashSet<BodyID>();
        foreach (var r in results)
        {
            var id = r.BodyID2;
            if (!id.IsValid || !processed.Add(id)) continue;

            var bli = world.Native.BodyLockInterface;
            BodyLockRead bodyLock = default;
            bli.LockRead(id, out bodyLock);
            if (!bodyLock.Succeeded) continue;

            var body = bodyLock.Body;
            if (!body.IsDynamic) { bli.UnlockRead(bodyLock); continue; }

            float invMass = body.MotionProperties.InverseMassUnchecked;
            Vector3 bodyPos = body.Position;
            bli.UnlockRead(bodyLock);

            if (invMass <= 0) continue;

            Vector3 dir = bodyPos - origin;
            float dist = dir.Length();
            if (dist < 0.001f) dir = Vector3.UnitY;
            else dir /= dist;

            float falloff = float.Max(0f, 1f - dist / radius);
            float mag = force * falloff;

            bi.AddImpulse(id, dir * mag);
        }
    }
}