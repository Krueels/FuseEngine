using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core;
using SoLoudSharp;

namespace Fuse.Audio;

public sealed class AudioSystem : IDisposable
{
    private sealed class AudioPreloadJob
    {
        public required string Key { get; init; }
        public required string RequestPath { get; init; }
        public required AssetManagement.AssetPriority Priority { get; init; }
    }

    public static AudioSystem? Instance { get; private set; }

    private readonly Soloud _soloud;
    private readonly Dictionary<string, Wav> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WavStream> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _preloadGate = new();
    private readonly PriorityQueue<AudioPreloadJob, int> _preloadQueue = new();
    private readonly PriorityQueue<Action, int> _audioUploadQueue = new();
    private readonly HashSet<string> _pendingPreloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoResetEvent _preloadWake = new(false);
    private readonly CancellationTokenSource _preloadCancellation = new();
    private readonly Task _preloadWorker;
    private bool _preloadStopped;
    private bool _disposed;

    public EngineProfiler? Profiler { get; set; }

    public AudioSystem()
    {
        _soloud = new Soloud(
            SoloudInitFlags.ClipRoundoff,
            SoloudBackend.MiniAudio,
            samplerate: 0,
            bufferSize: 0,
            channels: 2);
        _soloud.SetMaxActiveVoiceCount(32);
        Instance = this;
        _preloadWorker = Task.Run(PreloadWorkerLoop);
        Logger.Important($"AudioSystem: Backend {_soloud.BackendString}");
    }

    public float GlobalVolume
    {
        get => _soloud.GlobalVolume;
        set => _soloud.GlobalVolume = value;
    }

    public int PendingPreloads
    {
        get { lock (_preloadGate) return _preloadQueue.Count + _audioUploadQueue.Count; }
    }

    public void SetPaused(bool paused) => _soloud.SetPauseAll(paused);

    public VoiceHandle Play(string path, float volume = 1.0f)
    {
        Wav? wav = GetSound(path);
        if (wav == null) return VoiceHandle.None;
        return _soloud.Play(wav, volume);
    }

    public VoiceHandle Play3D(string path, Vector3 position, float volume = 1.0f,
        float minDist = 5.0f, float maxDist = 150.0f, float pitch = 1.0f)
    {
        Wav? wav = GetSound(path);
        if (wav == null) return VoiceHandle.None;

        VoiceHandle voice = _soloud.Play3D(wav, position.X, position.Y, position.Z, volume: volume);
        _soloud.Set3DSourceMinMaxDistance(voice, minDist, maxDist);
        _soloud.Set3DSourceAttenuation(voice, AttenuationModel.InverseDistance, 1.0f);
        if (pitch != 1.0f)
            _soloud.SetRelativePlaySpeed(voice, pitch);
        return voice;
    }

