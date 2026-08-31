using System;
using System.IO;

namespace Blowtorch;

public enum EditorAssetKind
{
    Model,
    Material,
    Texture,
    Skybox,
    GeometryGraph
}

public sealed class EditorAssetEntry
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required EditorAssetKind Kind { get; init; }
    public bool Broken { get; set; }
    public string Error { get; set; } = "";

    public string DisplayName => Path.GetFileNameWithoutExtension(RelativePath);
}

/// <summary>
/// The ImGui payload is intentionally empty. The source and target are in the
/// same editor frame, so this avoids unsafe pointer marshalling while keeping
/// paths in their canonical res-relative form.
/// </summary>
public static class AssetDragDrop
{
    public static EditorAssetKind CurrentKind { get; private set; }
    public static string CurrentPath { get; private set; } = "";

    public static void Publish(EditorAssetEntry entry)
    {
        CurrentKind = entry.Kind;
        CurrentPath = entry.RelativePath;
    }
}
