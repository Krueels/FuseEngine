using System.Net.NetworkInformation;
using System.Numerics;
using System.Security.Policy;
using JoltPhysicsSharp;

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
    private float _recoilPitchMax = 22.0f;
    private float _recoilYawMax = 3.0f;
    private float _recoilRecoverySpeed = 6.0f;     // yaw/roll recovery

    // Shake Tilt
    private float _shakeTilt;
    private float _shakeVelocity;
    private float _shakeStiffness = 1000f; // rigidez da mola (mais alto = mais rápido)
    private float _shakeDamping = 14.0f; // amortecimento (mais alto = menos bounce)

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
        float.DegreesToRadians(_roll),
        float.DegreesToRadians(-(_pitch + _recoilPitch)));

    public float FOV { get => _fov; set => _fov = value; }

    public Matrix4x4 GetViewMatrix()
    {
        float totalRoll = _roll + float.DegreesToRadians(_shakeTilt);
        var rolledUp = Vector3.Transform(_up, Quaternion.CreateFromAxisAngle(_front, totalRoll));
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

    /// <summary>Converte posição world para coordenadas de tela (pixels). Retorna (-9999,-9999) se atrás da câmera.</summary>
    public Vector2 WorldToScreenPoint(Vector3 worldPos, int screenWidth, int screenHeight)
    {
        var view = GetViewMatrix();
        var proj = GetProjectionMatrix((float)screenWidth / screenHeight);
        var clip = Vector4.Transform(new Vector4(worldPos, 1.0f), view * proj);
        if (clip.W <= 0.001f) return new Vector2(-9999, -9999);
        float x = (clip.X / clip.W + 1) * 0.5f * screenWidth;
        float y = (1 - clip.Y / clip.W) * 0.5f * screenHeight;
        return new Vector2(x, y);
    }

    /// <summary>Cria um Ray do Jolt a partir da posição do mouse na tela.</summary>
    public Ray GetMouseRay(Vector2 mousePos, int width, int height)
    {
        float x = (2.0f * mousePos.X) / width - 1.0f;
        float y = 1.0f - (2.0f * mousePos.Y) / height;
        float z = 1.0f;

        Matrix4x4.Invert(GetProjectionMatrix((float)width / height), out Matrix4x4 invProj);
        Matrix4x4.Invert(GetViewMatrix(), out Matrix4x4 invView);

        var rayClip = new Vector4(x, y, z, 1.0f);
        var rayEye = Vector4.Transform(rayClip, invProj);
        rayEye = new Vector4(rayEye.X, rayEye.Y, -1.0f, 0.0f);
        var rayWorld = Vector4.Transform(rayEye, invView);
        var rayDir = Vector3.Normalize(new Vector3(rayWorld.X, rayWorld.Y, rayWorld.Z));

        var origin = Position;
        var dirScaled = rayDir * 100f; // 100m range
        return new Ray(ref origin, ref dirScaled);
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

        float rollRad = float.DegreesToRadians(_roll);

        _right = Vector3.Normalize(Vector3.Cross(_front, WorldUp));
        _up = Vector3.Normalize(Vector3.Cross(_right, _front));

        if (MathF.Abs(_roll) > 0.001f)
        {
            _up = Vector3.Transform(_up, Quaternion.CreateFromAxisAngle(_front, rollRad));
            _right = Vector3.Normalize(Vector3.Cross(_front, _up));
        }
    }

    // === RECOIL SYSTEM ===
    public void AddRecoil(float yawKick, float pitchKick, float rollKick = 0f)
    {
        _recoilPitch = MathF.Min(_recoilPitch + pitchKick, _recoilPitchMax);
        _recoilYaw += yawKick;
        _recoilYaw = float.Clamp(_recoilYaw, -_recoilYawMax, _recoilYawMax);
        UpdateVectors(); // Aplica imediatamente para o frame atual
    }

    public void UpdateRecoil(float dt)
    {
        bool changed = false;

        // Decaimento exponencial — ease-out natural (rápido no início, devagar perto do zero)
        if (_recoilPitch != 0)
        {
            _recoilPitch *= MathF.Exp(-_recoilRecoverySpeed * dt);
            if (MathF.Abs(_recoilPitch) < 0.01f) _recoilPitch = 0;
            changed = true;
        }

        if (_recoilYaw != 0)
        {
            _recoilYaw *= MathF.Exp(-_recoilRecoverySpeed * 1.2f * dt);
            if (MathF.Abs(_recoilYaw) < 0.01f) _recoilYaw = 0;
            changed = true;
        }

        if (changed) UpdateVectors();

        if (_shakeTilt != 0 || _shakeVelocity != 0)
        {
            float acceleration = -_shakeStiffness * _shakeTilt - _shakeDamping * _shakeVelocity;
            _shakeVelocity += acceleration * dt;
            _shakeTilt += _shakeVelocity * dt;
            if (MathF.Abs(_shakeTilt) < 0.001f && MathF.Abs(_shakeVelocity) < 0.001f)
            {
                _shakeTilt = 0;
                _shakeVelocity = 0;
            }
            UpdateVectors();
        }
    }

    public void AddShakeTilt(float degress)
    {
        _shakeTilt += degress;
        UpdateVectors();
    }

    // Getters para ler o recoil atual (para viewmodel sync)
    public float RecoilYaw => _recoilYaw;
    public float RecoilPitch => _recoilPitch;

    // O recoil já afeta _front/_right/_up via UpdateVectors
}
