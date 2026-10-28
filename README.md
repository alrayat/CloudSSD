# CloudSSD

**Cloud storage, with a local SSD cache.**

CloudSSD connects cloud folders to a Windows drive letter and caches the files you read on a local NTFS disk. Games and applications run on your own PC. Files already cached can be read locally; uncached files still depend on your internet connection and cloud provider.

**Current release: CloudSSD 1.0.0 for Windows x64.**

- [Download the Windows application ZIP](https://github.com/alrayat/CloudSSD/releases/download/v1.0.0/CloudSSD-1.0.0-win-x64.zip)
- [Release page and checksum file](https://github.com/alrayat/CloudSSD/releases/tag/v1.0.0)
- [User guide](docs/USER_GUIDE.md) · [Troubleshooting](docs/TROUBLESHOOTING.md) · [Known limitations](KNOWN_LIMITATIONS.md)

Download **CloudSSD-1.0.0-win-x64.zip** under the release's **Assets**. It contains **CloudSSD.exe**, an installation script and the application binaries. GitHub's **Source code (zip)** and **Code → Download ZIP** contain development files, not an installed application. This project currently provides a Windows package; no Android APK or setup EXE is available.

## Contents

- [Requirements](#requirements)
- [Install CloudSSD](#install-cloudssd)
- [First launch](#first-launch)
- [Connect cloud storage](#connect-cloud-storage)
- [Use games and applications](#use-games-and-applications)
- [Pages and features](#pages-and-features)
- [Close, update and uninstall](#close-update-and-uninstall)
- [Troubleshooting](#troubleshooting)
- [Release status and verification](#release-status-and-verification)
- [Build from source](#build-from-source)
- [Documentation, support and licensing](#documentation-support-and-licensing)

## Requirements

| Requirement | Details |
| --- | --- |
| Windows x64 | The current build was verified on Windows 11 x64. Windows 10 compatibility needs separate verification. |
| Local NTFS storage | Use a local NTFS cache folder, preferably on an SSD. Leave room for the cache, upload staging and a Windows free-space reserve. |
| WinFsp | Required to mount virtual cloud drives. Download it from the [official WinFsp releases page](https://winfsp.dev/rel/). |
| Cloud account and internet | Required for authentication, mounting and uncached transfers. Account quotas, file limits and rate limits still apply. |
| Microsoft WebView2 Runtime | Needed for the optional TeraBox website sign-in. Use Microsoft's [WebView2 Runtime download](https://developer.microsoft.com/microsoft-edge/webview2/#download-section) if it is missing. |
| .NET runtime | Included in the Windows release. You do not need the .NET SDK to run the downloaded application. |
| Microsoft VC++ x64 runtime | Many games require it. Diagnostics reports when it is missing; see Microsoft's [supported Visual C++ downloads](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist). |
| Symbolic-link privileges | Only needed for **Hybrid Optimized** mode. Windows Developer Mode or appropriate privileges are required; CloudSSD does not enable them automatically. |

Cache space is real local disk space. A cloud drive's reported capacity does not increase your physical SSD capacity. **Full Local** needs enough local space for the entire game.

## Install CloudSSD

1. Download [CloudSSD-1.0.0-win-x64.zip](https://github.com/alrayat/CloudSSD/releases/download/v1.0.0/CloudSSD-1.0.0-win-x64.zip).
2. Install **WinFsp** from its official site if it is not already installed. Its system driver installation may request administrator permission. Install WebView2 only if you need TeraBox website sign-in and the runtime is missing.
3. Right-click the downloaded ZIP and choose **Extract All**. Keep the complete folder structure, including `tools`, `docs` and the files beside `CloudSSD.exe`. Do not run the app from inside the ZIP or copy only the EXE.
4. Open the extracted folder and double-click **Install.cmd**.
5. Start **CloudSSD** from the Windows Start menu. The install script also launches it when installation finishes.

The script installs CloudSSD for the current Windows user into `%LOCALAPPDATA%\Programs\CloudSSD`, creates a Start menu shortcut and adds an entry to Windows **Installed apps**. It does not automatically install WinFsp.

For an optional desktop shortcut, open PowerShell in the extracted release folder and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-CloudSSD.ps1 -DesktopShortcut
```

### Run without installing

After extracting the complete release and installing the required prerequisites, double-click **CloudSSD.exe**. This skips copying the program and creating shortcuts. Settings and cached data still use the configured user profile; they are not automatically stored beside the EXE.

### Verify the download

Download the `.sha256` asset from the release page. In PowerShell, from the folder containing the ZIP, run:

```powershell
Get-FileHash .\CloudSSD-1.0.0-win-x64.zip -Algorithm SHA256
```

For the published **1.0.0** ZIP, the expected SHA-256 is:

```text
5b6416ed5ea5429e7a56f3098820776d63529672e4c0379d68d347dd87a3cc72
```

## First launch

1. In the welcome window, choose a **cache location** on a local NTFS disk.
2. Choose a **cache budget** that fits the available space. Keep a separate Windows free-space reserve; the welcome window defaults to 8 GB.
3. Enter your download speed in **Mbps**. For example, 70 Mbps is about 8.75 MB/s before overhead.
4. Click **Save and continue**.
5. Open **Diagnostics** and resolve missing prerequisites before mounting a drive.
6. Connect an account on **Cloud Drives**, then confirm the mounted drive in File Explorer.

Use **Settings → Open data folder** to find the active profile. The published 1.0.0 build defaults to `%LOCALAPPDATA%\CloudSSD`. It stores settings, the library, access profiles, logs and protected credentials there; a custom cache folder can be elsewhere. Changes in Settings require **Save settings**.

## Connect cloud storage

### Google Drive

1. Open **Cloud Drives** and choose **Sign in with Google Drive**.
2. Follow the Google sign-in page in your browser.
3. If the shared sign-in client is unavailable or restricted, use **Advanced** and configure your own Google OAuth desktop client. Enable the Google Drive API and configure consent/test-user settings as described in the [Google Drive setup guide](docs/GOOGLE_DRIVE_SETUP.md).
4. Review the drive created by the connection. For a separate drive or folder, use **Add virtual drive**, select the account/folder, choose an unused letter and mount it.

Read-only access requires games to keep their saves outside the cloud drive. API and account limits still apply.

### TeraBox

Open **Cloud Drives → Connect TeraBox** and select the appropriate method:

- **Official API:** import approved TeraBox developer credentials and complete the QR/device-code authorization.
- **Website session:** read and accept the in-app notice, then sign in through the embedded WebView2 page. This optional method is unofficial and may require renewed sign-in or provider verification.

Provider credentials are not bundled with the public release. Read [TeraBox setup](TERABOX_INTEGRATION.md) before connecting. A connection can create and mount its drive automatically; review its state in **Cloud Drives** and open it in Explorer. File limits, quotas and throughput depend on the provider and account.

Other rclone backends can be configured, but their complete CloudSSD workflows have not been verified. See [compatibility](COMPATIBILITY.md).

## Use games and applications

### Import an installed game

1. Install the game normally on local storage.
2. Connect your cloud account and mount its drive.
3. In **Game Library**, choose **Import installed game to cloud**.
4. Select the local game folder, destination drive and cloud folder. Review capacity and compatibility warnings.
5. Import and verify the files. **Full verification** reads uploaded data back for comparison and can take additional time and bandwidth.
6. Keep your local original until the cloud workflow has been checked. Review the game's compatibility report before launching.

The import keeps the local source. Any offered action to move the local copy to the Recycle Bin is separate and asks for confirmation; it can also move saves stored inside that folder. See [game installation](GAME_INSTALLATION.md).

| Execution mode | What it does |
| --- | --- |
| **Cloud Direct** | Launches from the mounted drive. Cold reads wait for the network. |
| **Hybrid Optimized** | Copies essential files locally and uses symbolic links for large data. Requires symbolic-link privileges. |
| **Full Local** | Downloads the entire game to local storage and uses its normal launcher/ownership flow. |

Profile a launch, then use **Optimize & Prepare** to cache learned data before another run. Unseen files still download on demand. Preparation and full caching use local space.

### Can I run browsers or other apps?

Some portable programs may work from a mounted drive, but CloudSSD is not a general Windows application installer. Chrome/Edge installations, browser profiles, services, registry-dependent installers and updaters have not been verified on cloud storage. Install browsers and these applications normally on local storage. Commercial games, DRM and anti-cheat also require individual compatibility checks.

## Pages and features

| Page | Purpose |
| --- | --- |
| Dashboard | Connection health, storage metrics, setup checklist and warnings. |
| Cloud Drives | Account connections, drive creation, mount/open/refresh/unmount and permanent-drive settings. |
| Game Library | Search, add/import, compatibility, launch modes, preparation, full caching and game settings. |
| Game Accelerator | Read profiles, predictions, queued downloads and foreground waits. |
| Smart Cache | Cache location/budget/reserve, physical usage, pinning and guarded cleanup. |
| Performance Monitor | Actual throughput, cache activity, read waits and history. |
| Settings | Network, prediction, profiling, startup, security and advanced drive options. |
| Diagnostics | Dependency checks, logs, recovery guidance and sanitized diagnostic export. |

Use **Ctrl+1 through Ctrl+8** to select pages. The desktop frame provides minimize, maximize/restore, close, title-bar dragging and resize borders.

## Close, update and uninstall

### Close safely

Finish cloud writes and close games or other programs using the drive before unmounting it. If CloudSSD reports open files or pending uploads, keep the drive and cache available and retry after activity stops. Do not force-stop rclone or delete dirty cache files to dismiss the warning.

**Keep Cloud SSD Permanent** lets the background agent manage the drive after the window closes and register current-user sign-in startup. Turn it off and safely unmount before replacing program files. See [permanent drives](PERSISTENCE.md).

### Update

Download and extract the desired release, finish uploads, unmount drives and close CloudSSD and its background agent. Run the new release's **Install.cmd** to copy program files to the same current-user installation. Back up your active profile before an upgrade.

### Uninstall

Open **Windows Settings → Apps → Installed apps → CloudSSD → Uninstall**, or run this from the installed program folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-CloudSSD.ps1
```

Finish uploads and close drive readers first. The script asks whether to remove settings/library/login data and cached data. Choose **N** or press Enter to retain them. WinFsp is a separate installed component.

For a portable copy, safely close the app and its mounts before removing the extracted program folder. The user profile and any custom cache remain separate.

## Troubleshooting

| Problem | First step |
| --- | --- |
| Download contains projects but no CloudSSD.exe | Download the named application ZIP from **Releases → Assets**, then extract it. |
| WinFsp missing or drive cannot mount | Install official WinFsp, restart the app and check Diagnostics. |
| rclone missing | Extract the entire release again, including `tools/rclone/rclone.exe`. |
| TeraBox website sign-in cannot open | Check that Microsoft WebView2 Runtime is installed. |
| Google sign-in fails | Use your own OAuth client and check Drive API/consent configuration in the setup guide. |
| Hybrid mode reports symbolic-link failure | Use another execution mode, or enable the required Windows privileges yourself. |
| Cold launch is slow | Check actual provider speed. Prepare/cache data or use Full Local with enough local space. |
| Drive is busy or import cannot unmount | Close readers, wait for uploads and retry. The published 1.0.0 ZIP has an import-shutdown issue; current source contains the fix. |
| Disk space is low | Review cache budget/reserve and staging usage. Keep pending-upload data; use the app's guarded cleanup. |

See [detailed troubleshooting](docs/TROUBLESHOOTING.md). Export a sanitized report from Diagnostics when reporting a problem; keep credentials and private profiles out of issues.

## Release status and verification

CloudSSD 1.0.0 is published as a regular GitHub release. Its existing ZIP uses native engine revision `.1` and has not been replaced by the source fixes described below.

The source fix uses native engine revision `.2` to identify retained files and distinguish inactive executable mappings from active readers. The demo import, byte verification, resume, launch and guarded shutdown test now passes. Regression tests cover ordinary readers and memory-mapped files in both the app process and another process. Automatic power actions use the same readiness checks; pending uploads and unknown status still block them. See [the test report](TEST_REPORT.md) for the complete verification results and boundaries.

Revision `.2` also fixes native filesystem sync so explicit flushes persist local sparse-range metadata. A worker-crash regression verifies that two flushed writes survive pause, repair and restore with exact bytes. Build current source to get these fixes; the published 1.0.0 ZIP remains unchanged.

Live cloud-account authentication, commercial gameplay, general app/browser installation, clean-PC installation/upgrade/uninstall, Windows reboot/logon, sleep/resume and mouse-driven window drag/edge resize still need verification. Go vet was not verified. New product license terms are pending. These are separate from whether the application compiles successfully. See [the test report](TEST_REPORT.md) and [build status](BUILD_STATUS.md) for the exact boundaries.

## Build from source

This section is for developers. To run the application, use the release installation instructions above.

### Development tools

- Git and **.NET SDK 10.0.402** or a newer .NET 10 feature band accepted by `global.json`.
- PowerShell; the native build helper requires **PowerShell 7.4+**.
- Python 3 for the source audit.
- WinFsp for real virtual-drive tests.
- Go **1.26+** to build the modified native engine; the recorded binary used **Go 1.27.3**.
- Optional Inno Setup 6 for a setup EXE. It is not needed for ZIP packaging.

Use tools already installed on your machine. Build from a source checkout:

```powershell
git clone https://github.com/alrayat/CloudSSD.git
cd CloudSSD

# Run this native step in PowerShell 7.4+, with your installed Go on PATH.
# Choose a fresh build directory; the helper preserves existing directories.
& .\installer\build-native.ps1 -GoExe go -BuildDirectory .\.work\native-build -Install

python .\scripts\check-source.py
& .\scripts\cloudssd.ps1 -Action Build
& .\scripts\cloudssd.ps1 -Action Test
& .\scripts\cloudssd.ps1 -Action Run
```

If needed, set `DOTNET_ROOT` to your installed SDK directory. The helpers also check `%USERPROFILE%\.dotnet` and the SDK on PATH. Source downloads exclude built binaries and credential files.

The native helper verifies the pinned upstream archive, applies the included patch, runs native tests and builds `v1.75.2-cloudssd.2`. `-Install` requires the binary to match the recorded checksum. Different Go versions can change the hash; review and test a candidate before updating its checksum. The normal command retains Go vet. See [native build instructions](docs/NATIVE_RCLONE.md) for compiler, candidate-install and `-SkipVet` details.

### Checks and packaging

```powershell
& .\scripts\cloudssd.ps1 -Action DesktopChecks

# From PowerShell with .NET 10 on PATH, or DOTNET_ROOT set.
# Choose an unused version; existing release folders and archives are preserved.
& .\installer\build-release.ps1 -Version 1.0.1
```

Desktop checks open real WPF views against an isolated profile. Real-mount integration tests need WinFsp, the verified native engine and disposable test profiles; follow [test commands](TEST_REPORT.md) before running them.

Packaging creates self-contained Windows x64 Desktop, CLI and Agent binaries, includes the verified native engine and required notices, runs unit/dependency checks and produces a ZIP plus SHA-256 under `dist/`. A setup EXE is optional when Inno Setup is available. Packaging does not publish a release automatically. Provider credentials are excluded by default; keep secrets out of Git and public packages.

## Documentation, support and licensing

- [User guide](docs/USER_GUIDE.md) and [documentation index](docs/INDEX.md).
- [Architecture](ARCHITECTURE.md), [contributing](CONTRIBUTING.md) and [native drive engine](docs/NATIVE_RCLONE.md).
- [Test report](TEST_REPORT.md), [limitations](KNOWN_LIMITATIONS.md) and [compatibility](COMPATIBILITY.md).
- [Report a bug](https://github.com/alrayat/CloudSSD/issues/new/choose) with the release version, Windows version, provider, steps and sanitized diagnostics. Do not attach tokens, client secrets or private settings.
- [Security guidance](SECURITY.md).

The owner has not supplied new product license terms; [LICENSE](LICENSE) records this status. Required attribution and rights for included source remain in [the source notice](licenses/Source-MIT-NOTICE.txt). Dependencies retain their own licenses in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `licenses/`. Public source availability does not replace these terms.
