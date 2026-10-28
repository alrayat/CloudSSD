using System.Windows;
using System.Windows.Input;

namespace CloudSSD.Desktop.Controls;

/// <summary>Native WPF commands shared by the shell and owned dialogs.</summary>
public static class WindowFrame
{
    private static readonly DependencyProperty BindingsProperty = DependencyProperty.RegisterAttached(
        "Bindings", typeof(CommandBinding[]), typeof(WindowFrame));
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WindowFrame), new PropertyMetadata(false, OnEnabledChanged));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Window window) return;
        if (window.GetValue(BindingsProperty) is CommandBinding[] previous)
            foreach (var binding in previous) window.CommandBindings.Remove(binding);
        window.ClearValue(BindingsProperty);
        if (e.NewValue is not true) return;
        CommandBinding[] bindings = [
        new(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(window)),
        new(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(window),
            (_, args) => { args.CanExecute = window.ResizeMode != ResizeMode.NoResize; args.Handled = true; }),
        new(SystemCommands.MaximizeWindowCommand, (_, _) =>
        {
            if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
            else SystemCommands.MaximizeWindow(window);
        }, CanResize),
        new(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(window),
            (_, args) => { args.CanExecute = window.WindowState != WindowState.Normal; args.Handled = true; }),
        new(SystemCommands.ShowSystemMenuCommand,
            (_, _) => SystemCommands.ShowSystemMenu(window, window.PointToScreen(new Point(16, 46))))
        ];
        foreach (var binding in bindings) window.CommandBindings.Add(binding);
        window.SetValue(BindingsProperty, bindings);

        void CanResize(object sender, CanExecuteRoutedEventArgs args)
        {
            args.CanExecute = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
            args.Handled = true;
        }
    }
}
