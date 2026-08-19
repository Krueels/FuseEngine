using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using JoltPhysicsSharp;

namespace Fuse.Audio;

public sealed class ImpactSoundSystem : IDisposable
{
    private readonly Physics.PhysicsWorld _world;
    private readonly AudioSystem _audio;
    private readonly ConcurrentQueue<ImpactEvent> _peding = new();
    private readonly Dictionary<BodyID, float> _cooldowns = [];

    private readonly string[] _impactSounds;
    private readonly float _minSpeed;
    private readonly float _maxSpeed;
    private readonly float _cooldownTime;
    private int _lastSoundIndex = -1;

    public ImpactSoundSystem(Physics.PhysicsWorld world, AudioSystem audio, string[]? impactSounds = null, float minSpeed = 1.2f, float maxSpeed = 8.0f, float cooldownTime = 0.08f)
    {
        _world = world;
        _audio = audio;
        _impactSounds = impactSounds is { Length: > 0 }
            ? impactSounds
            : ["Audio/Physics/impact_0.wav", "Audio/Physics/impact_1.wav", "Audio/Physics/impact_2.wav", "Audio/Physics/impact_3.wav"
               , "Audio/Physics/impact_4.wav", "Audio/Physics/impact_5.wav", "Audio/Physics/impact_6.wav"
               , "Audio/Physics/impact_7.wav", "Audio/Physics/impact_8.wav", "Audio/Physics/impact_9.wav"
               , "Audio/Physics/impact_10.wav", "Audio/Physics/impact_11.wav", "Audio/Physics/impact_12.wav"];
        _minSpeed = minSpeed;
        _maxSpeed = maxSpeed;
        _cooldownTime = cooldownTime;

        _world.Native.OnContactAdded += OnContactAdded;
    }

    public void Update(float dt)
    {
        // Cooldowns (main thread)
        foreach (var key in _cooldowns.Keys.ToArray())
        {
            _cooldowns[key] -= dt;
            if (_cooldowns[key] <= 0f)
                _cooldowns.Remove(key);
        }

        // drena eventos da thread da física
        while (_peding.TryDequeue(out var evt))
        {
            if (_cooldowns.ContainsKey(evt.SourceID))
                continue;

            _cooldowns[evt.SourceID] = _cooldownTime;

            float t = float.Clamp((evt.ImpactSpeed - _minSpeed) / (_maxSpeed - _minSpeed), 0f, 1f);
            float volume = 0.15f + t * 0.85f;

            int index = PickSoundIndex();
            float pitch = 0.9f + Random.Shared.NextSingle() * 0.2f; // 0.9 .. 1.1
            _audio.Play3D(_impactSounds[index], evt.Position, volume, pitch: pitch);
        }
    }

    public void Clear()
    {
        _cooldowns.Clear();
        while (_peding.TryDequeue(out _)) { }
    }

    private int PickSoundIndex()
    {
        if (_impactSounds.Length == 1) return 0;

        int index;
        do
        {
            index = Random.Shared.Next(_impactSounds.Length);
        }
        while (index == _lastSoundIndex);

        _lastSoundIndex = index;
        return index;
    }

    public void Dispose()
    {
        _world.Native.OnContactAdded -= OnContactAdded;
    }

    private void OnContactAdded(PhysicsSystem system, in Body body1, in Body body2, in ContactManifold manifold, ref ContactSettings settings)
    {
        if (body1.IsSensor || body2.IsSensor) return;
        if (!body1.IsDynamic && !body2.IsDynamic) return;

        var relVel = body1.GetLinearVelocity() - body2.GetLinearVelocity();
        float impactSpeed = MathF.Abs(Vector3.Dot(relVel, manifold.WorldSpaceNormal));
        if (impactSpeed < _minSpeed) return;

        var sourceID = body1.IsDynamic ? body1.ID : body2.ID;
        var pos = body1.IsStatic ? body2.Position : body1.Position;

        _peding.Enqueue(new ImpactEvent(sourceID, pos, impactSpeed));
    }

    private readonly record struct ImpactEvent(BodyID SourceID, Vector3 Position, float ImpactSpeed);
}