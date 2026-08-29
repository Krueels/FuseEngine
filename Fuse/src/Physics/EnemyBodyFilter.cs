using JoltPhysicsSharp;

namespace Fuse.Physics;

public sealed class EnemyBodyFilter : BodyFilter
{
    private readonly BodyID? _excludeId;
    private readonly System.Collections.Generic.IReadOnlySet<BodyID>? _excludedIds;

    public EnemyBodyFilter(BodyID excludeId)
    {
        _excludeId = excludeId;
    }

    public EnemyBodyFilter(
        BodyID excludeId,
        System.Collections.Generic.IReadOnlySet<BodyID>? excludedIds)
    {
        _excludeId = excludeId;
        _excludedIds = excludedIds;
    }

    public EnemyBodyFilter(
        System.Collections.Generic.IReadOnlySet<BodyID> excludedIds)
    {
        _excludedIds = excludedIds;
    }

    private bool IsExcluded(BodyID bodyId) =>
        (_excludeId.HasValue && bodyId == _excludeId.Value) ||
        (_excludedIds?.Contains(bodyId) == true);

    protected override bool ShouldCollide(BodyID bodyId)
    {
        return !IsExcluded(bodyId);
    }

    protected override bool ShouldCollideLocked(Body body)
    {
        return !IsExcluded(body.ID);
    }
}
