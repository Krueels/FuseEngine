using System;

namespace Blowtorch;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string splashPath = Path.Combine(Fuse.ResPath.Path, "splash.txt");
        if (File.Exists(splashPath))
        {
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.WriteLine(File.ReadAllText(splashPath));
            Console.WriteLine(" - GOD BLESS YOU - ");
            Console.ResetColor();
        }

        using var app = new EditorApplication();
        if (!app.Init()) return;
        app.Run();
    }
}
