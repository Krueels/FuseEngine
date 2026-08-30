using System;
using System.Collections.Generic;
using System.Numerics;

namespace Fuse.Scene.Model;

public static class CSGOperations
{
    private const float Epsilon = 0.005f;

    private static string? ResolveFaceMaterial(Brush brush, int slot)
    {
        if (slot >= 0 && slot < brush.MaterialSlots.Count &&
            !string.IsNullOrWhiteSpace(brush.MaterialSlots[slot]))
            return brush.MaterialSlots[slot];
        return string.IsNullOrWhiteSpace(brush.MaterialPath) ? null : brush.MaterialPath;
    }

    private static int ImportFaceMaterial(Brush destination, Brush source, int sourceSlot)
    {
        string? materialPath = ResolveFaceMaterial(source, sourceSlot);
        if (materialPath == null)
            return 0;

        int existing = destination.MaterialSlots.FindIndex(path =>
            path.Equals(materialPath, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            return existing;

        // Preserve the destination's implicit slot zero before adding a material
        // imported from the second brush.
        if (destination.MaterialSlots.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(destination.MaterialPath))
                return 0; // Keep legacy-texture CSG behavior unchanged.
            destination.MaterialSlots.Add(destination.MaterialPath);
            if (destination.MaterialPath.Equals(materialPath, StringComparison.OrdinalIgnoreCase))
                return 0;
        }

        destination.MaterialSlots.Add(materialPath);
        return destination.MaterialSlots.Count - 1;
    }

    public static Brush ToWorldSpace(Brush brush)
    {
        var worldBrush = new Brush
        {
            Id = brush.Id,
            Texture = brush.Texture,
            MaterialPath = brush.MaterialPath,
            MaterialSlots = brush.MaterialSlots.ToList(),
            Visible = brush.Visible,
            ParentId = brush.ParentId,
            UvScale = brush.UvScale,
            ModelScale = brush.ModelScale,
            Body = brush.Body != null ? new MapBody
            {
                Shape = brush.Body.Shape,
                Position = brush.Body.Position,
                Rotation = brush.Body.Rotation,
                HalfExtents = brush.Body.HalfExtents
            } : null
        };

        Vector3 pos = brush.Body?.Position ?? Vector3.Zero;
        Quaternion rot = brush.Body?.Rotation ?? Quaternion.Identity;

        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);

        foreach (var face in brush.Faces)
        {
            Vector3 worldNormal = Vector3.TransformNormal(face.Plane.Normal, m);
            worldNormal = Vector3.Normalize(worldNormal);

            Vector3 localPoint = -face.Plane.Normal * face.Plane.D;
            Vector3 worldPoint = Vector3.Transform(localPoint, m);

            float worldD = -Vector3.Dot(worldNormal, worldPoint);
            var worldPlane = new Plane(worldNormal, worldD);

            Vector3 worldU = Vector3.TransformNormal(face.UAxis, m);
            Vector3 worldV = Vector3.TransformNormal(face.VAxis, m);

            worldBrush.Faces.Add(new Face(worldPlane)
            {
                Texture = face.Texture,
                MaterialSlot = face.MaterialSlot,
                UAxis = worldU,
                VAxis = worldV,
                UScale = face.UScale,
                VScale = face.VScale,
                UOffset = face.UOffset,
                VOffset = face.VOffset,
                Rotation = face.Rotation
            });
        }

        return worldBrush;
    }

    public static Brush ToLocalSpace(Brush worldBrush, Vector3 newCenter, Vector3 halfExtents)
    {
        var localBrush = new Brush
        {
            Id = worldBrush.Id,
            Texture = worldBrush.Texture,
            MaterialPath = worldBrush.MaterialPath,
            MaterialSlots = worldBrush.MaterialSlots.ToList(),
            Visible = worldBrush.Visible,
            ParentId = worldBrush.ParentId,
            UvScale = worldBrush.UvScale,
            ModelScale = worldBrush.ModelScale,
            Body = new MapBody
            {
                Shape = MapShapeType.Box,
                Position = newCenter,
                Rotation = Quaternion.Identity,
                HalfExtents = halfExtents
            }
        };

        foreach (var face in worldBrush.Faces)
        {
            float localD = face.Plane.D + Vector3.Dot(face.Plane.Normal, newCenter);
            var localPlane = new Plane(face.Plane.Normal, localD);

            localBrush.Faces.Add(new Face(localPlane)
            {
                Texture = face.Texture,
                MaterialSlot = face.MaterialSlot,
                UAxis = face.UAxis,
                VAxis = face.VAxis,
                UScale = face.UScale,
                VScale = face.VScale,
                UOffset = face.UOffset,
                VOffset = face.VOffset,
                Rotation = face.Rotation
            });
        }

        return localBrush;
    }

