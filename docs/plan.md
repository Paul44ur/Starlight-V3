
# Starlight V3: implementation plan

> Copy of the plan doc (https://claude.ai/artifact/6JTZTbV3tYUa6a76RSTvQc), rev 75, 22 Nov 2026. The doc is the source; this copy is for the coding agent.

2026-11-22 · 

## 1. Goal

Starlight V3 is a Jarvis-like assistant that does what the user asks on their own PC, locally. It is one native Windows program: no browser, no Python, no terminal. You talk to it first, and a chat window comes second.

The user's words (22 Nov 2026): "a true assistant that could just do anything I asked of it on my device locally", "a truly real program, it doesn't use your browser, it's one separate thing", "first come voice and overlay, then the chat window", "a clean rewrite from the ground up".

**V3 1.0 does what V2's first plan (6 Oct 2026) promised, and nothing else:**

1. Hears and answers by voice, in English, with a push-to-talk key and a wake phrase the user can set (default "Hey Starlight").
2. Shows itself as an overlay on the screen: an orb, captions, a line and frame that point at things, and Yes/No cards.
3. Sees the screen and uses mouse, keyboard and apps for you.
4. Runs a local model on the RX 9060 XT. No account, no cloud.
5. Remembers what you tell it, and you can see and delete it.
6. Uses the web: search, read pages, log in to your accounts.
7. Checks and talks to your servers (ping, HTTP, SSH).
8. Writes and tests small programs in a sandbox, and learns skills you approve.
9. Asks before anything risky, and one key (Ctrl+Alt+J) stops everything.
10. Shows statistics: tasks, errors, model speed.

**What V3 is not (until ****the user**** agrees a feature in writing, see section 8):** a notes app, a to-do or calendar app, a recipe, train, podcast or flashcard tool, a security cockpit, a web server, a phone web app, or anything that reaches the user outside the PC (WhatsApp, Telegram, mail). The user decided on 22 Nov: 1.0 is the local agent only.

## 2. What went wrong in V2, and the rule for V3

V2 built all ten planned steps on its first day (6 Oct). Then it kept adding for seven weeks without testing on the real PC. By 21 Nov it had about 100,000 lines (29k core, 21k modules, 49k tests, 7k launcher), 61 modules and 361 tools.

| What happened in V2 | Evidence in the repo | Rule for V3 |
| --- | --- | --- |
| Features were added that nobody agreed beforehand | Recipes, trains, podcasts, flashcards, boards, an Obsidian-style notes app (35 commits), the Absolution security cockpit | **R1.** `FEATURES.md` is the contract. Nothing is built unless it is in that file and the user said yes. |
| Too many tools for a 14B model | 361 tools; tool routing, `tools.find`, a tool-use exam and "runaway answer" fixes were added as workarounds | **R2.** Every tool and feature needs a clear reason to exist. No duplicates, nothing useless or obsolete; one that stops earning its place is removed. A tool is only added if the eval suite (section 6) still passes with it. |
| Tested in a Linux container, not on the PC | The overlay's 9-step Windows test is still open; most fixes came from container runs | **R3.** A phase is done only when its acceptance test passed on the user's PC. |
| Not a real program | A Python FastAPI server, web pages in a WebView2 window, a 7,000-line launcher that downloads uv, Python, llama.cpp or LM Studio | **R4.** One installer, one `Starlight.exe`, native windows. The only child process is the model server. |
| The look was redone four times | plan-ui, Night Sky, Paper, then a different style for the overlay | **R5.** One visual design, agreed in phase 0 with a mock-up, then frozen until 1.0. |
| Many ways to do one thing | Ollama, llama.cpp, LM Studio, LAN servers; browser, app window, terminal, `--both` | **R6.** One way per job. One model runtime, one UI toolkit, one settings store. |
| Settings in YAML files the user edits | `config/modules/*.yaml`, `starlight.local.yaml` | **R7.** Every setting is in the Settings window. No file editing. |

**R8. A progress log for the coding agent (the user, 22 Nov).** `PROGRESS.md` in the repo records, every few implementations, what is done and verified (with the commit and how it was checked), what is in progress, and what is broken or untested. Before saying anything is finished, the agent reads it and checks the code. Nothing is marked done without a check that ran.

What V2 did well and V3 keeps as ideas (no code is copied): the single tool dispatcher with permissions, the kill switch with cleanup callbacks, the audit log, act-and-check after each desktop action, find-target order (UI Automation, then OCR, then vision), the overlay as a separate always-on-top layer, and "ask before clicking".

## 3. Decisions

V3 is written in C# on .NET 10 with WPF, for Windows 11 only. The model runs in a bundled llama.cpp server. The user left these to me and confirmed Windows 11 on 22 Nov.

| Area | Choice | Why | Not chosen, and why |
| --- | --- | --- | --- |
| Platform | Windows 11, 64-bit only | The user's PC. The overlay, UI Automation, input and audio are Windows APIs anyway. | Linux support: it doubled V2's code paths and tests. |
| Language and runtime | C# 14 on .NET 10 (LTS until Nov 2028) | Native Windows program, direct access to Win32, UI Automation, WinRT OCR and audio. Publishes as one self-contained folder, no runtime to install. Core libraries also build and test on Linux. | Python + Qt: still a bundled interpreter, slow start, virus-scanner false alarms. Rust: slow to write UI. Electron or Tauri: web pages, which the user ruled out. |
| UI toolkit | WPF, with the WPF-UI library for a Fluent look | Transparent, click-through, always-on-top windows are easy and well known. Mature, fast, works per monitor DPI. | WinUI 3: nicer controls, but transparent overlay windows and packaging are harder. |
| Model runtime | `llama-server.exe` from llama.cpp, Vulkan build, pinned version, shipped inside the app | Best AMD support on Windows (RDNA 4 via Vulkan), tool calling with grammars, vision with `--mmproj`, embeddings. A child process: a crash does not take Starlight down, and game mode can free the GPU by stopping it. | LM Studio and Ollama: a second program the user installs and keeps in sync. LLamaSharp in-process: a native crash kills the app. |
| Model | Chosen in phase 0 by measurement on the user's PC, not by guess. Candidates: one 12-14B text model and one 7-8B vision model (Qwen and Gemma families, current versions). | V2 picked a model first and wrote workarounds after. | A fixed pick now. |
| Speech to text | whisper.cpp through Whisper.net, Vulkan, model large-v3-turbo (English). Silero VAD to find the end of speech. | Local, accurate, runs on the same GPU. | Windows speech recognition: less accurate, no control. |
| Text to speech | Kokoro-82M through KokoroSharp (MIT; model Apache-2.0), English voices; Piper through sherpa-onnx as the fallback | Local, natural, fast on the CPU, streams per sentence. | Windows SAPI voices: robotic. |
| Wake word | Phase 6: sherpa-onnx open-vocabulary keyword spotting (Apache-2.0). Any phrase typed in Settings works without training; default "Hey Starlight" | Small, on the CPU, always listening without whisper, and the custom name the user asked for. | openWakeWord: needs training per phrase, and its ready-made models are non-commercial. Whisper on every snippet (V2's way): GPU load and false wakes. |
| Desktop | FlaUI (UI Automation 3), Windows.Graphics.Capture for screenshots, Windows.Media.Ocr, SendInput | All built into Windows. OCR reads English out of the box. | Tesseract: an extra install. |
| Storage | One SQLite file (`starlight.db`) for conversations, memory, stats, audit; Windows Credential Manager for secrets | One place, transactions, easy backup. | YAML and Markdown files spread over folders. |
| Web | HttpClient with a polite limiter; Playwright for .NET (headless Chromium, downloaded on first use) for script-heavy pages and logins | Reads like a browser, but the user never sees a browser. | A visible browser window. |
| Installer and updates | Velopack: one `Starlight-Setup.exe`, per-user install, no admin, updates from GitHub Releases | One file, delta updates, clean uninstall. | MSIX: certificate trouble without signing. A custom launcher: V2's 7,000 lines. |
| Logs | Serilog, JSON, rolling files, secrets masked | Standard, readable. |  |
| Tests | xUnit; core tests on Linux in the cloud; Windows tests and the eval on the user's PC | GitHub Actions minutes are used up, and only the real PC proves it works. |  |

## 4. Architecture

Starlight is one program. Every input goes through the agent core, and every action goes through its one tool dispatcher. The only other processes are the model server, the code sandbox and a headless browser. All three sit in a Windows Job Object and end when Starlight ends.

*(Architecture diagram: see the plan doc.)*

The overlay and the voice output never call tools. They only show and say what the core sends them as events.

**Projects in the solution** (`src/`):

| Project | Target | Holds |
| --- | --- | --- |
| `Starlight.Core` | net10.0 | Turn loop, tool attribute and dispatcher, permissions, kill switch, events, settings, database |
| `Starlight.Model` | net10.0 | `ModelHost` (llama-server), OpenAI-compatible client, streaming, embeddings |
| `Starlight.Voice` | net10.0-windows | Capture, VAD, whisper, Piper, wake word |
| `Starlight.Overlay` | net10.0-windows (WPF) | Overlay windows, geometry, animation |
| `Starlight.Tools.Desktop` | net10.0-windows | UI Automation, capture, OCR, input |
| `Starlight.Tools.Web`, `.Network`, `.Code` | net10.0 | One tool group each |
| `Starlight.Memory`, `Starlight.Skills` | net10.0 | Memory store and search, skills |
| `Starlight.App` | net10.0-windows (WPF) | `Starlight.exe`: tray, chat window, settings, first-start setup, wiring |
| `Starlight.Eval` | net10.0 | The eval runner and its cases |
| `tests/*` | as above | xUnit tests per project |

The `net10.0` projects build and test in the Linux cloud container. The `-windows` projects build there too (with `EnableWindowsTargeting`) but run only on the PC.

**Data on the PC:** the app in `%LocalAppData%\Starlight`; models in a folder the user picks; `starlight.db` and logs in `%AppData%\Starlight`; secrets in Windows Credential Manager.

## 5. Subsystems in detail

### 5.1 Agent core (`Starlight.Core`)

- **Request flow.** Input (voice or chat) becomes a `Turn`. The core adds the system prompt (date, time, language, user profile, the top memories), sends the conversation and the tools to the model, runs tool calls, and loops until an answer or `maxSteps` (default 8).
- **Tools.** A tool is a C# method with `[Tool("desktop.click", Permission.Ask)]` and a parameter record. The JSON schema is generated from the record. One `ToolDispatcher` runs every call: kill-switch check, permission, argument validation, timeout, audit entry, stats. No other code calls tools.
- **Tool budget.** All tools go to the model on every request, so there is no router to get wrong. There is no fixed cap; each tool must earn its place (R2). If the eval drops when a tool is added, the tools are merged or redesigned (for example one `app` tool with `action: open|close|focus|list`, not four).
- **Tool calls are constrained.** llama-server runs with `--jinja` and returns structured tool calls with a grammar, so the model cannot write a broken call or a call as plain text. No text parsing of tool calls, unlike V2.
- **Permissions.** Three levels: `Safe` (runs), `Ask` (Yes/No card on the overlay or by voice; "always for this tool" can be ticked in Settings), `Blocked`. Each tool has a fixed default. The user changes them only in Settings, at the PC.
- **Kill switch.** `RegisterHotKey` Ctrl+Alt+J, the tray menu, and the orb's menu. It cancels one root `CancellationToken`, releases held keys and mouse buttons, closes the headless browser, kills sandbox processes (they sit in a Job Object), hides the overlay's pointer and writes the audit log. It stays engaged until the user resumes it. Target: everything stops within 300 ms.
- **Context.** When a conversation fills 75 % of the context, the oldest turns are summarized by the same model. The summary is kept in the database.
- **Plugins.** Capabilities are separate assemblies (`Starlight.Tools.Desktop`, `.Web`, …) behind one `IToolProvider` interface, loaded at start. They never reference each other; they use `IToolCaller` to call another tool through the dispatcher. No hot reload (V2's never got used).

### 5.2 Model runtime (`Starlight.Model`)

- `ModelHost` starts `llama-server.exe` in a Job Object (it dies with Starlight), waits for `/health`, restarts it after a crash (at most 3 times in 5 minutes, then tells the user).
- Start flags: `-ngl 99 --flash-attn on --cache-type-k q8_0 --cache-type-v q8_0 -c 32768 --jinja`, plus `--mmproj` for vision. Exact context size comes from the phase 0 measurement.
- VRAM budget for 16 GB: model about 9 GB, KV cache about 2 GB, whisper about 1.5 GB, 2 to 3 GB left for Windows and the desktop. If the vision model does not fit next to the main model, the main model is chosen to be multimodal, or vision runs on request and unloads after.
- Streaming over the OpenAI-compatible API. First words are spoken while the rest is still being written.
- **Game mode.** When a game runs full screen (or a listed program starts), Starlight stops `llama-server` and whisper to free the GPU. It says it is paused if spoken to, and restarts the model when the game ends.
- Embeddings for memory come from a small multilingual model (for example multilingual-e5-small) run with ONNX Runtime on the CPU, so the GPU model is not interrupted.

### 5.3 Voice (`Starlight.Voice`), phase 3 and 6

- **Input.** WASAPI capture at 16 kHz mono. Push-to-talk hotkey first (hold Ctrl+Alt+Space). Silero VAD marks the end of speech after 600 ms of quiet.
- **Recognition.** whisper.cpp large-v3-turbo, English. Target: under 0.8 s for a 5-second sentence on the RX 9060 XT.
- **Speaking.** Kokoro through KokoroSharp. The answer is cut into sentences and each is spoken as soon as it is complete. Speaking while Starlight talks stops it (barge-in, with echo cancellation from Windows' communications mode).
- **Wake word** (phase 6). sherpa-onnx keyword spotting with the phrase from Settings (default "Hey Starlight"). Sensitivity slider. Runs on the CPU at under 2 % load. Can be turned off.
- **States** go to the overlay: idle, listening (with mic level), thinking, speaking (with the speech envelope), asking.
- **Target latency:** end of the user's sentence to the first spoken word, under 2.5 s for an answer without tools.

### 5.4 Overlay (`Starlight.Overlay`), phase 4

- One transparent WPF window per monitor, created from the monitor layout, rebuilt when monitors change.
- Window styles: `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, topmost. Clicks go through. No taskbar or Alt+Tab entry. It never takes focus.
- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`: the overlay is not in screenshots, so the vision model never sees its own drawing.
- Per-monitor DPI awareness v2 in the manifest. All positions in physical pixels, converted per monitor. Tested at 100 %, 125 % and 150 %, and with a monitor left of or above the main one.
- Parts: the **orb** (top centre of the main monitor, dim when idle, animates per voice state), the **caption** (what the user said, then the answer, fades after 6 s), the **pointer** (a line from the orb to a target and a frame around it; solid when UI Automation found it, dashed when OCR or vision guessed), the **ask card** (Yes/No; the only part that takes clicks, by switching off `WS_EX_TRANSPARENT` on its own small window), and the **red frame** round the screen while Starlight controls mouse and keyboard.
- The orb's right-click menu: open chat, kill switch, pause listening, settings, quit.
- Animation at 60 fps while active, 0 fps when idle. Target: under 1 % CPU at rest.

### 5.5 Desktop control (`Starlight.Tools.Desktop`), phase 5

Tools (8): `screen.look` (screenshot of a monitor or window, given to the vision model), `screen.find` (a target by name: UI Automation first, then OCR, then vision; returns a box and how it was found), `screen.point` (draws the pointer, Safe), `input.click` (Ask), `input.type` (Ask), `input.keys` (Ask, for shortcuts), `app` (open, close, focus, list; open is Safe, close asks), `window` (move, resize, minimize, arrange).

- **Act and check.** After each click or type, Starlight looks again and checks the expected change (a window opened, text appeared). If not, it tells the user and stops, not guess further.
- `input.*` refuses to run while the kill switch is engaged or the user is typing (input in the last 500 ms), and never types into password fields (UI Automation `IsPassword`).
- Everything runs as the logged-in user, never elevated.

### 5.6 Chat window (`Starlight.App`), phase 7

A normal WPF window, opened from the orb or the tray. Left: conversations (search, pin, delete). Right: the conversation, Markdown rendered natively (code blocks, tables, lists), tool steps shown as small expandable lines, Yes/No inline. A message box with attach (image, file). Further views in the same window: **Memory** (see, edit, delete), **Activity** (audit log, waiting approvals), **Statistics**, **Settings**. English UI, texts kept in `.resx` files. Light and dark follow Windows.

### 5.7 Memory (`Starlight.Memory`), phase 8

- Facts in SQLite with text, topic, source conversation, date, optional expiry, and an embedding. FTS5 plus vector search; the top 5 matching facts go into each prompt.
- After a turn the model may call `memory.remember`. A fact on an existing topic replaces the old one (kept in history).
- The user sees and deletes everything in the Memory view. "Forget this conversation" removes what was learned there.
- Tools (3): `memory.remember`, `memory.recall`, `memory.forget` (Ask).

### 5.8 Web (`Starlight.Tools.Web`), phase 9

Tools (4): `web.search` (SearXNG if the user runs one, else Brave Search API with a free key, else DuckDuckGo HTML), `web.read` (fetch, clean to text; Playwright if the page needs scripts), `web.research` (search, read up to 6 pages, write a short report with numbered sources), `web.login` (Ask; credentials from Credential Manager, 2FA code asked by voice or card, session cookies kept encrypted with DPAPI).

Limits: 5 requests per second overall, 1 per second per site, robots.txt for anything crawled, page text is marked as untrusted in the prompt (prompt-injection guard: page text can never trigger an `Ask` tool without the user's Yes).

### 5.9 Servers and network (`Starlight.Tools.Network`), phase 10

Tools (3): `server.check` (ping, port, DNS, HTTP, TLS expiry; Safe), `server.ssh` (SSH.NET; read-only commands from an allow-list run, anything else asks), `server.list` (the servers the user added in Settings). Optional watcher: checks every N minutes and tells the user when one goes down.

### 5.10 Code and skills (`Starlight.Tools.Code`, `Starlight.Skills`), phase 11

- `code.run` and `code.write`: Starlight writes a Python script, runs it and its tests in a sandbox, fixes and retries up to 3 times. Python is the embeddable build (about 10 MB) shipped inside the app, invisible to the user.
- Sandbox: a Windows AppContainer process (no network, no access outside a temp folder) inside a Job Object (memory, time, no child processes). Docker is not needed.
- Skills: when a multi-step task succeeds, Starlight may offer to save it as a skill (name, steps, parameters). Saved only after the user approves in the chat window. Approved skills appear as one tool `skill.run` with the skill name.

### 5.12 Statistics, logs, audit

Every turn, tool call, model call (tokens, tokens/s, time to first token), error and GPU reading (VRAM through DXGI) goes into SQLite. The Statistics view shows them. The audit log lists every tool call with its arguments (secrets masked) and who approved it.

### 5.13 Installer and first start

- `Starlight-Setup.exe` (Velopack) installs to `%LocalAppData%\Starlight` without admin. It contains the app, `llama-server` and the voice runtimes. About 150 MB.
- First start: a short setup window. The folder for models (default `D:\Starlight\models` if D: exists), download of the chosen models (about 12 GB, resumable, SHA-256 checked), a microphone test, a 3-sentence voice test, and "start with Windows" on or off.
- Starlight then runs in the tray with the orb on screen. Updates download in the background and apply at the next start, after the user says yes.
- Not signed at first: SmartScreen warns once. Signing (Azure Trusted Signing) can come later if the user wants it.

## 6. Testing: how "it works" is proven

A feature works when it passes on the user's PC with the real model, not when container tests are green. There are three levels.

| Level | What it checks | Where it runs | When |
| --- | --- | --- | --- |
| Unit and integration tests (xUnit) | Core logic, dispatcher, permissions, kill switch, geometry, parsers, the database. A fake model replays recorded answers. | Cloud container (Linux) for `net10.0` projects; the user's PC for Windows-only projects | Every commit |
| Eval suite (`Starlight.Eval`) | The real model with the real tool list: does it pick the right tool with the right arguments? 10 to 20 cases per tool, in English, plus "no tool needed" and "should ask" cases. Tools run against fakes, so nothing on the PC changes. Reports pass rate, tokens/s and time per case. | The user's PC, through a Remote Control session started from this project | Every phase that adds or changes a tool, and after a model change |
| Acceptance test | A short list of real tasks the user (or Claude through Remote Control and computer use) does on the PC, with expected results. Written into the phase before work starts. | The user's PC | End of every phase |

**Thresholds for 1.0:** eval pass rate of at least 90 % overall and at least 80 % per tool; no "should ask" case runs without asking (100 %); kill switch stops within 300 ms in every test; voice latency under 2.5 s (median of 20 tries).

**Size rule.** Tests prove behaviour. No tests that pin colours, layout or wording (V2 had to remove those on 16 Nov). Target ratio: test code no larger than product code.

**Remote runs.** `scripts/run-on-pc.ps1` builds, runs the Windows tests and the eval, and writes a short report into `reports/`. Claude starts it on the user's PC through Remote Control, so the user only has to keep the PC on.

**Bulk tests (****the user****, 22 Nov).** From the first beta on, testing runs in cycles. Claude hands the user a build. The user uses it for a while. The user sends back the diagnostics file and what they noticed. Claude fixes and hands over the next build. What makes this work is built in from P1:

- **Error log** that piles up across sessions: every exception with stack trace, the turn and tool it happened in, and the build version. Kept for 30 days, rotated at 50 MB.
- **Crash capture:** unhandled exceptions on any thread, WPF dispatcher errors, `llama-server` exits and Windows Error Reporting dumps for Starlight are caught and written down before the process ends.
- **Session trace:** each turn with what was heard, the tools called, their results (shortened), timings and the model's tokens/s, so a failure can be replayed in the eval.
- **"Report a problem"** in the orb menu and the tray: the user says or types what went wrong; it is stored with the last 5 minutes of trace.
- **"Export diagnostics"** in the tray and Settings: one zip on the desktop with logs, crash data, the trace, settings (secrets removed) and system info (GPU, driver, Windows build, model). That zip is what the user sends back.
- Failures from a cycle become new eval cases or tests before they are fixed, so they stay fixed.

## 7. Phases

Thirteen phases, in this order: voice and the overlay come before the chat window, as the user asked. A phase starts only when the one before it is accepted on the PC. Sizes are rough, in work blocks (one Claude session and one commit each), about 45 in total.

| Phase | What | Blocks | Status |
| --- | --- | --- | --- |
| P0 | Foundations and the model choice | 4 | Not started |
| P1 | Agent core | 4 | Not started |
| P2 | App shell, tray and installer | 4 | Not started |
| P3 | Voice (push-to-talk) | 4 | Not started |
| P4 | Overlay | 4 | Not started |
| P5 | Desktop control | 5 | Not started |
| P6 | Wake word, then a week of daily use ("0.5") | 2 | Not started |
| P7 | Chat window | 4 | Not started |
| P8 | Memory | 2 | Not started |
| P9 | Web | 3 | Not started |
| P10 | Servers and network | 2 | Not started |
| P11 | Code and skills | 4 | Not started |
| P12 | Hardening and release 1.0 | 3 | Not started |

### P0. Foundations and the model choice

- Repository: one solution, `Directory.Build.props` (nullable on, warnings as errors, analyzers), the projects from section 4, `CLAUDE.md` for V3, `FEATURES.md`, a short README.
- `ModelHost` and a small developer console to talk to the model.
- `Starlight.Eval` with the first 40 cases, written against the tool names in section 5.
- Benchmark 3 or 4 candidate models on the user's PC: tokens/s, time to first token, VRAM, eval pass rate. Any GGUF model can be swapped in later from Settings, uncensored ones included.
- One design mock-up (orb, caption, pointer, ask card, chat window), light and dark.

**Accepted when:** the user picks the model from the benchmark report, approves the mock-up, and the solution builds on their PC.

### P1. Agent core

- Turn loop, `ToolDispatcher`, permissions, kill switch, audit log, Serilog, `starlight.db`.
- First tools: `app` (open, focus, list), `system.info`, `files.find`, `notify`.

**Accepted when:** in the console, "open Notepad" opens Notepad; Ctrl+Alt+J during a 10-second tool stops it within 300 ms; the audit log shows both.

### P2. App shell, tray and installer

- `Starlight.exe`: tray icon, one instance only, settings store, Settings window (first sections), first-start setup with model download.
- Velopack installer and updates from GitHub Releases. Start with Windows.

**Accepted when:** `Starlight-Setup.exe` installs on the user's PC with nothing else installed, downloads the model, starts in the tray, survives a reboot, updates to a test version, and uninstalls cleanly.

### P3. Voice

- Mic capture, push-to-talk, VAD, whisper, Kokoro, sentence streaming, barge-in, English.
- Voice states published for the overlay.

**Accepted when:** of 20 spoken test commands (written in P3's first block), at least 18 are understood; median latency is under 2.5 s; "Stop" stops a long answer at once.

### P4. Overlay

- Windows per monitor, orb with states, caption, pointer, ask card, red control frame, orb menu.

**Accepted when** the overlay test list passes: clicks go through everywhere; focus is never taken; it is not in Win+Shift+S screenshots; the pointer is exact on both monitors and at 125 %; a full-screen game is not covered and nothing freezes; idle CPU is under 1 %; the kill switch hides the pointer at once.

### P5. Desktop control

- The 8 tools of section 5.5, act-and-check, the red frame, eval cases for each tool.

**Accepted when:** "Where are the settings?" in Explorer points at the right control within 2 s; "open Notepad and write Hallo Welt" works after one Yes; a failed check is reported, not hidden; the desktop eval cases pass at 90 % or more.

### P6. Wake word and "0.5"

- Keyword spotting with a phrase set in Settings (default "Hey Starlight"), sensitivity, on/off.
- Then the user uses voice, overlay and desktop control daily for one week. Problems go into a list, fixed before P7.

**Accepted when:** 45 of 50 wakes from 2 m work; under 1 false wake per hour with talk or TV in the room; the week's list is fixed.

### P7. Chat window

- Conversations, native Markdown, tool steps, inline Yes/No, attachments. Views: Activity, Statistics, Settings (all sections). English. Light and dark.

**Accepted when:** a voice conversation shows up in the chat and can be continued by typing; screenshots in light and dark match the P0 mock-up.

### P8. Memory

- Memory store, recall into prompts, the 3 memory tools, the Memory view, "forget this conversation".

**Accepted when:** a fact told by voice on Monday is used in an answer on Tuesday; it can be seen and deleted in the Memory view; after deleting it is not used.

### P9. Web

- The 4 web tools, the polite limiter, Playwright on first use, Credential Manager, the injection guard.

**Accepted when:** "What's new about …" answers with sources; a login to one of the user's accounts works with 2FA by voice; a test page with hidden instructions does not trigger any Ask tool.

### P10. Servers and network

- The 3 server tools, the server list in Settings, the watcher.

**Accepted when:** the user's own server (or a test one) is checked, an allowed SSH command runs, a disallowed one asks, and a stopped service is reported within the set interval.

### P11. Code and skills

- Embedded Python, AppContainer sandbox, `code.write` and `code.run`, skill drafts and approval, `skill.run`.

**Accepted when:** "write a script that renames the photos in this folder by date" is written, tested on copies in the sandbox and only then run on the folder after a Yes; a script that tries to open a network connection fails in the sandbox; an approved skill runs again by name.

### P12. Hardening and release 1.0

- The full eval on the final model and tool list; a crash and error review of two weeks of logs; backup and restore of `starlight.db`; release notes.

**Accepted when:** all section 6 thresholds are met, two weeks of daily use pass without a crash, and the user says it is 1.0.

## 8. After 1.0: how V2's other features come back

V2's other features return one at a time, each only after the user agrees to it in `FEATURES.md`. None is dropped for good. None comes back without being asked for.

**Admission steps for each feature:**

1. A short entry in `FEATURES.md`: what it does in one sentence, the tools it adds or changes, 10 or more eval cases, and its acceptance test.
2. The user says yes in writing (the thread or the file).
3. Built as its own small phase, on its own branch.
4. The full eval still passes at 90 % or more with the new tools. If the tool list grows so large that the eval drops, tools are grouped into toolsets that load by topic, measured by the eval first.
5. Accepted on the PC, then merged.

**V2 features waiting, grouped (my suggested order, ****the user**** decides):**

| Group | V2 modules | Note |
| --- | --- | --- |
| Daily help | reminders, calendar, briefing, todo, weather, news, focus | Fits the voice-first assistant best |
| PC | system check-up, media and volume, clipboard, files and office, pdf, backup, commands | Small tools, easy to test |
| Communication | phone (WhatsApp or Telegram), mail, approval queue | Needs careful Ask rules |
| Home | smart home (Home Assistant), home network | Needs the user's setup |
| Automations | rules with when / if / then | Large; best after the rest is stable |
| Knowledge | docs (your folders), notes, journal, recordings, YouTube summaries | The notes app was V2's biggest drift; a much smaller version first |
| Security | recon, cve, certs, breach, Absolution | Off by default, as in V2 |
| Life extras | trains, finance, expenses, birthdays, habits, recipes, trips, podcasts, flashcards, translate, calc | One at a time, if the user still wants them |

## 9. Risks and open questions

**Risks**

| Risk | Effect | What the plan does |
| --- | --- | --- |
| The local model is not good enough at tool use | Wrong actions, the user stops trusting it | P0 measures before anything is built on it; the eval gates every tool; Ask cards on every action that changes something |
| Model, whisper and a game do not fit in 16 GB together | Stutter or out-of-memory | VRAM budget in 5.2, game mode stops the model, vision can load on demand |
| Whisper on the same GPU slows the model | Voice latency over 2.5 s | Measured in P3; fallback: whisper small on the CPU for short commands |
| Overlay breaks on unusual monitor setups or DPI | Pointer in the wrong place | Physical-pixel geometry with tests for left/above monitors and 125/150 % |
| Unsigned installer | SmartScreen warning, some virus scanners flag it | Velopack, no self-modifying launcher; signing later if needed |
| Scope creep again | V2 repeats | R1 and R2: `FEATURES.md` and the tool rule; each phase has a written acceptance test |
| Work is checked only in the cloud | "Works in tests, fails on the PC" | R3: acceptance on the PC; eval runs on the PC through Remote Control |
| C# is new for this project | The user reads Python more easily (assumption) | Short, plain code; docs explain each project; the user reads both and left the choice to Claude |

**The user****'s answers (22 Nov 2026)**

1. **Language:** English only. Everything German from V2 is dropped.
2. **Web and servers:** both stay in 1.0. Web search comes first.
3. **Model:** no uncensored requirement. Any GGUF model can be swapped in, uncensored ones included.
4. **Wake phrase:** "Hey Starlight" by default, and the user can type any other phrase or name in Settings.
5. **Repository:** Claude pushes directly, with no pull requests and no progress reports. The next report is a beta that is ready for the user to test.
6. **Outside channels:** none in 1.0 (no WhatsApp, Telegram, phone).
7. **Language and platform:** C# chosen by Claude; Windows 11.

## 10. What we take from other projects

I looked at similar open-source assistants and Windows agents on 22 Nov 2026. Code is reused only where the licence allows it (MIT, Apache-2.0, BSD), with the licence notice kept in `THIRD-PARTY-NOTICES.md`. Public code without a licence, or with a non-commercial licence, gives ideas only.

| Project | What it is | Licence | What V3 takes |
| --- | --- | --- | --- |
| [microsoft/UFO](https://github.com/microsoft/UFO) (UFO²) | Microsoft's Windows desktop agent | MIT | Hybrid control finding (UI Automation plus a vision parser), an app-level plan then per-step actions, its prompts for picking controls |
| [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | Windows control tools used by over 2 million Claude Desktop users | MIT | The tool set and the "state" snapshot: a numbered list of the clickable UI elements a small model can pick from. Port to C# with notice |
| [isair/jarvis](https://github.com/isair/jarvis) | Local voice-first assistant, floating face, chat window, memory, eval report | Custom non-commercial, share-alike | Ideas only: its eval report format (96 % over 354 cases), the floating face instead of a window, the setup wizard steps, the memory viewer |
| [ONEPUNCHMAN411/Jarvis](https://github.com/ONEPUNCHMAN411/Jarvis) | Windows 11 voice assistant, HUD with an orb, 50+ tools | MIT | The "orb only" mode and the dark HUD look as a reference; Silero VAD use. Its UI is crowded; we keep the orb, not the dashboard |
| [noahrohlinger40-commits/Jarvis](https://github.com/noahrohlinger40-commits/Jarvis) | C# .NET 8 Windows assistant with local llama-server, wake word, 67 tools | No licence (all rights reserved) | Ideas only: follow-ups like "click that button" resolved from recent screen context; per-microphone wake calibration |
| [Adamdesgns/jarvis-local-assistant](https://github.com/Adamdesgns/jarvis-local-assistant) | Local "Hey Jarvis" assistant with approval-gated file actions | MIT | Its approval flow for file changes, as a check against ours |
| [Lyrcaxis/KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp) | Kokoro text-to-speech for C# | MIT | Used directly as the voice |
| [k2-fsa/sherpa-onnx](https://k2-fsa.github.io/sherpa/onnx/kws/index.html) | Speech toolkit with C# bindings | Apache-2.0 | Used directly for the custom wake phrase |

Three screenshots are attached in the thread: the HUD with the orb (ONEPUNCHMAN411), the floating face and the chat window (isair). The user picks the look; P0's mock-up follows their pick (the glowing orb).

**How work runs from now on (****the user****, 22 Nov):** Claude builds and pushes without pull requests or progress reports. The user does not test early versions. Claude still runs each phase's acceptance test on the user's PC through Remote Control (rule R3), because V2 failed exactly there. The next message to the user is a beta ready to test; after that, bulk-test cycles (section 6).
