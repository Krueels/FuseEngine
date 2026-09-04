using JoltPhysicsSharp;

namespace Fuse.Physics;

/// <summary>Locomotion sees solid scenery, never damage/trigger sensors or actors.</summary>
public sealed class SurfaceBodyFilter(
    BodyID? self,
    IReadOnlySet<BodyID>? ignored,
    IReadOnlySet<BodyID> actors) : BodyFilter
{
    protected override bool ShouldCollide(BodyID id) =>
        id != self && ignored?.Contains(id) != true && !actors.Contains(id);

    protected override bool ShouldCollideLocked(Body body) =>
        !body.IsSensor && ShouldCollide(body.ID);
}
