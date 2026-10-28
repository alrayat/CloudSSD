using System.Collections.ObjectModel;
using System.Diagnostics;
using CloudSSD.App;
using CloudSSD.App.Persistence;
using CloudSSD.Cache;
using CloudSSD.Core;
using CloudSSD.Games;
using CloudSSD.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudSSD.Desktop.ViewModels;

public sealed partial class DriveItem : ObservableObject
{
    public required MountDefinition Definition { get; init; }
    [ObservableProperty] private string _letter = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _tone = "muted";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isMounted;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _canMount;
    [ObservableProperty] private bool _canRepair;
    [ObservableProperty] private bool _hasPausedWrites;
    [ObservableProperty] private bool _isPermanent;
    public string Name => Definition.DisplayName;
    public string Remote => Definition.RemoteSpec;
}

public sealed record ProviderRow(string Name, string Support, string Tone, string Notes);

public sealed partial class CloudDrivesViewModel : PageViewModel
{
    public CloudDrivesViewModel(AppHost host) : base(host, "Cloud Drives", "") { }

    public ObservableCollection<KeyValueRow> Accounts { get; } = new();
    public ObservableCollection<DriveItem> Drives { get; } = new();
    public ObservableCollection<ProviderRow> ProviderRows { get; } = new(ProviderRegistry.All.Select(p => new ProviderRow(
        p.DisplayName,
        p.Support switch
        {
            SupportLevel.Supported => "Supported", SupportLevel.AvailableUntested => "Via rclone · untested",
            SupportLevel.NeedsProviderApproval => "Website sign-in (unofficial) · official API needs developer keys", _ => "Not supported"
        },
        p.Support switch { SupportLevel.Supported => "ok", SupportLevel.AvailableUntested or SupportLevel.NeedsProviderApproval => "warn", _ => "error" },
        p.Capabilities.Notes ?? p.SetupSummary)));

    [ObservableProperty] private KeyValueRow? _selectedAccount;
    [ObservableProperty] private DriveItem? _selectedDrive;
    [ObservableProperty] private string _teraBoxStatus = "";
    [ObservableProperty] private string _permanentStatus = "";

    private readonly StartupRegistration _startup = new();
    /// <summary>Drives whose unmount waits until they are idle ("Unmount pending"), with the latest reason.</summary>
    private readonly Dictionary<string, (CancellationTokenSource Cts, string Message)> _pending = new();

    public override void OnShown()
    {
        OnStateChanged();
        _ = RefreshTeraBoxStatusAsync();
    }

    private async Task RefreshTeraBoxStatusAsync()
    {
        if (!Host.TeraBox.HasLogin) { TeraBoxStatus = ""; return; }
        var s = await Host.TeraBox.StatusAsync();
        TeraBoxStatus = s is null
            ? $"TeraBox: signed in ({Host.Settings.TeraBox.Plan ?? "plan unknown"}); the drive bridge is not running."
            : $"TeraBox {s.Plan ?? Host.Settings.TeraBox.Plan}: " +
              (s.QuotaTotal is { } total ? $"{ByteSize.Format(s.QuotaUsed ?? 0)} of {ByteSize.Format(total)} used" : "capacity unavailable") +
              $" · {s.Uploads} uploads, {ByteSize.Format(s.ReadBytes)} read this session" +
              (s.LocalSourceBytes > 0 ? $" · {ByteSize.Format(s.LocalSourceBytes)} supplied by verified local files" : "") +
              (s.ProviderThrottled ? $" · CLOUD TOO SLOW: uncached games cannot start reliably. Retry in {s.RetryAfterSeconds}s or prepare local data." : "") +
              (s.RangeSupported == false ? " · TeraBox ignores byte ranges: random reads are slow" : "") +
              (s.Errors > 0 ? $" · {s.Errors} errors (last: {s.LastError})" : "");
    }

