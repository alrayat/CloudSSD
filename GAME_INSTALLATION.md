# CloudSSD game installation

CloudSSD manages installed game files and imports them into cloud storage. It is not a general Windows application installer. Browsers, services, registry-dependent installers and arbitrary desktop apps remain unverified on cloud drives.

## Import an installed game

Use **Game Library → Import installed game to cloud**:

1. Select the existing local folder, cloud drive and destination folder.
2. Check sizes, cloud free space, provider object limits and compatibility warnings.
3. Import: hash files with SHA-256, upload with resumable rclone operations, verify the cloud copy and write a completion manifest last.
4. Refresh the cloud folder and add the game to the real library.

The import preserves the local folder. An offered Recycle Bin action is separate and requires confirmation; saves inside that folder move with it. Keep the local copy until the exact cloud workflow has been verified.

Google Drive verification can use checksums/sizes. TeraBox size-only checks offer less evidence; Full verification downloads and compares data. Upload staging requires local disk space. Chunked TeraBox drives store large logical files in parts; the native unchunked remote still enforces object limits. See [large-file storage](LARGE_FILE_VIRTUALIZATION.md).

## Execution and updates

| Mode | Behavior |
| --- | --- |
| Cloud Direct | Launch from the virtual drive; cold asset reads wait for network data |
| Hybrid Optimized | Local essential files, symbolic links for large data; requires appropriate symlink privileges |
| Full Local | Download the whole game to local storage and use the normal launcher/ownership path |

Direct Steam/other launcher installation on cloud storage is not accepted as reliable support. Install locally and import appropriate games, or keep launcher-dependent games local. CloudSSD does not change launcher ownership, DRM or anti-cheat. Patch locally and import again; changed files/parts can be re-uploaded.

The current fixture import hashed and byte-verified ten files, resumed and launched the demo with exit 0. The overall test failed when four open files blocked teardown. No real-provider import or commercial game acceptance is claimed. See [compatibility](GAME_COMPATIBILITY.md) and [tests](TEST_REPORT.md).
