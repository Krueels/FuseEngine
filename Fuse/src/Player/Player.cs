using System.Numerics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks.Dataflow;
using Fuse.Core;
using Fuse.Input;
using Fuse.Physics;
using Fuse.Renderer;
using JoltPhysicsSharp;

namespace Fuse.Player;

public class Player : IDisposable
{
    private readonly Physics.PhysicsWorld _world;
    private readonly Camera _camera;
    private readonly CharacterVirtual _character;
    private readonly BodyLockInterface _bli;
    private readonly CharacterVirtualSettings _settings = new();
    private ObjectLayer _objectLayer;

    private float _currentEyeHeight = 0.9f;

    private float _maxSpeedGround = 4.0f;
    private float _maxSpeedAir = 2.0f;
    private float _groundAccel = 10.0f;
    private float _airAccel = 350.0f;
    private float _frictionValue = 4.0f;
    private float _stopSpeed = 1.5f;
    private float _jumpForce = 3.8f;
    private float boostFactor = 1.05f;
    private float _surfAirAccel = 150.0f;
    private float _surfMaxSpeed = 3.8f;
    private float _maxVelocity = 3500.0f;
    private float _surfMinSpeed = 1.0f;

    private bool _isSprinting;
    private float _sprintSpeedMul = 1.5f;

    private bool _isCrouching;
    private float _capsuleRadius = 0.5f;
    private float _standCapsuleH = 1.8f;
    private float _crouchCapsuleH = 0.8f;
    private float _standEyeH = 0.9f;
    private float _crouchEyeH = 0.5f;
    private float _crouchSpeedMul = 0.5f;

    public bool SurfMode { get; set; } = false;
    public void ToggleSurfMode() => SurfMode = !SurfMode;

    private bool _noclip;
    private Vector3 _noclipPosition;
    private float _noclipSpeed = 10.0f;
    private float _noclipSprintSpeed = 15.0f;

    private Light? _flashlight;
    private Audio.AudioSystem? _audio;

    private float _tiltTarget;
    private float _tiltCurrent;
    private const float MaxTilt = MathF.PI / 55f;
    private const float TiltSpeed = 6f;


    public Player(Physics.PhysicsWorld world, Vector3 position)
    {
        _world = world;
        _camera = new Camera(position) { FOV = 75 };
        _objectLayer = world.ObjectLayer;
        _bli = world.Native.BodyLockInterface;

        _settings.Mass = 80.0f;
        _settings.Shape = new CapsuleShape(_standCapsuleH * 0.5f, _capsuleRadius);
        _settings.MaxSlopeAngle = float.DegreesToRadians(45.0f);
        _settings.CharacterPadding = 0.02f;
        _settings.PenetrationRecoverySpeed = 0.9f;
        _settings.PredictiveContactDistance = 0.05f;
        _settings.MaxCollisionIterations = 10;
        _settings.MaxConstraintIterations = 30;
        _settings.MinTimeRemaining = 1.0e-4f;
        _settings.CollisionTolerance = 1.0e-3f;
        _settings.MaxNumHits = 256;
        _settings.HitReductionCosMaxAngle = 0.999f;

        Vector3 posVec = position;
        Quaternion identity = Quaternion.Identity;
        _character = new CharacterVirtual(
            _settings,
            ref posVec,
            ref identity,
            0,
            _world.Native);

        _character.OnContactAdded += OnContactAdded;
        _character.OnContactPersisted += OnContactPersisted;

        _camera.SetRotation(-90.0f, 0.0f);
        SyncCamera();
    }

    public void Dispose()
    {
        _character.OnContactAdded -= OnContactAdded;
        _character.OnContactPersisted -= OnContactPersisted;
        _character.Dispose();
    }