    [RelayCommand]
    private async Task ConnectTeraBox()
    {
        var w = new Views.ConnectTeraBoxWindow(Host) { Owner = System.Windows.Application.Current.MainWindow };
        if (w.ShowDialog() != true) return;
        await Host.RefreshRemotesAsync();
        var def = Host.Settings.Mounts.FirstOrDefault(Host.IsTeraBoxMount);
        if (def is null)
        {
            def = new MountDefinition { DisplayName = "TeraBox Cloud SSD", RemoteName = Host.TeraBox.DriveRemoteName, RemotePath = "", DriveLetter = "T", AutoMount = true };
            Host.Settings.Mounts.Add(def);
        }
        Host.SaveSettings();
        OnStateChanged();
        Say($"TeraBox connected ({w.User?.Plan} account). Mounting…", "accent");
        await RunAsync(async () =>
        {
            var s = await Host.MountAsync(def);
            def.DriveLetter = s.DriveLetter.ToString();
            Host.SaveSettings();
            Say($"TeraBox is ready as drive {s.DriveLetter}: in File Explorer." +
                (w.User?.VipType == 0 ? " Free TeraBox accounts accept files up to 4 GB each; bigger files are not uploaded." : ""), "ok");
        });
        OnStateChanged();
        await RefreshTeraBoxStatusAsync();
    }

    public override void OnStateChanged()
    {
        Accounts.ReplaceWith(Host.Remotes.Where(r => r.Name != TeraBoxService.ChunkedRemoteName).Select(r => new KeyValueRow(r.Name,
            r.Name == TeraBoxService.RemoteName
                ? Host.TeraBox.UsesWebSession ? "TeraBox (website session, unofficial)" : "TeraBox (official API)"
                : ProviderRegistry.ForBackend(r.Type)?.DisplayName ?? r.Type)));
        var sessions = Host.Mounts.Sessions;
        var selected = SelectedDrive?.Definition.Id;
        Drives.ReplaceWith(Host.Settings.Mounts.Select(d =>
        {
            var s = sessions.FirstOrDefault(x => x.Id == d.Id);
            var state = s?.State ?? MountState.Stopped;
            var pending = _pending.TryGetValue(d.Id, out var p) ? p.Message : null;
            return new DriveItem
            {
                Definition = d,
                Letter = s != null ? $"{s.DriveLetter}:" : (d.DriveLetter is { } l ? $"{l}:" : "auto"),
                State = pending != null ? "Unmount pending" : state.ToString(),
                Tone = pending != null ? "warn" : Fmt.Tone(state),
                IsMounted = state is MountState.Mounted or MountState.Degraded,
                IsActive = state is MountState.Mounted or MountState.Degraded or MountState.Starting,
                CanMount = state is MountState.Stopped or MountState.Failed,
                CanRepair = TeraBoxService.IsTeraBoxMount(d) && state is MountState.Mounted or MountState.Degraded or MountState.Starting,
                HasPausedWrites = s?.PausedSteamWrites > 0,
                IsPermanent = d.Permanent,
                Detail = (pending ?? s?.Message ?? "") + (s?.Warnings.Count > 0 ? " · " + string.Join(" ", s.Warnings) : "") + (d.ReadOnly ? " · read-only" : "")
            };
        }));
        SelectedDrive = Drives.FirstOrDefault(d => d.Definition.Id == selected);
        PermanentStatus = DescribePermanence();
    }

    private string DescribePermanence()
    {
        if (!Host.Settings.Mounts.Any(m => m.Permanent)) return "";
        var expected = StartupRegistration.CommandFor(StartupRegistration.AgentPath, Host.Paths);
        return _startup.State(expected) switch
        {
            StartupState.Registered => "Permanent drives stay mounted when this window closes and return automatically after you sign in to Windows " +
                                       "(they start at sign-in, not before it). Closing the window hands them to the CloudSSD background agent.",
            StartupState.DisabledInWindows => "Permanent drives will NOT return after a restart: CloudSSD is disabled in Task Manager → Startup apps. " +
                                              "Enable it there; CloudSSD does not override that choice.",
            _ => "The Windows sign-in entry for permanent drives is missing or points to another copy of CloudSSD. Turn the toggle off and on again to repair it.",
        };
    }

