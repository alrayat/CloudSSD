# CloudSSD native drive engine

This directory contains the patch and supplemental source for the modified rclone v1.75.2 drive engine. Upstream source is [rclone v1.75.2](https://github.com/rclone/rclone/tree/v1.75.2), licensed under MIT.

See [native design, build and verification](../../docs/NATIVE_RCLONE.md) for deferred hydration, durable metadata, open-file safeguards, recovery snapshots and current test boundaries. Preserve upstream `COPYING` and identify the binary as a modified build. The release script enforces the pinned native identity/checksum.
