using Fuse.Renderer.Materials;

namespace Blowtorch;

public enum MaterialGraphDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record MaterialGraphDiagnostic(
    MaterialGraphDiagnosticSeverity Severity,
    string Message,
    string NodeId = "");

/// <summary>
/// Editor-side validation kept separate from shader generation. It gives the
/// graph editor useful diagnostics without changing the legacy .fmat format.
/// </summary>
public static class MaterialGraphValidator
{
    public static IReadOnlyList<MaterialGraphDiagnostic> Validate(MaterialAsset asset)
    {
        MaterialGraph graph = asset.Graph;
        var diagnostics = new List<MaterialGraphDiagnostic>();
        var nodesById = new Dictionary<string, MaterialGraphNode>(StringComparer.Ordinal);

        foreach (MaterialGraphNode node in graph.Nodes)
        {
            if (!nodesById.TryAdd(node.Id, node))
                diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                    $"Duplicate node id '{node.Id}'.", node.Id));
            if (MaterialNodeCatalog.Find(node.Type) == null)
                diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                    $"Unsupported node type '{node.Type}'.", node.Id));
        }

        MaterialGraphNode[] outputs = graph.Nodes.Where(node => node.Type == "PBROutput").ToArray();
        if (outputs.Length == 0)
            diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error, "The graph needs one Material Output node."));
        else if (outputs.Length > 1)
            diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error, "The graph can contain only one Material Output node."));

        var occupiedInputs = new HashSet<(string Node, string Socket)>();
        foreach (MaterialGraphLink link in graph.Links)
        {
            if (!nodesById.TryGetValue(link.FromNode, out MaterialGraphNode? source))
            {
                diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                    "Connection source node does not exist."));
                continue;
            }
            if (!nodesById.TryGetValue(link.ToNode, out MaterialGraphNode? target))
            {
                diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                    "Connection target node does not exist."));
                continue;
            }

            MaterialNodeDefinition? sourceDefinition = MaterialNodeCatalog.Find(source.Type);
            MaterialNodeDefinition? targetDefinition = MaterialNodeCatalog.Find(target.Type);
            MaterialSocketDefinition? sourceSocket = sourceDefinition?.Outputs.FirstOrDefault(socket =>
                socket.Name.Equals(link.FromSocket, StringComparison.OrdinalIgnoreCase));
            MaterialSocketDefinition? targetSocket = targetDefinition?.Inputs.FirstOrDefault(socket =>
                socket.Name.Equals(link.ToSocket, StringComparison.OrdinalIgnoreCase));

            if (sourceSocket == null)
                diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                    $"'{link.FromSocket}' is not an output of '{source.Name}'.", source.Id));
            if (targetSocket == null)
                diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                    $"'{link.ToSocket}' is not an input of '{target.Name}'.", target.Id));
            if (sourceSocket != null && targetSocket != null &&
                !CanConvert(sourceSocket.Value.Type, targetSocket.Value.Type))
                diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                    $"Cannot convert {sourceSocket.Value.Type} to {targetSocket.Value.Type}.", target.Id));

            if (!occupiedInputs.Add((link.ToNode, link.ToSocket)))
                diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Warning,
                    $"Input '{target.Name}.{link.ToSocket}' has multiple connections; the last one wins.", target.Id));
        }

        if (outputs.Length == 1)
        {
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            Visit(outputs[0], graph, visiting, visited, diagnostics);
        }

        foreach (MaterialGraphNode node in graph.Nodes)
        {
            if (node.Type is "Frame" or "Comment" or "PBROutput")
                continue;
            if (node.Type is "Texture2D" or "ScalarTexture" or "PackedMetallicRoughness" or
                "TriplanarTexture" or "TriplanarNormal")
            {
                string path = MaterialAsset.GetString(node.Properties, "path", "");
                if (string.IsNullOrWhiteSpace(path))
                    diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Warning,
                        $"Texture node '{node.Name}' has no asset assigned.", node.Id));
                else if (!File.Exists(MaterialRuntime.ResolveAssetPath(path)))
                    diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                        $"Texture asset was not found: {path}.", node.Id));
            }
            else if (node.Type == "Texture2DArray")
            {
                List<string> paths = MaterialAsset.GetStringArray(node.Properties, "paths");
                if (paths.Count == 0)
                    diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Warning,
                        $"Texture array node '{node.Name}' has no layers assigned.", node.Id));
                foreach (string path in paths)
                {
                    if (!File.Exists(MaterialRuntime.ResolveAssetPath(path)))
                        diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                            $"Texture array asset was not found: {path}.", node.Id));
                }
            }
            else if (node.Type == "TerrainLayer")
            {
                string[] properties = ["albedo_paths", "normal_paths", "orm_paths", "height_paths"];
                foreach (string property in properties)
                {
                    foreach (string path in MaterialAsset.GetStringArray(node.Properties, property))
                    {
                        if (!File.Exists(MaterialRuntime.ResolveAssetPath(path)))
                            diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                                $"Terrain layer asset was not found: {path}.", node.Id));
                    }
                }
                if (MaterialAsset.GetStringArray(node.Properties, "albedo_paths").Count == 0)
                    diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Warning,
                        $"Terrain layer '{node.Name}' has no albedo layers assigned.", node.Id));
            }
        }

        return diagnostics;
    }

    public static bool CanConvert(MaterialValueType source, MaterialValueType target) =>
        source is MaterialValueType.Float or MaterialValueType.Vector2 or MaterialValueType.Vector3 &&
        target is MaterialValueType.Float or MaterialValueType.Vector2 or MaterialValueType.Vector3;

    private static void Visit(
        MaterialGraphNode node,
        MaterialGraph graph,
        HashSet<string> visiting,
        HashSet<string> visited,
        List<MaterialGraphDiagnostic> diagnostics)
    {
        if (!visiting.Add(node.Id))
        {
            diagnostics.Add(new(MaterialGraphDiagnosticSeverity.Error,
                $"Cycle detected around '{node.Name}'.", node.Id));
            return;
        }
        if (!visited.Add(node.Id))
        {
            visiting.Remove(node.Id);
            return;
        }

        foreach (MaterialGraphLink link in graph.Links.Where(link => link.ToNode == node.Id))
        {
            MaterialGraphNode? source = graph.FindNode(link.FromNode);
            if (source != null && source.Type is not "Frame" and not "Comment")
                Visit(source, graph, visiting, visited, diagnostics);
        }
        visiting.Remove(node.Id);
    }
}
