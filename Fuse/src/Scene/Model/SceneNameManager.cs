using System.Collections.Generic;
using System.Linq;

namespace Fuse.Scene.Model;

public static class SceneNameManager
{
    public static IReadOnlyList<string> ValidateAndRepairHierarchy(MapDocument doc)
    {
        var warnings = new List<string>();
        var byId = doc.Objects
            .GroupBy(obj => obj.Id, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), System.StringComparer.OrdinalIgnoreCase);

        foreach (MapObject obj in doc.Objects)
        {
            if (string.IsNullOrWhiteSpace(obj.ParentId))
            {
                obj.ParentId = null;
                continue;
            }

            obj.ParentId = obj.ParentId.Trim();
            if (obj.Id.Equals(obj.ParentId, System.StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Object '{obj.Id}' could not be its own parent; the parent link was removed.");
                obj.ParentId = null;
            }
            else if (!byId.ContainsKey(obj.ParentId))
            {
                warnings.Add($"Object '{obj.Id}' referenced missing parent '{obj.ParentId}'; the parent link was removed.");
                obj.ParentId = null;
            }
        }

        foreach (MapObject obj in doc.Objects)
        {
            var visited = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            MapObject? current = obj;
            while (current != null)
            {
                if (!visited.Add(current.Id))
                {
                    warnings.Add($"Hierarchy cycle involving '{obj.Id}' was repaired by detaching it from its parent.");
                    obj.ParentId = null;
                    break;
                }

                if (string.IsNullOrEmpty(current.ParentId))
                    break;
                current = byId.GetValueOrDefault(current.ParentId);
            }
        }

        return warnings;
    }

    /// <summary>
    /// Gets a unique name for an object by appending a suffix (e.g. _1, _2) if there is a conflict.
    /// </summary>
    public static string GetUniqueName(MapDocument doc, MapObject currentObj, string proposedName)
    {
        if (string.IsNullOrWhiteSpace(proposedName))
        {
            proposedName = "object";
        }

        proposedName = proposedName.Trim();

        // If there's no conflict with other objects, return the proposed name
        if (!doc.Objects.Any(o => o != currentObj && o.Id == proposedName))
        {
            return proposedName;
        }

        // Find a suffix that makes the name unique
        int suffix = 1;
        string baseName = proposedName;
        
        // If proposedName already ends with _number, extract it as the starting base and suffix
        int underscoreIdx = proposedName.LastIndexOf('_');
        if (underscoreIdx != -1 && underscoreIdx < proposedName.Length - 1)
        {
            string suffixStr = proposedName.Substring(underscoreIdx + 1);
            if (int.TryParse(suffixStr, out int existingSuffix))
            {
                baseName = proposedName.Substring(0, underscoreIdx);
                suffix = existingSuffix + 1;
            }
        }

        while (true)
        {
            string candidate = $"{baseName}_{suffix}";
            if (!doc.Objects.Any(o => o != currentObj && o.Id == candidate))
            {
                return candidate;
            }
            suffix++;
        }
    }

    /// <summary>
    /// Scans all objects in order and automatically renames duplicate IDs, preserving the first seen.
    /// </summary>
    public static void EnsureAllUnique(MapDocument doc)
    {
        var seenIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var obj in doc.Objects)
        {
            if (string.IsNullOrWhiteSpace(obj.Id))
            {
                obj.Id = "object";
            }

            obj.Id = obj.Id.Trim();

            if (seenIds.Contains(obj.Id))
            {
                int suffix = 1;
                string baseName = obj.Id;
                
                int underscoreIdx = obj.Id.LastIndexOf('_');
                if (underscoreIdx != -1 && underscoreIdx < obj.Id.Length - 1)
                {
                    string suffixStr = obj.Id.Substring(underscoreIdx + 1);
                    if (int.TryParse(suffixStr, out int existingSuffix))
                    {
                        baseName = obj.Id.Substring(0, underscoreIdx);
                        suffix = existingSuffix + 1;
                    }
                }

                string candidate = $"{baseName}_{suffix}";
                while (seenIds.Contains(candidate) || doc.Objects.Any(o => o != obj && o.Id == candidate))
                {
                    suffix++;
                    candidate = $"{baseName}_{suffix}";
                }
                
                obj.Id = candidate;
            }
            
            seenIds.Add(obj.Id);
        }
    }
}
