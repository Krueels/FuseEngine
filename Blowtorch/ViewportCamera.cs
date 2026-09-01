using System;
using System.Numerics;

namespace Blowtorch;

public enum CameraViewType
{
    Perspective3D,
    Top,
    Front,
    Side
}

public class ViewportCamera
{
    private float _yaw;
    private float _pitch = -20.0f;
    
    public Vector3 Position { get; set; } = new Vector3(0, 5, 8);

    public float Sensitivity { get; set; } = 0.3f;
    public float PanTwoDSensitivity { get; set; } = 1.8f;
    public float ScrollSpeed { get; set; } = 1.5f;
    public float PanSpeed { get; set; } = 0.005f;
    public float FlySpeed { get; set; } = 15.0f;
    public float MinDistance { get; set; } = 0.5f;
    public float MaxDistance { get; set; } = 200.0f;

    public CameraViewType ViewType { get; set; } = CameraViewType.Perspective3D;
    public float OrthoSize { get; set; } = 10.0f;
    public float FieldOfView { get; set; } = 65.0f;
    public float NearClipPlane { get; set; } = 0.1f;
    public float FarClipPlane { get; set; } = 5000f;

    public bool IsOrthographic => ViewType != CameraViewType.Perspective3D;

    public void Look(float deltaX, float deltaY)
    {
        if (IsOrthographic) return; // No look in ortho views
        _yaw += deltaX * Sensitivity;
        _pitch += deltaY * Sensitivity;
        _pitch = float.Clamp(_pitch, -89.0f, 89.0f);
    }

    public void Zoom(float delta, Vector2 mousePos, Vector2 viewportSize)
    {
        if (viewportSize.X <= 1.0f || viewportSize.Y <= 1.0f || !float.IsFinite(delta))
            return;
        if (IsOrthographic)
        {
            // Zoom to mouse logic
            float nx = (mousePos.X / viewportSize.X) * 2.0f - 1.0f;
            float ny = 1.0f - (mousePos.Y / viewportSize.Y) * 2.0f;
            float aspect = viewportSize.X / viewportSize.Y;

            Vector3 offsetBefore = Right * (nx * OrthoSize * aspect * 0.5f) + Up * (ny * OrthoSize * 0.5f);
            
            OrthoSize -= delta * (OrthoSize * 0.1f);
            OrthoSize = float.Clamp(OrthoSize, 0.1f, 10000.0f);
            
            Vector3 offsetAfter = Right * (nx * OrthoSize * aspect * 0.5f) + Up * (ny * OrthoSize * 0.5f);
            Position += offsetBefore - offsetAfter;
        }
        // No zoom needed for 3D FPS noclip
    }

    public void AdjustFlySpeed(float scrollDelta)
    {
        float scale = 1.0f + scrollDelta * 0.1f;
        FlySpeed = float.Clamp(FlySpeed * scale, 1.0f, 500.0f);
    }

    public void Pan(float deltaX, float deltaY, float viewportHeight)
    {
        if (IsOrthographic)
        {
            // Pixel-perfect pan
            float panScale = (OrthoSize / MathF.Max(viewportHeight, 1.0f)) * PanTwoDSensitivity;
            Position += -Right * deltaX * panScale + Up * deltaY * panScale;
        }
    }

    public void Fly(float forward, float rightInput, float upInput, float dt)
    {
        if (IsOrthographic) return; // No fly in ortho views
        
        Position += (Front * forward + Right * rightInput + Vector3.UnitY * upInput) * FlySpeed * dt;
    }

    public Matrix4x4 ViewMatrix
    {
        get
        {
            switch (ViewType)
            {
                case CameraViewType.Top:
                    return Matrix4x4.CreateLookAt(Position, Position - Vector3.UnitY, -Vector3.UnitZ);
                case CameraViewType.Front:
                    return Matrix4x4.CreateLookAt(Position, Position - Vector3.UnitZ, Vector3.UnitY);
                case CameraViewType.Side:
                    return Matrix4x4.CreateLookAt(Position, Position - Vector3.UnitX, Vector3.UnitY);
                default:
                    return Matrix4x4.CreateLookAt(Position, Position + Front, Vector3.UnitY);
            }
        }
    }

    public Matrix4x4 ProjectionMatrix(float aspect)
    {
        if (IsOrthographic)
        {
            return Matrix4x4.CreateOrthographic(OrthoSize * aspect, OrthoSize, -10000.0f, 10000.0f);
        }
        return Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(float.Clamp(FieldOfView, 1.0f, 170.0f)), aspect, NearClipPlane, FarClipPlane);
    }

    // Position is now an auto-property

    public Vector3 Front
    {
        get
        {
            switch (ViewType)
            {
                case CameraViewType.Top: return -Vector3.UnitY;
                case CameraViewType.Front: return -Vector3.UnitZ;
                case CameraViewType.Side: return -Vector3.UnitX;
                default:
                    float yawRad = float.DegreesToRadians(_yaw);
                    float pitchRad = float.DegreesToRadians(_pitch);
                    return Vector3.Normalize(new Vector3(
                        -MathF.Cos(yawRad) * MathF.Cos(pitchRad),
                        -MathF.Sin(pitchRad),
                        -MathF.Sin(yawRad) * MathF.Cos(pitchRad)));
            }
        }
    }

    public Vector3 Right
    {
        get
        {
            switch (ViewType)
            {
                case CameraViewType.Top: return Vector3.UnitX;
                case CameraViewType.Front: return Vector3.UnitX;
                case CameraViewType.Side: return -Vector3.UnitZ;
                default:
                    return Vector3.Normalize(Vector3.Cross(Front, Vector3.UnitY));
            }
        }
    }

    public Vector3 Up
    {
        get
        {
            switch (ViewType)
            {
                case CameraViewType.Top: return -Vector3.UnitZ;
                case CameraViewType.Front: return Vector3.UnitY;
                case CameraViewType.Side: return Vector3.UnitY;
                default:
                    return Vector3.Normalize(Vector3.Cross(Right, Front));
            }
        }
    }
}
