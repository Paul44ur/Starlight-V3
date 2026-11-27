# Starlight V3: notes for the coding agent

A local, Jarvis-like assistant for Windows 11: one native program (C# on .NET 10, WPF), voice and
an overlay first, a chat window second. The model runs in a bundled `llama-server` child process.
No browser UI, no Python app, no cloud.

The plan is `docs/plan.md` (a copy of the plan doc; the doc is the source). Read it before
starting a phase. The user's decisions are in its section 9.

## Rules (plan section 2)
- **R1.** `FEATURES.md` is the contract. Build nothing that is not in it with the user's yes.
  If something seems missing or wrong, ask the user before building it.
- **R2.** Every tool and feature needs a clear reason to exist. No duplicates, nothing useless
  or obsolete. Merge tools rather than add near-copies. A tool is added only if the eval still
  passes.
- **R3.** A phase is done only when its acceptance test passed on the user's PC (through Remote
  Control). Green tests in the cloud are not "done".
- **R4.** One installer, one `Starlight.exe`, native windows. The only child processes are the
  model server, the code sandbox and a headless browser, all in a Job Object.
- **R5.** One visual design (the glowing orb, chosen 22 Nov), frozen until 1.0.
- **R6.** One way per job: one model runtime, one UI toolkit, one settings store.
- **R7.** Every setting is in the Settings window. The user never edits files.
- **R8.** Keep `PROGRESS.md` up to date every few implementations: what is done and verified
  (commit and the check that ran), what is in progress, what is broken or untested. Read it
  before saying anything is finished, and check the code. Never mark something done without a
  check that ran.

## Other rules
- English only, in the app and in the docs. Never hardcode the user's name; it is a setting.
- Code from other projects only under MIT, Apache-2.0 or BSD, with its notice in
  `THIRD-PARTY-NOTICES.md`. Public code without a licence gives ideas only.
- Tool names use `snake_case` with underscores (`screen_find`), because the OpenAI tool format
  that llama-server uses does not allow dots. The plan's dotted names map one to one.
- Tools run only through `ToolDispatcher`. The model gets declarations only.
- Docs in plain, short sentences.

## How we work
- Branches are named `v3/<kind>/<topic>`: kind is `feature`, `fix` or `chore`, the topic is a few
  words in kebab-case (`v3/feature/notify`, `v3/fix/review-findings`). One branch per piece of work,
  started from the newest one that is not merged yet. Commit and push when a piece of work is done.
  No pull requests, no progress messages to the user. The next message to the user is a beta to test.
- Analyzer warnings are errors. Fix a hit; switch a rule off only in `Directory.Build.props` or
  `.globalconfig`, with the reason next to it.
- GitHub Actions minutes are used up: no CI.
- Build and test in the container (the .NET 10 SDK is installed from apt `dotnet-sdk-10.0`):
  `dotnet build Starlight.slnx` and `dotnet test --project tests/Starlight.Core.Tests`.
  `-windows` projects build here (`EnableWindowsTargeting`) but only run on the PC.
- Every behaviour change gets a test. Tests prove behaviour, not colours or wording.
- The container cannot reach huggingface.co or api.github.com. Model and runtime downloads
  are checked on the user's PC.
