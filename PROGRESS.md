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
| 2026-11-22 | 372e8c3 | Eval runner `starlight-eval` with 40 cases (skips cases for tools not built yet), scorer | Scorer and case files tested in the container. Not run with a model yet |
| 2026-11-22 | 14496be | App shell `Starlight.exe`: single instance, tray menu, Settings window (General, Voice, Model, Permissions, Diagnostics), report a problem, export diagnostics, start with Windows | Builds; `dotnet publish -r win-x64 --self-contained` works in the container (183 MB). Not run on Windows yet |
| 2026-11-22 | 99373de | Voice: push-to-talk, Silero VAD + whisper.cpp (Whisper.net, Vulkan), Kokoro through sherpa-onnx, sentence streaming, barge-in, stop words | Sentence splitting, speech text, audio conversion tested in the container. Microphone, whisper, Kokoro: built, not run |
| 2026-11-22 | 41a06d6 | Overlay: glowing orb with states, captions, pointer across monitors, ask card (also answered by voice), red control frame, hidden for full-screen apps | Geometry (two monitors, 125 %) tested in the container. Windows: built, not run |
| 2026-11-22 | 0aa417a | Desktop tools: `screen_look` (numbered controls + picture or OCR), `screen_find`, `screen_point`, `input_click`, `input_type`, `input_keys`, `window`; act-and-check; pictures go to the model once and are then dropped | Target parsing, key names, picture handling tested in the container. UI Automation, OCR, input: built, not run |
| 2026-11-22 | 299f1bd | Wake phrase: sherpa-onnx keyword spotting for any typed phrase ("Hey Starlight" by default), hands-free end of speech | Phrase-to-token logic tested in the container. Keyword spotting: built, not run |
| 2026-11-22 | e960420 | Chat window: conversations (search, pin, rename, delete), native Markdown, tool steps, attachments, Activity and Statistics views | Builds. Windows: not run |
| 2026-11-22 | 6b9e3cb | Memory: `memory_remember` (same topic replaces, old kept in history), `memory_recall`, `memory_forget` (asks); top 5 matching facts in each prompt; Memory view (search, delete); "Forget what was learned here" per conversation | 7 tests in the container (replace with history, FTS search, FTS syntax in requests, forget, forget a conversation, tools through the dispatcher, forget asks). 92 tests pass. Memory view: built, not run |
| 2026-11-22 | 9a62abc | Web: `web_search` (SearXNG, Brave key, DuckDuckGo), `web_read` (HTML to text; headless Edge for script pages and saved sign-ins), `web_research` (up to 6 sources, numbered, robots.txt), `web_login` (asks; password from Credential Manager, 2FA code by window or voice, cookies kept with DPAPI). Polite limiter (5/s overall, 1/s per site). Public web only. Injection guard: after page text, "Always" no longer skips Ask tools in that turn, and `memory_remember` asks. Settings > Web | 37 new tests in the container (limiter, robots, page text, three search parsers, address guard, sessions, excerpts, all four tools with a fake web and browser, the guard in the dispatcher, spoken codes). 129 tests pass. Edge, Credential Manager, DPAPI, the code window: built, not run |
| 2026-11-22 | bd9acc1 | Servers: `server_check` (DNS, ping, ports, HTTP, certificate end), `server_ssh` (SSH.NET; allow-listed commands without shell tricks run, others ask, "Always" there is only a yes; host key trusted on first use, then pinned; output marked as outside content), `server_list`, the watcher (says down and back up once each), Settings > Servers | 20 new tests in the container with a fake network and SSH. 149 tests pass. Real ping, TLS, SSH: built, not run |

## In progress
- P0/P1 on the PC: get llama-server (Vulkan) and 3-4 candidate models, run the eval as the model benchmark, then the P1 acceptance in the console ("open Notepad", Ctrl+Alt+J during `dev_wait 10`).

## Deviations from the plan doc (technical, same intent)
- Kokoro runs through sherpa-onnx, not KokoroSharp: one ONNX runtime for VAD, speech and the wake phrase (R6).
- The external model endpoint setting was removed (R6: one model runtime).
- Tool projects: `Starlight.Tools.Pc` holds `system_info` and `files_find` (not in the plan's project table).
- Memory uses FTS5 (bm25) only, no vector search and no expiry yet. Add embeddings only if the eval shows recall misses.
- The headless browser is the Edge that comes with Windows, driven over the DevTools protocol, not Playwright: Playwright's .NET package runs a Node.js driver process (R4 allows only the browser) and downloads its own Chromium.
- The design mock-up step of P0 is replaced by building the chosen look (glowing orb) directly; the user judges it in the beta.

## Open questions for the user (ask with the beta, or earlier if blocking)
- `notify` (plan P1) has no clear reason to exist yet: answers are spoken and shown anyway, and reminders are after 1.0. Not built (rule R2).

- Kokoro's English voices need espeak-ng data, which is GPL-3.0. It is data read by sherpa-onnx, not linked code, but it ships with Starlight. Is that fine, or should Starlight use a voice that needs no espeak data?

## Broken or untested
- `JobObject` is Windows-only and has no test yet; untested on the PC.
- `CrashHandler` and `Logging.Configure` have no tests yet.
- Nothing has run on the user's PC yet. No phase is accepted (rule R3).
- Everything Windows-only (app, voice, overlay, desktop tools, hotkeys) is built but has not run on the PC.
- Voice needs files in `runtime\voice` (silero_vad.onnx, kokoro model.onnx, voices.bin, tokens.txt, espeak-ng-data) and the whisper model; how they get there (installer or first-start download) is not done.
- Kokoro voice names map to speaker numbers by an assumed order (Kokoro v1.0); unverified.
- The installer (Velopack) is not packed yet; it needs the llama-server files from the PC.
- `screen_find` has UI Automation and OCR, not the vision step yet.
- The browser path (web_read with a sign-in, script pages) does not have the public-web check on redirects; plain HTTP does.
- `web_login` finds forms by common field names; sites with unusual sign-in pages may fail. Untested on real sites.
- Tool names are snake_case, not the plan's dotted names (llama-server needs that).
