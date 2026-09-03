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

    /// <summary>
    /// Points the perspective camera at a world-space target. This is used by
    /// editor previews that have their own camera and should not depend on the
    /// main viewport's fly-camera state.
    /// </summary>
    public void LookAt(Vector3 target)
    {
        if (IsOrthographic)
            return;

        Vector3 direction = target - Position;
        if (direction.LengthSquared() <= 0.000001f)
            return;

        direction = Vector3.Normalize(direction);
        _pitch = float.Clamp(
            -MathF.Asin(float.Clamp(direction.Y, -1.0f, 1.0f)) * 180.0f / MathF.PI,
            -89.0f,
            89.0f);
        _yaw = MathF.Atan2(-direction.Z, -direction.X) * 180.0f / MathF.PI;
    }

    /// <summary>
    /// Orbits a perspective camera around a target. The camera position and
    /// orientation are updated together so a preview can be rotated without
    /// exposing or mutating the editor's main viewport camera.
    /// </summary>
    public void OrbitAround(
        Vector3 target,
        float deltaYaw,
        float deltaPitch,
        float zoomFactor = 1.0f)
    {
        if (IsOrthographic)
            return;

        Vector3 offset = Position - target;
        float distance = offset.Length();
        if (!float.IsFinite(distance) || distance <= 0.001f)
            distance = MathF.Max(MinDistance, 1.0f);

        float azimuth = MathF.Atan2(offset.Z, offset.X);
        float elevation = MathF.Asin(float.Clamp(offset.Y / distance, -1.0f, 1.0f));
        const float radiansPerDegree = MathF.PI / 180.0f;
        azimuth += deltaYaw * radiansPerDegree;
        elevation = float.Clamp(
            elevation + deltaPitch * radiansPerDegree,
            -89.0f * radiansPerDegree,
            89.0f * radiansPerDegree);

        if (float.IsFinite(zoomFactor) && zoomFactor > 0.0f)
            distance *= zoomFactor;

        distance = float.Clamp(
            distance,
            MathF.Max(0.05f, MinDistance),
            MathF.Max(MinDistance, MaxDistance));

        float horizontalDistance = MathF.Cos(elevation) * distance;
        Position = target + new Vector3(
            MathF.Cos(azimuth) * horizontalDistance,
            MathF.Sin(elevation) * distance,
            MathF.Sin(azimuth) * horizontalDistance);
        LookAt(target);
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
