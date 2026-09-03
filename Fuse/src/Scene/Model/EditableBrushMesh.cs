using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Fuse.Renderer;

namespace Fuse.Scene.Model;

/// <summary>
/// Defines how a brush owns its geometry. Plane CSG brushes remain the default so
/// existing maps continue to behave exactly as before. Editable meshes keep their
/// polygon topology and are used by the component editor in Blowtorch.
/// </summary>
public enum BrushGeometryMode
{
    PlaneCsg,
    EditableMesh
}

/// <summary>
/// A stable, undirected edge key. Edges are derived from face loops, therefore we
/// never need to serialize a second copy of the topology just for edges.
/// </summary>
public readonly record struct EditableBrushEdge(int A, int B)
{
    public static EditableBrushEdge Create(int first, int second) =>
        first < second ? new EditableBrushEdge(first, second) : new EditableBrushEdge(second, first);

    public bool Contains(int vertexId) => A == vertexId || B == vertexId;
}

public sealed class EditableBrushVertex
{
    public int Id { get; set; }
    public Vector3 Position { get; set; }
}

/// <summary>
/// A polygon face. Vertex IDs are stored in winding order. Material and UV data
/// deliberately mirror <see cref="Face"/> so converting a legacy brush preserves
/// its appearance.
/// </summary>
public sealed class EditableBrushFace
{
    public int Id { get; set; }
    public List<int> Vertices { get; set; } = [];
    public string Texture { get; set; } = "default";
    public int MaterialSlot { get; set; }
    public Vector3 UAxis { get; set; } = Vector3.UnitX;
    public Vector3 VAxis { get; set; } = -Vector3.UnitZ;
    public float UScale { get; set; } = 1.0f;
    public float VScale { get; set; } = 1.0f;
    public float UOffset { get; set; }
    public float VOffset { get; set; }
    public float Rotation { get; set; }

    public EditableBrushFace CloneWithVertices(IEnumerable<int> vertices) => new()
    {
        Vertices = vertices.ToList(),
        Texture = Texture,
        MaterialSlot = MaterialSlot,
        UAxis = UAxis,
        VAxis = VAxis,
        UScale = UScale,
        VScale = VScale,
        UOffset = UOffset,
        VOffset = VOffset,
        Rotation = Rotation
    };
}

/// <summary>
/// Persistent polygon topology for an editable brush. The renderer still receives
/// regular MeshData, while editor operations work on shared vertex/edge/face data.
/// </summary>
public sealed class EditableBrushMesh
{
    private const float Epsilon = 0.0001f;

    public List<EditableBrushVertex> Vertices { get; set; } = [];
    public List<EditableBrushFace> Faces { get; set; } = [];
    public int NextVertexId { get; set; } = 1;
    public int NextFaceId { get; set; } = 1;

    public EditableBrushVertex? FindVertex(int id) => Vertices.FirstOrDefault(vertex => vertex.Id == id);
    public EditableBrushFace? FindFace(int id) => Faces.FirstOrDefault(face => face.Id == id);

    public Vector3 GetPosition(int id) => FindVertex(id)?.Position ?? Vector3.Zero;

    public int AddVertex(Vector3 position)
    {
        EnsureNextIds();
        var vertex = new EditableBrushVertex { Id = NextVertexId++, Position = position };
        Vertices.Add(vertex);
        return vertex.Id;
    }

    public int AddOrGetVertex(Vector3 position, float epsilon = Epsilon)
    {
        float epsilonSquared = epsilon * epsilon;
        foreach (EditableBrushVertex vertex in Vertices)
        {
            if (Vector3.DistanceSquared(vertex.Position, position) <= epsilonSquared)
                return vertex.Id;
        }
        return AddVertex(position);
    }

    public EditableBrushFace AddFace(EditableBrushFace face)
    {
        EnsureNextIds();
        if (face.Id <= 0 || Faces.Any(existing => existing.Id == face.Id))
            face.Id = NextFaceId++;
        else
            NextFaceId = System.Math.Max(NextFaceId, face.Id + 1);
        Faces.Add(face);
        return face;
    }

    public IReadOnlyList<EditableBrushEdge> GetEdges()
    {
        var edges = new HashSet<EditableBrushEdge>();
        foreach (EditableBrushFace face in Faces)
        {
            for (int index = 0; index < face.Vertices.Count; index++)
            {
                int first = face.Vertices[index];
                int second = face.Vertices[(index + 1) % face.Vertices.Count];
                if (first != second)
                    edges.Add(EditableBrushEdge.Create(first, second));
            }
        }
        return edges.OrderBy(edge => edge.A).ThenBy(edge => edge.B).ToArray();
    }

    public List<EditableBrushFace> GetFacesUsingEdge(EditableBrushEdge edge) => Faces
        .Where(face => ContainsEdge(face, edge))
        .ToList();