    [RelayCommand]
    private async Task SetupSteam(DriveItem? d)
    {
        if (d is null) return;
        if (d.Definition.ReadOnly) { Say("Steam installation needs a writable drive. This drive is read-only.", "warn"); return; }
        var mount = Host.Mounts.Get(d.Definition.Id);
        if (mount is not { State: MountState.Mounted or MountState.Degraded }) { Say("Mount the drive first.", "warn"); return; }
        await RunAsync(async () =>
        {
            var (client, result) = await BoundedFileSystem.Shared.RunAsync(() =>
            {
                var steam = SteamLaunch.FindClient();
                return (steam, SteamLibrarySetup.Prepare(mount.Root, steam));
            }, TimeSpan.FromSeconds(10));
            if (!result.SteamInstalled) { Say($"Library prepared at {result.Path}. Install Steam and sign in, then add this folder in Steam Settings → Storage.", "warn"); return; }
            if (result.Registered) { Say($"Steam already recognises {result.Path}. Select this library when installing a game.", "ok"); return; }
            var copied = false;
            try { System.Windows.Clipboard.SetText(result.Path); copied = true; }
            catch (System.Runtime.InteropServices.ExternalException) { }
            using (Process.Start(new ProcessStartInfo(client!) { UseShellExecute = false, Arguments = "-open steam://open/settings/storage" })) { }
            Say($"{(copied ? "Library path copied" : "Library path")}: {result.Path}. In Steam Storage, choose Add Drive → Let me choose another location and select this folder. Steam can register it without restarting.", "accent");
        });
    }

    /// <summary>"Keep Cloud SSD Permanent" on/off for one drive.</summary>
    [RelayCommand]
    private async Task TogglePermanent(DriveItem? d)
    {
        if (d is null) return;
        var def = d.Definition;
        if (!def.Permanent)
        {
            if (!File.Exists(StartupRegistration.AgentPath))
            {
                Say("CloudSSD.Agent.exe is missing next to CloudSSD, so the drive cannot be kept permanently. Reinstall CloudSSD.", "error");
                OnStateChanged();
                return;
            }
            CancelPending(def.Id);
            await RunAsync(async () =>
            {
                var s = Host.Mounts.Get(def.Id) is { State: MountState.Mounted or MountState.Degraded } running ? running : await Host.MountAsync(def);
                def.DriveLetter = s.DriveLetter.ToString(); // a permanent drive keeps this letter from now on
                def.AutoMount = true;
                def.Permanent = true;
                _startup.Register(StartupRegistration.CommandFor(StartupRegistration.AgentPath, Host.Paths));
                Host.SaveSettings();
                Say($"{def.DisplayName} ({s.DriveLetter}:) is now permanent: it stays mounted when you close CloudSSD and comes back after you sign in to Windows.", "ok");
            });
        }
        else
        {
            def.Permanent = false;
            if (!Host.Settings.Mounts.Any(m => m.Permanent)) _startup.Unregister();
            Host.SaveSettings();
            if (Host.Mounts.Get(def.Id) is null)
                Say($"{def.DisplayName} is no longer permanent. Your cloud files, login and cache are kept.", "ok");
            else if (await Host.BusyReasonAsync(def.Id) is { } reason)
            {
                if (Confirm($"{def.DisplayName} is no longer permanent, but it is busy: {reason}.\n\nUnmount it automatically as soon as it is idle?\n\n" +
                            "Yes: unmount when idle (shown as \"Unmount pending\").\nNo: keep it mounted until you unmount it or close CloudSSD."))
                    StartPendingUnmount(def);
                else Say($"{def.DisplayName} is no longer permanent; it stays mounted for now.", "ok");
            }
            else
            {
                await RunAsync(() => Host.UnmountAsync(def.Id),
                    $"{def.DisplayName} is no longer permanent and was unmounted. Your cloud files, login and cache are kept; it will not return after a restart.");
            }
        }
        OnStateChanged();
    }

    private void StartPendingUnmount(MountDefinition def)
    {
        CancelPending(def.Id);
        var cts = new CancellationTokenSource();
        _pending[def.Id] = (cts, "Unmount pending");
        var progress = new Progress<string>(m =>
        {
            if (_pending.TryGetValue(def.Id, out var p) && p.Cts == cts) _pending[def.Id] = (cts, m);
            OnStateChanged();
        });
        _ = RunPendingAsync();

        async Task RunPendingAsync()
        {
            try
            {
                await Host.UnmountWhenIdleAsync(def.Id, progress, cts.Token);
                Say($"{def.DisplayName} was idle and has been unmounted.", "ok");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Say("Unmount failed: " + Diagnostics.Redactor.Redact(e.Message), "error"); }
            finally
            {
                if (_pending.TryGetValue(def.Id, out var p) && p.Cts == cts) _pending.Remove(def.Id);
                OnStateChanged();
            }
        }
    }

