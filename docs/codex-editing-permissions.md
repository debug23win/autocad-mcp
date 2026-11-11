# CAD permission fix in 0.2.1

Panel version 0.2.0 set `approvalPolicy="never"` without granting MCP editing-tool approval. Codex could therefore reject `cad_edit` before contacting AutoCAD: approval was required but could not be requested.

Since 0.2.1, every panel-owned Codex process explicitly configures the local `cad` server in `approve` mode. `cad_edit`, `cad_lisp`, and `cad_focus` also receive `approval_mode="approve"`, preventing an existing per-tool setting from overriding the server default. MCP declarations retain their actual mutation/destructiveness annotations; they are not disguised as reads.

In that version, the filesystem sandbox remained `read-only` and approval dialogs remained `never`. Other MCP servers and the global `.codex/config.toml` were unchanged. Resuming starts a new process with the same arguments, then calls `thread/resume`; clearing history is unnecessary.

Settings follow the [official OpenAI configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

## Validation

- Build: no warnings or errors.
- 21 offline tests passed; a fake CLI verifies CAD arguments and sandbox preservation on start/resume.
- Real Codex CLI 0.154.0: `config/read` confirmed effective permissions for all three tools.
- Account-catalog model `gpt-6-astra` executed `cad_edit`, `cad_lisp`, `cad_focus`, and status checks against a separate writable broker without approval requests.
- The same calls passed after stopping the first CLI process and resuming the conversation in a second one.

This checks real Codex permissions and delivery to our MCP host. The broker uses a unique test pipe and does not connect to AutoCAD. It does not validate geometry, Undo, or drawing quality in a real DWG.

From the repository root: `tests/CadMcp.Tests/bin/Release/net8.0-windows/CadMcp.Tests.exe --live-codex-approval-test <path-to-codex.exe>`. This optional test uses the active subscription separately from offline tests and saves `.runtime/approval-probe-*/result.json`.
