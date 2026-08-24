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

    // Recoil
    private float _recoilYaw;
    private float _recoilPitch;
    private float _recoilRoll;
    private float _recoilRecoverySpeed = 15.0f;     // yaw/roll recovery
    // Pitch NÃO recupera automaticamente - player deve compensar manualmente

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

    // Quaternion representing the camera's full orientation (yaw + pitch + roll + recoil)
    // Note: _yaw increases when turning RIGHT (CW), but standard math uses CCW positive.
    // Negate yaw to match standard math convention (CCW = positive).
    public Quaternion Rotation => Quaternion.CreateFromYawPitchRoll(
        float.DegreesToRadians(-(_yaw + _recoilYaw)),
        float.DegreesToRadians(_roll + _recoilRoll),
        float.DegreesToRadians(-(_pitch + _recoilPitch)));

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
        
        // Clamp TOTAL pitch (base + recoil) to avoid lockup
        float totalPitch = _pitch + _recoilPitch;
        totalPitch = float.Clamp(totalPitch, -89.0f, 89.0f);
        _pitch = totalPitch - _recoilPitch;
        
        UpdateVectors();
    }

    private void UpdateVectors()
    {
        // Combinar yaw/pitch base com recoil
        float totalYaw = _yaw + _recoilYaw;
        float totalPitch = _pitch + _recoilPitch;

        float yawRad = float.DegreesToRadians(totalYaw);
        float pitchRad = float.DegreesToRadians(totalPitch);

        _front.X = MathF.Cos(yawRad) * MathF.Cos(pitchRad);
        _front.Y = MathF.Sin(pitchRad);
        _front.Z = MathF.Sin(yawRad) * MathF.Cos(pitchRad);
        _front = Vector3.Normalize(_front);

        // Roll (base + recoil)
        float totalRoll = _roll + _recoilRoll;
        float rollRad = float.DegreesToRadians(totalRoll);

        _right = Vector3.Normalize(Vector3.Cross(_front, WorldUp));
        _up = Vector3.Normalize(Vector3.Cross(_right, _front));

        // Aplicar roll no up vector
        if (MathF.Abs(totalRoll) > 0.001f)
        {
            _up = Vector3.Transform(_up, Quaternion.CreateFromAxisAngle(_front, rollRad));
            _right = Vector3.Normalize(Vector3.Cross(_front, _up));
        }
    }

    // === RECOIL SYSTEM ===
    public void AddRecoil(float yawKick, float pitchKick, float rollKick = 0f)
    {
        _recoilYaw += yawKick;
        _recoilPitch += pitchKick;
        _recoilRoll += rollKick;
        UpdateVectors(); // Aplica imediatamente para o frame atual
    }

    public void UpdateRecoil(float dt)
    {
        // APENAS yaw e roll recuperam automaticamente
        // Pitch NÃO recupera - player deve compensar manualmente
        if (_recoilYaw != 0 || _recoilRoll != 0)
        {
            float yawRollRecovery = _recoilRecoverySpeed * dt;
            _recoilYaw = MathF.Abs(_recoilYaw) <= yawRollRecovery ? 0 : _recoilYaw - MathF.Sign(_recoilYaw) * yawRollRecovery;
            _recoilRoll = MathF.Abs(_recoilRoll) <= yawRollRecovery ? 0 : _recoilRoll - MathF.Sign(_recoilRoll) * yawRollRecovery;
            UpdateVectors();
        }
    }

    // Getters para ler o recoil atual (para viewmodel sync)
    public float RecoilYaw => _recoilYaw;
    public float RecoilPitch => _recoilPitch;
    public float RecoilRoll => _recoilRoll;

    // Aplicar recoil na Rotation property (já incluso no UpdateVectors acima)
    // O recoil já afeta _front/_right/_up via UpdateVectors
}