    private void CancelPending(string id)
    {
        if (_pending.Remove(id, out var p)) p.Cts.Cancel();
    }

    [RelayCommand]
    private async Task ConnectGoogleDrive()
    {
        var w = new Views.ConnectDriveWindow(Host) { Owner = System.Windows.Application.Current.MainWindow };
        if (w.ShowDialog() == true)
        {
            await Host.RefreshRemotesAsync();
            await ReplaceOldGoogleConnectionsAsync(w.RemoteName);
            OnStateChanged();
            // One click: mount the whole Google Drive straight away on the next free drive letter.
            // A read-only login must give a read-only drive, so writes (e.g. a Steam library) fail immediately and clearly.
            var def = new MountDefinition { DisplayName = "Google Drive", RemoteName = w.RemoteName, RemotePath = "", AutoMount = true, ReadOnly = w.ReadOnlyScope };
            Host.Settings.Mounts.Add(def);
            Host.SaveSettings();
            Say("Google Drive connected. Mounting…", "accent");
            await RunAsync(async () =>
            {
                var s = await Host.MountAsync(def);
                def.DriveLetter = s.DriveLetter.ToString();
                Host.SaveSettings();
                Say($"Google Drive is ready as drive {s.DriveLetter}: in File Explorer{(def.ReadOnly ? " (read-only)" : "")}. Next: Game Library → Add game.", "ok");
            });
            OnStateChanged();
        }
    }

