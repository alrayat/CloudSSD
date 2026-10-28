# CloudSSD verified local sources


Cloud files still need to reach the PC before the CPU/GPU can use them. A mounted drive cannot turn a
2 KB/s cloud download into an SSD. Full VFS caching avoids repeat downloads, but the first read of
uncached game code or assets can still block Windows and Steam.

CloudSSD supports an optional source of **already-existing local files** beneath the WebDAV bridge.
It does not remove the local originals, make an additional complete installation, or claim cloud-only
performance. The bridge checks the cloud object's size and checksum before serving local bytes.
Rclone still sees the same cloud object, processes chunker metadata, and maintains its own bounded cache.
Writes always follow the normal cloud upload path; the local source is read-only.

## Configuration

Create `terabox-local-sources.json` in the active data folder (open it from Settings), then restart the bridge:

```json
[
  {
    "remotePrefix": "SteamLibrary/steamapps/common/Example",
    "localRoot": "G:\\SteamLibrary\\steamapps\\common\\Example",
    "chunkSize": 1073741824
  }
]
```

Use the bridge-relative cloud folder and the existing local folder. Chunk size must match the
rclone chunker remote. Do not point a source into a cloud mount, network share, or unstable disk.
The CLI rejects configured mount letters; junctions, symlinks, traversal, and alternate streams are rejected.

The implementation supports ordinary files and default rclone chunk names
`name.rclone_chunk.001` (including transaction suffixes). For large objects it checks either the full
MD5 or the website upload checksum: MD5 of the compact JSON array of 4 MiB shard MD5s.
Other shard sizes/custom chunk naming fall back to cloud reads. An absent checksum, mismatch, missing
file, or unreadable file also falls back. It never treats matching sizes alone as proof.

The initial access hashes the entire physical object/chunk from the local source, so that first access
can still take seconds on an HDD. Verification is serialized to avoid competing hash scans and is
remembered by path, offset, size, checksum, local length, creation time, and modification time.
Already verified reads bypass the hash queue so an unrelated large scan cannot stall them.
The CLI persists those verification records in `terabox-local-verification.json`; it contains metadata,
not game contents or credentials. Every reuse checks the open file's length and timestamps against the
saved record, as well as the current cloud checksum. Changes invalidate the record; a missing or corrupt
cache triggers verification again. This is a performance cache for a trusted local source, not protection
against someone deliberately modifying a file while forging its timestamps. The open source handle
denies concurrent writes/deletes while it is verified and served.

## Slow-cloud behavior

Two slow upstream windows in one request open a 30-second cooldown. A 90-second progress deadline also
cuts off stalled headers, reads, and repeated resumes. Healthy transfer windows renew the deadline,
allowing large downloads to finish. New uncached reads during cooldown return HTTP 503 with Retry-After;
already-started responses are closed, never padded with fake bytes. A retry after cooldown probes again.
Metadata, existing VFS cache hits, zero markers, and verified local sources remain available.

The bridge reports `providerThrottled`, `retryAfterSeconds`, `localSourceCount`, `localSourceHits`, and
`localSourceBytes` through its authenticated loopback status endpoint. Status no longer waits for a provider API
call. The CLI (`cloudssd-cli agent status`) and Cloud Drives view show slow-cloud status and local bytes served.
The flag means observed slow/timed-out reads, not proof of an account restriction.

Rclone and games have their own retries; a per-request deadline is not a guaranteed end-to-end launch
deadline. A game that requires uncached data can fail with an I/O error. This makes failures bounded at
the bridge, but does not make a throttled provider playable.

## Validation

Unit coverage includes byte-exact full/chunk reads, multipart checksums, changed/mismatching sources,
unsafe paths, persistent trickles, truncated responses, cooldown, and recovery. The chunked-drive
integration test uses real rclone/WinFsp with a mock provider and a fresh cache, including a read
across a chunk boundary using verified local originals.

Official rclone references: [VFS caching](https://rclone.org/commands/rclone_mount/#vfs-file-caching)
and [chunker](https://rclone.org/chunker/). Real-account launch results should be recorded separately
from mock test results; local-assisted success is not proof of cold cloud-only performance.