    public void Update(float dt)
    {
        HandleNoclipToggle();
        HandleSprint();

        if (_noclip)
        {
            UpdateNoclip(dt);
            SyncCamera();
            return;
        }

        HandleCrouch();
        HandleFlashlightToggle();
        ApplyMovement(dt);

        bool isGrounded = _character.GroundState == GroundState.OnGround;

        var updSettings = new ExtendedUpdateSettings
        {
            StickToFloorStepDown = (!SurfMode || isGrounded) ? new Vector3(0, -0.5f, 0) : Vector3.Zero,
            WalkStairsStepUp = (!SurfMode || isGrounded) ? new Vector3(0, 0.5f, 0) : Vector3.Zero,
            WalkStairsMinStepForward = 0.02f,
            WalkStairsStepForwardTest = 0.15f,
            WalkStairsCosAngleForwardContact = float.Cos(float.DegreesToRadians(75.0f)),
            WalkStairsStepDownExtra = Vector3.Zero,
        };

        using var bodyFilter = new Physics.DefaultBodyFilter();
        using var shapeFilter = new Physics.DefaultShapeFilter();
        _character.ExtendedUpdate(dt, updSettings, ref _objectLayer, _world.Native, bodyFilter, shapeFilter);

        if (_character.GroundState == GroundState.OnGround)
        {
            Vector3 groundVel = _character.GroundVelocity;
            if (groundVel.LengthSquared() > 0.0001f)
            {
                Vector3 charPos = _character.Position;
                _character.Position = new Vector3(
                    charPos.X + groundVel.X * dt,
                    charPos.Y,
                    charPos.Z + groundVel.Z * dt);
            }
        }

        Vector3 charVel = _character.LinearVelocity;
        if (charVel.Y > 0.0f)
        {
            var contacts = _character.GetActiveContacts();
            foreach (var c in contacts)
            {
                if (c.ContactNormal.Y < -0.7f)
                {
                    charVel.Y = 0.0f;
                    _character.LinearVelocity = charVel;
                    break;
                }
            }
        }

        // Camera Tilt (quake reference XD)
        //_tiltTarget = 0f;
        //if (Input.Input.KeyDown(Input.KeyCodes.D)) _tiltTarget += MaxTilt;
        //if (Input.Input.KeyDown(Input.KeyCodes.A)) _tiltTarget -= MaxTilt;
        //_tiltCurrent = float.Lerp(_tiltCurrent, _tiltTarget, float.Min(dt * TiltSpeed, 1f));
        //_camera.Roll = _tiltCurrent;

        SyncCamera();
        SyncFlashlight();
        PushDynamicBodies();
    }

    private void HandleFlashlightToggle()
    {
        if (_flashlight == null) return;
        if (Input.Input.KeyPressed(KeyCodes.F))
        {
            _flashlight.Enabled = !_flashlight.Enabled;
            _audio.Play("Audio/flashlight.wav", volume: 1.0f);
        }
    }

