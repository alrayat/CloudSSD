# CloudSSD verification

Checks below were executed against the CloudSSD source on Windows 11 x64 on 2026-10-28, using .NET SDK 10.0.402, runtime 10.0.13, Go 1.27.3 and WinFsp 2.1.25156. Local test profiles and results are excluded from Git.

| Check | Result |
| --- | --- |
| Release solution build | Passed; 0 warnings, 0 errors |
| Unit suite | 153 passed; 0 failed/skipped; repeated successfully by release packaging |
| Real WPF desktop harness | 79 checks passed; 0 binding errors or unhandled UI exceptions |
| Native runtime packages | `vfs`, `vfscache`, `writeback`, `downloaders` passed with vet disabled |
| Selected mount/agent/bridge cases | 17 passed |
| Chunked TeraBox drive cases | 7 passed |
| TeraBox demo import | Failed during guarded shutdown after file verification, resume and launch succeeded |
| Production publish/package | Desktop, CLI and Agent self-contained win-x64 publish, dependency smoke and portable ZIP succeeded |
| Source audit | Passed; required source attribution retained separately |

## Desktop coverage

The harness loads all eight pages and eleven dialog views, saves/reloads real settings in an isolated profile, exercises library search/selection, keyboard navigation, validation, scrolling, compact layouts and confirmation dismissal. It executes minimize, maximize/restore and close commands and checks WPF resize behavior, native caption/resize configuration and state-dependent icons.

Mouse-driven title-bar dragging, double-click and edge resizing were **not exercised**. WindowChrome supplies those behaviors, but configuration checks are not mouse acceptance. Live OAuth and WebView2 sign-in were not exercised by this harness.

## Integration coverage and failure

The 17-case run covers directory listing/metadata, Unicode names, whole-file hashes, random ranges, 64-bit offsets beyond 2 GiB, concurrent handles, memory-mapped reads, warm sparse-cache reads, portable executable launches, lock behavior characterization, write/rename/delete propagation, crash upload recovery, three private-key startup-registration cases, desktop-to-agent handoff and TeraBox bridge restart/adoption/shutdown.

The seven chunked-drive cases cover exact cold reads across part boundaries, files exceeding a simulated plan limit, partial-write close latency, sparse write recovery, preserved dirty ranges, bounded recovery concurrency, reservation markers and refusal to unmount while a test reader is open followed by successful unmount after it closes.

The import test used a generated DRM-free demo: **10 files, 384.2 MiB**. Full download/byte comparison succeeded, a repeated import resumed without repeating the complete transfer, and the demo launched from the mounted drive with **exit 0**. Shutdown then failed because the native driver reported **four open files**. The overall case remains **failed**. A shared filesystem fixture also retained open-file telemetry after its assertions passed. Both disposable mounts were explicitly cleaned up after their test owners exited and pending uploads/transfers were zero; the orphaned fixture bridge was stopped. The production unmount guard remains unchanged.

Real WinFsp/rclone mounts were used; HTTP/API fixtures and the local write-back backend supplied test content. They do not establish live-account authentication, throughput, commercial gameplay or browser/general app installation. This was a selected run, not the full integration suite.

## Native and package boundaries

Native identity: **`v1.75.2-cloudssd.1`**. Recorded rclone executable SHA-256:

```text
f9058d3bc32c43dde9fd2b9729da97a6976386797dc43a4400daf003db048301
```

Native runtime tests passed with `-SkipVet` / `-vet=off`; **Go vet was not verified**. Normal native builds retain vet.

The portable package dependency check found WinFsp, the bundled rclone, NTFS sparse support, VC++ runtime and .NET runtime. Symbolic-link privileges were unavailable, so Hybrid execution was not accepted. Inno Setup was absent; **no setup EXE was built or install-tested**. Clean-PC prerequisites, install/upgrade/uninstall, Windows reboot/logon and sleep/resume remain unverified.

## Reproduce

Run from the repository root. Select your installed SDK with `DOTNET_ROOT` if necessary. Build the native engine first for drive tests; see [native instructions](docs/NATIVE_RCLONE.md).

```powershell
python .\scripts\check-source.py
& .\scripts\cloudssd.ps1 -Action Build
& .\scripts\cloudssd.ps1 -Action Test
& .\scripts\cloudssd.ps1 -Action DesktopChecks

$env:CLOUDSSD_TEST_ROOT = Join-Path $PWD '.work\verification\mounts'
$env:CLOUDSSD_TEST_RCLONE = Join-Path $PWD 'tools\rclone\rclone.exe'
dotnet test tests\CloudSSD.IntegrationTests -c Release --filter 'FullyQualifiedName~FilesystemTests|FullyQualifiedName~WriteBackTests|FullyQualifiedName~StartupRegistrationTests|FullyQualifiedName~Window_close_hands_drive_to_agent_and_back_without_unmounting|FullyQualifiedName~Connect_mount_restart_adopt_and_shutdown'
dotnet test tests\CloudSSD.IntegrationTests -c Release --filter 'FullyQualifiedName~TeraBoxChunkedDriveTests'

# Required fixture for the import case; this creates disposable data:
$demo = Join-Path $env:CLOUDSSD_TEST_ROOT 'dataset\Games\CloudSSD Demo Game'
dotnet publish tests\CloudSSD.DemoGame -c Release -r win-x64 --self-contained false -o $demo
& (Join-Path $demo 'CloudSSD.DemoGame.exe') --generate $demo
dotnet test tests\CloudSSD.IntegrationTests -c Release --filter 'FullyQualifiedName~Import_existing_game_into_TeraBox_verify_resume_and_play_from_T'

# Choose an unused release version; existing archives are preserved:
& .\installer\build-release.ps1 -Version 1.0.0
```

The demo uses the installed Windows Desktop runtime. Keep test drive letters free and close fixture readers before cleanup; do not stop unrelated user drives or discard dirty cache files.
