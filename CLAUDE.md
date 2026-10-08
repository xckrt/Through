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
dotnet run --project src/Tropa.Tray                            # WPF window, auto-runs a check on open
```

- Tests are **not** xUnit/NUnit. `tests/Tropa.SelfTest/Program.cs` is a plain console runner over a `List<(name, Func<Task>)>`, and Tuner/engine tests are appended from `TunerTests.All(opts)` and `EngineTests.All()`. It exits 1 on any failure. `dotnet test` does nothing useful and there is no filter flag, so to run one test, temporarily narrow the list. Add a test by appending a tuple to that list.
- CLI exit codes: 0 if every target is OK, 1 otherwise, 2 for a bad argument. The built binary is `tropa-probe`.

## Architecture

- **`Tropa.Core`**: platform-neutral, no Windows dependencies (Prober, Tuner, models). Keep Windows-only code (service, WinDivert/winws hosting, WPF tray) out of it.
  - `ServiceProber.ProbeAsync` runs one target through the stages via a private `Run` helper that tracks the current stage and timings. Each stage maps exceptions/timeouts to a `Failure` with `run.Fail(...)`. A result is a `ProbeResult` with `Verdict` (Ok/Throttled/Blocked/Inconclusive) + `Failure` + `FailedAt` stage + a human-readable `Detail`.
  - A transfer that stalls inside `FreezeWindowMin..FreezeWindowMax` (8–32 KB of body) gets tagged as the TSPU "16 КБ" signature in `Detail`. Tests assert on that substring.
  - `ProbeRunner` probes targets in parallel (default 4). If **all** `IsControl` targets fail, every non-OK result is rewritten to `Inconclusive` (no network, so it isn't a block).
  - `TargetCatalog` loads `targets.json`: services → target lists, camelCase, comments and trailing commas allowed. `Select(only)` always adds the control targets.
  - `ProbeOptions` holds all the knobs. `Resolver`, `AllowPrivateAddresses` and `ValidateCertificate=false` exist so tests can run against loopback.
  - When TLS fails (`TlsReset`/`TlsTimeout`), the prober reconnects to the same IP with `ProbeOptions.NeutralSni` (`ya.ru`) and sets `ProbeResult.SniFiltered`: `true` means the filtering is by name, `false` means all TLS to that IP is dead.
  - `Tuning/`: `Tuner` diagnoses without bypass, then for each service tries only the `presets.json` presets whose `Treats` matches the diagnosis, ordered by `Cost`. Each preset is probed twice and the worst result per target counts (`TuneScore`). It stops early when every target is OK and skips services blocked by IP (`PlanStatus.NotTreatable`). The engine is behind `IEngineHost`/`IEngineSession`. No real winws host exists yet; it belongs in a Windows-specific project, not Core. `TuningProfile` is `profile.json`.
  - `Engine/`: `EngineManifest` (`engine.json`: version, pinned sha256, mirror URLs) and `EngineFetcher` (download → verify hash → zip-bomb and `../` checks → atomic extract under `%ProgramData%\Tropa\engine\<version>`). It fails closed while the manifest is empty.
- **`Tropa.Engine`** (`net8.0` on purpose, so cross-platform tests can reference it): `WinwsEngineHost` writes a temp hostlist, starts winws, and waits for `ReadyMarker` on stdout or stderr. On dispose it kills the process tree. Tests point `ExecutablePath` at the SelfTest binary itself with `--fake-winws ready|crash|silent`, which `EngineTests.FakeWinwsAsync` handles at the top of `Program.cs`.
- **`Tropa.Cli`**: thin arg parsing + text/JSON output over Core. `--plan` prints the Tuner's candidate list without starting an engine. `--tune` lives in `TuneCommand.cs`. It needs admin and refuses if a `winws` process is already running. `targets.json`, `presets.json` and `engine.json` are copied to the output directory and can be edited without rebuilding. SelfTest links `presets.json` and validates it.
- **`Tropa.Tray`** (WPF, `net8.0-windows`, binary `Tropa.exe`): a diagnostics window. With no service yet, it calls Core in-process through `MainViewModel`. The constructor takes `ProbeOptions` and a data directory, so it can run against fake servers. `ProbeRunner`'s `IProgress<ProbeResult>` lets rows fill in as results arrive. `TrailView` is the custom-drawn five-stage "trail" (DNS → TCP → TLS → HTTP → передача) that breaks at the failing stage. Its node centres line up with the stage labels through the shared `NameColumn`/`TrailColumn` widths in `App.xaml`. All colours and fonts are resources in `App.xaml`, and the plain-language failure text is in `Wording.cs`. There are no UI tests.
- **`Tropa.SelfTest`**: `TunerTests.cs` uses `FakeEngineHost`. While a preset is "running", `FakeServer.SniFilter(..., bypassed: engine.AllowConnection)` lets connections through when `passes(presetId, connectionNo)` returns true, which also covers flaky presets. `FakeServer` stands up a local TLS server that mimics each blocking behavior (RST after ClientHello, silence, a stalled body, a 451 or block-page redirect). Each test checks that the prober reports the expected `Verdict/Failure/Stage`. No internet needed.

The `Failure` → strategy mapping table in `docs/architecture.md` drives the design: Tuner tries the `winws` presets that treat the diagnosed failure first, instead of brute-forcing all of them. When you add a `Failure` value, update that table and the `treats` lists in `presets.json`.

## Conventions

- Code comments, XML docs, CLI output and `Detail` strings are in **Russian**. Keep it that way.
- No binaries in the repo. `winws`/WinDivert must be downloaded at runtime from official zapret releases with SHA-256 verification (the planned Fetcher).
- Known Prober gaps (in the docs): no QUIC/UDP (YouTube QUIC, Discord voice), DNS spoofing is caught only for obvious bogus addresses (no DoH cross-check), and HTTP is HTTP/1.1 with `Connection: close`.
- Windows quirk: connecting to a closed loopback port takes ~2 s (Windows retries the SYN after RST), so test `ConnectTimeout` is 5 s.