    private void ApplyMovement(float dt)
    {
        Vector3 velocity = _character.LinearVelocity;
        bool onGround = _character.GroundState == GroundState.OnGround;
        bool onSteepGround = _character.GroundState == GroundState.OnSteepGround;

        Vector2 wishDir = BuildWishDir();

        if (onGround)
        {
            bool wantsJump = Input.Input.KeyDown(Input.KeyCodes.Space);

            Vector2 horiz = new(velocity.X, velocity.Z);
            if (!wantsJump)
                horiz = ApplyFriction(horiz, dt);
            float groundSpeed = _maxSpeedGround;
            if (_isSprinting)
                groundSpeed *= _sprintSpeedMul;
            else if (_isCrouching)
                groundSpeed *= _crouchSpeedMul;
            horiz = Accelerate(horiz, wishDir, groundSpeed, _groundAccel, dt);

            velocity.X = horiz.X;
            velocity.Y = 0.0f;
            velocity.Z = horiz.Y;

            if (horiz.Length() > 0.01f)
            {
                Vector3 groundNormal = _character.GroundNormal;
                if (groundNormal.Y < 0.99f)
                {
                    // If you're wondering why this exists, it's because Trimesh ghost contacts are
                    // complete bullshit and occasionally report walls as walkable slopes.
                    //
                    // Without this check the player randomly gets launched upward because some
                    // fucking invisible dumb-ass contact decided a vertical wall was actually ground.
                    //
                    // Don't remove this unless you enjoy wasting an entire weekend debugging
                    // the same stupid fucking problem again.
                    bool hasRealGround = false;
                    float charCenterY = (float)_character.Position.Y;

                    foreach (var c in _character.GetActiveContacts())
                    {
                        if (c.HadCollision && c.SurfaceNormal.Y > 0.5f && c.Position.Y < charCenterY)
                        {
                            hasRealGround = true;
                            break;
                        }
                    }

                    if (hasRealGround)
                    {
                        Vector3 moveDir = Vector3.Normalize(new Vector3(horiz.X, 0.0f, horiz.Y));
                        float dot = Vector3.Dot(moveDir, groundNormal);
                        if (dot < 0.0f)
                        {
                            float slopeFactor = 1.0f - groundNormal.Y;
                            velocity.Y = -dot * slopeFactor * horiz.Length();
                        }
                    }
                }
            }

            if (Input.Input.KeyDown(Input.KeyCodes.Space))
            {
                velocity.Y = _jumpForce;

                float currentSpeed = horiz.Length();
                if (currentSpeed > 0.1f)
                {
                    horiz *= boostFactor;
                    velocity.X = horiz.X;
                    velocity.Z = horiz.Y;
                }
            }
        }
        else
        {
            if (!SurfMode)
            {
                Vector2 horiz = new(velocity.X, velocity.Z);
                horiz = Accelerate(horiz, wishDir, _maxSpeedAir, _airAccel, dt);
                velocity.X = horiz.X;
                velocity.Z = horiz.Y;
            }
            else
            {
                bool isSurfing = onSteepGround;
                var contacts = _character.GetActiveContacts();
                foreach (var c in contacts)
                {
                    if (c.ContactNormal.Y < 0.707f && c.ContactNormal.Y > 0.05f)
                    {
                        isSurfing = true;
                        break;
                    }
                }

                Vector2 horiz = new(velocity.X, velocity.Z);
                float currentAirSpeed = isSurfing ? _surfMaxSpeed : _maxSpeedAir;
                float currentAirAccel = isSurfing ? _surfAirAccel : _airAccel;
                horiz = Accelerate(horiz, wishDir, currentAirSpeed, currentAirAccel, dt);
                velocity.X = horiz.X;
                velocity.Z = horiz.Y;
                
                // Add gravity
                velocity += _world.Gravity * dt;
                
                // Clip velocity against all steep contacts to avoid Jolt's internal collision killing speed
                foreach (var c in contacts)
                {
                    if (c.ContactNormal.Y < 0.707f) // Steeper than 45 degrees
                    {
                        velocity = ClipVelocity(velocity, Vector3.Normalize(c.ContactNormal), 1.0f);
                    }
                }
                
                if (onSteepGround)
                {
                    Vector3 rampNormal = _character.GroundNormal;
                    if (rampNormal.LengthSquared() > 0.01f)
                    {
                        velocity = ClipVelocity(velocity, Vector3.Normalize(rampNormal), 1.0f);
                    }
                }
            
                // Hard cap to prevent engine explosions
                float speed = velocity.Length();
                if (speed > _maxVelocity)
                    velocity = Vector3.Normalize(velocity) * _maxVelocity;
            }
        }

        if (!SurfMode && !onGround)
            velocity += _world.Gravity * dt;

        _character.LinearVelocity = velocity;
    }

    private Vector2 BuildWishDir()
    {
        if (!Input.Input.IsCursorDisabled())
            return Vector2.Zero;

        if (!SurfMode)
        {
            Vector3 oldDir = Vector3.Zero;
            if (Input.Input.KeyDown(Input.KeyCodes.W)) oldDir += _camera.Front;
            if (Input.Input.KeyDown(Input.KeyCodes.S)) oldDir -= _camera.Front;
            if (Input.Input.KeyDown(Input.KeyCodes.A)) oldDir -= _camera.Right;
            if (Input.Input.KeyDown(Input.KeyCodes.D)) oldDir += _camera.Right;
            oldDir.Y = 0.0f;
            if (oldDir.Length() < 0.01f) return Vector2.Zero;
            oldDir = Vector3.Normalize(oldDir);
            return new Vector2(oldDir.X, oldDir.Z);
        }

        Vector3 forward = _camera.Front;
        forward.Y = 0.0f;
        if (forward.LengthSquared() > 0.001f)
            forward = Vector3.Normalize(forward);

        Vector3 right = _camera.Right;
        right.Y = 0.0f;
        if (right.LengthSquared() > 0.001f)
            right = Vector3.Normalize(right);

        Vector3 dir = Vector3.Zero;
        if (Input.Input.KeyDown(Input.KeyCodes.W)) dir += forward;
        if (Input.Input.KeyDown(Input.KeyCodes.S)) dir -= forward;
        if (Input.Input.KeyDown(Input.KeyCodes.A)) dir -= right;
        if (Input.Input.KeyDown(Input.KeyCodes.D)) dir += right;

        if (dir.LengthSquared() < 0.01f) return Vector2.Zero;
        dir = Vector3.Normalize(dir);
        return new Vector2(dir.X, dir.Z);
    }

    private static Vector2 Accelerate(Vector2 velocity, Vector2 wishDir, float wishSpeed, float accel, float dt)
    {
        if (wishDir.Length() < 0.01f) return velocity;
        float currentSpeed = Vector2.Dot(velocity, wishDir);
        float addSpeed = wishSpeed - currentSpeed;
        if (addSpeed <= 0.0f) return velocity;
        float accelSpeed = float.Min(accel * dt * wishSpeed, addSpeed);
        return velocity + wishDir * accelSpeed;
    }

