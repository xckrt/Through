# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Tropa is a Windows tool for network-reachability diagnosis and automatic DPI-bypass tuning (it will drive bol-van/zapret's `winws.exe`). Right now only the core exists: a **prober** that checks a service stage by stage (DNS → TCP → TLS → HTTP → transfer) and names *how* the connection is being blocked (SNI RST, silent drop, the ~16 KB "TSPU freeze", DNS spoofing, throttling). The full plan (Tuner, EngineHost, Fetcher, Watchdog, Service, Tray) is in `docs/architecture.md`. Read it before starting a new module.

## Commands

Requires the .NET 8 SDK. Run from the repo root:

```
dotnet build
dotnet run --project tests/Tropa.SelfTest                      # all tests
dotnet run --project src/Tropa.Cli -- --only youtube,discord   # real probe (needs internet)
dotnet run --project src/Tropa.Cli -- --json --targets path/to/targets.json
```

- Tests are **not** xUnit/NUnit. `tests/Tropa.SelfTest/Program.cs` is a plain console runner over a `List<(name, Func<Task>)>`. It exits 1 on any failure. `dotnet test` does nothing useful and there is no filter flag, so to run one test, temporarily narrow the list. Add a test by appending a tuple to that list.
- CLI exit codes: 0 if every target is OK, 1 otherwise, 2 for a bad argument. The built binary is `tropa-probe`.

## Architecture

- **`Tropa.Core`**: platform-neutral, no Windows dependencies (Prober and models now, Tuner planned). Keep Windows-only code (service, WinDivert/winws hosting, WPF tray) out of it.
  - `ServiceProber.ProbeAsync` runs one target through the stages via a private `Run` helper that tracks the current stage and timings. Each stage maps exceptions/timeouts to a `Failure` with `run.Fail(...)`. A result is a `ProbeResult` with `Verdict` (Ok/Throttled/Blocked/Inconclusive) + `Failure` + `FailedAt` stage + a human-readable `Detail`.
  - A transfer that stalls inside `FreezeWindowMin..FreezeWindowMax` (8–32 KB of body) gets tagged as the TSPU "16 КБ" signature in `Detail`. Tests assert on that substring.
  - `ProbeRunner` probes targets in parallel (default 4). If **all** `IsControl` targets fail, every non-OK result is rewritten to `Inconclusive` (no network, so it isn't a block).
  - `TargetCatalog` loads `targets.json`: services → target lists, camelCase, comments and trailing commas allowed. `Select(only)` always adds the control targets.
  - `ProbeOptions` holds all the knobs. `Resolver`, `AllowPrivateAddresses` and `ValidateCertificate=false` exist so tests can run against loopback.
  - When TLS fails (`TlsReset`/`TlsTimeout`), the prober reconnects to the same IP with `ProbeOptions.NeutralSni` (`ya.ru`) and sets `ProbeResult.SniFiltered`: `true` means the filtering is by name, `false` means all TLS to that IP is dead.
- **`Tropa.Cli`**: thin arg parsing + text/JSON output over Core. `targets.json` is copied to the output directory and can be edited without rebuilding.
- **`Tropa.SelfTest`**: `FakeServer` stands up a local TLS server that mimics each blocking behavior (RST after ClientHello, silence, a stalled body, a 451 or block-page redirect). Each test checks that the prober reports the expected `Verdict/Failure/Stage`. No internet needed.

The `Failure` → strategy mapping table in `docs/architecture.md` drives the design: the planned Tuner tries the `winws` presets that treat the diagnosed failure first, instead of brute-forcing all of them. When you add a `Failure` value, update that table.

## Conventions

- Code comments, XML docs, CLI output and `Detail` strings are in **Russian**. Keep it that way.
- No binaries in the repo. `winws`/WinDivert must be downloaded at runtime from official zapret releases with SHA-256 verification (the planned Fetcher).
- Known Prober gaps (in the docs): no QUIC/UDP (YouTube QUIC, Discord voice), DNS spoofing is caught only for obvious bogus addresses (no DoH cross-check), and HTTP is HTTP/1.1 with `Connection: close`.
- Windows quirk: connecting to a closed loopback port takes ~2 s (Windows retries the SYN after RST), so test `ConnectTimeout` is 5 s.