    /// <summary>
    /// Removes the selected polygon faces while preserving every vertex that is
    /// still referenced by the remaining topology. Editable meshes are allowed
    /// to be open, so deleting a face creates a real hole instead of trying to
    /// cap it or deleting neighboring faces.
    /// </summary>
    public bool TryDeleteFaces(IEnumerable<int> selectedFaceIds, out string error)
    {
        HashSet<int> requestedIds = selectedFaceIds.ToHashSet();
        if (requestedIds.Count == 0)
        {
            error = "Selecione ao menos uma face para apagar.";
            return false;
        }

        List<EditableBrushFace> selected = Faces
            .Where(face => requestedIds.Contains(face.Id))
            .ToList();
        if (selected.Count != requestedIds.Count)
        {
            error = "Uma ou mais faces selecionadas não pertencem à malha.";
            return false;
        }

        // An empty editable brush cannot generate render or collision geometry.
        // Keeping one face also makes an accidental Ctrl+A/Delete recoverable
        // through the normal undo command instead of leaving a broken object.
        if (selected.Count >= Faces.Count)
        {
            error = "A malha precisa manter ao menos uma face.";
            return false;
        }

        EditableBrushMesh backup = DeepClone();
        Faces.RemoveAll(face => requestedIds.Contains(face.Id));

        HashSet<int> usedVertexIds = Faces
            .SelectMany(face => face.Vertices)
            .ToHashSet();
        Vertices.RemoveAll(vertex => !usedVertexIds.Contains(vertex.Id));
        EnsureNextIds();

        if (!TryValidate(out error))
        {
            RestoreFrom(backup);
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Creates one polygonal bridge between exactly two open boundary edges.
    /// This follows ProBuilder's default Bridge Edges rule: a selected edge must
    /// have one adjacent face, otherwise the operation would create a
    /// non-manifold result. The existing edge winding determines the endpoint
    /// correspondence and the orientation of the generated face.
    /// </summary>
    public bool TryBridgeEdges(IEnumerable<EditableBrushEdge> selectedEdges, out string error)
    {
        EditableBrushEdge[] edges = selectedEdges
            .Select(edge => EditableBrushEdge.Create(edge.A, edge.B))
            .Distinct()
            .ToArray();
        if (edges.Length != 2)
        {
            error = "Bridge Edges exige exatamente duas arestas selecionadas.";
            return false;
        }

        Dictionary<EditableBrushEdge, List<int>> incidences = BuildEdgeIncidences();
        var adjacentFaces = new List<EditableBrushFace>(2);
        foreach (EditableBrushEdge edge in edges)
        {
            if (!incidences.TryGetValue(edge, out List<int>? faceIds) || faceIds.Count != 1)
            {
                error = "Bridge Edges só pode usar duas arestas abertas, com uma face em cada lado.";
                return false;
            }

            EditableBrushFace? face = FindFace(faceIds[0]);
            if (face == null)
            {
                error = "Não foi possível localizar a face vizinha de uma aresta selecionada.";
                return false;
            }
            adjacentFaces.Add(face);
        }

        if (adjacentFaces[0].Id == adjacentFaces[1].Id)
        {
            error = "Bridge Edges precisa conectar arestas de faces diferentes.";
            return false;
        }
        if (edges[0].Contains(edges[1].A) || edges[0].Contains(edges[1].B))
        {
            error = "As duas arestas precisam ter quatro endpoints diferentes.";
            return false;
        }

        if (!TryGetDirectedEdge(adjacentFaces[0].Vertices, edges[0], out int firstStart, out int firstEnd) ||
            !TryGetDirectedEdge(adjacentFaces[1].Vertices, edges[1], out int secondStart, out int secondEnd))
        {
            error = "Não foi possível manter a orientação das arestas selecionadas.";
            return false;
        }

        int[] endpointIds = [firstStart, firstEnd, secondStart, secondEnd];
        if (endpointIds.Distinct().Count() != endpointIds.Length || endpointIds.Any(id => FindVertex(id) == null))
        {
            error = "Bridge Edges encontrou endpoints inválidos na topologia.";
            return false;
        }

        // The direction of each selected edge comes from its existing face. A
        // manifold bridge must traverse both boundary edges in the opposite
        // direction, otherwise the generated polygon is inside-out on one of
        // the openings. This winding also removes the old mesh-center normal
        // heuristic, which could flip an otherwise correct bridge.
        List<int> bridgeLoop = [firstEnd, firstStart, secondEnd, secondStart];
        EditableBrushMesh backup = DeepClone();
        string lastError = "As arestas não formam uma face válida.";
        EditableBrushFace? prototype = FindFace(adjacentFaces[0].Id);
        if (prototype == null || CalculateNormal(bridgeLoop, GetPosition).LengthSquared() < Epsilon * Epsilon)
        {
            error = "Bridge Edges falhou sem alterar a malha: a ponte geraria uma face degenerada.";
            return false;
        }

        int previousFaceCount = Faces.Count;
        AddFace(prototype.CloneWithVertices(bridgeLoop));
        if (Faces.Count != previousFaceCount + 1 || !TryValidate(out lastError))
        {
            RestoreFrom(backup);
            error = $"Bridge Edges falhou sem alterar a malha: {lastError}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool ContainsEdge(EditableBrushFace face, EditableBrushEdge edge)
    {
        for (int index = 0; index < face.Vertices.Count; index++)
        {
            if (EditableBrushEdge.Create(face.Vertices[index], face.Vertices[(index + 1) % face.Vertices.Count]) == edge)
                return true;
        }
        return false;
    }

    public Vector3 CalculateFaceNormal(EditableBrushFace face)
    {
        if (face.Vertices.Count < 3)
            return Vector3.UnitY;

        Vector3 normal = Vector3.Zero;
        for (int index = 0; index < face.Vertices.Count; index++)
        {
            Vector3 current = GetPosition(face.Vertices[index]);
            Vector3 next = GetPosition(face.Vertices[(index + 1) % face.Vertices.Count]);
            normal.X += (current.Y - next.Y) * (current.Z + next.Z);
            normal.Y += (current.Z - next.Z) * (current.X + next.X);
            normal.Z += (current.X - next.X) * (current.Y + next.Y);
        }

        if (normal.LengthSquared() < Epsilon * Epsilon)
        {
            Vector3 a = GetPosition(face.Vertices[0]);
            for (int index = 1; index + 1 < face.Vertices.Count; index++)
            {
                normal = Vector3.Cross(GetPosition(face.Vertices[index]) - a, GetPosition(face.Vertices[index + 1]) - a);
                if (normal.LengthSquared() >= Epsilon * Epsilon)
                    break;
            }
        }

        return normal.LengthSquared() < Epsilon * Epsilon ? Vector3.Zero : Vector3.Normalize(normal);
    }

    public Vector3 CalculateFaceCenter(EditableBrushFace face)
    {
        if (face.Vertices.Count == 0)
            return Vector3.Zero;

        Vector3 center = Vector3.Zero;
        foreach (int vertexId in face.Vertices)
            center += GetPosition(vertexId);
        return center / face.Vertices.Count;
    }

    public bool TryGetBounds(out Vector3 min, out Vector3 max)
    {
        IEnumerable<EditableBrushVertex> usedVertices = Vertices.Where(vertex => Faces.Any(face => face.Vertices.Contains(vertex.Id)));
        using IEnumerator<EditableBrushVertex> enumerator = usedVertices.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            min = max = Vector3.Zero;
            return false;
        }

        min = max = enumerator.Current.Position;
        while (enumerator.MoveNext())
        {
            min = Vector3.Min(min, enumerator.Current.Position);
            max = Vector3.Max(max, enumerator.Current.Position);
        }
        return true;
    }

    /// <summary>Recenters local geometry and returns the translation applied.</summary>
    public Vector3 NormalizeOrigin()
    {
        if (!TryGetBounds(out Vector3 min, out Vector3 max))
            return Vector3.Zero;

        Vector3 center = (min + max) * 0.5f;
        if (center.LengthSquared() < Epsilon * Epsilon)
            return Vector3.Zero;

        foreach (EditableBrushVertex vertex in Vertices)
            vertex.Position -= center;
        return center;
    }

    public bool IsClosedManifold()
    {
        var incidences = new Dictionary<EditableBrushEdge, int>();
        foreach (EditableBrushFace face in Faces)
        {
            for (int index = 0; index < face.Vertices.Count; index++)
            {
                EditableBrushEdge edge = EditableBrushEdge.Create(face.Vertices[index], face.Vertices[(index + 1) % face.Vertices.Count]);
                incidences[edge] = incidences.GetValueOrDefault(edge) + 1;
            }
        }
        return incidences.Count > 0 && incidences.Values.All(count => count == 2);
    }

    public bool IsConvex()
    {
        var usedVertices = Vertices.Where(vertex => Faces.Any(face => face.Vertices.Contains(vertex.Id))).ToArray();
        if (usedVertices.Length < 4 || Faces.Count < 4)
            return false;

        Vector3 center = Vector3.Zero;
        foreach (EditableBrushVertex vertex in usedVertices)
            center += vertex.Position;
        center /= usedVertices.Length;

        foreach (EditableBrushFace face in Faces)
        {
            if (face.Vertices.Count < 3)
                return false;
            Vector3 normal = CalculateFaceNormal(face);
            if (normal.LengthSquared() < Epsilon * Epsilon)
                return false;
            Vector3 planePoint = GetPosition(face.Vertices[0]);
            float insideSign = MathF.Sign(Vector3.Dot(normal, center - planePoint));
            if (MathF.Abs(insideSign) < Epsilon)
                continue;

            foreach (EditableBrushVertex vertex in usedVertices)
            {
                float side = Vector3.Dot(normal, vertex.Position - planePoint) * insideSign;
                if (side > 0.0025f)
                    return false;
            }
        }
        return true;
    }

    public bool TryValidate(out string error)
    {
        if (Faces.Count == 0)
        {
            error = "A malha não possui faces.";
            return false;
        }

        foreach (EditableBrushFace face in Faces)
        {
            if (face.Vertices.Count < 3)
            {
                error = $"A face {face.Id} possui menos de três vértices.";
                return false;
            }
            if (face.Vertices.Distinct().Count() != face.Vertices.Count)
            {
                error = $"A face {face.Id} possui vértices duplicados.";
                return false;
            }
            if (face.Vertices.Any(vertexId => FindVertex(vertexId) == null))
            {
                error = $"A face {face.Id} referencia um vértice inexistente.";
                return false;
            }
            if (CalculateFaceNormal(face).LengthSquared() < 0.5f)
            {
                error = $"A face {face.Id} é degenerada.";
                return false;
            }
            if (!TryTriangulateFace(face, out _, out string triangulationError))
            {
                error = $"A face {face.Id} não pode ser triangulada: {triangulationError}";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public MeshData GenerateMeshData()
    {
        if (!TryValidate(out _))
            return new MeshData([], []);

        TryGetBounds(out Vector3 minCorner, out _);
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var lineIndices = new List<uint>();
        var indicesByMaterial = new Dictionary<int, List<uint>>();
        var materialOrder = new List<int>();

        foreach (EditableBrushFace face in Faces)
        {
            if (!TryTriangulateFace(face, out List<FaceTriangle> triangles, out _))
                continue;

            Vector3 faceNormal = CalculateFaceNormal(face);
            GetSafeUvAxes(face, faceNormal, out Vector3 uAxis, out Vector3 vAxis);

            uint AddRenderVertex(int vertexId, Vector3 normal)
            {
                Vector3 position = GetPosition(vertexId);
                float u = Vector3.Dot(position - minCorner, uAxis) / SafeScale(face.UScale) + face.UOffset;
                float v = Vector3.Dot(position - minCorner, vAxis) / SafeScale(face.VScale) + face.VOffset;
                if (MathF.Abs(face.Rotation) > 0.001f)
                {
                    float radians = float.DegreesToRadians(face.Rotation);
                    float cosine = MathF.Cos(radians);
                    float sine = MathF.Sin(radians);
                    (u, v) = (u * cosine - v * sine, u * sine + v * cosine);
                }

                uint renderIndex = (uint)vertices.Count;
                vertices.Add(new Vertex { Position = position, Normal = normal, TexCoord = new Vector2(u, v) });
                return renderIndex;
            }

            // Keep one set of boundary vertices for the editor wireframe. The
            // rendered triangles below intentionally use their own vertices so
            // a non-planar n-gon receives a correct normal per triangle.
            var boundaryIndices = new uint[face.Vertices.Count];
            for (int index = 0; index < face.Vertices.Count; index++)
                boundaryIndices[index] = AddRenderVertex(face.Vertices[index], faceNormal);

            int materialSlot = System.Math.Max(0, face.MaterialSlot);
            if (!indicesByMaterial.TryGetValue(materialSlot, out List<uint>? materialIndices))
            {
                materialIndices = [];
                indicesByMaterial[materialSlot] = materialIndices;
                materialOrder.Add(materialSlot);
            }

            foreach (FaceTriangle triangle in triangles)
            {
                Vector3 first = GetPosition(face.Vertices[triangle.First]);
                Vector3 second = GetPosition(face.Vertices[triangle.Second]);
                Vector3 third = GetPosition(face.Vertices[triangle.Third]);
                Vector3 triangleNormal = Vector3.Cross(second - first, third - first);
                if (triangleNormal.LengthSquared() < Epsilon * Epsilon)
                    continue;
                triangleNormal = Vector3.Normalize(triangleNormal);

                int secondIndex = triangle.Second;
                int thirdIndex = triangle.Third;
                if (Vector3.Dot(triangleNormal, faceNormal) < 0.0f)
                {
                    triangleNormal = -triangleNormal;
                    (secondIndex, thirdIndex) = (thirdIndex, secondIndex);
                }

                materialIndices.Add(AddRenderVertex(face.Vertices[triangle.First], triangleNormal));
                materialIndices.Add(AddRenderVertex(face.Vertices[secondIndex], triangleNormal));
                materialIndices.Add(AddRenderVertex(face.Vertices[thirdIndex], triangleNormal));
            }

            for (int index = 0; index < face.Vertices.Count; index++)
            {
                lineIndices.Add(boundaryIndices[index]);
                lineIndices.Add(boundaryIndices[(index + 1) % face.Vertices.Count]);
            }
        }

        var parts = new List<MeshPart>();
        foreach (int materialSlot in materialOrder)
        {
            List<uint> materialIndices = indicesByMaterial[materialSlot];
            uint offset = (uint)indices.Count;
            indices.AddRange(materialIndices);
            if (materialIndices.Count > 0)
                parts.Add(new MeshPart(offset, (uint)materialIndices.Count, materialSlot));
        }

        return new MeshData(vertices.ToArray(), indices.ToArray(), lineIndices.ToArray(), parts.ToArray());
    }

    public bool TryRaycastFace(Vector3 rayOrigin, Vector3 rayDirection, out int faceId, out Vector3 point, out float distance)
    {
        faceId = -1;
        point = Vector3.Zero;
        distance = float.MaxValue;

        foreach (EditableBrushFace face in Faces)
        {
            if (!TryTriangulateFace(face, out List<FaceTriangle> triangles, out _))
                continue;

            foreach (FaceTriangle triangle in triangles)
            {
                if (!RayTriangle(
                        rayOrigin,
                        rayDirection,
                        GetPosition(face.Vertices[triangle.First]),
                        GetPosition(face.Vertices[triangle.Second]),
                        GetPosition(face.Vertices[triangle.Third]),
                        out float hitDistance))
                    continue;
                if (hitDistance >= distance)
                    continue;

                distance = hitDistance;
                faceId = face.Id;
                point = rayOrigin + rayDirection * hitDistance;
            }
        }
        return faceId >= 0;
    }

    public bool TryExtrude(IEnumerable<int> selectedFaceIds, float distance, out string error)
    {
        var selected = Faces.Where(face => selectedFaceIds.Contains(face.Id)).ToList();
        if (selected.Count == 0)
        {
            error = "Selecione ao menos uma face para extrudar.";
            return false;
        }
        if (MathF.Abs(distance) < Epsilon)
        {
            error = "A distância da extrusão precisa ser diferente de zero.";
            return false;
        }

        EditableBrushMesh backup = DeepClone();

        Vector3 direction = Vector3.Zero;
        foreach (EditableBrushFace face in selected)
            direction += CalculateFaceNormal(face);
        if (direction.LengthSquared() < Epsilon * Epsilon)
        {
            error = "Não foi possível calcular a direção da extrusão.";
            return false;
        }
        direction = Vector3.Normalize(direction) * distance;

        var selectedIds = selected.Select(face => face.Id).ToHashSet();
        var originalLoops = selected.ToDictionary(face => face.Id, face => face.Vertices.ToList());
        var selectedVertices = originalLoops.Values.SelectMany(loop => loop).Distinct().ToArray();
        var duplicatedVertices = new Dictionary<int, int>();
        foreach (int vertexId in selectedVertices)
            duplicatedVertices[vertexId] = AddVertex(GetPosition(vertexId) + direction);

        var boundaryEdges = new List<(int first, int second, EditableBrushFace source)>();
        foreach (EditableBrushFace face in selected)
        {
            List<int> loop = originalLoops[face.Id];
            for (int index = 0; index < loop.Count; index++)
            {
                int first = loop[index];
                int second = loop[(index + 1) % loop.Count];
                EditableBrushEdge edge = EditableBrushEdge.Create(first, second);
                int selectedUseCount = GetFacesUsingEdge(edge).Count(candidate => selectedIds.Contains(candidate.Id));
                if (selectedUseCount == 1)
                    boundaryEdges.Add((first, second, face));
            }
        }

        foreach (EditableBrushFace face in selected)
            face.Vertices = originalLoops[face.Id].Select(vertexId => duplicatedVertices[vertexId]).ToList();

        foreach ((int first, int second, EditableBrushFace source) in boundaryEdges)
        {
            AddFace(source.CloneWithVertices([first, second, duplicatedVertices[second], duplicatedVertices[first]]));
        }

        if (!TryValidate(out error))
        {
            RestoreFrom(backup);
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TryInset(IEnumerable<int> selectedFaceIds, float amount, out string error)
    {
        var selected = Faces.Where(face => selectedFaceIds.Contains(face.Id)).ToList();
        if (selected.Count == 0)
        {
            error = "Selecione ao menos uma face para aplicar inset.";
            return false;
        }

        float factor = float.Clamp(amount, 0.001f, 0.95f);
        foreach (EditableBrushFace face in selected)
        {
            List<int> outer = face.Vertices.ToList();
            Vector3 center = CalculateFaceCenter(face);
            var inner = new List<int>(outer.Count);
            foreach (int vertexId in outer)
                inner.Add(AddVertex(Vector3.Lerp(GetPosition(vertexId), center, factor)));

            face.Vertices = inner;
            for (int index = 0; index < outer.Count; index++)
            {
                int next = (index + 1) % outer.Count;
                AddFace(face.CloneWithVertices([outer[index], outer[next], inner[next], inner[index]]));
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Bevels arbitrary manifold edges locally. Unlike the former brush-wide
    /// clipping approach, this splits the adjacent face corners, creates bevel
    /// strips on selected edges, repairs the neighboring unselected edges and
    /// closes every affected vertex fan. Concave brushes are therefore supported.
    /// </summary>
    public bool TryBevel(IEnumerable<EditableBrushEdge> selectedEdges, float width, out string error)
    {
        EditableBrushEdge[] edges = selectedEdges
            .Select(edge => EditableBrushEdge.Create(edge.A, edge.B))
            .Distinct()
            .ToArray();
        if (edges.Length == 0)
        {
            error = "Selecione ao menos uma aresta para aplicar bevel.";
            return false;
        }
        if (width <= Epsilon)
        {
            error = "A largura do bevel precisa ser maior que zero.";
            return false;
        }

        Dictionary<EditableBrushEdge, List<int>> incidences = BuildEdgeIncidences();
        var selected = edges.ToHashSet();
        foreach (EditableBrushEdge edge in edges)
        {
            if (!incidences.TryGetValue(edge, out List<int>? adjacent) || adjacent.Count != 2)
            {
                error = "Bevel exige arestas manifold: cada aresta selecionada deve pertencer a exatamente duas faces.";
                return false;
            }
        }

        HashSet<int> affectedVertices = edges
            .SelectMany(edge => new[] { edge.A, edge.B })
            .ToHashSet();

        // Neighboring open/non-manifold topology would leave the generated
        // vertex cap without a valid boundary. Reject only the local problem;
        // unrelated parts of a map may still be modeled normally.
        foreach ((EditableBrushEdge edge, List<int> adjacent) in incidences)
        {
            if ((affectedVertices.Contains(edge.A) || affectedVertices.Contains(edge.B)) && adjacent.Count != 2)
            {
                error = "Bevel não pode atravessar uma borda aberta ou não-manifold.";
                return false;
            }
        }

        EditableBrushMesh backup = DeepClone();
        try
        {
            Dictionary<int, EditableBrushFace> facesById = Faces.ToDictionary(face => face.Id);
            Dictionary<int, List<int>> originalLoops = Faces.ToDictionary(face => face.Id, face => face.Vertices.ToList());
            Vector3 meshCenter = CalculateMeshCenter();
            var faceVertexMap = new Dictionary<FaceVertexKey, int>();

            // Split only the face corners touched by a selected edge. Faces
            // outside the bevel keep their original shared vertices.
            foreach (EditableBrushFace face in Faces)
            {
                List<int> loop = originalLoops[face.Id];
                for (int index = 0; index < loop.Count; index++)
                {
                    int vertexId = loop[index];
                    if (!affectedVertices.Contains(vertexId))
                        continue;

                    int previous = loop[(index - 1 + loop.Count) % loop.Count];
                    int next = loop[(index + 1) % loop.Count];
                    bool previousSelected = selected.Contains(EditableBrushEdge.Create(previous, vertexId));
                    bool nextSelected = selected.Contains(EditableBrushEdge.Create(vertexId, next));
                    if (!previousSelected && !nextSelected)
                        continue;

                    Vector3 offset = CalculateBevelCornerOffset(face, previous, vertexId, next, previousSelected, nextSelected, width);
                    if (offset.LengthSquared() < Epsilon * Epsilon)
                    {
                        error = "Uma das faces selecionadas não possui espaço suficiente para o bevel.";
                        RestoreFrom(backup);
                        return false;
                    }

                    faceVertexMap[new FaceVertexKey(face.Id, vertexId)] = AddVertex(GetPosition(vertexId) + offset);
                }
            }

            foreach (EditableBrushFace face in Faces)
            {
                List<int> original = originalLoops[face.Id];
                List<int> updated = original
                    .Select(vertexId => GetFaceVertex(face.Id, vertexId, faceVertexMap))
                    .ToList();
                if (!updated.SequenceEqual(original))
                    face.Vertices = updated;
            }

            // Every original edge whose two neighboring faces now use different
            // vertices needs a small bridge. For selected edges that bridge is
            // the visible bevel strip; for every other affected edge it seals
            // the transition back into the original surface.
            foreach ((EditableBrushEdge edge, List<int> adjacentFaceIds) in incidences)
            {
                if (adjacentFaceIds.Count != 2 ||
                    (!affectedVertices.Contains(edge.A) && !affectedVertices.Contains(edge.B)))
                    continue;

                EditableBrushFace firstFace = facesById[adjacentFaceIds[0]];
                EditableBrushFace secondFace = facesById[adjacentFaceIds[1]];
                List<int> firstLoop = originalLoops[firstFace.Id];
                if (!TryGetDirectedEdge(firstLoop, edge, out int start, out int end))
                    continue;

                int firstStart = GetFaceVertex(firstFace.Id, start, faceVertexMap);
                int firstEnd = GetFaceVertex(firstFace.Id, end, faceVertexMap);
                int secondStart = GetFaceVertex(secondFace.Id, start, faceVertexMap);
                int secondEnd = GetFaceVertex(secondFace.Id, end, faceVertexMap);
                bool changed = firstStart != secondStart || firstEnd != secondEnd;
                if (!changed)
                    continue;

                Vector3 preferredNormal = CalculateFaceNormal(firstFace) + CalculateFaceNormal(secondFace);
                AddOrientedGeneratedFace(
                    [firstStart, firstEnd, secondEnd, secondStart],
                    firstFace,
                    preferredNormal,
                    meshCenter);
            }

            // Close the hole left around every moved original vertex. The cap is
            // sorted in the local face fan, so it works on concave shapes and on
            // selections containing multiple connected edges.
            foreach (int vertexId in affectedVertices)
            {
                List<int> incidentFaceIds = incidences
                    .Where(pair => pair.Key.Contains(vertexId))
                    .SelectMany(pair => pair.Value)
                    .Distinct()
                    .ToList();
                if (incidentFaceIds.Count < 2)
                    continue;

                List<int> capVertices = incidentFaceIds
                    .Select(faceId => GetFaceVertex(faceId, vertexId, faceVertexMap))
                    .Distinct()
                    .ToList();
                if (capVertices.Count < 3)
                    continue;

                Vector3 preferredNormal = Vector3.Zero;
                foreach (int faceId in incidentFaceIds)
                    preferredNormal += CalculateFaceNormal(facesById[faceId]);
                SortVertexCap(capVertices, GetPosition(vertexId), preferredNormal);
                AddOrientedGeneratedFace(capVertices, facesById[incidentFaceIds[0]], preferredNormal, meshCenter);
            }

            if (!TryValidate(out error))
            {
                RestoreFrom(backup);
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            RestoreFrom(backup);
            error = $"Bevel falhou sem alterar a malha: {ex.Message}";
            return false;
        }
    }

    public bool TryLoopCut(EditableBrushEdge startEdge, float factor, out string error)
    {
        List<EditableBrushFace> startFaces = GetFacesUsingEdge(startEdge);
        if (startFaces.Count == 0)
        {
            error = "A aresta selecionada não pertence à malha.";
            return false;
        }

        var path = new List<(EditableBrushFace face, EditableBrushEdge entering, EditableBrushEdge opposite)>();
        var visitedFaces = new HashSet<int>();
        EditableBrushFace currentFace = startFaces[0];
        EditableBrushEdge currentEdge = startEdge;

        while (visitedFaces.Add(currentFace.Id))
        {
            if (currentFace.Vertices.Count != 4 || !TryGetOppositeQuadEdge(currentFace, currentEdge, out EditableBrushEdge opposite))
            {
                error = "Loop Cut precisa de uma sequência contínua de faces quadrangulares.";
                return false;
            }
            path.Add((currentFace, currentEdge, opposite));

            List<EditableBrushFace> nextFaces = GetFacesUsingEdge(opposite)
                .Where(face => face.Id != currentFace.Id)
                .ToList();
            if (nextFaces.Count == 0 || visitedFaces.Contains(nextFaces[0].Id))
                break;
            currentFace = nextFaces[0];
            currentEdge = opposite;
        }

        if (path.Count == 0)
        {
            error = "Não foi encontrado um loop de faces quadrangulares.";
            return false;
        }

        factor = float.Clamp(factor, 0.02f, 0.98f);
        var midpoints = new Dictionary<EditableBrushEdge, int>();
        int Midpoint(EditableBrushEdge edge)
        {
            if (midpoints.TryGetValue(edge, out int existing))
                return existing;
            int id = AddVertex(Vector3.Lerp(GetPosition(edge.A), GetPosition(edge.B), factor));
            midpoints[edge] = id;
            return id;
        }

        foreach ((EditableBrushFace face, EditableBrushEdge entering, EditableBrushEdge opposite) in path)
        {
            List<int> loop = face.Vertices.ToList();
            int start = FindDirectedEdgeIndex(loop, entering);
            if (start < 0)
            {
                error = "Não foi possível manter o winding do Loop Cut.";
                return false;
            }

            int a = loop[start];
            int b = loop[(start + 1) % 4];
            int c = loop[(start + 2) % 4];
            int d = loop[(start + 3) % 4];
            int enterMid = Midpoint(EditableBrushEdge.Create(a, b));
            int oppositeMid = Midpoint(EditableBrushEdge.Create(c, d));
            face.Vertices = [a, enterMid, oppositeMid, d];
            AddFace(face.CloneWithVertices([enterMid, b, c, oppositeMid]));
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Splits one polygon between two points. The points are snapped to the
    /// boundary of the face so neighboring faces keep shared vertices.
    /// </summary>
    public bool TryKnifeCut(int faceId, Vector3 firstPoint, Vector3 secondPoint, out string error)
    {
        EditableBrushFace? face = FindFace(faceId);
        if (face == null || face.Vertices.Count < 3)
        {
            error = "Selecione uma face válida para usar Knife.";
            return false;
        }

        if (!TrySnapToFaceEdge(face, firstPoint, out EditableBrushEdge firstEdge, out Vector3 firstSnapped) ||
            !TrySnapToFaceEdge(face, secondPoint, out EditableBrushEdge secondEdge, out Vector3 secondSnapped))
        {
            error = "Knife precisa de dois pontos sobre a borda da face.";
            return false;
        }
        if (firstEdge == secondEdge)
        {
            error = "Os dois pontos do Knife precisam estar em arestas diferentes.";
            return false;
        }

        int firstVertex = SplitEdgeInAllFaces(firstEdge, firstSnapped);
        int secondVertex = SplitEdgeInAllFaces(secondEdge, secondSnapped);
        face = FindFace(faceId);
        if (face == null || firstVertex == secondVertex)
        {
            error = "Os pontos do Knife precisam estar em posições diferentes.";
            return false;
        }

        int firstIndex = face.Vertices.IndexOf(firstVertex);
        int secondIndex = face.Vertices.IndexOf(secondVertex);
        if (firstIndex < 0 || secondIndex < 0 || System.Math.Abs(firstIndex - secondIndex) == 1 || System.Math.Abs(firstIndex - secondIndex) == face.Vertices.Count - 1)
        {
            error = "Knife precisa dividir a face em duas regiões válidas.";
            return false;
        }

        List<int> firstLoop = CollectLoop(face.Vertices, firstIndex, secondIndex);
        List<int> secondLoop = CollectLoop(face.Vertices, secondIndex, firstIndex);
        if (firstLoop.Count < 3 || secondLoop.Count < 3)
        {
            error = "Knife geraria uma face inválida.";
            return false;
        }

        face.Vertices = firstLoop;
        AddFace(face.CloneWithVertices(secondLoop));
        error = string.Empty;
        return true;
    }

    public static EditableBrushMesh FromPlaneBrush(Brush brush)
    {
        var topology = new EditableBrushMesh();
        List<Face> faces = brush.Faces;
        var faceVertices = faces.Select(_ => new List<Vector3>()).ToArray();

        for (int first = 0; first < faces.Count - 2; first++)
        {
            for (int second = first + 1; second < faces.Count - 1; second++)
            {
                for (int third = second + 1; third < faces.Count; third++)
                {
                    if (!MeshGenerator.TryGetIntersection(faces[first].Plane, faces[second].Plane, faces[third].Plane, out Vector3 point))
                        continue;

                    bool inside = true;
                    for (int check = 0; check < faces.Count; check++)
                    {
                        if (check == first || check == second || check == third)
                            continue;
                        if (Vector3.Dot(faces[check].Plane.Normal, point) + faces[check].Plane.D > 0.05f)
                        {
                            inside = false;
                            break;
                        }
                    }
                    if (!inside)
                        continue;

                    MeshGenerator.AddUniqueVertex(faceVertices[first], point);
                    MeshGenerator.AddUniqueVertex(faceVertices[second], point);
                    MeshGenerator.AddUniqueVertex(faceVertices[third], point);
                }
            }
        }

        for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
        {
            Face source = faces[faceIndex];
            List<Vector3> polygon = faceVertices[faceIndex];
            if (polygon.Count < 3)
                continue;

            SortPolygon(polygon, source.Plane.Normal);
            var editableFace = new EditableBrushFace
            {
                Texture = source.Texture,
                MaterialSlot = source.MaterialSlot,
                UAxis = source.UAxis,
                VAxis = source.VAxis,
                UScale = source.UScale,
                VScale = source.VScale,
                UOffset = source.UOffset,
                VOffset = source.VOffset,
                Rotation = source.Rotation
            };
            foreach (Vector3 position in polygon)
                editableFace.Vertices.Add(topology.AddOrGetVertex(position, 0.0025f));
            topology.AddFace(editableFace);
        }

        return topology;
    }

    private readonly record struct FaceVertexKey(int FaceId, int VertexId);

    private Dictionary<EditableBrushEdge, List<int>> BuildEdgeIncidences()
    {
        var incidences = new Dictionary<EditableBrushEdge, List<int>>();
        foreach (EditableBrushFace face in Faces)
        {
            for (int index = 0; index < face.Vertices.Count; index++)
            {
                EditableBrushEdge edge = EditableBrushEdge.Create(
                    face.Vertices[index],
                    face.Vertices[(index + 1) % face.Vertices.Count]);
                if (!incidences.TryGetValue(edge, out List<int>? adjacentFaces))
                {
                    adjacentFaces = [];
                    incidences[edge] = adjacentFaces;
                }
                adjacentFaces.Add(face.Id);
            }
        }
        return incidences;
    }

    private Vector3 CalculateBevelCornerOffset(
        EditableBrushFace face,
        int previous,
        int current,
        int next,
        bool previousSelected,
        bool nextSelected,
        float width)
    {
        Vector3 position = GetPosition(current);
        Vector3 toPrevious = GetPosition(previous) - position;
        Vector3 toNext = GetPosition(next) - position;
        float previousLength = toPrevious.Length();
        float nextLength = toNext.Length();
        if (previousLength < Epsilon || nextLength < Epsilon)
            return Vector3.Zero;

        Vector3 direction = Vector3.Zero;
        if (!previousSelected)
            direction += toPrevious / previousLength;
        if (!nextSelected)
            direction += toNext / nextLength;

        // When a selected chain turns at this corner both neighboring edges
        // are selected. Move toward the face interior, which produces a stable
        // miter rather than collapsing the two new corner vertices together.
        if (direction.LengthSquared() < Epsilon * Epsilon)
            direction = CalculateFaceCenter(face) - position;
        if (direction.LengthSquared() < Epsilon * Epsilon)
            return Vector3.Zero;

        float maximumSafeWidth = MathF.Min(previousLength, nextLength) * 0.45f;
        return Vector3.Normalize(direction) * MathF.Min(width, maximumSafeWidth);
    }

    private static int GetFaceVertex(
        int faceId,
        int originalVertexId,
        IReadOnlyDictionary<FaceVertexKey, int> replacements) =>
        replacements.TryGetValue(new FaceVertexKey(faceId, originalVertexId), out int replacement)
            ? replacement
            : originalVertexId;

    private static bool TryGetDirectedEdge(
        IReadOnlyList<int> loop,
        EditableBrushEdge edge,
        out int start,
        out int end)
    {
        int index = FindDirectedEdgeIndex(loop, edge);
        if (index < 0)
        {
            start = end = -1;
            return false;
        }

        start = loop[index];
        end = loop[(index + 1) % loop.Count];
        return true;
    }

    private void AddOrientedGeneratedFace(
        IEnumerable<int> sourceVertices,
        EditableBrushFace prototype,
        Vector3 preferredNormal,
        Vector3 meshCenter)
    {
        var vertices = new List<int>();
        foreach (int vertexId in sourceVertices)
        {
            if (vertices.Count == 0 || vertices[^1] != vertexId)
                vertices.Add(vertexId);
        }
        if (vertices.Count > 1 && vertices[0] == vertices[^1])
            vertices.RemoveAt(vertices.Count - 1);
        if (vertices.Count < 3)
            return;

        Vector3 normal = CalculateNormal(vertices, GetPosition);
        if (normal.LengthSquared() < Epsilon * Epsilon)
            return;

        if (preferredNormal.LengthSquared() < Epsilon * Epsilon)
        {
            Vector3 center = Vector3.Zero;
            foreach (int vertexId in vertices)
                center += GetPosition(vertexId);
            preferredNormal = center / vertices.Count - meshCenter;
        }

        if (preferredNormal.LengthSquared() > Epsilon * Epsilon &&
            Vector3.Dot(normal, preferredNormal) < 0.0f)
        {
            vertices.Reverse();
        }

        AddFace(prototype.CloneWithVertices(vertices));
    }

    private void SortVertexCap(List<int> vertices, Vector3 originalPosition, Vector3 preferredNormal)
    {
        if (preferredNormal.LengthSquared() < Epsilon * Epsilon)
            preferredNormal = Vector3.UnitY;
        preferredNormal = Vector3.Normalize(preferredNormal);

        Vector3 uAxis = Vector3.Zero;
        foreach (int vertexId in vertices)
        {
            uAxis = GetPosition(vertexId) - originalPosition;
            uAxis -= preferredNormal * Vector3.Dot(uAxis, preferredNormal);
            if (uAxis.LengthSquared() >= Epsilon * Epsilon)
            {
                uAxis = Vector3.Normalize(uAxis);
                break;
            }
        }
        if (uAxis.LengthSquared() < Epsilon * Epsilon)
        {
            Vector3 fallback = MathF.Abs(preferredNormal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            uAxis = Vector3.Normalize(Vector3.Cross(fallback, preferredNormal));
        }
        Vector3 vAxis = Vector3.Normalize(Vector3.Cross(preferredNormal, uAxis));

        vertices.Sort((first, second) =>
        {
            Vector3 firstOffset = GetPosition(first) - originalPosition;
            Vector3 secondOffset = GetPosition(second) - originalPosition;
            float firstAngle = MathF.Atan2(Vector3.Dot(firstOffset, vAxis), Vector3.Dot(firstOffset, uAxis));
            float secondAngle = MathF.Atan2(Vector3.Dot(secondOffset, vAxis), Vector3.Dot(secondOffset, uAxis));
            return firstAngle.CompareTo(secondAngle);
        });
    }

    private Vector3 CalculateMeshCenter()
    {
        HashSet<int> usedVertexIds = Faces.SelectMany(face => face.Vertices).ToHashSet();
        if (usedVertexIds.Count == 0)
            return Vector3.Zero;

        Vector3 center = Vector3.Zero;
        foreach (int vertexId in usedVertexIds)
            center += GetPosition(vertexId);
        return center / usedVertexIds.Count;
    }

    private EditableBrushMesh DeepClone()
    {
        return new EditableBrushMesh
        {
            Vertices = Vertices.Select(vertex => new EditableBrushVertex
            {
                Id = vertex.Id,
                Position = vertex.Position
            }).ToList(),
            Faces = Faces.Select(face => new EditableBrushFace
            {
                Id = face.Id,
                Vertices = face.Vertices.ToList(),
                Texture = face.Texture,
                MaterialSlot = face.MaterialSlot,
                UAxis = face.UAxis,
                VAxis = face.VAxis,
                UScale = face.UScale,
                VScale = face.VScale,
                UOffset = face.UOffset,
                VOffset = face.VOffset,
                Rotation = face.Rotation
            }).ToList(),
            NextVertexId = NextVertexId,
            NextFaceId = NextFaceId
        };
    }

    private void RestoreFrom(EditableBrushMesh snapshot)
    {
        Vertices = snapshot.Vertices.Select(vertex => new EditableBrushVertex
        {
            Id = vertex.Id,
            Position = vertex.Position
        }).ToList();
        Faces = snapshot.Faces.Select(face => new EditableBrushFace
        {
            Id = face.Id,
            Vertices = face.Vertices.ToList(),
            Texture = face.Texture,
            MaterialSlot = face.MaterialSlot,
            UAxis = face.UAxis,
            VAxis = face.VAxis,
            UScale = face.UScale,
            VScale = face.VScale,
            UOffset = face.UOffset,
            VOffset = face.VOffset,
            Rotation = face.Rotation
        }).ToList();
        NextVertexId = snapshot.NextVertexId;
        NextFaceId = snapshot.NextFaceId;
    }

    private int SplitEdgeInAllFaces(EditableBrushEdge edge, Vector3 point)
    {
        Vector3 first = GetPosition(edge.A);
        Vector3 second = GetPosition(edge.B);
        if (Vector3.DistanceSquared(first, point) <= Epsilon * Epsilon)
            return edge.A;
        if (Vector3.DistanceSquared(second, point) <= Epsilon * Epsilon)
            return edge.B;

        int inserted = AddOrGetVertex(point);
        foreach (EditableBrushFace candidate in GetFacesUsingEdge(edge))
        {
            for (int index = 0; index < candidate.Vertices.Count; index++)
            {
                int current = candidate.Vertices[index];
                int next = candidate.Vertices[(index + 1) % candidate.Vertices.Count];
                if (EditableBrushEdge.Create(current, next) != edge)
                    continue;
                candidate.Vertices.Insert(index + 1, inserted);
                break;
            }
        }
        return inserted;
    }

    private bool TrySnapToFaceEdge(EditableBrushFace face, Vector3 point, out EditableBrushEdge edge, out Vector3 snapped)
    {
        edge = default;
        snapped = Vector3.Zero;
        float closestDistance = float.MaxValue;
        for (int index = 0; index < face.Vertices.Count; index++)
        {
            int firstId = face.Vertices[index];
            int secondId = face.Vertices[(index + 1) % face.Vertices.Count];
            Vector3 first = GetPosition(firstId);
            Vector3 second = GetPosition(secondId);
            Vector3 segment = second - first;
            float lengthSquared = segment.LengthSquared();
            if (lengthSquared < Epsilon * Epsilon)
                continue;
            float t = float.Clamp(Vector3.Dot(point - first, segment) / lengthSquared, 0.0f, 1.0f);
            Vector3 candidate = Vector3.Lerp(first, second, t);
            float distance = Vector3.DistanceSquared(point, candidate);
            if (distance >= closestDistance)
                continue;
            closestDistance = distance;
            edge = EditableBrushEdge.Create(firstId, secondId);
            snapped = candidate;
        }
        return closestDistance < float.MaxValue;
    }

    private static List<int> CollectLoop(IReadOnlyList<int> vertices, int from, int to)
    {
        var result = new List<int>();
        int index = from;
        while (true)
        {
            result.Add(vertices[index]);
            if (index == to)
                break;
            index = (index + 1) % vertices.Count;
        }
        return result;
    }

    private static bool TryGetOppositeQuadEdge(EditableBrushFace face, EditableBrushEdge entering, out EditableBrushEdge opposite)
    {
        int index = FindDirectedEdgeIndex(face.Vertices, entering);
        if (index < 0)
        {
            opposite = default;
            return false;
        }
        opposite = EditableBrushEdge.Create(face.Vertices[(index + 2) % 4], face.Vertices[(index + 3) % 4]);
        return true;
    }

    private static int FindDirectedEdgeIndex(IReadOnlyList<int> vertices, EditableBrushEdge edge)
    {
        for (int index = 0; index < vertices.Count; index++)
        {
            if (EditableBrushEdge.Create(vertices[index], vertices[(index + 1) % vertices.Count]) == edge)
                return index;
        }
        return -1;
    }

    private readonly record struct FaceTriangle(int First, int Second, int Third);
    private readonly record struct ProjectedFaceVertex(int SourceIndex, Vector2 Position);

    /// <summary>
    /// Tessellates an arbitrary simple n-gon with ear clipping. Faces are
    /// projected onto the dominant plane of their Newell normal, so editing a
    /// slope may produce non-planar faces without creating the crossed fan
    /// diagonals that a fixed vertex-0 triangulation produced.
    /// </summary>
    private bool TryTriangulateFace(EditableBrushFace face, out List<FaceTriangle> triangles, out string error)
    {
        triangles = [];
        if (face.Vertices.Count < 3)
        {
            error = "possui menos de três vértices";
            return false;
        }

        Vector3 normal = CalculateFaceNormal(face);
        if (normal.LengthSquared() < Epsilon * Epsilon)
        {
            error = "não possui uma normal válida";
            return false;
        }

        var polygon = new List<ProjectedFaceVertex>(face.Vertices.Count);
        for (int index = 0; index < face.Vertices.Count; index++)
        {
            Vector2 projected = ProjectToDominantPlane(GetPosition(face.Vertices[index]), normal);
            if (polygon.Count == 0 || Vector2.DistanceSquared(polygon[^1].Position, projected) > Epsilon * Epsilon)
                polygon.Add(new ProjectedFaceVertex(index, projected));
        }
        if (polygon.Count > 1 && Vector2.DistanceSquared(polygon[0].Position, polygon[^1].Position) <= Epsilon * Epsilon)
            polygon.RemoveAt(polygon.Count - 1);

        RemoveCollinearProjectedVertices(polygon);
        if (polygon.Count < 3)
        {
            error = "colapsa para uma linha";
            return false;
        }
        if (HasSelfIntersection(polygon))
        {
            error = "possui bordas que se cruzam";
            return false;
        }

        float signedArea = CalculateSignedArea(polygon);
        if (MathF.Abs(signedArea) < Epsilon * Epsilon)
        {
            error = "não possui área válida";
            return false;
        }
        bool counterClockwise = signedArea > 0.0f;

        int guard = polygon.Count * polygon.Count;
        while (polygon.Count > 3 && guard-- > 0)
        {
            bool clippedEar = false;
            for (int index = 0; index < polygon.Count; index++)
            {
                ProjectedFaceVertex previous = polygon[(index - 1 + polygon.Count) % polygon.Count];
                ProjectedFaceVertex current = polygon[index];
                ProjectedFaceVertex next = polygon[(index + 1) % polygon.Count];
                float corner = Cross(previous.Position, current.Position, next.Position);
                if (counterClockwise ? corner <= Epsilon : corner >= -Epsilon)
                    continue;

                bool containsOtherVertex = false;
                for (int candidateIndex = 0; candidateIndex < polygon.Count; candidateIndex++)
                {
                    if (candidateIndex == index ||
                        candidateIndex == (index - 1 + polygon.Count) % polygon.Count ||
                        candidateIndex == (index + 1) % polygon.Count)
                    {
                        continue;
                    }
                    if (IsPointInsideOrOnTriangle(polygon[candidateIndex].Position, previous.Position, current.Position, next.Position))
                    {
                        containsOtherVertex = true;
                        break;
                    }
                }
                if (containsOtherVertex)
                    continue;

                triangles.Add(new FaceTriangle(previous.SourceIndex, current.SourceIndex, next.SourceIndex));
                polygon.RemoveAt(index);
                clippedEar = true;
                break;
            }

            if (!clippedEar)
            {
                triangles.Clear();
                error = "não é um polígono simples";
                return false;
            }
        }

        if (polygon.Count != 3)
        {
            triangles.Clear();
            error = "não foi possível concluir a triangulação";
            return false;
        }

        triangles.Add(new FaceTriangle(polygon[0].SourceIndex, polygon[1].SourceIndex, polygon[2].SourceIndex));
        error = string.Empty;
        return true;
    }

    private static Vector2 ProjectToDominantPlane(Vector3 point, Vector3 normal)
    {
        Vector3 absoluteNormal = Vector3.Abs(normal);
        if (absoluteNormal.X >= absoluteNormal.Y && absoluteNormal.X >= absoluteNormal.Z)
            return new Vector2(point.Y, point.Z);
        if (absoluteNormal.Y >= absoluteNormal.Z)
            return new Vector2(point.X, point.Z);
        return new Vector2(point.X, point.Y);
    }

    private static void RemoveCollinearProjectedVertices(List<ProjectedFaceVertex> polygon)
    {
        bool removed;
        do
        {
            removed = false;
            if (polygon.Count <= 3)
                return;

            for (int index = 0; index < polygon.Count; index++)
            {
                Vector2 previous = polygon[(index - 1 + polygon.Count) % polygon.Count].Position;
                Vector2 current = polygon[index].Position;
                Vector2 next = polygon[(index + 1) % polygon.Count].Position;
                if (MathF.Abs(Cross(previous, current, next)) > Epsilon)
                    continue;

                polygon.RemoveAt(index);
                removed = true;
                break;
            }
        }
        while (removed);
    }

    private static bool HasSelfIntersection(IReadOnlyList<ProjectedFaceVertex> polygon)
    {
        for (int first = 0; first < polygon.Count; first++)
        {
            int firstNext = (first + 1) % polygon.Count;
            for (int second = first + 1; second < polygon.Count; second++)
            {
                int secondNext = (second + 1) % polygon.Count;
                if (first == second || first == secondNext || firstNext == second || firstNext == secondNext)
                    continue;
                if (SegmentsIntersect(
                        polygon[first].Position,
                        polygon[firstNext].Position,
                        polygon[second].Position,
                        polygon[secondNext].Position))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool SegmentsIntersect(Vector2 firstStart, Vector2 firstEnd, Vector2 secondStart, Vector2 secondEnd)
    {
        float first = Cross(firstStart, firstEnd, secondStart);
        float second = Cross(firstStart, firstEnd, secondEnd);
        float third = Cross(secondStart, secondEnd, firstStart);
        float fourth = Cross(secondStart, secondEnd, firstEnd);

        if (((first > Epsilon && second < -Epsilon) || (first < -Epsilon && second > Epsilon)) &&
            ((third > Epsilon && fourth < -Epsilon) || (third < -Epsilon && fourth > Epsilon)))
        {
            return true;
        }

        return (MathF.Abs(first) <= Epsilon && IsPointOnSegment(secondStart, firstStart, firstEnd)) ||
               (MathF.Abs(second) <= Epsilon && IsPointOnSegment(secondEnd, firstStart, firstEnd)) ||
               (MathF.Abs(third) <= Epsilon && IsPointOnSegment(firstStart, secondStart, secondEnd)) ||
               (MathF.Abs(fourth) <= Epsilon && IsPointOnSegment(firstEnd, secondStart, secondEnd));
    }

    private static bool IsPointOnSegment(Vector2 point, Vector2 start, Vector2 end) =>
        point.X >= MathF.Min(start.X, end.X) - Epsilon && point.X <= MathF.Max(start.X, end.X) + Epsilon &&
        point.Y >= MathF.Min(start.Y, end.Y) - Epsilon && point.Y <= MathF.Max(start.Y, end.Y) + Epsilon;

    private static float CalculateSignedArea(IReadOnlyList<ProjectedFaceVertex> polygon)
    {
        float area = 0.0f;
        for (int index = 0; index < polygon.Count; index++)
        {
            Vector2 first = polygon[index].Position;
            Vector2 second = polygon[(index + 1) % polygon.Count].Position;
            area += first.X * second.Y - first.Y * second.X;
        }
        return area * 0.5f;
    }

    private static bool IsPointInsideOrOnTriangle(Vector2 point, Vector2 first, Vector2 second, Vector2 third)
    {
        float firstSide = Cross(first, second, point);
        float secondSide = Cross(second, third, point);
        float thirdSide = Cross(third, first, point);
        bool hasPositive = firstSide > Epsilon || secondSide > Epsilon || thirdSide > Epsilon;
        bool hasNegative = firstSide < -Epsilon || secondSide < -Epsilon || thirdSide < -Epsilon;
        return !hasPositive || !hasNegative;
    }

    private static float Cross(Vector2 first, Vector2 second, Vector2 third)
    {
        Vector2 firstEdge = second - first;
        Vector2 secondEdge = third - first;
        return firstEdge.X * secondEdge.Y - firstEdge.Y * secondEdge.X;
    }

    private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 first, Vector3 second, Vector3 third, out float distance)
    {
        Vector3 edge1 = second - first;
        Vector3 edge2 = third - first;
        Vector3 p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < Epsilon)
        {
            distance = 0.0f;
            return false;
        }
        float inverse = 1.0f / determinant;
        Vector3 t = origin - first;
        float u = Vector3.Dot(t, p) * inverse;
        if (u < 0.0f || u > 1.0f)
        {
            distance = 0.0f;
            return false;
        }
        Vector3 q = Vector3.Cross(t, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0.0f || u + v > 1.0f)
        {
            distance = 0.0f;
            return false;
        }
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance > Epsilon;
    }

    private static void GetSafeUvAxes(EditableBrushFace face, Vector3 normal, out Vector3 uAxis, out Vector3 vAxis)
    {
        uAxis = face.UAxis;
        vAxis = face.VAxis;
        if (MathF.Abs(Vector3.Dot(normal, uAxis)) <= 0.9f && MathF.Abs(Vector3.Dot(normal, vAxis)) <= 0.9f)
            return;

        float x = MathF.Abs(normal.X);
        float y = MathF.Abs(normal.Y);
        float z = MathF.Abs(normal.Z);
        if (x > y && x > z)
        {
            uAxis = Vector3.UnitZ;
            vAxis = -Vector3.UnitY;
        }
        else if (y > x && y > z)
        {
            uAxis = Vector3.UnitX;
            vAxis = -Vector3.UnitZ;
        }
        else
        {
            uAxis = Vector3.UnitX;
            vAxis = -Vector3.UnitY;
        }
    }

    private static float SafeScale(float value) => MathF.Abs(value) < Epsilon ? 1.0f : value;

    private static void SortPolygon(List<Vector3> polygon, Vector3 normal)
    {
        Vector3 center = Vector3.Zero;
        foreach (Vector3 vertex in polygon)
            center += vertex;
        center /= polygon.Count;
        SortPolygon(polygon, normal, _ => _, center);
    }

    private static void SortPolygon(List<int> polygon, Vector3 normal, Func<int, Vector3> getPosition)
    {
        Vector3 center = Vector3.Zero;
        foreach (int vertex in polygon)
            center += getPosition(vertex);
        center /= polygon.Count;
        SortPolygon(polygon, normal, getPosition, center);
    }

    private static void SortPolygon<T>(List<T> polygon, Vector3 normal, Func<T, Vector3> getPosition, Vector3 center)
    {
        Vector3 uDirection = MathF.Abs(normal.Y) > 0.999f
            ? Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitX))
            : Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY));
        Vector3 vDirection = Vector3.Cross(normal, uDirection);
        polygon.Sort((first, second) =>
        {
            Vector3 firstOffset = getPosition(first) - center;
            Vector3 secondOffset = getPosition(second) - center;
            float firstAngle = MathF.Atan2(Vector3.Dot(firstOffset, vDirection), Vector3.Dot(firstOffset, uDirection));
            float secondAngle = MathF.Atan2(Vector3.Dot(secondOffset, vDirection), Vector3.Dot(secondOffset, uDirection));
            return firstAngle.CompareTo(secondAngle);
        });
    }

    private static Vector3 CalculateNormal(IReadOnlyList<int> polygon, Func<int, Vector3> getPosition)
    {
        Vector3 normal = Vector3.Zero;
        for (int index = 0; index < polygon.Count; index++)
        {
            Vector3 current = getPosition(polygon[index]);
            Vector3 next = getPosition(polygon[(index + 1) % polygon.Count]);
            normal.X += (current.Y - next.Y) * (current.Z + next.Z);
            normal.Y += (current.Z - next.Z) * (current.X + next.X);
            normal.Z += (current.X - next.X) * (current.Y + next.Y);
        }
        return normal.LengthSquared() < Epsilon * Epsilon ? Vector3.Zero : Vector3.Normalize(normal);
    }

    private void EnsureNextIds()
    {
        NextVertexId = System.Math.Max(NextVertexId, Vertices.Count == 0 ? 1 : Vertices.Max(vertex => vertex.Id) + 1);
        NextFaceId = System.Math.Max(NextFaceId, Faces.Count == 0 ? 1 : Faces.Max(face => face.Id) + 1);
    }
}
