# ClaudeRevit analysis for CAD MCP 0.2.0

Reviewed [debug23win/ClaudeRevit](https://github.com/debug23win/ClaudeRevit/tree/25a25ba925dbff48e33e0700233a7fbe53c7863e), commit `25a25ba925dbff48e33e0700233a7fbe53c7863e`, MIT. License retained as `licenses/ClaudeRevit-MIT.txt`. This is source analysis; Revit features were not run here.

| Mechanism | CAD MCP application |
|---|---|
| [HistoryStore.cs](https://github.com/debug23win/ClaudeRevit/blob/25a25ba925dbff48e33e0700233a7fbe53c7863e/ClaudeRevit/Services/HistoryStore.cs): adjacent-file write and replace | Adapted in `ChatStateStore`: text/settings/CLI session ID, flush before replace, per-process files, size limits, and corrupt-file skipping. |
| [SettingsStore.cs](https://github.com/debug23win/ClaudeRevit/blob/25a25ba925dbff48e33e0700233a7fbe53c7863e/ClaudeRevit/Services/SettingsStore.cs): preference persistence | Saves both CLI paths, host, and workspace; resumes when settings match. |
| [ChatService.cs](https://github.com/debug23win/ClaudeRevit/blob/25a25ba925dbff48e33e0700233a7fbe53c7863e/ClaudeRevit/Services/ChatService.cs): stable instructions separate from dynamic context | Own `CadAgent` instructions; tools fetch current DWG before work and after restoring history. |
| [ChatPaneView.xaml.cs](https://github.com/debug23win/ClaudeRevit/blob/25a25ba925dbff48e33e0700233a7fbe53c7863e/ClaudeRevit/UI/ChatPaneView.xaml.cs): streaming/task state | Text batches every 50 ms, selected model/current tool indicators, settings locked during requests. |
| [ToolResultAging.cs](https://github.com/debug23win/ClaudeRevit/blob/25a25ba925dbff48e33e0700233a7fbe53c7863e/ClaudeRevit/Services/ToolResultAging.cs), [ToolResultArchive.cs](https://github.com/debug23win/ClaudeRevit/blob/25a25ba925dbff48e33e0700233a7fbe53c7863e/ClaudeRevit/Services/ToolResultArchive.cs): shrinking old results with original recovery | Studied. Subscription history belongs to the CLI and is not rewritten. Snapshot metadata/pagination already exist; a large-result archive was the next improvement. |
| Stable API prompt-cache prefix | Applied to instructions and stable tool schemas. Codex/Claude Code manage model caching; Anthropic API cache_control was not ported to subscription mode. |
| Repeated-error limits in agent loops | Instructions prohibit replaying uncertain writes; operation IDs, status reads, and clear errors were added. No full internal CLI repeated-error counter is claimed. |

0.3.0 added paginated large-result archives and entity cards with Show in AutoCAD. Official CLI history remains untouched: archives cover our CAD results, not the provider's internal conversation.

Our CAD read cache keys catalogs, snapshots, and targeted search pages by document/revision/space. Manual edits, native transactions, and Lisp increment revision through database events. Database identity uses the native object rather than an incidental Document.Database managed wrapper. Images, selection, and views are uncached because their changes need not modify the DWG.

Further useful extensions identified at this stage: attachments, tool discovery as inventory grows, scenario logs, and comparison across servers on identical DWGs. API-key storage, API cost accounting, and forced conversation compression were not added to subscription mode.
