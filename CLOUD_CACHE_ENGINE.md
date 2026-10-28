# CloudSSD cache engine

The same engine serves every drive (Google Drive, TeraBox, others through rclone). Details of each component are in
[ARCHITECTURE.md](ARCHITECTURE.md). This page summarizes behaviour and guarantees.

## Storage

- **Block-level sparse cache** (rclone VFS, `--vfs-cache-mode full`) on an NTFS volume you choose. Only the byte ranges a
  game reads are stored. Explorer shows logical sizes; CloudSSD measures the **physical** size of sparse files
  (`GetCompressedFileSizeW`) and rclone's per-file range index.
- **Budget**: presets 10 / 20 / 30 / 40 GB or custom, plus a **minimum free space reserve** for Windows (default 8 GB).
  Effective budget = min(requested, current usage + free space − reserve), minus a 10 % margin, split across active
  drives.
- **Eviction**: least recently used, unmodified data first. rclone never evicts open files or data still waiting for
  upload (dirty data), so evicting cannot corrupt an active write. Cleanup runs every 30 s, so usage can briefly
  exceed the limit.
- **Pinning** keeps chosen folders recent (best effort).
- **Never deleted by cleanup**: cloud originals, files waiting for upload, and save games (cache cleanup only touches
  folders carrying CloudSSD's cache marker).

## Reads and prediction

| Priority | Source | Behaviour |
|---|---|---|
| P0 | The game's own read | Never competes with prefetch: background work pauses while foreground reads wait |
| P1 | Strong predictions (learned startup set, just-in-time during launch, confidence ≥ 0.7) | Released 10 s ahead of game progress |
| P2 | Frequent files / Markov transitions | Confidence-gated |
| P3 | Sequential read-ahead | Low priority, budget-limited |

- Telemetry: every read's offset, size, hit or miss and wait time from rclone's debug log; CloudSSD's own prefetch
  reads are separated by byte range and time window.
- Predictors with less than 25 % accuracy after 64 samples are disabled automatically.
- Concurrency follows the measured bandwidth-delay product; more parallel downloads are not assumed to be faster.
- **Optimize & Prepare** downloads the learned startup set before launch and says *Ready to launch* only after
  rclone's own range index confirms the data is cached.
- Universal mode uses only file access patterns. It does **not** know where the player is in the game world.
  Game-Aware mode accepts region telemetry from authorized game plugins (none exist yet).

## Writes

Write-back caching (5 s). A file is uploaded after it is closed and settles. Pending uploads survive restarts (rclone
re-uploads dirty cache files), and planned unmount checks external open handles and waits for uploads to finish (up to 60 s). On TeraBox the bridge stages
each file and uploads it in 4 MiB shards with per-shard MD5 confirmation.

## Offline

On an already mounted drive, cached ranges can stay readable without internet. Uncached ranges fail after retries.
Fixture tests check byte integrity; they do not prove every failure mode. A fresh mount still needs the provider reachable.

## Refresh

Files added to the cloud by other programs appear after **Refresh listing**, which uses a recursive `vfs/refresh`
to re-read listed folders. Imports refresh only the affected folder.

## Modified driver and verification limits

CloudSSD uses a modified rclone v1.75.2 driver: deferred hydration moves partly cached dirty-file completion into write-back, while durable metadata and hashed local recovery snapshots protect staging data. Recovery copies/staging consume local space beyond ordinary cache eviction targets. They are not automatically applied over Steam or silently deleted. [Native details](docs/NATIVE_RCLONE.md) describe the native safety mechanisms.

Offline and integrity statements refer to bounded fixture tests, not every provider failure. Current source and native revision `.2` pass TeraBox import and guarded teardown while preserving active-reader and mapped-file guards. CloudSSD 1.0.1 includes these fixes. See [current evidence](TEST_REPORT.md).
