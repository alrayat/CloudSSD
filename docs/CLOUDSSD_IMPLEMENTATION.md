# CloudSSD desktop implementation

CloudSSD uses WPF, .NET 10 and CommunityToolkit.Mvvm. Its visual identity combines an ivory workspace, graphite navigation, evergreen actions, restrained lime highlights, Segoe UI Variable typography, Fluent icons and a stacked-storage mark.

## Interface inventory

| Screen | Services and interactions |
| --- | --- |
| Dashboard | Connection/storage metrics, checklist, warnings and summaries |
| Cloud Drives | Real account connection, drive creation/mount/open/refresh/unmount/remove, permanence and recovery |
| Game Library | Search/add/import, compatibility, launch, prepare/full cache, local installation, settings and profile reset |
| Game Accelerator | Read telemetry, prediction, queue and activity |
| Smart Cache | Budget/reserve/location, usage, cleanup, pin/warm and cached-file inspection |
| Performance Monitor | Throughput, cache/read metrics, waits and histories |
| Settings | Network, prediction, profiling, startup/security, local folders, native tuning and persistence |
| Diagnostics | Dependencies, recovery suggestions, log filtering and sanitized export |

Owned dialogs cover First Run, Add Drive, Add Game, Google connection, TeraBox connection/website sign-in, Game Settings, Full Local, Import, Optimize & Prepare and Completion Actions. Confirmations use the shared theme. The UI retains AppHost, real bindings, provider requests and persisted state.

## Window behavior

Native WPF WindowChrome provides dragging, double-click maximize, system menus and resize borders. SystemCommands provide minimize/maximize/restore/close. Icons, tooltips and accessibility state track maximize/restore. Initial sizing respects the Windows work area. Close follows the application's mount/agent shutdown flow.

Ctrl+1 through Ctrl+8 selects pages. Library search filters the actual stored collection. Settings retains its Save action while scrolling; the shared style covers fields, buttons, lists, tabs, scrollbars, focus, progress and tooltips.

## Verification

[TEST_REPORT.md](../TEST_REPORT.md) records checks against this exact source and the remaining acceptance boundaries.