    private Vector3 ClipVelocity(Vector3 inputVel, Vector3 normal, float overbounce = 1.0f)
    {
        float backoff = Vector3.Dot(inputVel, normal);
        if (backoff < 0.0f)
        {
            return inputVel - normal * backoff * overbounce;
        }
        return inputVel;
    }

    private Vector2 ApplyFriction(Vector2 velocity, float dt)
    {
        float speed = velocity.Length();
        if (speed < 0.1f) return Vector2.Zero;
        float control = (speed < _stopSpeed) ? _stopSpeed : speed;
        float drop = control * _frictionValue * dt;
        float newSpeed = float.Max(speed - drop, 0.0f);
        return velocity * (newSpeed / speed);
    }

    private void SyncCamera()
    {
        var charPos = _character.Position;
        _camera.Position = new Vector3(charPos.X, (float)charPos.Y + _currentEyeHeight, charPos.Z);
    }

    private void SyncFlashlight()
    {
        if (_flashlight == null) return;
        _flashlight.Position = _camera.Position;
        _flashlight.Direction = _camera.Front;
    }

    public Camera Camera => _camera;
    public BodyLockInterface GetBodyLockInterface() => _bli;
    public Vector3 Position => new((float)_character.Position.X, (float)_character.Position.Y, (float)_character.Position.Z);
    public Vector3 EyePosition => new((float)_character.Position.X, (float)_character.Position.Y + _currentEyeHeight, (float)_character.Position.Z);
    public CharacterVirtual NativeCharacter => _character;
    public Vector3 LinearVelocity => new((float)_character.LinearVelocity.X, (float)_character.LinearVelocity.Y, (float)_character.LinearVelocity.Z);
    public bool IsOnGround => _character.GroundState == GroundState.OnGround;
    public bool IsCrouching => _isCrouching;
    public bool IsSprinting => _isSprinting;
    public bool IsNoclip => _noclip;
    public Vector3 FeetPosition
    {
        get
        {
            Vector3 pos = Position;
            float halfH = (_isCrouching ? _crouchCapsuleH : _standCapsuleH) * 0.5f;
            pos.Y -= halfH + _capsuleRadius;
            return pos;
        }
    }

    public void ToggleNoclip()
    {
        _noclip = !_noclip;
        if (_noclip)
            _noclipPosition = Position;
        else
        {
        _character.Position = new Vector3(_noclipPosition.X, _noclipPosition.Y, _noclipPosition.Z);
        _character.LinearVelocity = Vector3.Zero;
        }
    }

    public void SetSpeed(float speed) => _maxSpeedGround = speed;
    public float Speed => _maxSpeedGround;
    public void SetFlashlight(Light light) => _flashlight = light;
    public void SetAudioSystem(Audio.AudioSystem audio) => _audio = audio;

    private void HandleSprint()
    {
        _isSprinting = Input.Input.KeyDown(Input.KeyCodes.LeftShift) && !_isCrouching;
    }

    private void HandleCrouch()
    {
        bool wantsCrouch = Input.Input.KeyDown(Input.KeyCodes.LeftControl);

        if (wantsCrouch && !_isCrouching)
        {
            _isCrouching = true;
            _currentEyeHeight = _crouchEyeH;
            bool wasOnGround = IsOnGround;
            RebuildCapsule(_crouchCapsuleH);

            if (wasOnGround)
            {
                var pos = _character.Position;
                float lowerBy = (_standCapsuleH - _crouchCapsuleH) * 0.5f;
                _character.Position = new Vector3(pos.X, (float)pos.Y - lowerBy, pos.Z);
            }
        }
        else if (!wantsCrouch && _isCrouching)
        {
            var pos = _character.Position;
            float crouchHalfH = _crouchCapsuleH * 0.5f;
            float standHalfH = _standCapsuleH * 0.5f;

            float topY = (float)pos.Y + crouchHalfH + _capsuleRadius;
            float checkDist = standHalfH - crouchHalfH;
            var origin = new Vector3(pos.X, topY, pos.Z);
            var dir = new Vector3(0, 1, 0) * checkDist;
            Ray ray = new(ref origin, ref dir);

            using var bpFilter = new Physics.DefaultBroadPhaseLayerFilter();
            using var olFilter = new Physics.DefaultObjectLayerFilter();
            using var bodyFilter = new Physics.DefaultBodyFilter();
            bool blocked = _world.NarrowPhaseQuery.CastRay(ray, out _, bpFilter, olFilter, bodyFilter);

            if (!blocked)
            {
                _isCrouching = false;
                _currentEyeHeight = _standEyeH;
                bool wasOnGround = IsOnGround;
                RebuildCapsule(_standCapsuleH);

                if (wasOnGround)
                {
                    float lowerBy = (_standCapsuleH - _crouchCapsuleH) * 0.5f;
                    _character.Position = new Vector3(pos.X, (float)pos.Y + lowerBy, pos.Z);
                }
            }
        }
    }

