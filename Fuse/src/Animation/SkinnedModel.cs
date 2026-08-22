using Fuse.Renderer;

namespace Fuse.Animation;

public sealed class SkinnedSubmesh
{
    public required SkinnedMesh Mesh { get; init; }
    public Texture? Texture { get; init; }
}

public sealed class SkinnedModel : IDisposable
{
    public required string SourcePath { get; init; }
    public required Skeleton Skeleton { get; init; }
    public required SkinnedSubmesh[] Submeshes { get; init; }
    public required Dictionary<string, AnimationClip> Clips { get; init; }
    public string DefaultClipName { get; set; } = "";

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
