using System.Numerics;
using Silk.NET.OpenGL;
using Silk.NET.Assimp;
using Fuse.Core;

namespace Fuse.Renderer;

public class LoadedModel : IDisposable
{
    public Mesh? Mesh { get; set; }
    public Mesh? CollMesh { get; set; }
    public Mesh? ConvexCollMesh { get; set; }
    public Vector3[] CollVertices { get; set; } = [];
    public uint[] CollIndices { get; set; } = [];
    public int MaterialSlotCount { get; set; }

    public void Dispose()
    {
        Mesh?.Dispose();
        CollMesh?.Dispose();
        ConvexCollMesh?.Dispose();
    }
}

public class HullVertex : MIConvexHull.IVertex
{
    public double[] Position { get; set; }
}

public static unsafe class ModelLoader
{
    private static Assimp? s_assimp;

    private static Assimp Api => s_assimp ??= Assimp.GetApi();

    public static LoadedModel? Load(GL gl, string path)
    {
        string cleanPath = path;
        int meshIndex = -1;
        int hashIdx = path.IndexOf('#');
        if (hashIdx != -1)
        {
            cleanPath = path.Substring(0, hashIdx);
            int.TryParse(path.Substring(hashIdx + 1), out meshIndex);
        }

        if (!System.IO.File.Exists(cleanPath))
        {
            Logger.Error($"Model file not found: {cleanPath}");
            return null;
        }

        var scene = Api.ImportFile(cleanPath,
            (uint)(PostProcessSteps.Triangulate | PostProcessSteps.GenerateSmoothNormals |
                   PostProcessSteps.TransformUVCoords | (PostProcessSteps)0x1u));

        if (scene == null || scene->MRootNode == null || scene->MNumMeshes == 0)
        {
            Logger.Error($"Failed to load model: {cleanPath}");
            return null;
        }

        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var collVerts = new List<Vector3>();
        var meshParts = new List<MeshPart>();
        int materialSlotCount = 0;

        int startMesh = 0;
        int endMesh = (int)scene->MNumMeshes;
        if (meshIndex >= 0 && meshIndex < (int)scene->MNumMeshes)
        {
            startMesh = meshIndex;
            endMesh = meshIndex + 1;
        }

        for (int m = startMesh; m < endMesh; m++)
        {
            var mesh = scene->MMeshes[m];
            uint baseIndex = (uint)vertices.Count;
            uint partIndexOffset = (uint)indices.Count;

            for (int i = 0; i < mesh->MNumVertices; i++)
            {
                var pos = new Vector3(
                    mesh->MVertices[i].X,
                    mesh->MVertices[i].Y,
                    mesh->MVertices[i].Z);

                var uv = Vector2.Zero;
                if (mesh->MTextureCoords[0] != null)
                    uv = new Vector2(mesh->MTextureCoords[0][i].X, mesh->MTextureCoords[0][i].Y);

                var normal = new Vector3(0, 1, 0);
                if (mesh->MNormals != null)
                    normal = new Vector3(mesh->MNormals[i].X, mesh->MNormals[i].Y, mesh->MNormals[i].Z);

                var tangent = mesh->MTangents != null
                    ? new Vector3(mesh->MTangents[i].X, mesh->MTangents[i].Y, mesh->MTangents[i].Z)
                    : Vector3.Zero;
                var bitangent = mesh->MBitangents != null
                    ? new Vector3(mesh->MBitangents[i].X, mesh->MBitangents[i].Y, mesh->MBitangents[i].Z)
                    : Vector3.Zero;
                vertices.Add(new Vertex { Position = pos, TexCoord = uv, Normal = normal, Tangent = tangent, Bitangent = bitangent });
                collVerts.Add(pos);
            }

            for (int i = 0; i < mesh->MNumFaces; i++)
            {
                var face = mesh->MFaces[i];
                for (int j = 0; j < face.MNumIndices; j++)
                    indices.Add(face.MIndices[j] + baseIndex);
            }

            uint partIndexCount = (uint)indices.Count - partIndexOffset;
            int materialSlot = (int)mesh->MMaterialIndex;
            meshParts.Add(new MeshPart(partIndexOffset, partIndexCount, materialSlot));
            materialSlotCount = System.Math.Max(materialSlotCount, materialSlot + 1);
        }

        Api.ReleaseImport(scene);

        var resultMesh = new Mesh(gl, [.. vertices], [.. indices], null, [.. meshParts]);
        Logger.Asset($"Model loaded: {path} ({vertices.Count} verts, {indices.Count} indices)");

        var collLineIndices = new List<uint>();
        int countMinus2 = indices.Count - 2;
        for (int i = 0; i < countMinus2; i += 3)
        {
            collLineIndices.Add(indices[i]);
            collLineIndices.Add(indices[i + 1]);
            collLineIndices.Add(indices[i + 1]);
            collLineIndices.Add(indices[i + 2]);
            collLineIndices.Add(indices[i + 2]);
            collLineIndices.Add(indices[i]);
        }

        var collMeshVerts = new Vertex[collVerts.Count];
        for (int i = 0; i < collVerts.Count; i++) collMeshVerts[i] = new Vertex { Position = collVerts[i] };
        
        var resultCollMesh = new Mesh(gl, collMeshVerts, [.. indices], [.. collLineIndices]);

        var resultConvexMesh = GenerateConvexHullMesh(gl, collVerts);

        return new LoadedModel
        {
            Mesh = resultMesh,
            CollMesh = resultCollMesh,
            ConvexCollMesh = resultConvexMesh,
            CollVertices = [.. collVerts],
            CollIndices = [.. indices],
            MaterialSlotCount = materialSlotCount,
        };
    }

