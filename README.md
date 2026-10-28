# CloudSSD

**Cloud storage, with a local SSD cache.** CloudSSD mounts cloud files as a Windows drive letter and caches the data your applications read. Your PC runs the game or application; uncached files arrive at the connection and provider's speed.

CloudSSD is a WPF/.NET 10 desktop application for Windows x64. This is an **experimental preview**. See [build status](BUILD_STATUS.md), [test results](TEST_REPORT.md) and [limitations](KNOWN_LIMITATIONS.md) for verified behavior.

## Features

- Google Drive OAuth and an optional TeraBox bridge, using real provider requests and protected credentials.
- Cloud drive creation, browsing, refresh, safe mounting/unmounting and a background agent for permanent drives.
- An NTFS sparse cache with budgets, a Windows free-space reserve, pinning, physical usage reporting and guarded cleanup.
- A searchable game library, resumable import/verification, compatibility analysis, launch diagnostics, and Cloud Direct, Hybrid Optimized and Full Local modes.
- Read profiling, prediction, Optimize & Prepare, full caching, performance metrics and sanitized diagnostics.
- Eight pages, shared dialogs, native drag/resize/minimize/maximize/restore/close and Ctrl+1 through Ctrl+8 navigation.

Game-Aware telemetry is experimental; no real game plugin has been verified. Portable non-game programs may work, but browser and arbitrary application installation on the cloud drive remain unverified. Read [compatibility](COMPATIBILITY.md).

## Run

Requirements: Windows 10 (1809+) or Windows 11 x64, an NTFS cache location with enough free space, and [WinFsp](https://winfsp.dev/rel/) for virtual drives. Self-contained releases include .NET. TeraBox website sign-in needs the Microsoft WebView2 Runtime.

Extract `CloudSSD-1.0.0-win-x64.zip` and run `CloudSSD.exe`. The optional included script installs program files for the current user:

```powershell
.\Install.cmd
# Optional desktop shortcut, from the extracted release folder:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-CloudSSD.ps1 -DesktopShortcut
```

The script targets `%LOCALAPPDATA%\Programs\CloudSSD` and does not silently install WinFsp. Optional setup EXE packaging requires Inno Setup 6. Install/upgrade/uninstall acceptance has not been completed for this preview.

## First steps

1. Choose an NTFS cache folder, budget, free-space reserve and download speed in **Mbps**.
2. In **Cloud Drives**, connect [Google Drive](docs/GOOGLE_DRIVE_SETUP.md) or [TeraBox](TERABOX_INTEGRATION.md).
3. Add a virtual drive for the account/folder and confirm it in Explorer.
4. Add or import an installed game in **Game Library**; review compatibility before launch.
5. Profile a launch, then use **Optimize & Prepare** to cache learned data. **Fully cache** needs the full game's local disk footprint.
6. Review actual usage and read waits in **Smart Cache** and **Performance Monitor**.

The [user guide](docs/USER_GUIDE.md) covers all pages, settings and safe shutdown. **Settings → Open data folder** shows the active profile; respect the application's configured data and cloud paths.

## Develop and package

Run from the source checkout with .NET SDK 10.0.402 or a newer .NET 10 feature band and PowerShell. Git and Python are needed for source checks. Source downloads exclude compiled binaries, so [build the native drive engine](docs/NATIVE_RCLONE.md) before mounting or packaging. The helper selects the installed SDK and actual project files:

```powershell
& '.\scripts\cloudssd.ps1' -Action Build
& '.\scripts\cloudssd.ps1' -Action Test
& '.\scripts\cloudssd.ps1' -Action Run
# Choose an unused version; release folders/archives are not overwritten.
& '.\installer\build-release.ps1' -Version 1.0.0
```

WinFsp is needed for real-mount checks. Go 1.26+ is needed for native rebuilds; Inno Setup 6 is optional for setup EXE creation. No extra development tools need to be installed when those are already available. See [CONTRIBUTING.md](CONTRIBUTING.md).

Release packaging checks the modified driver's version/hash, runs unit tests, publishes self-contained desktop/CLI/agent binaries, includes current guides and required notices, runs the CLI dependency smoke test and generates a ZIP with SHA-256. Publisher credentials are excluded by default. No release is published automatically.

## Documentation and licensing

Start with [the documentation index](docs/INDEX.md). Product licensing is described in [LICENSE](LICENSE); the owner has not supplied new product terms. Included source and dependencies keep their required notices in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `licenses/`. Review [security guidance](SECURITY.md) before sharing diagnostics.
