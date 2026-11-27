# Third-party notices

Starlight uses these packages through NuGet, under their own licences:

- Microsoft.Extensions.AI, Microsoft.Extensions.AI.OpenAI, Microsoft.Data.Sqlite,
  System.Drawing.Common, System.Security.Cryptography.ProtectedData (MIT, Microsoft)
- OpenAI .NET (MIT)
- Serilog, Serilog.Sinks.File, Serilog.Formatting.Compact (Apache-2.0)
- WPF-UI (MIT), H.NotifyIcon.Wpf (MIT), Markdig (BSD-2-Clause), Velopack (MIT)
- NAudio (MIT), Whisper.net (MIT), sherpa-onnx (Apache-2.0), SSH.NET (MIT)
- FlaUI (MIT), AngleSharp (MIT), SharpCompress (MIT)
- xUnit (Apache-2.0), tests only

Files that ship next to the app or are downloaded on first start:

- llama.cpp `llama-server` (MIT), the Python embeddable package (PSF License)
- whisper.cpp models (MIT), Silero VAD (MIT), Kokoro-82M (Apache-2.0), espeak-ng data (GPL-3.0,
  used as data by sherpa-onnx; not linked into Starlight). The user agreed to ship it (27 Nov 2026).
  The installer must put the GPL-3.0 text and a link to the espeak-ng source next to the data.

No code is copied from other projects yet. Ideas only (no code) from Windows-MCP (MIT): the numbered
list of controls on screen.