    public static LoadedModel?[] LoadAllSubmeshes(GL gl, string cleanPath)
    {
        if (!System.IO.File.Exists(cleanPath))
        {
            Logger.Error($"Model file not found: {cleanPath}");
            return [];
        }

        var scene = Api.ImportFile(cleanPath,
            (uint)(PostProcessSteps.Triangulate | PostProcessSteps.GenerateSmoothNormals |
                   PostProcessSteps.TransformUVCoords | (PostProcessSteps)0x1u));

        if (scene == null || scene->MRootNode == null || scene->MNumMeshes == 0)
        {
            Logger.Error($"Failed to load model submeshes: {cleanPath}");
            return [];
        }

        int count = (int)scene->MNumMeshes;
        var results = new LoadedModel?[count];

        for (int m = 0; m < count; m++)
        {
            var mesh = scene->MMeshes[m];
            var vertices = new List<Vertex>();
            var indices = new List<uint>();
            var collVerts = new List<Vector3>();
            int materialSlot = (int)mesh->MMaterialIndex;

            for (int i = 0; i < mesh->MNumVertices; i++)
            {
                var pos = new Vector3(
                    mesh->MVertices[i].X,
                    mesh->MVertices[i].Y,
                    mesh->MVertices[i].Z);

                var uv = Vector2.Zero;
                if (mesh->MTextureCoords[0] != null)
                    uv = new Vector2(mesh->MTextureCoords[0][i].X, mesh->MTextureCoords[0][i].Y);

                var normal = new Vector3(0, 1, 0);
                if (mesh->MNormals != null)
                    normal = new Vector3(mesh->MNormals[i].X, mesh->MNormals[i].Y, mesh->MNormals[i].Z);

                var tangent = mesh->MTangents != null
                    ? new Vector3(mesh->MTangents[i].X, mesh->MTangents[i].Y, mesh->MTangents[i].Z)
                    : Vector3.Zero;
                var bitangent = mesh->MBitangents != null
                    ? new Vector3(mesh->MBitangents[i].X, mesh->MBitangents[i].Y, mesh->MBitangents[i].Z)
                    : Vector3.Zero;
                vertices.Add(new Vertex { Position = pos, TexCoord = uv, Normal = normal, Tangent = tangent, Bitangent = bitangent });
                collVerts.Add(pos);
            }

            for (int i = 0; i < mesh->MNumFaces; i++)
            {
                var face = mesh->MFaces[i];
                for (int j = 0; j < face.MNumIndices; j++)
                    indices.Add(face.MIndices[j]);
            }

            var resultMesh = new Mesh(gl, [.. vertices], [.. indices], null,
                [new MeshPart(0, (uint)indices.Count, materialSlot)]);

            var collLineIndices = new List<uint>();
            int countMinus2 = indices.Count - 2;
            for (int j = 0; j < countMinus2; j += 3)
            {
                collLineIndices.Add(indices[j]);
                collLineIndices.Add(indices[j + 1]);
                collLineIndices.Add(indices[j + 1]);
                collLineIndices.Add(indices[j + 2]);
                collLineIndices.Add(indices[j + 2]);
                collLineIndices.Add(indices[j]);
            }

            var collMeshVerts = new Vertex[collVerts.Count];
            for (int j = 0; j < collVerts.Count; j++) collMeshVerts[j] = new Vertex { Position = collVerts[j] };
            
            var resultCollMesh = new Mesh(gl, collMeshVerts, [.. indices], [.. collLineIndices]);
            var resultConvexMesh = GenerateConvexHullMesh(gl, collVerts);

            results[m] = new LoadedModel
            {
                Mesh = resultMesh,
                CollMesh = resultCollMesh,
                ConvexCollMesh = resultConvexMesh,
                CollVertices = [.. collVerts],
                CollIndices = [.. indices],
                MaterialSlotCount = materialSlot + 1,
            };
        }

        Api.ReleaseImport(scene);
        Logger.Asset($"Model loaded all submeshes: {cleanPath} ({count} meshes)");

        return results;
    }

    private static Mesh? GenerateConvexHullMesh(GL gl, List<Vector3> vertices)
    {
        try
        {
            if (vertices.Count < 4) return null;

            var hullVerts = vertices.Select(v => new HullVertex { Position = new double[] { v.X, v.Y, v.Z } }).ToList();
            var hull = MIConvexHull.ConvexHull.Create(hullVerts);
            
            var pointToIndex = hull.Result.Points.Select((p, i) => new { p, i }).ToDictionary(x => x.p, x => (uint)x.i);
            
            var cvxTriIndices = new List<uint>();
            var cvxLineIndices = new List<uint>();
            
            foreach (var face in hull.Result.Faces)
            {
                uint p1 = pointToIndex[face.Vertices[0]];
                uint p2 = pointToIndex[face.Vertices[1]];
                uint p3 = pointToIndex[face.Vertices[2]];
                
                cvxTriIndices.Add(p1);
                cvxTriIndices.Add(p2);
                cvxTriIndices.Add(p3);
                
                cvxLineIndices.Add(p1); cvxLineIndices.Add(p2);
                cvxLineIndices.Add(p2); cvxLineIndices.Add(p3);
                cvxLineIndices.Add(p3); cvxLineIndices.Add(p1);
            }

            var cvxMeshVerts = hull.Result.Points.Select(p => new Vertex { Position = new Vector3((float)p.Position[0], (float)p.Position[1], (float)p.Position[2]) }).ToArray();
            
            return new Mesh(gl, cvxMeshVerts, cvxTriIndices.ToArray(), cvxLineIndices.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
