using System.Windows;
using CadMcp.AutoCAD;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new System.Windows.Application();
        var panel = new ChatWorkspace();
        int session = Array.IndexOf(args, "--session");
        if (session >= 0 && session + 1 < args.Length) panel.CadSessionId = args[session + 1];
        var window = new ChatWindow(panel) { Width = 620, Height = 880, MinWidth = 430, MinHeight = 600 };
        window.Closed += (_, _) => panel.Stop();
        app.Run(window);
    }
}
