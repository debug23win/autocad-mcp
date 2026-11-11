# Model selection in 0.2.2

Select Codex, model, and reasoning effort in the panel. Automatic uses the account catalog's default model; Default uses the selected model's default effort. Refresh reloads the catalog without starting a model conversation. Hidden models are excluded and all catalog pages are read.

Settings persist with history under `%LOCALAPPDATA%/CadMcp/chat`; old JSON files remain compatible. Changing model/effort creates a new adapter process, resumes the conversation with `thread/resume`, and explicitly supplies model to start/resume/turn and effort to turn. Availability is checked before sending; unavailable models and unsupported effort levels produce clear errors.

The installer bundles official Codex CLI 0.159.0, app-server, code-mode-host, Windows tools, and ripgrep in the upstream package structure. A standalone `codex.exe` is insufficient for this version's tools. Archives/binaries are checksum-pinned in `installer/codex-assets.json`; `scripts/prepare-codex.ps1` prepares the verified package. The default desktop-app CLI path migrates to the bundled CLI; explicit custom paths remain unchanged. Official Codex still manages credentials.

## Claude Code

Claude Code supplies the installed CLI's model list. The panel runs `claude -p` in stream-json mode without MCP servers or session persistence and sends control request `initialize`. Claude returns account models, aliases (`sonnet`, `opus`, `fable`, `haiku`), current versions/descriptions, and supported `--effort` levels. No model inference is invoked.

- Automatic omits `--model`, allowing Claude's default; the panel displays its current version.
- Effort options follow the chosen model. Models without effort support offer Default only and omit `--effort`.
- Saved models/efforts no longer in the catalog are marked unavailable and rejected before sending.
- If catalog loading fails (old CLI/startup failure), fallback aliases Sonnet, Opus, Fable, and Haiku offer all efforts.
- Answers show Claude's actual model (`claude-opus-5-5` for `opus`) and selected effort.

Protocol: [official app-server reference](https://learn.chatgpt.com/docs/app-server). Distribution: [official 0.159.0 release](https://github.com/openai/codex/releases/tag/rust-v0.159.0).

## Validation

- 25 offline tests: catalog/pagination/hidden models, effort validation, actual start/resume/turn arguments, old histories, and migration of only the standard CLI path.
- Separate WPF probe `tests/CadMcp.ChatPanelProbe`: rendering/persistence, effort filtering, history restoration, and disabling Codex model controls under Claude. The panel was rendered and visually inspected.
- Real authenticated Codex 0.159.0: catalog included GPT-6 Sol. Astra / low called a test CAD server. A new Sol / medium process resumed the same conversation, recalled a control phrase, and called `cad_edit`, `cad_lisp`, `cad_focus`, and status tools without approval prompts.

The initial shared six-minute limit expired during Sol. Each turn now has its own limit; a subsequent resume of the saved Sol conversation succeeded. The isolated broker never connected to AutoCAD, so this does not validate real DWG geometry.

Optional subscription test: `tests/CadMcp.Tests/bin/Release/net8.0-windows/CadMcp.Tests.exe --live-codex-model-test <codex.exe>`. After the first completed turn, `--resume-conversation <id>` continues Sol with Astra context without repeating that first turn. Results: `.runtime/model-probe-*/result.json`. Offline tests never call a model.
