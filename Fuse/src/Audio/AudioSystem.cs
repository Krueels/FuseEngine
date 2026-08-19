using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Fuse.Core;
using SoLoudSharp;

namespace Fuse.Audio;

public sealed class AudioSystem : IDisposable
{
    public static AudioSystem? Instance { get; private set; }

    private readonly Soloud _soloud;
    private readonly Dictionary<string, Wav> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WavStream> _streams = new(StringComparer.OrdinalIgnoreCase);

    public AudioSystem()
    {
        _soloud = new Soloud(
            SoloudInitFlags.ClipRoundoff,
            SoloudBackend.MiniAudio,
            samplerate: 0,
            bufferSize: 0,
            channels: 2);
        Instance = this;
        Logger.Important($"AudioSystem: Backend {_soloud.BackendString}");
    }

    public float GlobalVolume
    {
        get => _soloud.GlobalVolume;
        set => _soloud.GlobalVolume = value;
    }

    public void SetPaused(bool paused) => _soloud.SetPauseAll(paused);

    public VoiceHandle Play(string path, float volume = 1.0f)
    {
        var wav = GetSound(path);
        if (wav == null) return VoiceHandle.None;
        return _soloud.Play(wav, volume);
    }

    public VoiceHandle Play3D(string path, Vector3 position, float volume = 1.0f,
        float minDist = 5.0f, float maxDist = 150.0f, float pitch = 1.0f)
    {
        var wav = GetSound(path);
        if (wav == null) return VoiceHandle.None;

        var voice = _soloud.Play3D(wav, position.X, position.Y, position.Z, volume: volume);
        _soloud.Set3DSourceMinMaxDistance(voice, minDist, maxDist);
        _soloud.Set3DSourceAttenuation(voice, AttenuationModel.InverseDistance, 1.0f);
        if (pitch !=  1.0f)
            _soloud.SetRelativePlaySpeed(voice, pitch);
        return voice;
    }

    public VoiceHandle PlayMusic(string path, float volume = 0.5f)
    {
        if (!_streams.TryGetValue(path, out var stream))
        {
            stream = new WavStream();
            if (stream.Load(ResolvePath(path)) != SoloudResult.Ok)
            {
                stream.Dispose();
                Logger.Error($"AudioSystem: Failed to load music '{path}'");
                return VoiceHandle.None;
            }
            stream.SetLooping(true);
            _streams[path] = stream;
        }

        return _soloud.Play(stream, volume);
    }

    public void Stop(VoiceHandle voice)
    {
        if (voice.IsValid)
            _soloud.Stop(voice);
    }

    public void UpdateListener(Vector3 position, Vector3 front, Vector3 up, Vector3 velocity)
    {
        _soloud.Set3DListenerParameters(
            position.X, position.Y, position.Z,
            front.X, front.Y, front.Z,
            up.X, up.Y, up.Z,
            velocity.X, velocity.Y, velocity.Z);
        _soloud.Update3DAudio();
    }

    public void Dispose()
    {
        foreach (var s in _streams.Values) s.Dispose();
        foreach (var s in _sounds.Values) s.Dispose();
        _streams.Clear();
        _sounds.Clear();
        _soloud.Dispose();
        Instance = null;
    }

    private Wav? GetSound(string path)
    {
        if (_sounds.TryGetValue(path, out var wav))
            return wav;

        wav = new Wav();
        if (wav.Load(ResolvePath(path)) != SoloudResult.Ok)
        {
            wav.Dispose();
            Logger.Error($"AudioSystem: Failed on Load '{path}'");
            return null;
        }
        _sounds[path] = wav;
        return wav;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path) || path.StartsWith(Fuse.ResPath.Path, StringComparison.OrdinalIgnoreCase))
            return path;
        return $"{Fuse.ResPath.Path}/{path}";
    }
}