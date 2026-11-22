# Features (the contract, rule R1)

Starlight 1.0 does what V2's first plan (6 Oct 2026) promised, and nothing else. This list was
agreed with the plan on 22 Nov 2026. A new feature is added here only after the user says yes
(plan section 8).

| # | Feature | Phase | Tools |
| --- | --- | --- | --- |
| 1 | Hears and answers by voice, in English: push-to-talk, then a wake phrase the user sets (default "Hey Starlight") | P3, P6 | none |
| 2 | Overlay: glowing orb, captions, a pointer line and frame, Yes/No cards, a red frame while in control | P4 | none |
| 3 | Sees the screen and uses mouse, keyboard and apps | P1, P5 | `app`, `window`, `screen_look`, `screen_find`, `screen_point`, `input_click`, `input_type`, `input_keys` |
| 4 | Runs a local, swappable GGUF model on the GPU; no account, no cloud | P0, P2 | none |
| 5 | Remembers what the user tells it; the user can see and delete it | P8 | `memory_remember`, `memory_recall`, `memory_forget` |
| 6 | Uses the web: search, read pages, research, log in | P9 | `web_search`, `web_read`, `web_research`, `web_login` |
| 7 | Checks and talks to the user's servers (ping, HTTP, SSH) | P10 | `server_check`, `server_ssh`, `server_list` |
| 8 | Writes and tests small programs in a sandbox, and learns skills the user approves | P11 | `code_write`, `code_run`, `skill_run` |
| 9 | Asks before anything risky; Ctrl+Alt+J stops everything | P1 | none |
| 10 | Statistics: tasks, errors, model speed | P1, P7 | none |
| 11 | Helpers for everyday requests | P1 | `system_info`, `files_find`, `notify` |
| 12 | Diagnostics for bulk tests: error log, crash capture, session trace, "Report a problem", "Export diagnostics" | P1, P2 | none |
| 13 | Chat window: conversations, Markdown, tool steps, attachments, Memory, Activity, Statistics and Settings views | P7 | none |

Not in 1.0: anything outside the PC (WhatsApp, Telegram, phone, mail), notes or to-do apps,
German, and the other V2 features listed in plan section 8.
