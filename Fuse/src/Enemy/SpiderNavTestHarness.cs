using System;
using System.Diagnostics;
using System.Numerics;
using Fuse.Core;
using Fuse.Debug;
using Fuse.Math;
using Fuse.Scene;

namespace Fuse.Enemy;

/// <summary>
/// Manual/automatic validation harness for the baked spider navigation
/// system. It generates a graph, resolves world positions through the public
/// SpiderPathfinding API, and exposes diagnostics without moving an enemy.
/// </summary>
public sealed class SpiderNavTestHarness : IGizmoDrawable
{
    private static readonly Vector3 StartColor = new(0.05f, 1f, 0.15f);
    private static readonly Vector3 GoalColor = new(1f, 0.10f, 0.05f);
    private static readonly Vector3 ReachableGoalColor = new(0.10f, 0.85f, 1f);
    private static readonly Vector3 FailedQueryColor = new(1f, 0.05f, 0.05f);
    private static readonly Vector3 QueryConnectorColor = new(0.85f, 0.85f, 0.85f);

    private readonly SpiderNavGenerator _generator;
    private readonly SpiderPathfinding _pathfinding;
    private SpiderNavDebugVisualizer? _graphVisualizer;
    private SpiderNavGraph? _graph;
    private SpiderPath _path = SpiderPath.Empty;
    private Vector3 _lastStartPosition;
    private Vector3 _lastGoalPosition;
    private float _lastRequiredClearance;
    private bool _hasLastQuery;
    private bool _graphDirty = true;
    private float _requiredClearance;

    public SpiderNavTestHarness(
        SceneManager scene,
        AABB bounds,
        SpiderNavGenerationSettings generationSettings,
        SpiderPathfinding pathfinding)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(generationSettings);
        ArgumentNullException.ThrowIfNull(pathfinding);