    public static bool IsValidSolid(Brush brush, out Vector3 min, out Vector3 max)
    {
        min = Vector3.Zero;
        max = Vector3.Zero;

        var uniqueVerts = new List<Vector3>();
        var faces = brush.Faces;
        int numFaces = faces.Count;

        for (int i = 0; i < numFaces - 2; i++)
        {
            for (int j = i + 1; j < numFaces - 1; j++)
            {
                for (int k = j + 1; k < numFaces; k++)
                {
                    if (MeshGenerator.TryGetIntersection(faces[i].Plane, faces[j].Plane, faces[k].Plane, out Vector3 p))
                    {
                        bool valid = true;
                        for (int m = 0; m < numFaces; m++)
                        {
                            if (m == i || m == j || m == k) continue;
                            float dist = Vector3.Dot(faces[m].Plane.Normal, p) + faces[m].Plane.D;
                            if (dist > Epsilon) 
                            {
                                valid = false;
                                break;
                            }
                        }

                        if (valid)
                        {
                            MeshGenerator.AddUniqueVertex(uniqueVerts, p);
                        }
                    }
                }
            }
        }

        if (uniqueVerts.Count < 4) return false;

        min = uniqueVerts[0];
        max = uniqueVerts[0];
        foreach (var v in uniqueVerts)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        Vector3 size = max - min;
        if (size.X < 0.01f || size.Y < 0.01f || size.Z < 0.01f)
            return false;

        return true;
    }

    public static void SplitBrush(Brush brush, Plane plane, Face templateFace, out Brush? outside, out Brush? inside)
    {
        outside = CreateBrushWithExtraPlane(brush, new Plane(-plane.Normal, -plane.D), templateFace);
        inside = CreateBrushWithExtraPlane(brush, plane, templateFace);
    }

    private static Brush? CreateBrushWithExtraPlane(Brush original, Plane newPlane, Face templateFace)
    {
        var newBrush = new Brush
        {
            Id = "brush_" + Guid.NewGuid().ToString().Substring(0, 8),
            Texture = original.Texture,
            MaterialPath = original.MaterialPath,
            MaterialSlots = original.MaterialSlots.ToList(),
            Visible = original.Visible,
            ParentId = original.ParentId,
            UvScale = original.UvScale,
            ModelScale = original.ModelScale
        };

        foreach (var face in original.Faces)
        {
            newBrush.Faces.Add(new Face(face.Plane)
            {
                Texture = face.Texture,
                MaterialSlot = face.MaterialSlot,
                UAxis = face.UAxis,
                VAxis = face.VAxis,
                UScale = face.UScale,
                VScale = face.VScale,
                UOffset = face.UOffset,
                VOffset = face.VOffset,
                Rotation = face.Rotation
            });
        }

        bool exists = false;
        foreach (var f in newBrush.Faces)
        {
            if (Vector3.DistanceSquared(f.Plane.Normal, newPlane.Normal) < 0.0001f && 
                System.Math.Abs(f.Plane.D - newPlane.D) < 0.001f)
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            newBrush.Faces.Add(new Face(newPlane)
            {
                Texture = templateFace.Texture,
                MaterialSlot = templateFace.MaterialSlot,
                UScale = templateFace.UScale,
                VScale = templateFace.VScale,
                UOffset = templateFace.UOffset,
                VOffset = templateFace.VOffset,
                Rotation = templateFace.Rotation
            });
        }

        if (!IsValidSolid(newBrush, out _, out _))
            return null;

        return newBrush;
    }

    public static List<Brush> Subtract(Brush target, Brush tool)
    {
        var worldTarget = ToWorldSpace(target);
        var worldTool = ToWorldSpace(tool);

        var toolMaterialSlots = new Dictionary<int, int>();
        foreach (Face toolFace in worldTool.Faces)
        {
            if (!toolMaterialSlots.ContainsKey(toolFace.MaterialSlot))
                toolMaterialSlots[toolFace.MaterialSlot] = ImportFaceMaterial(worldTarget, worldTool, toolFace.MaterialSlot);
        }

        var resultPieces = new List<Brush>();
        var currentPieces = new List<Brush> { worldTarget };

        foreach (var toolFace in worldTool.Faces)
        {
            var nextPieces = new List<Brush>();
            foreach (var p in currentPieces)
            {
                var cutFace = new Face(toolFace.Plane)
                {
                    Texture = toolFace.Texture,
                    MaterialSlot = toolMaterialSlots[toolFace.MaterialSlot],
                    UAxis = toolFace.UAxis,
                    VAxis = toolFace.VAxis,
                    UScale = toolFace.UScale,
                    VScale = toolFace.VScale,
                    UOffset = toolFace.UOffset,
                    VOffset = toolFace.VOffset,
                    Rotation = toolFace.Rotation
                };
                SplitBrush(p, toolFace.Plane, cutFace, out Brush? outside, out Brush? inside);
                
                if (outside != null)
                    resultPieces.Add(outside);
                
                if (inside != null)
                    nextPieces.Add(inside);
            }
            currentPieces = nextPieces;
        }

        var finalizedPieces = new List<Brush>();
        foreach (var piece in resultPieces)
        {
            if (IsValidSolid(piece, out Vector3 min, out Vector3 max))
            {
                Vector3 center = (min + max) / 2.0f;
                Vector3 extents = (max - min) / 2.0f;
                finalizedPieces.Add(ToLocalSpace(piece, center, extents));
            }
        }

        return finalizedPieces;
    }

