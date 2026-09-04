using Fuse.Renderer;

namespace Fuse.Animation;

public sealed class SkinnedSubmesh
{
    public string Name { get; set; } = "";
    public int MaterialSlot { get; set; }
    public string MaterialPath { get; set; } = "";
    public Renderer.Materials.MaterialRuntime? Material { get; set; }
    public required SkinnedMesh Mesh { get; init; }
    public Texture? Texture { get; set; }
}

public sealed class SkinnedModel : IDisposable
{
    private bool _ownsMeshes = true;
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

    public SkinnedModel CreateInstance() => new()
    {
        SourcePath = SourcePath,
        Skeleton = Skeleton.CreateInstance(),
        Submeshes = Submeshes.Select(s => new SkinnedSubmesh
        {
            Name = s.Name, MaterialSlot = s.MaterialSlot, MaterialPath = s.MaterialPath,
            Material = s.Material, Mesh = s.Mesh, Texture = s.Texture
        }).ToArray(),
        Clips = Clips, DefaultClipName = DefaultClipName,
        HiddenSubmeshes = new HashSet<string>(HiddenSubmeshes, HiddenSubmeshes.Comparer),
        _ownsMeshes = false
    };

    public void Dispose()
    {
        if (!_ownsMeshes) return;
        foreach (var sub in Submeshes)
            sub.Mesh.Dispose();
    }
}
