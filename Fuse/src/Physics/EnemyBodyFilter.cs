using JoltPhysicsSharp;

namespace Fuse.Physics;

public sealed class EnemyBodyFilter : BodyFilter
{
    private readonly BodyID _excludeId;

    public EnemyBodyFilter(BodyID excludeId)
    {
        _excludeId = excludeId;
    }

    protected override bool ShouldCollide(BodyID bodyId)
    {
        return bodyId != _excludeId;
    }

    protected override bool ShouldCollideLocked(Body body)
    {
        return body.ID != _excludeId;
    }
}