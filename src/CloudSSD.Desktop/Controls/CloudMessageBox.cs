using System.Windows;
using System.Windows.Controls;

namespace CloudSSD.Desktop.Controls;

/// <summary>Branded confirmations with the same result and default-button semantics as WPF MessageBox.</summary>
public static class CloudMessageBox
{
    public static MessageBoxResult Show(string message, string caption = "CloudSSD",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(Application.Current?.MainWindow, message, caption, buttons, image, defaultResult);

    public static MessageBoxResult Show(Window? owner, string message, string caption = "CloudSSD",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var choices = buttons switch
        {
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            _ => new[] { MessageBoxResult.OK }
        };
        var dismissed = choices.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
            : choices.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        var result = dismissed;
        var dialog = new Window
        {
            Title = caption,
            Width = 590,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        var content = new StackPanel { Margin = new Thickness(30, 26, 30, 26) };
        var tone = image is MessageBoxImage.Error ? "Danger" : image is MessageBoxImage.Warning ? "Warning" : "Accent";
        content.Children.Add(new TextBlock
        {
            Text = image is MessageBoxImage.Warning ? "PLEASE CONFIRM" : image is MessageBoxImage.Error ? "SOMETHING NEEDS ATTENTION" : "CLOUDSSD",
            Style = (Style)Application.Current.FindResource("Label"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource(tone),
            Margin = new Thickness(0, 0, 0, 12)
        });
        content.Children.Add(new TextBlock { Text = caption, Style = (Style)Application.Current.FindResource("H1") });
        content.Children.Add(new TextBlock { Text = message, Style = (Style)Application.Current.FindResource("Body"), Margin = new Thickness(0, 10, 0, 24) });
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var choice in choices)
        {
            var selected = choice;
            var button = new Button
            {
                Content = choice.ToString(), MinWidth = 82,
                IsDefault = defaultResult == choice || defaultResult == MessageBoxResult.None && choices[0] == choice,
                IsCancel = choice == dismissed,
                Style = (Style)Application.Current.FindResource(choice == choices[0] ? "Primary" : "ButtonBase")
            };
            button.Click += (_, _) => { result = selected; dialog.DialogResult = true; };
            footer.Children.Add(button);
        }
        content.Children.Add(footer);
        dialog.Content = content;
        dialog.ShowDialog();
        return result;
    }
}
