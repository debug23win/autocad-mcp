using System.Windows;
using CadMcp.AutoCAD;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new System.Windows.Application();
        var panel = new ChatPanel();
        int session = Array.IndexOf(args, "--session");
        if (session >= 0 && session + 1 < args.Length) panel.CadSessionId = args[session + 1];
        var window = new Window { Title = "CAD MCP — ассистент чертежей", Width = 620, Height = 880, MinWidth = 430, MinHeight = 600, Content = panel };
        window.Closed += (_, _) => panel.Stop();
        app.Run(window);
    }
}
