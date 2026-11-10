using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Control = System.Windows.Controls.Control;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Brush = System.Windows.Media.Brush;

namespace CadMcp.AutoCAD;

internal static class ChatTheme
{
    private const string Styles = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <Style TargetType="Button">
  <Setter Property="Background" Value="{DynamicResource Cad.Control}"/><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="BorderBrush" Value="{DynamicResource Cad.Border}"/>
  <Setter Property="Padding" Value="8,4"/><Setter Property="MinHeight" Value="25"/><Setter Property="Margin" Value="2"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="2" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource Cad.Hover}"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource Cad.Selected}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="TextBox"><Setter Property="Background" Value="{DynamicResource Cad.Control}"/><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="CaretBrush" Value="{DynamicResource Cad.Foreground}"/><Setter Property="BorderBrush" Value="{DynamicResource Cad.Border}"/><Setter Property="SelectionBrush" Value="{DynamicResource Cad.Selected}"/><Setter Property="Padding" Value="5,4"/></Style>
 <Style TargetType="ComboBoxItem"><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="Padding" Value="7,5"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="Row" Padding="{TemplateBinding Padding}" Background="Transparent"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource Cad.Hover}"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource Cad.Selected}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType="ComboBox">
  <Setter Property="Background" Value="{DynamicResource Cad.Control}"/><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="BorderBrush" Value="{DynamicResource Cad.Border}"/><Setter Property="MinHeight" Value="27"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
   <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press" IsChecked="{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="Box" Background="{DynamicResource Cad.Control}" BorderBrush="{DynamicResource Cad.Border}" BorderThickness="1" CornerRadius="2"><TextBlock Text="⌄" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,8,0" Foreground="{DynamicResource Cad.Foreground}"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="Background" Value="{DynamicResource Cad.Hover}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton>
   <ContentPresenter Margin="8,3,28,3" VerticalAlignment="Center" IsHitTestVisible="False" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"/>
   <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False" IsOpen="{TemplateBinding IsDropDownOpen}"><Border Background="{DynamicResource Cad.Control}" BorderBrush="{DynamicResource Cad.Border}" BorderThickness="1" MinWidth="{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}"><ScrollViewer MaxHeight="320" CanContentScroll="True"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border></Popup>
  </Grid><ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="TabItem"><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="Padding" Value="8,5"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Border x:Name="Tab" BorderThickness="1,1,1,2" BorderBrush="{DynamicResource Cad.Border}" Background="{DynamicResource Cad.Control}" Padding="{TemplateBinding Padding}"><ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="Tab" Property="Background" Value="{DynamicResource Cad.Panel}"/><Setter TargetName="Tab" Property="BorderBrush" Value="{DynamicResource Cad.Accent}"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Tab" Property="Background" Value="{DynamicResource Cad.Hover}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType="TabControl"><Setter Property="Background" Value="{DynamicResource Cad.Panel}"/><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabControl"><DockPanel><TabPanel DockPanel.Dock="Top" IsItemsHost="True" KeyboardNavigation.TabIndex="1"/><Border Background="{TemplateBinding Background}"><ContentPresenter ContentSource="SelectedContent"/></Border></DockPanel></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType="ScrollBar"><Setter Property="Background" Value="{DynamicResource Cad.Panel}"/><Setter Property="Width" Value="14"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar"><Grid Background="{TemplateBinding Background}"><Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True"><Track.DecreaseRepeatButton><RepeatButton x:Name="Decrease" Command="ScrollBar.PageUpCommand"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="{DynamicResource Cad.Border}" CornerRadius="4" Margin="3" MinHeight="12" MinWidth="6"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton x:Name="Increase" Command="ScrollBar.PageDownCommand"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton></Track></Grid><ControlTemplate.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/><Setter TargetName="Decrease" Property="Command" Value="ScrollBar.PageLeftCommand"/><Setter TargetName="Increase" Property="Command" Value="ScrollBar.PageRightCommand"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter><Style.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="14"/></Trigger></Style.Triggers></Style>
 <Style TargetType="Expander"><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="Background" Value="{DynamicResource Cad.Control}"/><Setter Property="BorderBrush" Value="{DynamicResource Cad.Border}"/></Style>
 <Style TargetType="CheckBox"><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><StackPanel Orientation="Horizontal"><Border Width="16" Height="16" Background="{DynamicResource Cad.Control}" BorderBrush="{DynamicResource Cad.Border}" BorderThickness="1" Margin="0,0,6,0"><TextBlock x:Name="Tick" Text="✓" Visibility="Collapsed" Foreground="{DynamicResource Cad.Foreground}" HorizontalAlignment="Center"/></Border><ContentPresenter VerticalAlignment="Center"/></StackPanel><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Tick" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType="ToolTip"><Setter Property="Background" Value="{DynamicResource Cad.Control}"/><Setter Property="Foreground" Value="{DynamicResource Cad.Foreground}"/><Setter Property="BorderBrush" Value="{DynamicResource Cad.Border}"/></Style>
</ResourceDictionary>
""";
    internal static void Apply(Control control, bool dark)
    {
        if (!control.Resources.Contains("Cad.ThemeInstalled"))
        {
            control.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Styles));
            control.Resources["Cad.ThemeInstalled"] = true;
            control.FontFamily = new FontFamily("Segoe UI"); control.FontSize = 12;
        }
        void Brush(string key, string value) => control.Resources[key] = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        Brush("Cad.Panel", dark ? "#394351" : "#f5f6f8"); Brush("Cad.Control", dark ? "#303946" : "#ffffff");
        Brush("Cad.Foreground", dark ? "#f0f2f5" : "#20232a"); Brush("Cad.Border", dark ? "#6a7b90" : "#bec5cf");
        Brush("Cad.Hover", dark ? "#4b5a6e" : "#e4edf7"); Brush("Cad.Selected", dark ? "#506b88" : "#cce3fa"); Brush("Cad.Accent", dark ? "#9cc3ed" : "#0867b4");
        control.Background = (Brush)control.Resources["Cad.Panel"]; control.Foreground = (Brush)control.Resources["Cad.Foreground"];
        if (control is not ChatWindow && Window.GetWindow(control) is ChatWindow chatWindow) chatWindow.ApplyHeaderTheme(dark);
        if (control is not ChatWindow && Window.GetWindow(control) is not ChatWindow && PresentationSource.FromVisual(control) is HwndSource source)
        {
            IntPtr root = GetAncestor(source.Handle, 2);
            int value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(root, 20, ref value, sizeof(int));
            int caption = dark ? 0x00514339 : 0x00f8f6f5;
            int text = dark ? 0x00f5f2f0 : 0x002a2320;
            _ = DwmSetWindowAttribute(root, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(root, 36, ref text, sizeof(int));
        }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
