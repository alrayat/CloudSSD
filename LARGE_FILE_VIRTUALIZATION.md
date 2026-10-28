# CloudSSD large-file storage

CloudSSD's TeraBox configuration can use rclone's chunker overlay, enabled by default with 1 GiB parts. Each part uses cloud quota and must fit the provider's object limit; this does not create extra quota or remove account restrictions. Confirm provider terms for your use.

## Layout and operations

- Large logical files are stored as numbered parts plus metadata. Windows sees one ordinary file with its original size.
- Small/native files remain readable. An interrupted upload does not become a complete logical file before metadata is committed.
- Range reads are routed to the appropriate part; boundary reads are split. The bridge validates the response range, length and total before returning bytes.
- The default part size is configurable and bounded below the provider object limit. Upload/retry can need temporary space for a part.
- Import on a chunked drive uses its logical filesystem; the native unchunked bridge still refuses oversized objects.

Real-account large-file acceptance and commercial gameplay have not been verified for the current release. Do not treat fixture coverage as provider acceptance or a storage-plan promise. [Tests](TEST_REPORT.md), [TeraBox setup](TERABOX_INTEGRATION.md) and [limitations](KNOWN_LIMITATIONS.md) record the boundaries.
