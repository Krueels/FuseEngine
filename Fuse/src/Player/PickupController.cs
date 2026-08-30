using System.Numerics;
using JoltPhysicsSharp;
using Fuse.Renderer;
using Fuse.Core;
using Fuse.Input;

namespace Fuse.Player;

public class PickupController
{
    private readonly Physics.PhysicsWorld _world;
    private readonly Camera _camera;
    private readonly BodyLockInterface _bli;
    private BodyID _heldBodyID = BodyID.Invalid;
    private BodyID _playerBodyID;

    private float _savedLinDamping;
    private float _savedAngDamping;
    private bool _savedGravity = true;

    private float _pickupRange = 5.0f;
    private float _holdDistance = 2.5f;
    private float _holdFollowSpeed = 20.0f;

    private float _forwardThrowForce = 12.0f;
    private float _lookThrowForce = 2.5f;
    private float _maxLookMomentum = 3.0f;
    private float _lookImpulseGain = 8.0f;
    private float _lookDecay = 12.0f;
    private float _lookDeadZone = 0.02f;
    private float _sideThrowWeight = 0.35f;
    private float _verticalThrowWeight = 0.25f;
    private float _maxTotalImpulse = 20.0f;

    private Vector2 _lookMomentum;
    private Audio.AudioSystem? _audio;

    public PickupController(Physics.PhysicsWorld world, Camera camera, BodyID playerBody, Audio.AudioSystem? audio = null)
    {
        _world = world;
        _camera = camera;
        _playerBodyID = playerBody;
        _audio = audio;
        _bli = world.Native.BodyLockInterface;
    }

    public void Update(float dt)
    {
        UpdateLookMomentum(dt);

        if (!Input.Input.IsCursorDisabled()) return;

        // Só processa pickup se NÃO estiver em contexto de arma
        if (InputManager.IsContextActive(InputContext.Weapon)) return;

        if (Input.Input.KeyPressed(Input.KeyCodes.E))
        {
            if (!_heldBodyID.IsValid)
                TryPickup();
            else
                DropObject(false);
        }

        if (_heldBodyID.IsValid && Input.Input.LeftMousePressed())
            DropObject(true);
    }

    public void PhysicsUpdate(float dt)
    {
        if (!_heldBodyID.IsValid) return;

        Vector3 target = GetHoldPosition();
        Vector3 comPos = _world.BodyInterface.GetCenterOfMassPosition(_heldBodyID);
        Vector3 toTarget = target - comPos;

        Vector3 desiredVel = toTarget * _holdFollowSpeed;
        Vector3 currentVel = _world.BodyInterface.GetLinearVelocity(_heldBodyID);
        Vector3 deltaV = desiredVel - currentVel;

        float mass = 1.0f;
        BodyLockRead readLock = default;
        _bli.LockRead(_heldBodyID, out readLock);
        if (readLock.Succeeded && readLock.Body is { } readBody)
        {
            float invMass = readBody.MotionProperties.InverseMassUnchecked;
            mass = (float.IsNaN(invMass) || invMass == 0.0f) ? 1.0f : 1.0f / invMass;
            _bli.UnlockRead(readLock);
        }

        Vector3 force = deltaV * mass * 10.0f;
        _world.BodyInterface.AddForce(_heldBodyID, force);
    }

    public bool IsHolding => _heldBodyID.IsValid;