    public static Brush? Intersect(Brush a, Brush b)
    {
        var worldA = ToWorldSpace(a);
        var worldB = ToWorldSpace(b);

        var newBrush = new Brush
        {
            Id = "brush_" + Guid.NewGuid().ToString().Substring(0, 8),
            Texture = a.Texture,
            MaterialPath = a.MaterialPath,
            MaterialSlots = a.MaterialSlots.ToList(),
            Visible = a.Visible,
            ParentId = a.ParentId,
            UvScale = a.UvScale,
            ModelScale = a.ModelScale
        };

        foreach (var face in worldA.Faces)
        {
            newBrush.Faces.Add(new Face(face.Plane)
            {
                Texture = face.Texture, MaterialSlot = face.MaterialSlot, UAxis = face.UAxis, VAxis = face.VAxis,
                UScale = face.UScale, VScale = face.VScale, UOffset = face.UOffset, VOffset = face.VOffset, Rotation = face.Rotation
            });
        }

        foreach (var face in worldB.Faces)
        {
            bool exists = false;
            foreach (var existing in newBrush.Faces)
            {
                if (Vector3.DistanceSquared(existing.Plane.Normal, face.Plane.Normal) < 0.0001f && 
                    System.Math.Abs(existing.Plane.D - face.Plane.D) < 0.001f)
                {
                    exists = true; break;
                }
            }
            if (!exists)
            {
                newBrush.Faces.Add(new Face(face.Plane)
                {
                    Texture = face.Texture,
                    MaterialSlot = ImportFaceMaterial(newBrush, worldB, face.MaterialSlot),
                    UAxis = face.UAxis, VAxis = face.VAxis,
                    UScale = face.UScale, VScale = face.VScale, UOffset = face.UOffset, VOffset = face.VOffset, Rotation = face.Rotation
                });
            }
        }

        if (IsValidSolid(newBrush, out Vector3 min, out Vector3 max))
        {
            Vector3 center = (min + max) / 2.0f;
            Vector3 extents = (max - min) / 2.0f;
            return ToLocalSpace(newBrush, center, extents);
        }

        return null;
    }

    public static List<Brush> Union(Brush a, Brush b)
    {
        var result = new List<Brush> { b }; // Start with B intact (B is already in local space)
        var aMinusB = Subtract(a, b); // Subtract outputs localized brushes correctly
        result.AddRange(aMinusB);
        return result;
    }

    public static List<Brush> Hollow(Brush brush, float thickness)
    {
        // For hollow, we can just use local space shifting since the tool and target are the same shape!
        var innerBrush = new Brush
        {
            Id = "brush_" + Guid.NewGuid().ToString().Substring(0, 8),
            Texture = brush.Texture,
            MaterialPath = brush.MaterialPath,
            MaterialSlots = brush.MaterialSlots.ToList(),
            Visible = brush.Visible,
            ParentId = brush.ParentId,
            UvScale = brush.UvScale,
            ModelScale = brush.ModelScale,
            Body = brush.Body != null ? new MapBody
            {
                Shape = brush.Body.Shape,
                Position = brush.Body.Position,
                Rotation = brush.Body.Rotation,
                HalfExtents = brush.Body.HalfExtents
            } : null
        };

        foreach (var face in brush.Faces)
        {
            var newPlane = new Plane(face.Plane.Normal, face.Plane.D + thickness);
            innerBrush.Faces.Add(new Face(newPlane)
            {
                Texture = face.Texture, MaterialSlot = face.MaterialSlot, UAxis = face.UAxis, VAxis = face.VAxis,
                UScale = face.UScale, VScale = face.VScale, UOffset = face.UOffset, VOffset = face.VOffset, Rotation = face.Rotation
            });
        }

        if (!IsValidSolid(innerBrush, out _, out _))
        {
            return new List<Brush>();
        }

        return Subtract(brush, innerBrush);
    }
}
