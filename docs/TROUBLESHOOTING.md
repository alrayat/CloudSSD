# CloudSSD troubleshooting

Start with **Diagnostics** in the app: it lists dependency status, recovery suggestions, recent warnings/errors and
the rclone mount log. **Export sanitized report…** creates a ZIP you can share (tokens and secrets are redacted; the
rclone config is never included). Logs live in `<active data folder>\logs`.

| Symptom | Cause | What to do |
|---|---|---|
| "WinFsp is not installed" | WinFsp missing | Install the official package from <https://winfsp.dev/rel/> (`winget install WinFsp.WinFsp`), then restart CloudSSD. |
| Mount fails: "No free drive letter" | All letters D–Z used | Free a letter (disconnect a network drive or USB disk). |
| Drive letter differs from the one chosen | The letter was taken | Ordinary mounts may choose the next free letter; permanent drives wait for their fixed letter. Resolve the conflict or change the drive definition. |
| "Open files" / "Unmount pending" | Native open handles or pending uploads | Close games and other readers (including Explorer previews), wait for uploads and retry. The fixture-import teardown issue remains unresolved; do not force-kill rclone. |
| TeraBox website sign-in fails to open | WebView2 Runtime missing/unavailable | Check Diagnostics and the official Microsoft runtime. The SDK bundled with the app is not the browser runtime. |
| Browser/app installer fails on the cloud drive | General app installation is unverified | Install normally on the local disk; portable-app support must be tested individually. |
| "Cloud authentication required" | Google token expired/revoked; OAuth app in *Testing* mode expires tokens after 7 days | Reconnect the account (Cloud Drives). Check Google consent/test-user settings and current publishing requirements; reconnect if needed. |
| Frequent "rate limiting" warnings | Google Drive API quota | rclone retries automatically. Lower *API transactions per second* in Settings. |
| Games load slowly on first launch | Data is not cached; 70 Mbps ≈ 8.75 MB/s at best | Expected. Launch once to profile, then use **Optimize & Prepare** before playing. Check *Game Accelerator* for misses and wait times. |
| Read errors ("I/O device error") while offline | Uncached data cannot be fetched | Expected — CloudSSD never returns fabricated data. Cached data stays readable. After reconnecting, reads may fail for ~5 s more (rclone's handle cache), then recover. |
| Cache bigger than the budget for a while | rclone evicts only closed files, every poll interval | Normal (documented in Smart Cache). CloudSSD keeps a 10 % margin and pauses its own prefetching under disk pressure. |
| "Disk space low — prefetching paused" | Free space below the protected reserve | Free space, lower the budget, or move the cache to another NTFS drive (Smart Cache). |
| Hybrid mode: "Symbolic link failed" | Unprivileged symlinks need Developer Mode | Enable *Settings → System → For developers → Developer Mode*, or use Cloud Direct. CloudSSD never changes this setting itself. |
| Game exits immediately with 0xC0000135 | Missing DLL / redistributable | See the game's compatibility report for the missing package (VC++ / DirectX) and install it. |
| Steam game relaunches Steam or quits | Steamworks ownership/launch handling | Launch through Steam with a local install (Full Local). CloudSSD does not bypass launchers. |
| Anti-cheat game refuses to start | Kernel anti-cheat and virtual drives | Install locally through the official launcher. CloudSSD does not modify or bypass anti-cheat. |
| Extra downloads whenever a file is opened | Windows Defender scans the start and end of opened files | Measured during development (reads at offset 0 and the file tail on each open). Excluding the drive from real-time scanning is a security decision for you to make; CloudSSD does not change it. |
| Mount keeps restarting | rclone crashed (e.g. system out of memory/commit) | Auto-restart stops after 5 crashes in 10 minutes. Check the rclone log in Diagnostics. Cached data is preserved. |
| After a crash, cached files are downloaded again | rclone persists its range index only when files are closed cleanly | Known limitation: data is safe, only the index for files open during the crash is lost. |
| CloudSSD says another instance is running | Second copy or the `cloudssd-cli` CLI holds the data folder | Close the other instance. Two processes must not own the same cache. |

## Collecting rclone details manually

```bash
cloudssd-cli deps
```

```bash
cloudssd-cli remotes
```

The rclone mount log for each drive is `<active data folder>\logs\rclone-<drive-id>.log` (rotated at 64 MiB).


## Find the active profile

Use **Settings → Open data folder**; configured/portable/test locations may differ. Inspect sanitized reports before sharing and never attach configs, tokens or key files. See [security](../SECURITY.md), [test failures](../TEST_REPORT.md) and [limitations](../KNOWN_LIMITATIONS.md).