    private void RebuildCapsule(float height)
    {
        Vector3 vel = _character.LinearVelocity;
        var newShape = new CapsuleShape(height * 0.5f, _capsuleRadius);

        using var bodyFilter = new Physics.DefaultBodyFilter();
        using var shapeFilter = new Physics.DefaultShapeFilter();
        _character.SetShape(0.0f, newShape, 1.0f, ref _objectLayer, _world.Native, bodyFilter, shapeFilter);
        _character.LinearVelocity = vel;
    }

    private void HandleNoclipToggle()
    {
        if (!Input.Input.KeyPressed(Input.KeyCodes.F1)) return;
        ToggleNoclip();
    }

    private void UpdateNoclip(float dt)
    {
        Vector3 dir = Vector3.Zero;
        if (Input.Input.KeyDown(Input.KeyCodes.W)) dir += _camera.Front;
        if (Input.Input.KeyDown(Input.KeyCodes.S)) dir -= _camera.Front;
        if (Input.Input.KeyDown(Input.KeyCodes.A)) dir -= _camera.Right;
        if (Input.Input.KeyDown(Input.KeyCodes.D)) dir += _camera.Right;
        if (Input.Input.KeyDown(Input.KeyCodes.Space)) dir += new Vector3(0, 1, 0);
        if (Input.Input.KeyDown(Input.KeyCodes.LeftControl)) dir -= new Vector3(0, 1, 0);

        if (dir.LengthSquared() > 0.000001f)
            dir = Vector3.Normalize(dir);

        float speed = _isSprinting ? _noclipSprintSpeed : _noclipSpeed;
        _noclipPosition += dir * speed * dt;
        _character.Position = new Vector3(_noclipPosition.X, _noclipPosition.Y, _noclipPosition.Z);
    }

    private void PushDynamicBodies()
    {
        var contacts = _character.GetActiveContacts();
        foreach (var c in contacts)
        {
            if (!c.HadCollision) continue;

            BodyID bodyID = c.BodyB;
            BodyLockWrite bodyLock = default;
            _bli.LockWrite(bodyID, out bodyLock);

            if (bodyLock.Succeeded)
            {
                if (bodyLock.Body.IsDynamic)
                {
                    float mass = 1.0f / bodyLock.Body.MotionProperties.InverseMassUnchecked;
                    float relVel = -Vector3.Dot(_character.LinearVelocity, c.ContactNormal);
                    if (relVel > 0.0f)
                    {
                        float impulseMag = relVel * mass * 0.3f;
                        Vector3 impulse = -c.ContactNormal * impulseMag;
                        _bli.UnlockWrite(bodyLock);
                        _world.BodyInterface.AddImpulse(bodyID, impulse);
                    }
                    else
                    {
                        _bli.UnlockWrite(bodyLock);
                    }
                }
                else
                {
                    _bli.UnlockWrite(bodyLock);
                }
            }
        }
    }



    private void OnContactAdded(CharacterVirtual character, in BodyID bodyID2, SubShapeID subShapeID2,
        in RVector3 contactPosition, in Vector3 contactNormal, ref CharacterContactSettings settings)
    {
        BodyLockRead bodyLock = default;
        _bli.LockRead(bodyID2, out bodyLock);
        if (bodyLock.Succeeded)
        {
            if (bodyLock.Body.IsStatic && contactNormal.Y > 0.707f && !bodyLock.Body.IsSensor)
                settings.CanPushCharacter = false;
            _bli.UnlockRead(bodyLock);
        }
    }

    private void OnContactPersisted(CharacterVirtual character, in BodyID bodyID2, SubShapeID subShapeID2,
        in RVector3 contactPosition, in Vector3 contactNormal, ref CharacterContactSettings settings)
    {
        OnContactAdded(character, bodyID2, subShapeID2, contactPosition, contactNormal, ref settings);
    }
}
