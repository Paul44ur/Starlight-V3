# Progress log (rule R8)

Updated every few implementations. "Done" means a check ran; the check is named. Read this
before claiming anything is finished, and check the code too.

## Done and verified
| Date | Commit | What | Check that ran |
| --- | --- | --- | --- |
| 2026-11-22 | 6af9d0d | Solution skeleton: `Starlight.Core`, `Starlight.Model` (empty), `Starlight.App` (template), tests | `dotnet build` of Core and tests, Linux container |
| 2026-11-22 | 6af9d0d | Core: settings store, paths, Serilog logs with secret masking, crash handler, session trace, problem reports, diagnostics zip, event hub, kill switch, tool attribute, registry, dispatcher (permissions, approval, timeout, audit), SQLite store, agent turn loop, Job Object wrapper | 27 xUnit tests pass in the container (`dotnet test --project tests/Starlight.Core.Tests`) |
| 2026-11-22 | d5e2a02 | `ModelHost`: llama-server child process, health wait, restart 3 times in 5 min, no restart for a model that never loaded | Tests against a Python stand-in server in the container. Not run with the real llama-server yet |
| 2026-11-22 | b74f7fa | Tools `app` (open, focus, close asks, list), `system_info`, `files_find`; global hotkeys; developer console `starlight-dev` | `system_info`, `files_find`, app-name matching and hotkey parsing tested in the container. `app`, hotkeys and the console are Windows-only: built, not run |
| 2026-11-22 | (next) | Eval runner `starlight-eval` with 40 cases (skips cases for tools not built yet), scorer | Scorer and case files tested in the container. Not run with a model yet |

## In progress
- P0/P1 on the PC: get llama-server (Vulkan) and 3-4 candidate models, run the eval as the model benchmark, then the P1 acceptance in the console ("open Notepad", Ctrl+Alt+J during `dev_wait 10`).

## Open questions for the user (ask with the beta, or earlier if blocking)
- `notify` (plan P1) has no clear reason to exist yet: answers are spoken and shown anyway, and reminders are after 1.0. Not built (rule R2).

## Broken or untested
- `JobObject` is Windows-only and has no test yet; untested on the PC.
- `CrashHandler` and `Logging.Configure` have no tests yet.
- Nothing has run on the user's PC yet. No phase is accepted (rule R3).
- `Starlight.App` is the empty WPF template.
- Tool names are snake_case, not the plan's dotted names (llama-server needs that).
