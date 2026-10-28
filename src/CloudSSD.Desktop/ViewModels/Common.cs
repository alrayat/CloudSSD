using System.Collections.ObjectModel;
using System.Windows;
using CloudSSD.App;
using CloudSSD.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudSSD.Desktop.ViewModels;

public static class Fmt
{
    public const string NA = "Unavailable";
    public static string Bytes(long? b) => b is { } v ? ByteSize.Format(v) : NA;
    public static string Rate(double? bps) => bps is { } v ? $"{Bandwidth.BytesPerSecondToMBps(v):0.00} MB/s · {Bandwidth.BytesPerSecondToMbps(v):0.0} Mbps" : NA;
    public static string RateShort(double v) => $"{Bandwidth.BytesPerSecondToMbps(v):0} Mbps";
    public static string Pct(double? p) => p is { } v ? $"{v:P1}" : NA;
    public static string Ms(double? ms) => ms is { } v ? (v >= 1000 ? $"{v / 1000:0.00} s" : $"{v:0.0} ms") : NA;
    public static string Count(long? n) => n is { } v ? v.ToString("N0") : NA;

    public static string Tone(MountState s) => s switch
    {
        MountState.Mounted => "ok",
        MountState.Starting or MountState.Stopping => "accent",
        MountState.Degraded => "warn",
        MountState.Failed => "error",
        _ => "muted"
    };

    public static string Tone(ReadinessStatus s) => s switch
    {
        ReadinessStatus.ReadyToLaunch => "ok",
        ReadinessStatus.PreparingStartupData or ReadinessStatus.MayRequireAdditionalDownloads => "warn",
        ReadinessStatus.Unknown => "muted",
        _ => "error"
    };

    public static string Tone(CompatibilityLevel l) => l switch
    {
        CompatibilityLevel.Verified => "ok",
        CompatibilityLevel.NoBlockersFound => "accent",
        CompatibilityLevel.Uncertain => "warn",
        CompatibilityLevel.NotAnalyzed => "muted",
        _ => "error"
    };

    public static string Tone(Severity s) => s switch { Severity.Error => "error", Severity.Warning => "warn", _ => "muted" };
}

/// <summary>Base class for navigation pages.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(AppHost host, string title, string icon)
    {
        Host = host;
        Title = title;
        Icon = icon;
    }

    public AppHost Host { get; }
    public string Title { get; }
    public string Icon { get; }
    public string Description => Title switch
    {
        "Dashboard" => "Your storage, connections and activity at a glance.",
        "Cloud Drives" => "Connect your accounts and bring cloud folders into File Explorer.",
        "Game Library" => "Prepare, launch and manage games stored across your cloud and SSD.",
        "Game Accelerator" => "Follow real reads and the prediction engine as it learns your games.",
        "Smart Cache" => "Give your data room to work while protecting space on your PC.",
        "Performance Monitor" => "Measured throughput, cache coverage and read latency over time.",
        "Settings" => "Make this workspace fit your connection, storage and workflow.",
        "Diagnostics" => "Inspect dependencies, recover from problems and export a sanitized report.",
        _ => "Your cloud storage, connected to this PC."
    };

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _messageTone = "muted";

    public virtual void OnShown() { }
    public virtual void OnMetrics(EngineMetrics m) { }
    public virtual void OnStateChanged() { }

    protected void Say(string text, string tone = "muted")
    {
        Message = text;
        MessageTone = tone;
    }

    protected async Task RunAsync(Func<Task> work, string? success = null)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            await work();
            if (success != null) Say(success, "ok");
        }
        catch (OperationCanceledException) { Say("Cancelled.", "muted"); }
        catch (Exception e) { Say(Diagnostics.Redactor.Redact(e.Message), "error"); }
        finally { Busy = false; }
    }

    protected static void Ui(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.InvokeAsync(a);
    }

    protected static bool Confirm(string text, string title = "CloudSSD") =>
        MessageBox.Show(Application.Current.MainWindow!, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}

public sealed record KeyValueRow(string Key, string Value, string Tone = "muted");

public static class CollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> c, IEnumerable<T> items)
    {
        c.Clear();
        foreach (var i in items) c.Add(i);
    }
}
