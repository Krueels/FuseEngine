using Fuse.Renderer;

namespace Fuse.Animation;

public sealed class SkinnedSubmesh
{
    public string Name { get; set; } = "";
    public required SkinnedMesh Mesh { get; init; }
    public Texture? Texture { get; set; }
}

public sealed class SkinnedModel : IDisposable
{
    public required string SourcePath { get; init; }
    public required Skeleton Skeleton { get; init; }
    public required SkinnedSubmesh[] Submeshes { get; init; }
    public required Dictionary<string, AnimationClip> Clips { get; init; }
    public string DefaultClipName { get; set; } = "";
    public HashSet<string> HiddenSubmeshes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Link(Animator animator)
    {
        animator.Model = this;
    }

    public void Dispose()
    {
        foreach (var sub in Submeshes)
            sub.Mesh.Dispose();
    }
}
