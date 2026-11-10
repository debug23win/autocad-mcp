# Follow-ups during execution

Input and attachments remain available while the agent works. Enter or Add follow-up sends an update; Shift+Enter inserts a newline. The original task continues. Compatible requirements are retained and corrections guide subsequent actions. Already completed DWG edits are not automatically undone.

Codex uses official `turn/steer` with the current turn ID, without creating another writer process or turn. Claude receives messages through the same process/session's open `stream-json` input and acknowledges them through `--replay-user-messages`; queue processing depends on Claude Code and may begin after the current answer. New images are sent as images; text attachments include their contents.

History records delivery states: waiting for connection, sending, accepted, rejected, or delivery unconfirmed. Acceptance proves provider receipt, not that the model has corrected geometry. Messages queue during connection startup. Unsent messages remain visible after completion/cancellation and can be resent manually. Ambiguous deliveries are never retried automatically, avoiding duplicate CAD edits.

Model, provider, and settings remain locked during execution; Stop stays available. A follow-up does not interrupt the current CAD transaction: the model applies it at the next opportunity. History and delivery markers persist.

Sources: [Codex App Server: turn/steer](https://learn.chatgpt.com/docs/app-server), [Claude Code: stream-json and replay-user-messages](https://code.claude.com/docs/en/cli-reference).
