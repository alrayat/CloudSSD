using System.Windows;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Desktop.ViewModels;
using CloudSSD.Diagnostics;
using CloudSSD.Games;
using CloudSSD.Streaming;

namespace CloudSSD.Desktop.Views;

public partial class PrepareWindow : Window
{
    private sealed record StepRow(string Name, string? Detail, string Icon, string Tone);

    private readonly AppHost _host;
    private readonly GameEntry _game;
    private readonly CancellationTokenSource _cts = new();
    private bool _done;

    public PrepareWindow(AppHost host, GameEntry game)
    {
        _host = host;
        _game = game;
        InitializeComponent();
        TitleText.Text = $"Optimize & Prepare — {game.Name}";
        Loaded += async (_, _) => await RunAsync();
        Closing += (_, e) => { if (!_done) { _cts.Cancel(); } };
    }

    private async Task RunAsync()
    {
        var steps = new Progress<IReadOnlyList<PrepareStep>>(list => Steps.ItemsSource = list.Select(s => new StepRow(s.Name, s.Detail,
            s.State switch
            {
                StepState.Done => "",
                StepState.Running => "",
                StepState.Warning => "",
                StepState.Failed => "",
                StepState.Skipped => "",
                _ => ""
            },
            s.State switch { StepState.Done => "ok", StepState.Running => "accent", StepState.Warning => "warn", StepState.Failed => "error", _ => "muted" })).ToList());
        var prefetch = new Progress<PrepareProgress>(p =>
        {
            PrefetchPanel.Visibility = Visibility.Visible;
            var done = p.DoneBlocks + p.SkippedCached + p.Failed;
            PrefetchBar.Value = p.TotalBlocks == 0 ? 0 : done / (double)p.TotalBlocks;
            PrefetchText.Text = $"Learned startup set: {done:N0} of {p.TotalBlocks:N0} blocks · fetched {ByteSize.Format(p.BytesFetched)} of {ByteSize.Format(p.BytesPlanned)} planned · " +
                                $"{Bandwidth.FormatRate(p.Elapsed.TotalSeconds > 0.5 ? p.BytesFetched / p.Elapsed.TotalSeconds : null)}" +
                                (p.Eta is { } eta ? $" · about {Bandwidth.FormatDuration(eta)} left" : "");
        });
        try
        {
            var report = await _host.PrepareAsync(_game, steps, prefetch, _cts.Token);
            ShowResult(ReadinessText.Describe(report.Status), report.Headline, Fmt.Tone(report.Status));
        }
        catch (OperationCanceledException)
        {
            ShowResult("Cancelled", "Preparation was cancelled. Data already downloaded stays in the cache.", "muted");
        }
        catch (Exception e)
        {
            ShowResult("Preparation failed", Redactor.Redact(e.Message), "error");
        }
        _done = true;
        CancelButton.Content = "Close";
    }

    private void ShowResult(string title, string text, string tone)
    {
        ResultBox.Visibility = Visibility.Visible;
        var brush = (System.Windows.Media.Brush)new Controls.ToneToBrush().Convert(tone, typeof(object), null!, System.Globalization.CultureInfo.CurrentCulture);
        ResultBox.BorderBrush = brush;
        ResultTitle.Foreground = brush;
        ResultTitle.Text = title;
        ResultText.Text = text;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (_done) Close();
        else _cts.Cancel();
    }
}
