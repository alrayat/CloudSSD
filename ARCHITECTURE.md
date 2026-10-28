# CloudSSD architecture

CloudSSD is a WPF/.NET 10 desktop application using CommunityToolkit.Mvvm. The desktop app and command-line tools share an `AppHost`; a background agent supervises permanent drives. rclone supplies provider backends and sparse VFS caching, while WinFsp supplies the Windows filesystem driver.

## Components

| Layer | Responsibility |
| --- | --- |
| Desktop | Eight WPF pages, dialogs, shared theme, charts, navigation and window controls |
| Application host | Settings, accounts, mount/engine wiring, metrics, imports, preparation and completion actions |
| Core | Models, units, active paths, atomic settings, disk information and read attribution |
| Mount | Driver discovery, protected config, OAuth, arguments, RC requests, lifecycle, adoption, logs and safe shutdown |
| Cache | Native range index, physical allocation, budgets/reserve, pinning and guarded maintenance |
| Streaming | Read profiles, predictors, priority queue, bandwidth-aware scheduling and prefetch accounting |
| Games | SQLite library, folder/PE scanning, compatibility, execution modes, process monitoring and diagnostics |
| Providers | Provider descriptors, capability/support boundaries and setup information |
| TeraBox | Official/website API clients and authenticated loopback WebDAV bridge |
| Diagnostics | Dependency checks, redaction, DPAPI and sanitized exports |
| Agent | Current-user background ownership, startup registration, retry and handoff |
| Test fixtures | Local HTTP/API fixtures, fault injection, deterministic simulation and demo workload |

## Data flow

```text
Desktop / CLI -> AppHost -> mount, cache, streaming, game and provider services
                              |
                              v
                         rclone process
                              |
                              v
                     WinFsp drive letter
                              |
                    sparse cache on NTFS
                              |
                   cloud provider / bridge
```

`MountAsync` checks disk safety, builds supported arguments, starts rclone, waits for both drive/RC readiness and persists ownership state. The host adopts a valid existing mount after restart. Health checks provide bounded crash retry. Planned unmount checks native open files and pending uploads before shutdown.

Read telemetry comes from native VFS logs, with offset, length, cache verdict and wait. Prefetch reads are attributed by range/time window. Profiles and predictor statistics live in SQLite. The scheduler prioritizes foreground reads and confidence-gates background requests; it pauses under disk pressure.

Optimize & Prepare scans the game, checks capacity/compatibility, measures the connection, prepares essentials and learned data, then checks native cache coverage. Cold or unknown data is not declared ready without evidence. Cloud Direct runs from the drive, Hybrid copies essentials and links large files, and Full Local copies the whole game locally. Launcher ownership checks remain active.

## Desktop and persistence

The shared theme and window frame provide the CloudSSD identity. WPF `WindowChrome` implements native drag, double-click maximize, system menus and resizing; `SystemCommands` implement minimize/maximize/restore/close. Shutdown remains connected to the application's mount/agent flow.

Settings and mount state use atomic JSON. Library/profiles use SQLite WAL. Credentials and RC/bridge secrets use current-user DPAPI. Logs and diagnostics live in the active data folder. Use **Settings → Open data folder** instead of assuming a path. Existing configured data, cache and cloud locations stay usable.

The `CloudSSD.Agent.exe` supervisor runs a worker hosting AppHost without UI. A current-user-only pipe coordinates status/release/stop. Permanent mounts use a fixed letter and retry when network/letter availability changes. HKCU sign-in startup honors disabled Windows entries. See [PERSISTENCE.md](PERSISTENCE.md).

TeraBox requests pass through an authenticated loopback bridge to the real configured transport. Large-file chunking is an optional overlay; all parts count against quota. Imports hash, upload, verify and publish a completion manifest last. Test fixtures do not replace production requests.

See [native design](docs/NATIVE_RCLONE.md), [cache design](CLOUD_CACHE_ENGINE.md), [security](SECURITY.md) and [current verification](TEST_REPORT.md).
