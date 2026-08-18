using System.Numerics;
using Fuse.Core;

namespace Fuse.Interaction;

[InteractableType("Door")]
public sealed class DoorInteract : IInteractable
{
    public Renderer.Entity? Entity { get; set; }
    public Physics.PhysicsWorld? World { get; set; }

    private Quaternion _baseRot;
    private bool _baseSet;
    private bool _open;
    private bool _animating;
    private float _elapsed;
    private float _duration = 1.0f;
    private Quaternion _from;
    private Quaternion _to;

    public void OnInteract()
    {
        if (Entity?.Body == null || !Entity.Body.IsBuilt || World == null || _animating)
            return;

        bool isChild = !string.IsNullOrEmpty(Entity.ParentId);

        if (!_baseSet)
        {
            _baseRot = isChild ? Entity.InitialRelativeRotation : Entity.Body.Rotation(World);
            _baseSet = true;
        }

        _open = !_open;
        _animating = true;
        _elapsed = 0f;
        _from = isChild ? Entity.InitialRelativeRotation : Entity.Body.Rotation(World);
        _to = _baseRot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, _open ? MathUtil.Deg(90f) : 0f);

        Audio.AudioSystem.Instance?.Play3D("Audio/door_move.wav", Entity.Body.Position(World), 1.0f, maxDist: 15f);

        Logger.Info($"DOOR: {Entity.Id}");
    }

    public void Update(float dt)
    {
        if (!_animating || Entity?.Body == null || !Entity.Body.IsBuilt || World == null)
            return;

        _elapsed += dt;
        float t = float.Clamp(_elapsed / _duration, 0f, 1f);

        float eased = _open 
            ? 1f - MathF.Pow(1f - t, 3f) // ease-out cúbico
            : t * t;    // ease-in quadrático

        var rot = Quaternion.Slerp(_from, _to, eased);
        
        if (string.IsNullOrEmpty(Entity.ParentId))
        {
            World.SetBodyPositionAndRotation(Entity.Body.Native, Entity.Body.Position(World), rot);
        }
        else
        {
            Entity.InitialRelativeRotation = rot; // O Scene.cs se encarrega de sincronizar a física baseada nisso
        }

        if (t >= 1f)
        {
            _animating = false;
            World.BodyInterface.SetAngularVelocity(Entity.Body.Native, Vector3.Zero);
        }
    }
}