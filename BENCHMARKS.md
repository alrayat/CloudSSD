# CloudSSD benchmark methodology

No new product throughput or commercial-game performance claim is established by the current release checks. Use reproducible measurements and distinguish live-provider runs from local fixtures and simulation.

## Measure comparable runs

Record OS, CPU/GPU, RAM, storage, driver version, provider/account plan, measured throughput/latency, cache budget, dataset and exact workload. Use the same route, settings and file versions for:

1. Local disk baseline.
2. Cold cloud drive without learned preparation.
3. Prepared cloud drive, recording preparation time and downloaded bytes separately.
4. Warm cache, recording its actual local disk footprint.

Report load time, read wait distribution, network bytes, cache hit/miss, physical allocation, memory/CPU and errors. For gameplay, include frame-time percentiles and stalls on the same route. A local fixture measures the stack under a controlled link; simulation measures a model, not a commercial game.

## Reproduce a controlled benchmark

From the source checkout, select its benchmark project and an existing SDK:

```powershell
$benchmarkProject = Get-ChildItem '.\benchmarks' -Recurse -Filter '*.csproj' |
    Where-Object { $_.BaseName -match '\.Benchmarks$' } |
    Select-Object -ExpandProperty FullName
if (@($benchmarkProject).Count -ne 1) { throw 'Choose exactly one benchmark project.' }
# Use the .NET 10 SDK already installed on this PC:
& "$env:USERPROFILE\.dotnet\dotnet.exe" run -c Release --project $benchmarkProject -- real --mbps 70 --latency 80
```

This command generates a fresh local benchmark; it does not establish provider speed or universal game support. Preserve raw outputs privately for verification and publish only measured, reviewed results. Never use paid resources or change live accounts without authorization.

See [performance interpretation](PERFORMANCE_BENCHMARKS.md), [current tests](TEST_REPORT.md) and [limitations](KNOWN_LIMITATIONS.md).
