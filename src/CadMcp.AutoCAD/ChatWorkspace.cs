using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using UserControl=System.Windows.Controls.UserControl;
using TabControl=System.Windows.Controls.TabControl;
using Button=System.Windows.Controls.Button;
using CadMcp.Core;
using CadMcp.Providers;
namespace CadMcp.AutoCAD;

public sealed class ChatWorkspace:UserControl
{
    private readonly TabControl tabs=new();private readonly TextBlock status=new();private readonly Dictionary<string,(TabItem Tab,ChatPanel Panel)> projects=new();
    private readonly string host;
    private readonly string brokerPipe;
    private readonly string? projectStoreRoot;
    public string? CadSessionId{get;set;}
    public Func<bool>? DarkThemeProvider{get;set;}
    private bool refreshing,darkTheme=true;
    private readonly System.Windows.Threading.DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(5)};
    public ChatWorkspace(string? hostExecutable=null,string? pipeName=null,string? historyRoot=null)
    {
        host=hostExecutable??Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(ChatWorkspace).Assembly.Location)!,"..","Host","CadMcp.Host.exe"));
        brokerPipe=pipeName??Wire.BrokerPipe;projectStoreRoot=historyRoot;
        var root=new DockPanel();var header=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal};var refresh=new Button{Content="Открытые DWG",Margin=new Thickness(4)};
        refresh.Click+=async(_,_)=>await Refresh();header.Children.Add(refresh);header.Children.Add(status);DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);root.Children.Add(tabs);Content=root;
        Loaded+=async(_,_)=>{timer.Start();await Refresh();};Unloaded+=(_,_)=>timer.Stop();timer.Tick+=async(_,_)=>await Refresh();
    }
    public async Task Refresh()
    {
        if(refreshing)return;refreshing=true;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));await BrokerBootstrap.EnsureAsync(host,timeout.Token,brokerPipe);
            if(CadSessionId is null){var workers=new Broker(Wire.WorkerRoot).Discover();if(workers.Count!=1)throw new IOException("Откройте чат из нужного AutoCAD");CadSessionId=workers[0].SessionId;}
            var reply=await PipeClient.CallAsync(brokerPipe,new(Guid.NewGuid().ToString("N"),"cad_documents",CadSessionId),timeout.Token);
            if(reply.Error is {} error)throw new IOException(error.Message);
            var data=Wire.Element(reply.Data!);var present=new HashSet<string>();TabItem? active=null;
            foreach(var drawing in data.EnumerateArray())
            {
                string id=drawing.Text("document_id")!,name=drawing.Text("name")!,project=drawing.Text("project_key")!;present.Add(id);darkTheme=drawing.GetProperty("dark_theme").GetBoolean();
                if(!projects.TryGetValue(id,out var entry))
                {
                    string root=projectStoreRoot is null?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CadMcp","chat","projects",project):Path.Combine(projectStoreRoot,project);
                    var panel=new ChatPanel(new ChatStateStore(root),Path.IsPathFullyQualified(name)?Path.GetDirectoryName(name):null){CadSessionId=CadSessionId,CadDocumentId=id,DarkThemeProvider=()=>DarkThemeProvider?.Invoke()??darkTheme};
                    var tab=new TabItem{Content=panel};entry=(tab,panel);projects.Add(id,entry);tabs.Items.Add(tab);
                }
                bool dark=DarkThemeProvider?.Invoke()??darkTheme;var background=dark?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36,40,46)):System.Windows.Media.Brushes.White;var foreground=dark?System.Windows.Media.Brushes.Gainsboro:System.Windows.Media.Brushes.Black;
                Background=tabs.Background=background;Foreground=tabs.Foreground=foreground;entry.Tab.Background=background;entry.Tab.Foreground=foreground;
                entry.Tab.Header=Path.GetFileName(name)+(entry.Panel.IsWorking?" · выполняется":"");entry.Tab.ToolTip=name;entry.Tab.IsEnabled=true;
                if(drawing.GetProperty("active").GetBoolean())active=entry.Tab;
            }
            foreach(var entry in projects.Where(p=>!present.Contains(p.Key))){if(entry.Value.Tab.IsEnabled){entry.Value.Panel.Stop();entry.Value.Tab.IsEnabled=false;entry.Value.Tab.Header+=" (закрыт)";}}
            if(tabs.SelectedItem is null&&active is not null)tabs.SelectedItem=active;
            status.Text="Каждая вкладка работает со своим DWG";
        }
        catch(System.Exception e){status.Text=e.Message;}
        finally{refreshing=false;}
    }
    public void Stop(){timer.Stop();foreach(var entry in projects.Values)entry.Panel.Stop();}
    internal IReadOnlyDictionary<string,ChatPanel> ProjectPanels=>projects.ToDictionary(p=>p.Key,p=>p.Value.Panel);
    internal void SelectProject(string document){if(projects.TryGetValue(document,out var entry))tabs.SelectedItem=entry.Tab;}
}
