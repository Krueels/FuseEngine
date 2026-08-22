using System.Numerics;

namespace Fuse.Animation;

public sealed class Bone
{
    public required string Name { get; init; }
    public int Index { get; init; }
    public int NodeIndex { get; init; } = -1;
    public Matrix4x4 OffsetMatrix { get; init; }
}
