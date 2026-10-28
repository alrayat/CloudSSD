# CloudSSD performance interpretation

CloudSSD's cache serves data already stored locally. Cold reads still depend on provider throughput, latency and retries. For example, 70 Mbps is at most 8.75 MB/s before overhead; that is a unit conversion, not a measured CloudSSD speed guarantee.

Preparation moves downloads ahead of launch and uses local storage. Include that preparation time and footprint when comparing a prepared run with a local installation. A warm cache or verified local source is not evidence of cold cloud-only performance.

The current verification establishes build, unit, UI/window and selected fixture behavior. It does not establish commercial gameplay or live-provider performance. Follow [benchmark methodology](BENCHMARKS.md), report actual test conditions and keep failed/unverified runs visible in [TEST_REPORT.md](TEST_REPORT.md).
