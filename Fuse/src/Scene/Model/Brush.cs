using System.Collections.Generic;

namespace Fuse.Scene.Model;

public class Brush : MapObject
{
    public List<Face> Faces { get; set; } = new();
    public BrushGeometryMode GeometryMode { get; set; } = BrushGeometryMode.PlaneCsg;
    public EditableBrushMesh? EditableMesh { get; set; }

    public bool IsEditableMesh => GeometryMode == BrushGeometryMode.EditableMesh && EditableMesh != null;
    // CSG still operates on the authored plane list. Once a brush owns polygon
    // topology, even a currently convex result must not be fed back into the
    // plane-only CSG implementation or it would discard component edits.
    public bool SupportsPlaneCsg => !IsEditableMesh;

    public void AddFace(Face face)
    {
        Faces.Add(face);
    }

    public void ApplyTransformMatrix(System.Numerics.Matrix4x4 transform)
    {
        if (IsEditableMesh)
        {
            foreach (EditableBrushVertex vertex in EditableMesh!.Vertices)
                vertex.Position = System.Numerics.Vector3.Transform(vertex.Position, transform);
            MarkGeometryChanged();
            return;
        }

        if (System.Numerics.Matrix4x4.Invert(transform, out var inverted))
        {
            var invTranspose = System.Numerics.Matrix4x4.Transpose(inverted);
            for (int i = 0; i < Faces.Count; i++)
            {
                var face = Faces[i];
                var plane = face.Plane;
                var v = new System.Numerics.Vector4(plane.Normal, plane.D);
                var vNew = System.Numerics.Vector4.Transform(v, invTranspose);
                
                var newNormal = new System.Numerics.Vector3(vNew.X, vNew.Y, vNew.Z);
                float len = newNormal.Length();
                if (len > 0.000001f)
                {
                    newNormal /= len;
                    face.Plane = new System.Numerics.Plane(newNormal, vNew.W / len);
                }
            }
        }
    }

    public void ScalePlanes(System.Numerics.Vector3 scale)
    {
        if (IsEditableMesh)
        {
            foreach (EditableBrushVertex vertex in EditableMesh!.Vertices)
                vertex.Position *= scale;
            MarkGeometryChanged();
            return;
        }

        for (int i = 0; i < Faces.Count; i++)
        {
            var face = Faces[i];
            var normal = face.Plane.Normal;
            float d = face.Plane.D;
            
            var newNormal = new System.Numerics.Vector3(
                scale.X != 0 ? normal.X / scale.X : normal.X,
                scale.Y != 0 ? normal.Y / scale.Y : normal.Y,
                scale.Z != 0 ? normal.Z / scale.Z : normal.Z
            );
            float len = newNormal.Length();
            if (len > 0.000001f)
            {
                newNormal /= len;
                Faces[i] = new Face(new System.Numerics.Plane(newNormal, d / len))
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
                };
            }
        }
    }

    /// <summary>
    /// Converts a legacy plane brush on demand. This is intentionally lazy: maps
    /// and CSG continue to use their original representation until the user edits
    /// a vertex, edge or face in Blowtorch.
    /// </summary>
    public EditableBrushMesh EnsureEditableMesh()
    {
        if (IsEditableMesh)
            return EditableMesh!;

        EditableMesh = EditableBrushMesh.FromPlaneBrush(this);
        GeometryMode = BrushGeometryMode.EditableMesh;
        MarkGeometryChanged();
        return EditableMesh;
    }

    /// <summary>
    /// Keeps the brush body centered around editable geometry. The local shift is
    /// moved into the body's world transform so existing placement does not jump.
    /// </summary>
    public void MarkGeometryChanged()
    {
        if (!IsEditableMesh || Body == null)
            return;

        System.Numerics.Vector3 localCenter = EditableMesh!.NormalizeOrigin();
        Body.Position += System.Numerics.Vector3.Transform(localCenter, Body.Rotation);
        UpdateEditableBounds();
    }

    /// <summary>
    /// Refreshes the brush bounds without changing its local origin. Dragging
    /// component vertices uses this path so the gizmo pivot stays stable until
    /// the drag is finished.
    /// </summary>
    public void UpdateEditableBounds()
    {
        EditableBrushMesh? editableMesh = EditableMesh;
        if (!IsEditableMesh || editableMesh == null || Body == null)
            return;

        if (editableMesh.TryGetBounds(out System.Numerics.Vector3 min, out System.Numerics.Vector3 max))
        {
            Body.HalfExtents = System.Numerics.Vector3.Max(new System.Numerics.Vector3(0.01f), (max - min) * 0.5f);
            Body.Shape = MapShapeType.Trimesh;
        }
    }

    public static Brush CreateCube(System.Numerics.Vector3 position, System.Numerics.Vector3 size)
    {
        var brush = new Brush { Id = "brush_" + System.Guid.NewGuid().ToString().Substring(0, 8) };
        System.Numerics.Vector3 half = size / 2.0f;
        
        // Front, Back, Top, Bottom, Right, Left
        brush.AddFace(new Face(new System.Numerics.Plane(new System.Numerics.Vector3(0, 0, 1), -half.Z)));
        brush.AddFace(new Face(new System.Numerics.Plane(new System.Numerics.Vector3(0, 0, -1), -half.Z)));
        brush.AddFace(new Face(new System.Numerics.Plane(new System.Numerics.Vector3(0, 1, 0), -half.Y)));
        brush.AddFace(new Face(new System.Numerics.Plane(new System.Numerics.Vector3(0, -1, 0), -half.Y)));
        brush.AddFace(new Face(new System.Numerics.Plane(new System.Numerics.Vector3(1, 0, 0), -half.X)));
        brush.AddFace(new Face(new System.Numerics.Plane(new System.Numerics.Vector3(-1, 0, 0), -half.X)));
        
        // Add a body for selection
        brush.Body = new MapBody
        {
            Shape = MapShapeType.Box,
            Position = position,
            HalfExtents = half
        };

        return brush;
    }
}
