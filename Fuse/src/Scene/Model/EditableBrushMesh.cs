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
            Vector3 normal = CalculateFaceNormal(face);
            GetSafeUvAxes(face, normal, out Vector3 uAxis, out Vector3 vAxis);

            uint startIndex = (uint)vertices.Count;
            foreach (int vertexId in face.Vertices)
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

                vertices.Add(new Vertex { Position = position, Normal = normal, TexCoord = new Vector2(u, v) });
            }

            int materialSlot = System.Math.Max(0, face.MaterialSlot);
            if (!indicesByMaterial.TryGetValue(materialSlot, out List<uint>? materialIndices))
            {
                materialIndices = [];
                indicesByMaterial[materialSlot] = materialIndices;
                materialOrder.Add(materialSlot);
            }

            for (uint index = 1; index < face.Vertices.Count - 1; index++)
            {
                materialIndices.Add(startIndex);
                materialIndices.Add(startIndex + index);
                materialIndices.Add(startIndex + index + 1);
            }

            for (uint index = 0; index < face.Vertices.Count; index++)
            {
                lineIndices.Add(startIndex + index);
                lineIndices.Add(startIndex + (index + 1) % (uint)face.Vertices.Count);
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
            if (face.Vertices.Count < 3)
                continue;

            Vector3 first = GetPosition(face.Vertices[0]);
            for (int index = 1; index + 1 < face.Vertices.Count; index++)
            {
                if (!RayTriangle(rayOrigin, rayDirection, first, GetPosition(face.Vertices[index]), GetPosition(face.Vertices[index + 1]), out float hitDistance))
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
    /// Bevels one or more convex edges by clipping the solid with a chamfer plane.
    /// This keeps a valid shared topology and intentionally refuses non-convex
    /// meshes, where an automatic bevel would otherwise create invalid collision.
    /// </summary>
    public bool TryBevel(IEnumerable<EditableBrushEdge> selectedEdges, float width, out string error)
    {
        EditableBrushEdge[] edges = selectedEdges.Distinct().ToArray();
        if (edges.Length == 0)
        {
            error = "Selecione ao menos uma aresta para aplicar bevel.";
            return false;
        }
        if (!IsConvex())
        {
            error = "O bevel atual é seguro apenas em brushes convexos.";
            return false;
        }

        var cuts = new List<(Vector3 point, Vector3 normal)>();
        foreach (EditableBrushEdge edge in edges)
        {
            List<EditableBrushFace> adjacent = GetFacesUsingEdge(edge);
            if (adjacent.Count != 2)
            {
                error = "Cada aresta do bevel precisa pertencer a exatamente duas faces.";
                return false;
            }

            Vector3 normal = CalculateFaceNormal(adjacent[0]) + CalculateFaceNormal(adjacent[1]);
            if (normal.LengthSquared() < Epsilon * Epsilon)
            {
                error = "Não é possível aplicar bevel em faces opostas.";
                return false;
            }

            Vector3 first = GetPosition(edge.A);
            Vector3 second = GetPosition(edge.B);
            Vector3 point = (first + second) * 0.5f;
            normal = Vector3.Normalize(normal);
            if (TryGetBounds(out Vector3 min, out Vector3 max) && Vector3.Dot(normal, ((min + max) * 0.5f) - point) > 0.0f)
                normal = -normal;
            cuts.Add((point, normal));
        }

        foreach ((Vector3 point, Vector3 normal) in cuts)
            ClipWithBevelPlane(point, normal, MathF.Max(0.0001f, width));

        error = string.Empty;
        return TryValidate(out error);
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

    private void ClipWithBevelPlane(Vector3 surfacePoint, Vector3 normal, float width)
    {
        float limit = Vector3.Dot(normal, surfacePoint) - width;
        var intersections = new List<int>();

        foreach (EditableBrushFace face in Faces.ToArray())
        {
            List<int> source = face.Vertices;
            var result = new List<int>();
            for (int index = 0; index < source.Count; index++)
            {
                int currentId = source[index];
                int nextId = source[(index + 1) % source.Count];
                Vector3 current = GetPosition(currentId);
                Vector3 next = GetPosition(nextId);
                float currentDistance = Vector3.Dot(normal, current) - limit;
                float nextDistance = Vector3.Dot(normal, next) - limit;
                bool currentInside = currentDistance <= Epsilon;
                bool nextInside = nextDistance <= Epsilon;

                if (currentInside)
                    result.Add(currentId);
                if (currentInside == nextInside)
                    continue;

                float t = currentDistance / (currentDistance - nextDistance);
                int intersection = AddOrGetVertex(Vector3.Lerp(current, next, t));
                result.Add(intersection);
                intersections.Add(intersection);
            }

            face.Vertices = result.Distinct().ToList();
        }

        Faces.RemoveAll(face => face.Vertices.Count < 3);
        int[] capVertices = intersections.Distinct().ToArray();
        if (capVertices.Length < 3)
            return;

        var polygon = capVertices.ToList();
        SortPolygon(polygon, normal, GetPosition);
        if (Vector3.Dot(CalculateNormal(polygon, GetPosition), normal) < 0.0f)
            polygon.Reverse();

        EditableBrushFace prototype = Faces.FirstOrDefault() ?? new EditableBrushFace();
        AddFace(prototype.CloneWithVertices(polygon));
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
