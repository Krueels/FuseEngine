using System.Numerics;

namespace Fuse.Renderer;

public class Camera
{
    private Vector3 _position;
    private Vector3 _front = new(0, 0, -1);
    private Vector3 _up = new(0, 1, 0);
    private Vector3 _right = new(1, 0, 0);
    private static readonly Vector3 WorldUp = new(0, 1, 0);

    private float _yaw = -90.0f;
    private float _pitch;
    public float _roll;
    private float _fov = 60.0f;
    private float _mouseSensitivity = 0.1f;

    public Camera(Vector3 position)
    {
        _position = position;
        UpdateVectors();
    }

    public Vector3 Position { get => _position; set => _position = value; }

    public void SetRotation(float yaw, float pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        UpdateVectors();
    }

    public float Yaw => _yaw;
    public float Pitch => _pitch;
    public float Roll { get => _roll; set => _roll = value; }

    // Quaternion representing the camera's full orientation (yaw + pitch + roll)
    // Note: _yaw increases when turning RIGHT (CW), but standard math uses CCW positive.
    // Negate yaw to match standard math convention (CCW = positive).
    public Quaternion Rotation => Quaternion.CreateFromYawPitchRoll(
        float.DegreesToRadians(-_yaw),
        float.DegreesToRadians(_roll),
        float.DegreesToRadians(-_pitch));

    public float FOV { get => _fov; set => _fov = value; }

    public Matrix4x4 GetViewMatrix()
    {
        var rolledUp = Vector3.Transform(_up, Quaternion.CreateFromAxisAngle(_front, _roll));
        return Matrix4x4.CreateLookAt(_position, _position + _front, rolledUp);
    }

    private float _nearPlane = 0.1f;
    private float _farPlane = 1000.0f;

    public float NearPlane { get => _nearPlane; set => _nearPlane = value; }
    public float FarPlane { get => _farPlane; set => _farPlane = value;  }

    public Matrix4x4 GetProjectionMatrix(float aspect)
    {
        return Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(_fov), aspect, _nearPlane, _farPlane);
    }

    public Vector3 Front => _front;
    public Vector3 Right => _right;
    public Vector3 Up => _up;

    public void MoveForward(float distance)
    {
        var fwd = Vector3.Normalize(new Vector3(_front.X, 0, _front.Z));
        _position += fwd * distance;
    }

    public void MoveRight(float distance)
    {
        _position += _right * distance;
    }

    public void MoveUp(float distance)
    {
        _position += WorldUp * distance;
    }

    public void ProcessMouseMovement(float deltaX, float deltaY, bool invertY = false)
    {
        _yaw += deltaX * _mouseSensitivity;
        _pitch += (invertY ? deltaY : -deltaY) * _mouseSensitivity;
        _pitch = float.Clamp(_pitch, -89.0f, 89.0f);
        UpdateVectors();
    }

    private void UpdateVectors()
    {
        float yawRad = float.DegreesToRadians(_yaw);
        float pitchRad = float.DegreesToRadians(_pitch);

        _front.X = MathF.Cos(yawRad) * MathF.Cos(pitchRad);
        _front.Y = MathF.Sin(pitchRad);
        _front.Z = MathF.Sin(yawRad) * MathF.Cos(pitchRad);
        _front = Vector3.Normalize(_front);

        _right = Vector3.Normalize(Vector3.Cross(_front, WorldUp));
        _up = Vector3.Normalize(Vector3.Cross(_right, _front));
    }
}
