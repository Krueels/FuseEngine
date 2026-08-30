using System.Diagnostics;

namespace Fuse.Core;

public enum ProfilerSection
{
    MainRender,
    DirectionalShadows,
    SpotShadows,
    PointShadows,
    Pbr,
    PostProcess,
    Physics,
    SpiderAi,
    AudioLoading,
    Count
}

public readonly record struct ProfilerSnapshot(
    double FrameMilliseconds,
    double MainRenderMilliseconds,
    double DirectionalShadowMilliseconds,
    double SpotShadowMilliseconds,
    double PointShadowMilliseconds,
    double PbrMilliseconds,
    double PostProcessMilliseconds,
    double PhysicsMilliseconds,
    double SpiderAiMilliseconds,
    double AudioLoadingMilliseconds,
    int ObjectsDrawn,
    int LightsInScene,
    int PointLights,
    int SpotLights,
    int LightsEvaluated)
{
    public double RenderMilliseconds => MainRenderMilliseconds;
}

/// <summary>
/// CPU frame profiler shared by the game loop, renderer and runtime systems.
/// The snapshot is intentionally copied at the end of a frame so the ImGui
/// panel can read it without keeping references to mutable frame state.
/// </summary>
public sealed class EngineProfiler
{
    private const int HistoryLength = 120;
    private readonly double[] _sectionMilliseconds = new double[(int)ProfilerSection.Count];
    private readonly double[] _pendingMilliseconds = new double[(int)ProfilerSection.Count];
    private readonly double[] _frameHistory = new double[HistoryLength];

    private bool _frameActive;
    private long _frameStart;
    private int _objectsDrawn;
    private int _lightsInScene;
    private int _pointLights;
    private int _spotLights;
    private int _lightsEvaluated;
    private int _historyCount;
    private int _historyIndex;

    public bool Enabled { get; set; } = true;
    public ProfilerSnapshot LastFrame { get; private set; }

    public double AverageFrameMilliseconds
    {
        get
        {
            if (_historyCount == 0)
                return 0.0;

            double total = 0.0;
            for (int i = 0; i < _historyCount; i++)
                total += _frameHistory[i];
            return total / _historyCount;
        }
    }

    public void BeginFrame()
    {
        if (!Enabled)
        {
            _frameActive = false;
            return;
        }

        for (int i = 0; i < _sectionMilliseconds.Length; i++)
        {
            _sectionMilliseconds[i] = _pendingMilliseconds[i];
            _pendingMilliseconds[i] = 0.0;
        }

        _objectsDrawn = 0;
        _lightsInScene = 0;
        _pointLights = 0;
        _spotLights = 0;
        _lightsEvaluated = 0;
        _frameStart = Stopwatch.GetTimestamp();
        _frameActive = true;
    }

    public void EndFrame()
    {
        if (!Enabled || !_frameActive)
            return;

        double frameMilliseconds = Stopwatch.GetElapsedTime(_frameStart).TotalMilliseconds;
        LastFrame = new ProfilerSnapshot(
            frameMilliseconds,
            GetMilliseconds(ProfilerSection.MainRender),
            GetMilliseconds(ProfilerSection.DirectionalShadows),
            GetMilliseconds(ProfilerSection.SpotShadows),
            GetMilliseconds(ProfilerSection.PointShadows),
            GetMilliseconds(ProfilerSection.Pbr),
            GetMilliseconds(ProfilerSection.PostProcess),
            GetMilliseconds(ProfilerSection.Physics),
            GetMilliseconds(ProfilerSection.SpiderAi),
            GetMilliseconds(ProfilerSection.AudioLoading),
            _objectsDrawn,
            _lightsInScene,
            _pointLights,
            _spotLights,
            _lightsEvaluated);

        _frameHistory[_historyIndex] = frameMilliseconds;
        _historyIndex = (_historyIndex + 1) % HistoryLength;
        _historyCount = System.Math.Min(_historyCount + 1, HistoryLength);
        _frameActive = false;
    }

    public Scope Measure(ProfilerSection section)
    {
        return Enabled && section < ProfilerSection.Count
            ? new Scope(this, section, Stopwatch.GetTimestamp())
            : default;
    }

    public void SetRenderCounts(int objectsDrawn)
    {
        if (_frameActive)
            _objectsDrawn = System.Math.Max(objectsDrawn, 0);
    }

    public void SetLightingCounts(int lightsInScene, int pointLights, int spotLights)
    {
        if (!_frameActive)
            return;

        _lightsInScene = System.Math.Max(lightsInScene, 0);
        _pointLights = System.Math.Max(pointLights, 0);
        _spotLights = System.Math.Max(spotLights, 0);
        // This is the number of local lights submitted to the PBR stage.
        // Exact per-fragment GPU evaluations would require a readback and
        // would distort the frame being measured.
        _lightsEvaluated = _pointLights + _spotLights;
    }

    public void Reset()
    {
        Array.Clear(_frameHistory);
        Array.Clear(_sectionMilliseconds);
        Array.Clear(_pendingMilliseconds);
        _historyCount = 0;
        _historyIndex = 0;
        LastFrame = default;
    }

    private double GetMilliseconds(ProfilerSection section) =>
        _sectionMilliseconds[(int)section];

    private void EndScope(ProfilerSection section, long start)
    {
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        double[] target = _frameActive ? _sectionMilliseconds : _pendingMilliseconds;
        target[(int)section] += milliseconds;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly EngineProfiler? _profiler;
        private readonly ProfilerSection _section;
        private readonly long _start;

        internal Scope(EngineProfiler profiler, ProfilerSection section, long start)
        {
            _profiler = profiler;
            _section = section;
            _start = start;
        }

        public void Dispose()
        {
            _profiler?.EndScope(_section, _start);
        }
    }
}
