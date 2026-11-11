using CadMcp.Core;
using System.Windows;
using System.Windows.Controls;
using Binding = System.Windows.Data.Binding;
using System.Windows.Markup;
using System.Windows.Shell;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
namespace CadMcp.AutoCAD;

/// <summary>Draw the caption in WPF so Windows 10 and 11 use the actual CAD palette.</summary>
internal sealed class ChatWindow : Window
{
    private readonly Border caption = new() { Height = 32 };
    private readonly Button maximize;
    private const string CaptionStyle = """
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
 <Setter Property="Width" Value="44"/><Setter Property="Height" Value="32"/><Setter Property="Margin" Value="0"/><Setter Property="Padding" Value="0"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="Focusable" Value="False"/><Setter Property="FontSize" Value="15"/>
 <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="Chrome" Background="{TemplateBinding Background}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource Cad.Hover}"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource Cad.Selected}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
</Style>
""";
    internal ChatWindow(UIElement content)
    {
        Title = UiText.T("CAD MCP — ассистент чертежей");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 32, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        ChatTheme.Apply(this, true);
        caption.SetResourceReference(Border.BackgroundProperty, "Cad.Panel");
        var root = new DockPanel(); DockPanel.SetDock(caption, Dock.Top); root.Children.Add(caption);
        root.Children.Add(content); Content = root;
        var bar = new DockPanel(); caption.Child = bar;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Right); bar.Children.Add(buttons);
        var style = (Style)XamlReader.Parse(CaptionStyle);
        Button Action(string label, string tip, System.Action clicked)
        {
            var button = new Button { Content = label, ToolTip = tip, Style = style };
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.Click += (_, _) => clicked(); buttons.Children.Add(button); return button;
        }
        Action("—", UiText.T("Свернуть"), () => SystemCommands.MinimizeWindow(this));
        maximize = Action("□", UiText.T("Развернуть"), () => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); });
        Action("×", UiText.T("Закрыть"), Close);
        var title = new TextBlock { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        title.SetResourceReference(TextBlock.ForegroundProperty, "Cad.Foreground"); bar.Children.Add(title);
        StateChanged += (_, _) => { maximize.Content = WindowState == WindowState.Maximized ? "❐" : "□"; maximize.ToolTip = WindowState == WindowState.Maximized ? UiText.T("Восстановить") : UiText.T("Развернуть"); };
        caption.MouseRightButtonUp += (_, e) => SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
    }
    internal void ApplyHeaderTheme(bool dark) => ChatTheme.Apply(this, dark);
}
