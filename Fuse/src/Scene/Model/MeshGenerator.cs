using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Fuse.Renderer;

namespace Fuse.Scene.Model;

public static class MeshGenerator
{
    private const float Epsilon = 0.05f;

    public static MeshData Generate(Brush brush)
    {
        if (brush.IsEditableMesh)
            return brush.EditableMesh!.GenerateMeshData();

        // Correct degenerate UV projection axes (e.g. U/V parallel to the normal)
        foreach (var face in brush.Faces)
        {
            if (System.Math.Abs(Vector3.Dot(face.Plane.Normal, face.UAxis)) > 0.9f ||
                System.Math.Abs(Vector3.Dot(face.Plane.Normal, face.VAxis)) > 0.9f)
            {
                var normal = face.Plane.Normal;
                float nx = System.Math.Abs(normal.X);
                float ny = System.Math.Abs(normal.Y);
                float nz = System.Math.Abs(normal.Z);

                if (nx > ny && nx > nz)
                {
                    face.UAxis = Vector3.UnitZ;
                    face.VAxis = -Vector3.UnitY;
                }
                else if (ny > nx && ny > nz)
                {
                    face.UAxis = Vector3.UnitX;
                    face.VAxis = -Vector3.UnitZ;
                }
                else
                {
                    face.UAxis = Vector3.UnitX;
                    face.VAxis = -Vector3.UnitY;
                }
            }
        }

        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var lineIndices = new List<uint>();
        var parts = new List<MeshPart>();
        var indicesByMaterial = new Dictionary<int, List<uint>>();
        var materialOrder = new List<int>();
        
        var faces = brush.Faces;
        int numFaces = faces.Count;
        
        // Map each face to its generated vertices
        var faceVertices = new Dictionary<Face, List<Vector3>>();
        foreach (var face in faces)
        {
            faceVertices[face] = new List<Vector3>();
        }

        // 1. Find all valid intersection points of 3 planes
        for (int i = 0; i < numFaces - 2; i++)
        {
            for (int j = i + 1; j < numFaces - 1; j++)
            {
                for (int k = j + 1; k < numFaces; k++)
                {
                    if (TryGetIntersection(faces[i].Plane, faces[j].Plane, faces[k].Plane, out Vector3 p))
                    {
                        // Check if p is inside all other planes
                        bool valid = true;
                        for (int m = 0; m < numFaces; m++)
                        {
                            if (m == i || m == j || m == k) continue;
                            
                            // Distance from point to plane
                            float dist = Vector3.Dot(faces[m].Plane.Normal, p) + faces[m].Plane.D;
                            if (dist > Epsilon)
                            {
                                valid = false;
                                break;
                            }
                        }

                        if (valid)
                        {
                            // Add vertex to the faces that share it
                            AddUniqueVertex(faceVertices[faces[i]], p);
                            AddUniqueVertex(faceVertices[faces[j]], p);
                            AddUniqueVertex(faceVertices[faces[k]], p);
                        }
                    }
                }
            }
        }

        // Find minCorner of the brush in local space
        Vector3 minCorner = Vector3.Zero;
        bool hasVerts = false;
        foreach (var polyVerts in faceVertices.Values)
        {
            foreach (var v in polyVerts)
            {
                if (!hasVerts)
                {
                    minCorner = v;
                    hasVerts = true;
                }
                else
                {
                    minCorner = Vector3.Min(minCorner, v);
                }
            }
        }

        // 2. Sort vertices for each face and triangulate
        uint currentIndex = 0;

        foreach (var face in faces)
        {
            var polyVerts = faceVertices[face];
            if (polyVerts.Count < 3) continue;

            // Calculate center of polygon
            Vector3 center = Vector3.Zero;
            foreach (var v in polyVerts)
            {
                center += v;
            }
            center /= polyVerts.Count;

            // Sort vertices counter-clockwise based on face normal
            var normal = face.Plane.Normal;
            
            // Create a local coordinate system for the face
            Vector3 uDir, vDir;
            if (System.Math.Abs(normal.Y) > 0.999f)
            {
                uDir = Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitX));
            }
            else
            {
                uDir = Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY));
            }
            vDir = Vector3.Cross(normal, uDir);

            polyVerts.Sort((a, b) =>
            {
                Vector3 da = a - center;
                Vector3 db = b - center;
                float angleA = (float)System.Math.Atan2(Vector3.Dot(da, vDir), Vector3.Dot(da, uDir));
                float angleB = (float)System.Math.Atan2(Vector3.Dot(db, vDir), Vector3.Dot(db, uDir));
                return angleA.CompareTo(angleB);
            });

            // Calculate UVs and add to global vertex list
            uint startIndex = currentIndex;
            for (int i = 0; i < polyVerts.Count; i++)
            {
                Vector3 pos = polyVerts[i];
                
                // Handle UV rotation, scale, offset relative to minCorner
                float u = Vector3.Dot(pos - minCorner, face.UAxis) / face.UScale + face.UOffset;
                float v = Vector3.Dot(pos - minCorner, face.VAxis) / face.VScale + face.VOffset;
                
                // If there's rotation, we'd apply a 2D rotation matrix here to (u,v).
                if (System.Math.Abs(face.Rotation) > 0.001f)
                {
                    float rad = face.Rotation * (float)System.Math.PI / 180f;
                    float cos = (float)System.Math.Cos(rad);
                    float sin = (float)System.Math.Sin(rad);
                    float nu = u * cos - v * sin;
                    float nv = u * sin + v * cos;
                    u = nu;
                    v = nv;
                }

                vertices.Add(new Vertex
                {
                    Position = pos,
                    Normal = normal,
                    TexCoord = new Vector2(u, v)
                });
                currentIndex++;
            }

            // Triangulate into a per-material bucket. This keeps per-face material
            // assignment without turning every face into a separate draw call.
            int materialSlot = System.Math.Max(0, face.MaterialSlot);
            if (!indicesByMaterial.TryGetValue(materialSlot, out List<uint>? materialIndices))
            {
                materialIndices = [];
                indicesByMaterial[materialSlot] = materialIndices;
                materialOrder.Add(materialSlot);
            }
            for (uint i = 1; i < polyVerts.Count - 1; i++)
            {
                materialIndices.Add(startIndex);
                materialIndices.Add(startIndex + i);
                materialIndices.Add(startIndex + i + 1);
            }

            // Generate Line Indices (edges of the face)
            for (uint i = 0; i < polyVerts.Count; i++)
            {
                lineIndices.Add(startIndex + i);
                lineIndices.Add(startIndex + ((i + 1) % (uint)polyVerts.Count));
            }
        }

        foreach (int materialSlot in materialOrder)
        {
            List<uint> materialIndices = indicesByMaterial[materialSlot];
            uint partIndexOffset = (uint)indices.Count;
            indices.AddRange(materialIndices);
            if (materialIndices.Count > 0)
                parts.Add(new MeshPart(partIndexOffset, (uint)materialIndices.Count, materialSlot));
        }

        return new MeshData(vertices.ToArray(), indices.ToArray(), lineIndices.ToArray(), parts.ToArray());
    }

    public static void AddUniqueVertex(List<Vector3> list, Vector3 v)
    {
        foreach (var item in list)
        {
            if (Vector3.DistanceSquared(item, v) < Epsilon * Epsilon)
            {
                return;
            }
        }
        list.Add(v);
    }

    public static bool TryGetIntersection(Plane p1, Plane p2, Plane p3, out Vector3 result)
    {
        result = Vector3.Zero;
        
        Vector3 n1 = p1.Normal;
        Vector3 n2 = p2.Normal;
        Vector3 n3 = p3.Normal;

        float det = Vector3.Dot(n1, Vector3.Cross(n2, n3));
        
        // If determinant is near zero, planes are parallel or intersect in a line
        if (System.Math.Abs(det) < 0.0000001f)
        {
            return false;
        }

        result = (-p1.D * Vector3.Cross(n2, n3) - p2.D * Vector3.Cross(n3, n1) - p3.D * Vector3.Cross(n1, n2)) / det;
        return true;
    }

    public static MeshData GenerateCapsule(float radius, float height, int rings = 12)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        float halfHeight = height * 0.5f;
        int segments = rings;
        int hemisphereRings = segments / 2;

        // 1. Top hemisphere
        for (int i = 0; i <= hemisphereRings; i++)
        {
            float v = (float)i / hemisphereRings;
            float phi = (1.0f - v) * MathF.PI * 0.5f;
            float y = halfHeight + radius * MathF.Sin(phi);
            float r = radius * MathF.Cos(phi);

            for (int j = 0; j < segments; j++)
            {
                float u = (float)j / segments;
                float theta = u * MathF.PI * 2f;
                float x = r * MathF.Cos(theta);
                float z = r * MathF.Sin(theta);

                Vector3 pos = new(x, y, z);
                Vector3 normal = Vector3.Normalize(new Vector3(x, radius * MathF.Sin(phi), z));

                vertices.Add(new Vertex
                {
                    Position = pos,
                    Normal = normal,
                    TexCoord = new Vector2(u, v * 0.25f)
                });
            }
        }

        // 2. Cylinder body
        int cylinderRings = 4;
        for (int i = 0; i <= cylinderRings; i++)
        {
            float v = (float)i / cylinderRings;
            float y = halfHeight - v * height;

            for (int j = 0; j < segments; j++)
            {
                float u = (float)j / segments;
                float theta = u * MathF.PI * 2f;
                float x = radius * MathF.Cos(theta);
                float z = radius * MathF.Sin(theta);

                Vector3 pos = new(x, y, z);
                Vector3 normal = Vector3.Normalize(new Vector3(x, 0, z));

                vertices.Add(new Vertex
                {
                    Position = pos,
                    Normal = normal,
                    TexCoord = new Vector2(u, 0.25f + v * 0.5f)
                });
            }
        }

        // 3. Bottom hemisphere
        for (int i = 0; i <= hemisphereRings; i++)
        {
            float v = (float)i / hemisphereRings;
            float phi = -v * MathF.PI * 0.5f;
            float y = -halfHeight + radius * MathF.Sin(phi);
            float r = radius * MathF.Cos(phi);

            for (int j = 0; j < segments; j++)
            {
                float u = (float)j / segments;
                float theta = u * MathF.PI * 2f;
                float x = r * MathF.Cos(theta);
                float z = r * MathF.Sin(theta);

                Vector3 pos = new(x, y, z);
                Vector3 normal = Vector3.Normalize(new Vector3(x, radius * MathF.Sin(phi), z));

                vertices.Add(new Vertex
                {
                    Position = pos,
                    Normal = normal,
                    TexCoord = new Vector2(u, 0.75f + v * 0.25f)
                });
            }
        }

        // -------------------------------------------------------------
        // Conexão dos Triângulos (Winding order unificado: a -> b -> c)
        // -------------------------------------------------------------
        int vertsPerRing = segments;

        // Top hemisphere connection
        for (int ring = 0; ring < hemisphereRings; ring++)
        {
            for (int seg = 0; seg < segments; seg++)
            {
                int a = ring * vertsPerRing + seg;
                int b = ring * vertsPerRing + (seg + 1) % segments;
                int c = (ring + 1) * vertsPerRing + seg;
                int d = (ring + 1) * vertsPerRing + (seg + 1) % segments;

                indices.Add((uint)a); indices.Add((uint)b); indices.Add((uint)c);
                indices.Add((uint)b); indices.Add((uint)d); indices.Add((uint)c);
            }
        }

        // Cylinder body connection
        int cylinderStart = (hemisphereRings + 1) * vertsPerRing;
        for (int ring = 0; ring < cylinderRings; ring++)
        {
            for (int seg = 0; seg < segments; seg++)
            {
                int a = cylinderStart + ring * vertsPerRing + seg;
                int b = cylinderStart + ring * vertsPerRing + (seg + 1) % segments;
                int c = cylinderStart + (ring + 1) * vertsPerRing + seg;
                int d = cylinderStart + (ring + 1) * vertsPerRing + (seg + 1) % segments;

                indices.Add((uint)a); indices.Add((uint)b); indices.Add((uint)c);
                indices.Add((uint)b); indices.Add((uint)d); indices.Add((uint)c);
            }
        }

        // Bottom hemisphere connection
        int bottomStart = cylinderStart + (cylinderRings + 1) * vertsPerRing;
        for (int ring = 0; ring < hemisphereRings; ring++)
        {
            for (int seg = 0; seg < segments; seg++)
            {
                int a = bottomStart + ring * vertsPerRing + seg;
                int b = bottomStart + ring * vertsPerRing + (seg + 1) % segments;
                int c = bottomStart + (ring + 1) * vertsPerRing + seg;
                int d = bottomStart + (ring + 1) * vertsPerRing + (seg + 1) % segments;

                indices.Add((uint)a); indices.Add((uint)b); indices.Add((uint)c);
                indices.Add((uint)b); indices.Add((uint)d); indices.Add((uint)c);
            }
        }

        return new MeshData(vertices.ToArray(), indices.ToArray());
    }
}

public class MeshData
{
    public Vertex[] Vertices { get; }
    public uint[] Indices { get; }
    public uint[] LineIndices { get; }
    public MeshPart[] Parts { get; }

    public MeshData(Vertex[] vertices, uint[] indices, uint[]? lineIndices = null, MeshPart[]? parts = null)
    {
        Vertices = vertices;
        Indices = indices;
        LineIndices = lineIndices ?? [];
        Parts = parts ?? [new MeshPart(0, (uint)indices.Length, 0)];
    }
}