    /// <summary>
    /// A new Google sign-in replaces earlier ones made by CloudSSD ("gdrive*" accounts and their drives), so the user
    /// never has to clean up by hand. Only local logins and drive entries are removed — files in Google Drive are untouched.
    /// Drives used by a running game are left alone.
    /// </summary>
    private async Task ReplaceOldGoogleConnectionsAsync(string keep)
    {
        if (Host.Rclone is null) return;
        var running = Host.CurrentLaunch is { State: Games.LaunchState.Running } l ? l.Game.MountId : null;
        var old = Host.Remotes.Where(r => r.Type == "drive" && r.Name != keep && r.Name.StartsWith("gdrive", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var remote in old)
        {
            var mounts = Host.Settings.Mounts.Where(m => m.RemoteName == remote.Name).ToList();
            if (mounts.Any(m => m.Id == running)) continue;
            foreach (var m in mounts)
            {
                try { if (Host.Mounts.Get(m.Id) != null) await Host.UnmountAsync(m.Id); } catch (Exception) { }
                Host.Settings.Mounts.Remove(m);
            }
            try { await Host.RcloneConfig.DeleteRemoteAsync(Host.Rclone, remote.Name); } catch (Exception) { }
        }
        Host.SaveSettings();
        await Host.RefreshRemotesAsync();
    }

    [RelayCommand]
    private async Task AddDrive(string? remote)
    {
        if (Host.Remotes.Count == 0)
        {
            Say("Connect a cloud account first.", "warn");
            return;
        }
        var w = new Views.AddDriveWindow(Host, remote) { Owner = System.Windows.Application.Current.MainWindow };
        if (w.ShowDialog() != true || w.Result is null) return;
        Host.Settings.Mounts.Add(w.Result);
        Host.SaveSettings();
        OnStateChanged();
        await RunAsync(async () =>
        {
            var s = await Host.MountAsync(w.Result);
            Host.SaveSettings();
            Say($"{w.Result.DisplayName} mounted at {s.DriveLetter}:", "ok");
        });
        OnStateChanged();
    }

    [RelayCommand]
    private async Task Mount(DriveItem? d)
    {
        if (d is null) return;
        await RunAsync(async () =>
        {
            var s = await Host.MountAsync(d.Definition);
            Host.SaveSettings();
            Say($"{d.Name} mounted at {s.DriveLetter}:", "ok");
        });
        OnStateChanged();
    }

    [RelayCommand]
    private async Task RepairSteamRecovery(DriveItem? d)
    {
        if (d is null) return;
        Say("Pausing incomplete Steam writes safely, then restoring the drive…", "accent");
        await RunAsync(() => Host.RepairSteamRecoveryAsync(d.Definition.Id),
            "Drive restored. Incomplete Steam writes are paused and preserved; use Resume saved writes when you want their cloud recovery to continue.");
        OnStateChanged();
    }

    [RelayCommand]
    private async Task ResumeSteamRecovery(DriveItem? d)
    {
        if (d is null) return;
        Say("Restoring saved Steam writes. The drive may wait for missing cloud data while recovery runs…", "accent");
        await RunAsync(() => Host.RepairSteamRecoveryAsync(d.Definition.Id, resumePaused: true));
        OnStateChanged();
    }

    [RelayCommand]
    private async Task Unmount(DriveItem? d)
    {
        if (d is null) return;
        if (Host.CurrentLaunch is { State: Games.LaunchState.Running } l && l.Game.MountId == d.Definition.Id
            && !Confirm($"{l.Game.Name} is running from this drive. Unmounting will break it. Continue?")) return;
        CancelPending(d.Definition.Id);
        await RunAsync(() => Host.UnmountAsync(d.Definition.Id), $"{d.Name} unmounted. Pending uploads were given up to 60 s to finish." +
            (d.Definition.Permanent ? " It is still set to \"Keep Cloud SSD Permanent\", so it returns at your next Windows sign-in." : ""));
        OnStateChanged();
    }

    [RelayCommand]
    private void Open(DriveItem? d)
    {
        if (d is null || Host.Mounts.Get(d.Definition.Id) is not { } s) return;
        Process.Start(new ProcessStartInfo("explorer.exe", s.Root) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task Refresh(DriveItem? d)
    {
        if (d is null) return;
        await RunAsync(() => Host.Mounts.ForgetAsync(d.Definition.Id), "Directory cache refreshed; changed cloud files will be re-validated on next access.");
    }

    [RelayCommand]
    private async Task RemoveDrive(DriveItem? d)
    {
        if (d is null) return;
        if (!Confirm($"Remove the drive '{d.Name}' from CloudSSD?\n\nYour cloud files are NOT touched. The drive will be unmounted.")) return;
        await RunAsync(async () =>
        {
            CancelPending(d.Definition.Id);
            if (Host.Mounts.Get(d.Definition.Id) != null) await Host.UnmountAsync(d.Definition.Id);
            Host.Settings.Mounts.RemoveAll(m => m.Id == d.Definition.Id);
            if (!Host.Settings.Mounts.Any(m => m.Permanent)) _startup.Unregister();
            Host.SaveSettings();
            var dir = Host.Paths.MountCacheDir(Host.Settings, d.Definition.Id);
            if (Directory.Exists(dir) && Confirm("Also delete this drive's local cache? (Only cached copies are deleted, never cloud files.)"))
            {
                var r = CacheMaintenance.ClearMountCache(dir, Host.Paths.CacheRootFor(Host.Settings));
                Say(r.Message, r.Cleared ? "ok" : "warn");
            }
            else Say("Drive removed.", "ok");
        });
        OnStateChanged();
    }

    [RelayCommand]
    private async Task RemoveAccount(KeyValueRow? a)
    {
        if (a is null || Host.Rclone is null) return;
        if (Host.Settings.Mounts.Any(m => m.RemoteName == a.Key || (a.Key == TeraBoxService.RemoteName && TeraBoxService.IsTeraBoxMount(m))))
        {
            Say("Remove the drives that use this account first.", "warn");
            return;
        }
        if (!Confirm($"Disconnect account '{a.Key}'? The saved login is deleted from this PC; your cloud files are not touched.")) return;
        await RunAsync(async () =>
        {
            if (a.Key == TeraBoxService.RemoteName) await Host.TeraBox.DisconnectAsync();
            else await Host.RcloneConfig.DeleteRemoteAsync(Host.Rclone, a.Key);
            await Host.RefreshRemotesAsync();
        }, "Account disconnected.");
        OnStateChanged();
    }
}
