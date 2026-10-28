# CloudSSD user guide

CloudSSD gives cloud files a Windows drive letter and caches read data on a local NTFS disk. Your PC runs the game/application; the provider supplies uncached data. This preview preserves the existing game workflows. Browsers and arbitrary application installation have not been accepted as supported workflows.

## First run and accounts

Choose a cache folder with enough free space, a cache budget and a Windows free-space reserve. Enter your connection speed in **Mbps**; 70 Mbps is about 8.75 MB/s before overhead. Existing profiles retain saved settings rather than moving data for branding.

Open **Cloud Drives**. Connect Google Drive using [its setup guide](GOOGLE_DRIVE_SETUP.md), or choose the preferred official TeraBox flow with approved developer keys. The optional website flow is unofficial and carries its in-app warning; read [TeraBox integration](../TERABOX_INTEGRATION.md) before using it. Add a virtual drive and confirm it in File Explorer. TeraBox defaults to T: when available, and provider quota determines its reported capacity.

## Pages

| Page | What you can do |
| --- | --- |
| Dashboard | Review connection/storage metrics, setup checklist and warnings; navigate to account/drive work |
| Cloud Drives | Connect accounts; add, mount, open, refresh, unmount or remove drive definitions; choose permanent-drive behavior |
| Game Library | Search/add/import installed games; review compatibility; launch, prepare, fully cache, use local installation, edit settings or reset a profile |
| Game Accelerator | Inspect read telemetry, predictions, queued prefetch and foreground waits |
| Smart Cache | Choose budget/reserve/location, see physical vs logical use, pin/warm data and use guarded cleanup |
| Performance Monitor | Read actual throughput, hits/misses, read waits, history and totals; unavailable sources stay unavailable |
| Settings | Adjust network/prediction/profiling/startup/security/local paths and advanced VFS fields; click **Save settings** |
| Diagnostics | Check prerequisites, warnings and mount logs, and export a sanitized report for review |

Use **Ctrl+1–Ctrl+8** in page order. Search filters the actual game library. Settings can scroll while its Save action remains visible.

## Window controls

Drag unused title-bar space to move the window. Double-click it to maximize/restore. Use the minimize, maximize/restore and close buttons, or drag an edge/corner to resize. The native system menu provides the usual Windows commands; **Alt+F4** closes the window through the app's shutdown flow. Closing can keep permanent mounts running in the agent.

## Put an installed game in the cloud

Use **Game Library → Import installed game to cloud**. Select the existing local folder and destination drive, check capacity/compatibility, then import. The app hashes files, uploads, verifies and writes a completion manifest last. Resume an interrupted import instead of deleting the local copy or dirty cache. Full TeraBox verification downloads data again; size-only verification offers less evidence. See [installation modes](../GAME_INSTALLATION.md).

The import operation preserves the local folder. Any offered **Move local copy to Recycle Bin** action is separate, asks for confirmation, and can also move saves located inside that folder. Keep your local copy until the cloud game's exact workflow has been verified.

## Choose a launch/preparation mode

| Mode/action | Effect and requirement |
| --- | --- |
| Cloud Direct | Runs from the virtual drive; cold reads wait for cloud data |
| Hybrid Optimized | Copies essential small files locally and links large files to the drive; needs symbolic-link privileges/Developer Mode |
| Full Local | Downloads the entire game to local storage; launchers/ownership checks still apply |
| Optimize & Prepare | Fetches learned startup/working data within budget and checks native cache coverage before readiness |
| Fully cache | Downloads the whole game into the VFS cache; needs the whole game's disk footprint and is not a partial-cache saving |

Profile a launch to teach preparation. Unvisited content still streams on demand. A compatibility report is advisory; commercial gameplay and general application installs remain unverified. Game-Aware Mode needs an authorized integration, and no real plugin is currently verified.

## Cache, profiles and shutdown

The cloud drive's capacity is remote quota. Smart Cache's physical usage, staging, Hybrid copies and recovery data consume local disk. Dirty/open files can exceed an eviction budget temporarily; preserve the reserve and do not clear files waiting for upload.

**Settings → Open data folder** shows the actual profile location. Saved settings, credentials and mount ownership remain at the configured location; do not move or rename application-managed data manually.

For a drive to remain managed after closing, enable **Keep Cloud SSD Permanent**. A background agent adopts it and registers sign-in startup. An occupied permanent letter waits; a disabled Windows startup entry stays disabled. Real reboot/logon and sleep/resume are still unverified in this CloudSSD session; see [PERSISTENCE.md](../PERSISTENCE.md).

Before unmounting, close games and files on the drive and wait for uploads. If open files block unmount, close other readers/Explorer previews and retry. Do not kill rclone or remove dirty caches. The current fixture-import teardown issue is documented in [known limitations](../KNOWN_LIMITATIONS.md).

When something fails, start at **Diagnostics** and [troubleshooting](TROUBLESHOOTING.md). Inspect redacted exports before sharing; never post credentials or raw configs. [Security guidance](../SECURITY.md) explains the protected files and report workflow.
