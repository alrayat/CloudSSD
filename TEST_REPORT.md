# CloudSSD verification

Executed on Windows 11 x64 on 2026-10-28 using .NET SDK 10.0.402, runtime 10.0.13, Go 1.27.3 and WinFsp 2.1.25156. Results and disposable profiles are excluded from Git. The build, unit suite, desktop harness and production publish used a source snapshot containing the changes selected for commit; unrelated local compatibility edits were excluded.

| Check | Result |
| --- | --- |
| Release solution build | Passed; 0 warnings, 0 errors |
| Unit suite | 155 passed; 0 failed/skipped |
| Real WPF desktop harness | 79 checks passed; no binding errors or unhandled UI exceptions |
| Native runtime packages | `vfs`, `vfscache`, `writeback`, `downloaders` passed with vet disabled |
| Broad integration run before the sync fix | 57 passed, 1 failed; Hybrid game-flow case excluded |
| Strengthened crash regression before the sync fix | Failed: the second flushed write's range was lost after worker termination |
| Strengthened crash regression after the sync fix | Passed: both flushed changes survived pause/repair/restore with exact bytes |
| Final affected integration run | 25 passed; 0 failed/skipped; includes import/readiness/shutdown and two-write crash recovery |
| Production publish | Desktop, CLI and Agent self-contained win-x64 ReadyToRun publish passed |
| Published-folder dependency check | Passed; final native engine copied and dependency smoke repeated |
| Local portable package | 1.0.1 win-x64 ZIP built from the selected source; not uploaded to GitHub |
| Source audit | Passed; required source attribution retained separately |

## Bugs corrected

The import test uses a generated DRM-free demo: **10 files, 384.2 MiB**. It imports, byte-verifies, resumes without repeating the full transfer and launches from the real mounted drive with **exit 0**. The test now also asserts that the drive becomes ready for automatic power actions and completes guarded application shutdown. No actual Windows shutdown/restart command was executed during verification.

Windows can retain native open-file telemetry after an executable exits. A proposed process/download heuristic incorrectly allowed an ordinary reader to be disconnected when its program ran from local disk. A regression failed against that proposal. The replacement requires complete native file paths/counts, no pending/error uploads and no active users before accepting retained executable mappings. Supported Windows Restart Manager and exclusive file-sharing checks detect external users; an additional mapped-region scan detects views held by the calling process. Ordinary readers, Steam writers and memory-mapped files in both the calling process and a separate PowerShell process continue to block unmount, retain readable bytes and permit unmount after closing. Automatic shutdown readiness uses the same checks. Missing status remains unsafe for automatic power actions.

The broad integration run exposed a sparse-write recovery failure. The native Windows `Fsync` callback returned success without calling the VFS handle's `Sync`. Strengthening the test to make two flushed changes reproduced loss at the second offset. The corrected callback syncs local data and sparse-range metadata without filling unknown ranges or waiting for cloud upload. The regression verifies both changes after fixture worker termination, pause, repair and restore. Unsynced writes and cloud upload completion remain separate concerns.

Game launch monitoring now disposes its completed process handle. Nonessential diagnostics-write permission failures no longer prevent normal completion cleanup.

## Integration coverage

The broad 58-case run exercised filesystem listing/metadata, Unicode paths, whole-file and range bytes, offsets beyond 2 GiB, concurrent handles, memory-mapped reads, executable launch, file-lock characterization, write/rename/delete propagation, upload crash recovery, cache reuse/eviction, low-space eviction, offline cached reads, remote changes, injected retries and simulated link speeds. It also exercised startup registration, occupied-letter/network waiting, app/agent handoff, supervisor/worker crashes, cache-job resume, full-folder cache verification, large-file chunking, TeraBox quota/token renewal, website-session rotation, bridge restart/adoption and demo import. It found the sparse-write failure described above; it was not a clean full-suite pass.

