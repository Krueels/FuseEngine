using System.Numerics;
using JoltPhysicsSharp;
using Fuse.Player;

namespace Fuse.Behaviours;

public sealed class TriggerSystem
{
    private readonly CharacterVirtual _character;
    private readonly List<IBehaviour> _behaviours;
    private readonly Func<BodyID, Renderer.Entity?> _entityLookup;
    private readonly BodyLockInterface _bli;
    private readonly HashSet<BodyID> _current = [];

    public TriggerSystem(
        CharacterVirtual character,
        List<IBehaviour> behaviours,
        Func<BodyID, Renderer.Entity?> entityLookup,
        BodyLockInterface bli)
    {
        _character = character;
        _behaviours = behaviours;
        _entityLookup = entityLookup;
        _bli = bli;
    }

    public void Update(float dt)
    {
        var prev = new HashSet<BodyID>(_current);
        _current.Clear();

        foreach (var c in _character.GetActiveContacts())
        {
            BodyLockRead bodyLock = default;
            _bli.LockRead(c.BodyB, out bodyLock);
            if (bodyLock.Succeeded)
            {
                if (bodyLock.Body is { } body && body.IsSensor)
                {
                    BodyID id = c.BodyB;
                    _current.Add(id);
                    if (!prev.Remove(id))
                        NotifyEnter(id);
                }
                _bli.UnlockRead(bodyLock);
            }
        }

        foreach (var exited in prev)
            NotifyExit(exited);
    }

    private void NotifyEnter(BodyID triggerId)
    {
        var entity = _entityLookup(triggerId);
        if (entity == null) return;

        foreach (var behaviour in _behaviours)
        {
            if (behaviour.Entity?.Id == entity.Id)
                behaviour.OnTriggerEnter("player");
        }
    }

    private void NotifyExit(BodyID triggerId)
    {
        var entity = _entityLookup(triggerId);
        if (entity == null) return;

        foreach (var behaviour in _behaviours)
        {
            if (behaviour.Entity?.Id == entity.Id)
                behaviour.OnTriggerExit("player");
        }
    }
}
