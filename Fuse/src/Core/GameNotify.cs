using System.Drawing.Text;
using System.Numerics;

namespace Fuse.Core;

public enum NotifyLevel { Info, Warn, Error }

public readonly record struct NotifyEntry(NotifyLevel Level, string Message, float CreatedAt);

public static class GameNotify
{
    private static readonly List<NotifyEntry> _entries = [];
    public static float _elapsed;

    public const float Duration = 3.0f;
    public const float FadeStart = 2.0f;

    public static void Info(string message) => Add(NotifyLevel.Info, message);
    public static void Warn(string message) => Add(NotifyLevel.Warn, message);
    public static void Error(string message) => Add(NotifyLevel.Error, message);

    public static void Update(float dt)
    {
        _elapsed += dt;
        _entries.RemoveAll(e => _elapsed - e.CreatedAt > Duration);
    }

    public static ReadOnlySpan<NotifyEntry> GetActive()
    {
        if (_entries.Count == 0) return ReadOnlySpan<NotifyEntry>.Empty;
        return _entries.ToArray().AsSpan();
    }

    public static Vector4 GetColor(NotifyLevel level) => level switch
    {
        NotifyLevel.Info => new(1, 1, 1, 1),
        NotifyLevel.Warn => new(1, 0.85f, 0, 1),
        NotifyLevel.Error => new(1, 0.3f, 0.3f, 1),
        _ => new(1, 1, 1, 1),
    };

    private static void Add(NotifyLevel level, string message)
    {
        _entries.Add(new NotifyEntry(level, message, _elapsed));
    }
}