The final affected run selects filesystem, write-back, TeraBox website-session, chunked-drive and application-flow cases. It uses the final native revision and strengthened two-write regression. Cache/agent cases that passed before the sync callback change were not all repeated afterward.

All mount checks used real WinFsp/rclone mounts with local HTTP/API fixtures and write-back storage. They do not establish live-account authentication, provider throughput, commercial gameplay or browser/general application installation. WinFsp was installed and the generated demo was present; its workload did not take the optional missing-demo early return.

## Desktop and package boundaries

The WPF harness loads all eight pages and eleven dialog views, saves/reloads settings in an isolated profile and exercises search/selection, keyboard navigation, validation, scrolling, compact layouts and confirmation dismissal. It executes minimize, maximize/restore and close commands and checks resize configuration and state-dependent icons.

Mouse-driven title-bar dragging, double-click and edge resizing were **not exercised**. Live OAuth and WebView2 sign-in were not exercised. Hybrid symbolic-link privileges were unavailable, so the full Hybrid game-flow test was excluded; no privilege settings were changed. Inno Setup was absent: **no setup EXE was built or install-tested**. Clean-PC install/upgrade/uninstall, actual Windows reboot/logon and sleep/resume remain unverified. New product license terms are pending.

The existing GitHub **v1.0.0 ZIP** still contains native revision `.1` and has not been replaced. This report documents corrected source, local production publishing and local 1.0.1 ZIP packaging, not a new downloadable GitHub release. That existing ZIP retains the import-shutdown issue and the sync limitation. Packaging used `-SkipTests` after the separate 155-test unit run and the final integration run passed.

## Native identity

Final native identity: **`v1.75.2-cloudssd.2`**. Recorded rclone executable SHA-256:

```text
942b524387c0e0a9b43fc720618d4abc215806a3022c5ef8df11b1571e7ae97d
```

Native runtime tests used `-SkipVet` / `-vet=off`; **Go vet was not verified**. Normal native builds retain vet. See [native build instructions](docs/NATIVE_RCLONE.md).

## Reproduce

Run from the repository root with an existing .NET 10 SDK, Go toolchain and WinFsp. Use a fresh isolated test directory. Native builds need PowerShell 7.4+.

```powershell
& .\installer\build-native.ps1 -GoExe go -BuildDirectory .\.work\native-build-check -Install
python .\scripts\check-source.py
& .\scripts\cloudssd.ps1 -Action Build
& .\scripts\cloudssd.ps1 -Action Test
& .\scripts\cloudssd.ps1 -Action DesktopChecks

$env:CLOUDSSD_TEST_ROOT = Join-Path $PWD '.work\verification-check\mounts'
$env:CLOUDSSD_TEST_RCLONE = Join-Path $PWD 'tools\rclone\rclone.exe'
$demo = Join-Path $env:CLOUDSSD_TEST_ROOT 'dataset\Games\CloudSSD Demo Game'
dotnet publish tests\CloudSSD.DemoGame -c Release -r win-x64 --self-contained false -o $demo
& (Join-Path $demo 'CloudSSD.DemoGame.exe') --generate $demo

dotnet test tests\CloudSSD.IntegrationTests -c Release --filter 'FullyQualifiedName~TeraBoxChunkedDriveTests|FullyQualifiedName~TeraBoxAppFlowTests|FullyQualifiedName~WriteBackTests|FullyQualifiedName~TeraBoxWebDriveTests|FullyQualifiedName~FilesystemTests'

# Broader checks; Hybrid requires privileges not available in this verification environment:
dotnet test tests\CloudSSD.IntegrationTests -c Release --filter 'FullyQualifiedName!~GameFlowTests'

& .\scripts\cloudssd.ps1 -Action Run
# Choose an unused version; existing package directories are preserved:
& .\installer\build-release.ps1 -Version 1.0.1
```

Fixture crash tests deliberately terminate only their own native workers. Preserve cache/profile folders if an upload is pending or a test owner remains alive; do not apply fixture cleanup to user drives.