        _generator = new SpiderNavGenerator(scene);
        _pathfinding = pathfinding;
        Bounds = bounds;
        GenerationSettings = generationSettings;
        DebugDrawer.Register(this);
    }

    public AABB Bounds { get; set; }
    public SpiderNavGenerationSettings GenerationSettings { get; }
    public Vector3 StartPosition { get; set; }
    public Vector3 GoalPosition { get; set; }

    public float RequiredClearance
    {
        get => _requiredClearance;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _requiredClearance = value;
        }
    }

    /// <summary>
    /// When enabled, Update generates a graph after RequestGraphRegeneration
    /// or when no graph exists. It is disabled by default because baking is
    /// intentionally an explicit and potentially expensive operation.
    /// </summary>
    public bool AutoRegenerateGraph { get; set; }

    /// <summary>
    /// Recalculates only when StartPosition, GoalPosition or clearance changes.
    /// </summary>
    public bool AutoRecalculatePath { get; set; } = true;

    public bool MeasureTimings { get; set; } = true;
    public bool ShowVisualization { get; set; } = true;
    public bool ShowQueryMarkers { get; set; } = true;

    public SpiderNavGraph? Graph => _graph;
    public SpiderPath Path => _path;
    public SpiderNavGraphDiagnostics? Diagnostics { get; private set; }
    public bool PathFound { get; private set; }
    public bool GraphNeedsRegeneration => _graphDirty;
    public string LastStatus { get; private set; } = "Navigation test has not run.";
    public TimeSpan? LastGraphGenerationTime { get; private set; }
    public TimeSpan? LastPathfindingTime { get; private set; }
    public int PathWaypointCount => _path.Count;

    /// <summary>
    /// Generates a new graph. Path recalculation is optional so graph and path
    /// tests can be profiled independently.
    /// </summary>
    public bool RegenerateGraph(bool recalculatePath = true)
    {
        _graphDirty = false;
        _graph = null;
        _path = SpiderPath.Empty;
        PathFound = false;
        Diagnostics = null;
        _hasLastQuery = false;
        _graphVisualizer = null;

        Stopwatch? timer = MeasureTimings ? Stopwatch.StartNew() : null;
        try
        {
            _graph = _generator.Generate(Bounds, GenerationSettings);
            LastGraphGenerationTime = timer?.Elapsed;
            Diagnostics = SpiderNavGraphDiagnostics.Analyze(_graph);

            _graphVisualizer = new SpiderNavDebugVisualizer(_graph)
            {
                Enabled = ShowVisualization
            };
            _graphVisualizer.SetPath(null);

            Logger.Info(
                $"[SpiderNavTest] Graph generated: {Diagnostics}; " +
                FormatDuration("generation", LastGraphGenerationTime));

            if (Diagnostics.IsolatedNodeCount > 0)
            {
                Logger.Warn(
                    $"[SpiderNavTest] Isolated nodes detected: " +
                    $"{Diagnostics.IsolatedNodeCount} ({FormatSample(Diagnostics.IsolatedNodeIds)}).");
            }

            if (Diagnostics.HasDisconnectedComponents)
            {
                Logger.Warn(
                    $"[SpiderNavTest] Disconnected components detected: " +
                    $"{Diagnostics.ConnectedComponentCount} " +
                    $"[{string.Join(", ", Diagnostics.ComponentSizes)}].");
            }
        }
        catch (Exception exception)
        {
            LastGraphGenerationTime = timer?.Elapsed;
            _graphDirty = true;
            LastStatus = $"Graph generation failed: {exception.Message}";
            Logger.Error($"[SpiderNavTest] {LastStatus}");
            return false;
        }

        LastStatus = $"Graph ready: {Diagnostics}";
        if (recalculatePath)
            RecalculatePath();

        return true;
    }

    /// <summary>
    /// Marks the current volume/settings for a later explicit or automatic
    /// regeneration.
    /// </summary>
    public void RequestGraphRegeneration()
    {
        _graphDirty = true;
    }

    /// <summary>
    /// Recalculates the path over the existing graph without baking again.
    /// </summary>
    public bool RecalculatePath()
    {
        if (_graph == null)
        {
            PathFound = false;
            _path = SpiderPath.Empty;
            LastPathfindingTime = null;
            LastStatus = "Path not calculated: graph is not generated.";
            Logger.Warn($"[SpiderNavTest] {LastStatus}");
            return false;
        }

        Stopwatch? timer = MeasureTimings ? Stopwatch.StartNew() : null;
        try
        {
            PathFound = _pathfinding.TryFindPath(
                _graph,
                StartPosition,
                GoalPosition,
                RequiredClearance,
                out SpiderPath path);
            _path = PathFound ? path : SpiderPath.Empty;
            LastPathfindingTime = timer?.Elapsed;
        }
        catch (Exception exception)
        {
            PathFound = false;
            _path = SpiderPath.Empty;
            LastPathfindingTime = timer?.Elapsed;
            LastStatus = $"Path calculation failed: {exception.Message}";
            Logger.Error($"[SpiderNavTest] {LastStatus}");
            _graphVisualizer?.SetPath(null);
            SaveLastQuery();
            return false;
        }

        _graphVisualizer?.SetPath(PathFound ? _path : null);
        SaveLastQuery();

        if (PathFound)
        {
            LastStatus =
                $"Path found: waypoints={_path.Count}, cost={_path.TotalCost:F2}, " +
                FormatDuration("query", LastPathfindingTime);
            Logger.Info($"[SpiderNavTest] {LastStatus}");
        }
        else
        {
            LastStatus =
                $"No path found: {_graph.Count} nodes available, " +
                FormatDuration("query", LastPathfindingTime);
            Logger.Warn($"[SpiderNavTest] {LastStatus}");
        }

        return PathFound;
    }

    /// <summary>
    /// Convenience method for running one of the floor/wall/ceiling/obstacle
    /// scenarios by changing only the two world positions.
    /// </summary>
    public bool RunPathTest(Vector3 startPosition, Vector3 goalPosition)
    {
        StartPosition = startPosition;
        GoalPosition = goalPosition;

        if (_graph == null || _graphDirty)
        {
            if (!RegenerateGraph(recalculatePath: false))
                return false;
        }

        return RecalculatePath();
    }

    /// <summary>
    /// Call from a test/update loop when automatic recalculation is desired.
    /// This method never moves or modifies the spider.
    /// </summary>
    public void Update(float dt)
    {
        _ = dt;

        if (AutoRegenerateGraph && (_graph == null || _graphDirty))
        {
            RegenerateGraph();
            return;
        }

        if (!AutoRecalculatePath || _graph == null || !HasQueryChanged())
            return;

        RecalculatePath();
    }

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        _graphVisualizer?.Enabled = ShowVisualization;
        _graphVisualizer?.SetPath(ShowVisualization && PathFound ? _path : null);

        if (!ShowVisualization || !ShowQueryMarkers)
            return;

        Vector3 goalColor = PathFound ? ReachableGoalColor : GoalColor;
        drawer.DrawSphere(StartPosition, Quaternion.Identity, 0.16f, StartColor);
        drawer.DrawSphere(GoalPosition, Quaternion.Identity, 0.16f, goalColor);

        if (PathFound && !_path.IsEmpty)
        {
            drawer.PushLine(
                StartPosition,
                _path.Waypoints[0].Position,
                QueryConnectorColor);
            drawer.PushLine(
                _path.Waypoints[^1].Position,
                GoalPosition,
                QueryConnectorColor);
        }
        else
        {
            drawer.PushLine(StartPosition, GoalPosition, FailedQueryColor);
        }
    }

    private bool HasQueryChanged() =>
        !_hasLastQuery ||
        StartPosition != _lastStartPosition ||
        GoalPosition != _lastGoalPosition ||
        RequiredClearance != _lastRequiredClearance;

    private void SaveLastQuery()
    {
        _lastStartPosition = StartPosition;
        _lastGoalPosition = GoalPosition;
        _lastRequiredClearance = RequiredClearance;
        _hasLastQuery = true;
    }

    private static string FormatDuration(string label, TimeSpan? duration) =>
        duration.HasValue ? $"{label}={duration.Value.TotalMilliseconds:F2}ms" : $"{label}=off";

    private static string FormatSample(System.Collections.Generic.IReadOnlyList<int> values)
    {
        const int maximumItems = 16;
        int itemCount = System.Math.Min(values.Count, maximumItems);
        var items = new string[itemCount];
        for (int i = 0; i < itemCount; i++)
            items[i] = values[i].ToString();

        string suffix = values.Count > maximumItems ? ", ..." : string.Empty;
        return string.Join(", ", items) + suffix;
    }
}
