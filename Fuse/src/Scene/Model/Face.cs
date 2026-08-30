using System.Numerics;

namespace Fuse.Scene.Model;

public class Face
{
    public Plane Plane { get; set; }
    public string Texture { get; set; } = "default";
    public int MaterialSlot { get; set; } = 0;
    
    // UV Mapping properties
    public Vector3 UAxis { get; set; } = Vector3.UnitX;
    public Vector3 VAxis { get; set; } = -Vector3.UnitZ;
    public float UScale { get; set; } = 1.0f;
    public float VScale { get; set; } = 1.0f;
    public float UOffset { get; set; } = 0.0f;
    public float VOffset { get; set; } = 0.0f;
    public float Rotation { get; set; } = 0.0f;

    public Face(Plane plane)
    {
        Plane = plane;
        
        var normal = plane.Normal;
        float nx = System.Math.Abs(normal.X);
        float ny = System.Math.Abs(normal.Y);
        float nz = System.Math.Abs(normal.Z);

        if (nx > ny && nx > nz)
        {
            UAxis = Vector3.UnitZ;
            VAxis = -Vector3.UnitY;
        }
        else if (ny > nx && ny > nz)
        {
            UAxis = Vector3.UnitX;
            VAxis = -Vector3.UnitZ;
        }
        else
        {
            UAxis = Vector3.UnitX;
            VAxis = -Vector3.UnitY;
        }
    }
}