    private void TryPickup()
    {
        Vector3 origin = _camera.Position;
        Vector3 dir = _camera.Front;

        using var bpFilter = new Physics.DefaultBroadPhaseLayerFilter();
        using var olFilter = new Physics.DefaultObjectLayerFilter();
        using var bodyFilter = new Physics.DefaultBodyFilter();
        Vector3 dirScaled = dir * _pickupRange;
        var ray = new Ray(in origin, in dirScaled);

        if (!_world.NarrowPhaseQuery.CastRay(ray, out var hit, bpFilter, olFilter, bodyFilter))
            return;

        BodyID hitID = hit.BodyID;
        if (!hitID.IsValid || hitID == _playerBodyID) return;

        BodyLockRead readLock = default;
        _bli.LockRead(hitID, out readLock);
        if (!readLock.Succeeded) return;

        if (readLock.Body is not { } body || !body.IsRigidBody || body.IsStatic)
        {
            _bli.UnlockRead(readLock);
            return;
        }

        var mp = body.MotionProperties;
        _savedLinDamping = mp.LinearDamping;
        _savedAngDamping = mp.AngularDamping;
        _savedGravity = _world.BodyInterface.GetGravityFactor(hitID) > 0.0f;
        _bli.UnlockRead(readLock);

        var bi = _world.BodyInterface;
        bi.SetGravityFactor(hitID, 0.0f);
        Vector3 zero = Vector3.Zero;
        bi.SetLinearVelocity(hitID, zero);
        bi.SetAngularVelocity(hitID, zero);

        BodyLockWrite writeLock = default;
        _bli.LockWrite(hitID, out writeLock);
        if (writeLock.Succeeded && writeLock.Body is { } writeBody)
        {
            writeBody.SetAllowSleeping(false);
            _bli.UnlockWrite(writeLock);
        }

        _heldBodyID = hitID;
        _audio?.Play("Audio/wpn_select.wav", volume: 1.0f);
    }

    private void DropObject(bool doThrow)
    {
        if (!_heldBodyID.IsValid) return;

        var bi = _world.BodyInterface;
        bi.SetGravityFactor(_heldBodyID, _savedGravity ? 1.0f : 0.0f);

        {
            BodyLockWrite writeLock = default;
            _bli.LockWrite(_heldBodyID, out writeLock);
            if (writeLock.Succeeded && writeLock.Body is { } writeBody)
            {
                writeBody.SetAllowSleeping(true);
                _bli.UnlockWrite(writeLock);
            }
        }

        if (doThrow)
        {
            Vector3 impulse = BuildThrowImpulse();
            bi.AddImpulse(_heldBodyID, impulse);
        }

        _heldBodyID = BodyID.Invalid;
    }

    private void UpdateLookMomentum(float dt)
    {
        float mx = Input.Input.MouseOffsetX;
        float my = Input.Input.MouseOffsetY;
        Vector2 mouseDelta = new(mx, my);

        if (mouseDelta.Length() > _lookDeadZone)
        {
            _lookMomentum += mouseDelta * _lookImpulseGain;
            _lookMomentum = Vector2.Clamp(_lookMomentum, -_maxLookMomentum * Vector2.One, _maxLookMomentum * Vector2.One);
        }
        else
        {
            float t = 1.0f - float.Exp(-_lookDecay * dt);
            _lookMomentum = Vector2.Lerp(_lookMomentum, Vector2.Zero, t);
        }
    }

    private Vector3 BuildThrowImpulse()
    {
        Vector3 forward = _camera.Front;
        Vector3 right = _camera.Right;
        Vector3 up = _camera.Up;

        Vector3 impulse = forward * _forwardThrowForce;
        impulse += right * (_lookMomentum.X * _lookThrowForce * _sideThrowWeight);
        impulse += up * (_lookMomentum.Y * _lookThrowForce * _verticalThrowWeight);

        float len = impulse.Length();
        if (len > _maxTotalImpulse)
            impulse *= _maxTotalImpulse / len;

        if (_heldBodyID.IsValid)
        {
            BodyLockRead readLock = default;
            _bli.LockRead(_heldBodyID, out readLock);
            if (readLock.Succeeded && readLock.Body is { } readBody)
            {
                float invMass = readBody.MotionProperties.InverseMassUnchecked;
                if (invMass > 0.0f)
                    impulse *= 1.0f / invMass;
                _bli.UnlockRead(readLock);
            }
        }

        return impulse;
    }

    private Vector3 GetHoldPosition()
    {
        return _camera.Position + _camera.Front * _holdDistance;
    }
}
