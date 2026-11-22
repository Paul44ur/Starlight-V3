# Progress log (rule R8)

Updated every few implementations. "Done" means a check ran; the check is named. Read this
before claiming anything is finished, and check the code too.

## Done and verified
| Date | Commit | What | Check that ran |
| --- | --- | --- | --- |
| 2026-11-22 | (this commit) | Solution skeleton: `Starlight.Core`, `Starlight.Model` (empty), `Starlight.App` (template), tests | `dotnet build` of Core and tests, Linux container |
| 2026-11-22 | (this commit) | Core: settings store, paths, Serilog logs with secret masking, crash handler, session trace, problem reports, diagnostics zip, event hub, kill switch, tool attribute, registry, dispatcher (permissions, approval, timeout, audit), SQLite store, agent turn loop, Job Object wrapper | 27 xUnit tests pass in the container (`dotnet test --project tests/Starlight.Core.Tests`) |

## In progress
- P0: `ModelHost` (llama-server in a Job Object), a developer console, the eval runner.

## Broken or untested
- `JobObject` is Windows-only and has no test yet; untested on the PC.
- `CrashHandler` and `Logging.Configure` have no tests yet.
- Nothing has run on the user's PC yet. No phase is accepted (rule R3).
- `Starlight.App` is the empty WPF template.
