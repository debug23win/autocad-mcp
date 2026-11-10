# Installation language

The installer offers Russian and English. Its selection is saved in `%LOCALAPPDATA%\CadMcp\installation-language.txt` and read when each CAD MCP process starts. Reinstall with another language and restart CAD MCP/AutoCAD to change it. Missing, unreadable, oversized, or unsupported settings fall back to Russian for compatibility with existing installations.

The installation language controls chat controls, tabs, window captions, progress/status text, copy feedback, metrics, benchmark reports, Word-export labels, Lisp approval dialogs, and the primary/assistant default response language. An explicit user request for another response language still takes precedence. UI strings live in `src/CadMcp.Core/Resources/ui.en.json`, selected by `UiText`; Codex and Claude receive the same language policy through `CadAgent` on start/resume.

Repository documentation and internal prompt sources remain English. They do not select the interface language. User messages, existing answers, attachments, code, CAD command identifiers, and DWG contents are never translated automatically. Russian Unicode benchmark text and steel-section codes remain fixture data. AutoCAD, Windows, and third-party API diagnostics retain their own localization. The plugin does not change AutoCAD's thread culture, product language, units, or Lisp policy.
