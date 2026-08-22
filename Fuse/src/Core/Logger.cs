using System.Windows.Forms;

namespace Fuse.Core;

public enum LogLevel { Info, Important, Asset, Warn, Error }

public readonly record struct LogEntry(LogLevel Level, string Message, DateTime Timestamp);

public static class Logger
{
    private static readonly List<LogEntry> _log = [];

    public static LogEntry[] GetRecentLogs(int count)
    {
        if (_log.Count == 0) return [];
        int start = System.Math.Max(0, _log.Count - count);
        return _log.GetRange(start, _log.Count - start).ToArray();
    }

    public static void ClearLog() => _log.Clear();

    public static void Info(string message)
    {
        _log.Add(new LogEntry(LogLevel.Info, message, DateTime.Now));
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"[ INFO ] {message}");
        Console.ResetColor();
    }
    public static void Important(string message)
    {
        _log.Add(new LogEntry(LogLevel.Important, message, DateTime.Now));
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"[ IMPORTANT ] {message}");
        Console.ResetColor();
    }
    public static void Asset(string message)
    {
        _log.Add(new LogEntry(LogLevel.Asset, message, DateTime.Now));
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[ ASSET ] {message}");
        Console.ResetColor();
    }

    public static void Warn(string message)
    {
        _log.Add(new LogEntry(LogLevel.Warn, message, DateTime.Now));
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[ WARN ] {message}");
        Console.ResetColor();
    }
    public static void InfoGold(string message)
    {
        _log.Add(new LogEntry(LogLevel.Warn, message, DateTime.Now));
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[ INFO GOLD ] {message}");
        Console.ResetColor();
    }

    public static void WarnPopup(string message, string popupMessage)
    {
        _log.Add(new LogEntry(LogLevel.Warn, message, DateTime.Now));
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[ WARN ] {message}");
        Console.ResetColor();

        MessageBox.Show(popupMessage, "Fuse Engine - WARNING", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public static void Error(string message)
    {
        _log.Add(new LogEntry(LogLevel.Error, message, DateTime.Now));
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[ ERROR ] {message}");
        Console.ResetColor();
    }

    public static void FatalError(string message, string popupMessage)
    {
        _log.Add(new LogEntry(LogLevel.Error, message, DateTime.Now));
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[ FATAL ERROR ] {message}");
        Console.ResetColor();

        MessageBox.Show(popupMessage, "Fuse Engine - FATAL ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