    public VoiceHandle PlayMusic(string path, float volume = 0.5f)
    {
        string key = AudioKey(path);
        if (!_streams.TryGetValue(key, out WavStream? stream))
        {
            EngineProfiler? profiler = Profiler;
            if (profiler == null)
                stream = LoadMusicStream(path);
            else
            {
                using var scope = profiler.Measure(ProfilerSection.AudioLoading);
                stream = LoadMusicStream(path);
            }

            if (stream == null)
                return VoiceHandle.None;

            stream.SetLooping(true);
            _streams[key] = stream;
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

    /// <summary>
    /// Uploads a bounded amount of decoded audio on the main thread. Call once
    /// per frame and during the loading screen.
    /// </summary>
    public int PumpPreloads(int maxUploads = 4)
    {
        int processed = 0;
        while (processed < System.Math.Max(1, maxUploads))
        {
            Action? upload = null;
            lock (_preloadGate)
            {
                if (_audioUploadQueue.Count > 0)
                    upload = _audioUploadQueue.Dequeue();
            }

            if (upload == null)
                break;

            try { upload(); }
            catch (Exception ex) { Logger.Error($"Audio preload upload failed: {ex.Message}"); }
            processed++;
        }
        return processed;
    }

    /// <summary>
    /// Queues file I/O and WAV decoding before gameplay. This is intentionally
    /// asynchronous; Play will use the cached sound as soon as PumpPreloads has
    /// completed it and will not synchronously stall the frame on first use.
    /// </summary>
    public void QueuePreloadSound(
        string path,
        AssetManagement.AssetPriority priority = AssetManagement.AssetPriority.High)
    {
        string key = AudioKey(path);
        lock (_preloadGate)
        {
            if (_preloadStopped || _sounds.ContainsKey(key) || !_pendingPreloads.Add(key))
                return;
            _preloadQueue.Enqueue(new AudioPreloadJob
            {
                Key = key,
                RequestPath = path,
                Priority = priority
            }, (int)priority);
        }
        _preloadWake.Set();
    }

    public void PreloadSound(string path) => QueuePreloadSound(path);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (_preloadGate)
            _preloadStopped = true;
        _preloadCancellation.Cancel();
        _preloadWake.Set();
        try { _preloadWorker.Wait(1000); }
        catch (AggregateException) { }

        lock (_preloadGate)
        {
            _preloadQueue.Clear();
            _audioUploadQueue.Clear();
            _pendingPreloads.Clear();
        }

        _preloadWake.Dispose();
        _preloadCancellation.Dispose();

        foreach (WavStream stream in _streams.Values) stream.Dispose();
        foreach (Wav sound in _sounds.Values) sound.Dispose();
        _streams.Clear();
        _sounds.Clear();
        _soloud.Dispose();
        Instance = null;
    }

    private Wav? GetSound(string path)
    {
        string key = AudioKey(path);
        if (_sounds.TryGetValue(key, out Wav? wav))
            return wav;

        // A queued preload is deliberately not loaded synchronously here. This
        // is what prevents a spider footstep from producing a random FPS spike.
        lock (_preloadGate)
        {
            if (_pendingPreloads.Contains(key))
                return null;
        }

        EngineProfiler? profiler = Profiler;
        if (profiler == null)
            wav = LoadSound(path);
        else
        {
            using var scope = profiler.Measure(ProfilerSection.AudioLoading);
            wav = LoadSound(path);
        }

        if (wav == null)
            return null;

        _sounds[key] = wav;
        return wav;
    }

    private void PreloadWorkerLoop()
    {
        while (!_preloadCancellation.IsCancellationRequested)
        {
            AudioPreloadJob? job = null;
            lock (_preloadGate)
            {
                if (_preloadQueue.Count > 0)
                    job = _preloadQueue.Dequeue();
            }

            if (job == null)
            {
                _preloadWake.WaitOne(25);
                continue;
            }

            try
            {
                string resolved = ResolvePath(job.RequestPath);
                if (!File.Exists(resolved))
                {
                    Logger.Error($"AudioSystem: Failed on Load '{job.RequestPath}'");
                    lock (_preloadGate) _pendingPreloads.Remove(job.Key);
                    continue;
                }

                // File read and WAV decoding happen off the render thread.
                byte[] encodedData = File.ReadAllBytes(resolved);
                Wav sound = new();
                if (sound.LoadMem(encodedData) != SoloudResult.Ok)
                {
                    sound.Dispose();
                    Logger.Error($"AudioSystem: Failed on Load '{job.RequestPath}'");
                    lock (_preloadGate) _pendingPreloads.Remove(job.Key);
                    continue;
                }

                EnqueueAudioUpload((int)job.Priority, () =>
                {
                    try
                    {
                        _sounds[job.Key] = sound;
                    }
                    finally
                    {
                        lock (_preloadGate) _pendingPreloads.Remove(job.Key);
                    }
                });
            }
            catch (Exception ex)
            {
                lock (_preloadGate) _pendingPreloads.Remove(job.Key);
                Logger.Error($"AudioSystem: Failed to preload '{job.RequestPath}': {ex.Message}");
            }
        }
    }

    private void EnqueueAudioUpload(int priority, Action upload)
    {
        lock (_preloadGate)
        {
            if (!_preloadStopped)
                _audioUploadQueue.Enqueue(upload, priority);
        }
    }

    private static Wav? LoadSound(string path)
    {
        Wav wav = new();
        if (wav.Load(ResolvePath(path)) != SoloudResult.Ok)
        {
            wav.Dispose();
            Logger.Error($"AudioSystem: Failed on Load '{path}'");
            return null;
        }
        return wav;
    }

    private static WavStream? LoadMusicStream(string path)
    {
        WavStream stream = new();
        if (stream.Load(ResolvePath(path)) != SoloudResult.Ok)
        {
            stream.Dispose();
            Logger.Error($"AudioSystem: Failed to load music '{path}'");
            return null;
        }
        return stream;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path) || path.StartsWith(Fuse.ResPath.Path, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(Fuse.ResPath.Path, path));
    }

    private static string AudioKey(string path) => ResolvePath(path).Replace('\\', '/');
}
