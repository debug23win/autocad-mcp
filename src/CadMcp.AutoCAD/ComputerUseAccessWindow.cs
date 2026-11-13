using System.Windows;
using System.Windows.Controls;
using CadMcp.Core;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace CadMcp.AutoCAD;

internal sealed class ComputerUseAccessWindow : Window
{
    internal bool? Choice { get; private set; }
    internal ComputerUseAccessWindow(ComputerUseTarget target, bool allowed, bool running, bool dark)
    {
        Title = UiText.T("Computer Use — доступ к AutoCAD"); Width = 620; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(18) }; Content = root;
        void Text(string text) => root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        Text(target.DisplayName);
        Text(UiText.T("Приложение: ") + target.ExecutablePath);
        Text(UiText.T("Computer Use сможет видеть окно AutoCAD, нажимать кнопки и вводить текст. Это отдельное разрешение от C# и CAD MCP."));
        Text(UiText.T("Действия через интерфейс не имеют транзакционной квитанции CAD MCP и не откатываются автоматически."));
        Text(UiText.T("Область разрешения: подпись издателя, продукт и имя acad.exe. Она может охватывать другие установленные версии с той же идентичностью."));
        var details = new TextBlock { Text = target.PublisherName + "\n" + target.ProductName + "\n" + target.BinaryName, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        root.Children.Add(new Expander { Header = UiText.T("Проверенная подпись и продукт"), Content = details, Margin = new Thickness(0, 0, 0, 10) });
        Text(allowed ? UiText.T("Разрешение пользователя сохранено. Доступность инструмента и ограничения организации проверяет Codex.") : UiText.T("Разрешение не выдано. Оно применяется со следующего хода Codex; глобальный config.toml не изменяется."));
        if (running) Text(UiText.T("Отзыв остановит текущий ход. Ранее принятые CAD-операции проверяются отдельно и автоматически не повторяются."));
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var allow = new Button { Content = UiText.T("Разрешить со следующего хода"), IsEnabled = !allowed, Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) };
        var deny = new Button { Content = running ? UiText.T("Отозвать и остановить ход") : UiText.T("Отозвать доступ"), IsEnabled = allowed, Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) };
        var close = new Button { Content = UiText.T("Закрыть"), IsCancel = true, Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) };
        allow.Click += (_, _) => { Choice = true; DialogResult = true; };
        deny.Click += (_, _) => { Choice = false; DialogResult = true; };
        buttons.Children.Add(allow); buttons.Children.Add(deny); buttons.Children.Add(close); root.Children.Add(buttons);
        ChatTheme.Apply(this, dark);
    }
}
