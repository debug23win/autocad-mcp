# Qwen Code and Kimi Code subscriptions

The CAD MCP panel includes **Qwen Code** and **Kimi Code**. Both adapters use the
official CLI's account login and Agent Client Protocol (ACP). They do not require
an API key and do not switch to metered API access when a subscription is missing
or exhausted. The CLI and the account's subscription determine model access and
usage limits.

These CLIs are installed separately from CAD MCP. Use the current official
installation instructions for [Qwen Code](https://qwenlm.github.io/qwen-code-docs/en/users/overview/)
or [Kimi Code](https://www.kimi.com/code/docs/en/kimi-code-cli/). The Kimi adapter
requires the current Node.js package `@moonshot-ai/kimi-code`; the older Python
`kimi-cli` protocol is not supported by this adapter.

## Qwen Code

1. Install the official `@qwen-code/qwen-code` CLI and its required Node.js runtime.
2. Select **Qwen Code** in CAD MCP. The executable defaults to `qwen`; choose the
   installed executable or npm command if it is not on AutoCAD's `PATH`.
3. Use the panel's sign-in button to open the official CLI. Complete `/auth` with
   **Qwen Account with Subscription**, using the account that has the subscription.
4. Return to CAD MCP, check the connection, refresh the model list, and send a task.

The adapter explicitly selects `qwen-subscription` authentication. It starts
Qwen in bare ACP mode, with the local CAD MCP server supplied for the current
drawing. Account credentials remain managed by Qwen Code. See the official
[authentication instructions](https://qwenlm.github.io/qwen-code-docs/en/users/configuration/auth/).

## Kimi Code

1. Install the current official `@moonshot-ai/kimi-code` CLI and its required
   Node.js runtime.
2. Select **Kimi Code** in CAD MCP. The executable defaults to `kimi`; choose the
   installed executable or npm command if needed.
3. Use **Sign in** in CAD MCP and complete the official `kimi login` flow for the
   subscription account. Sign in from this button even if another terminal is
   already signed in: CAD MCP uses its own Kimi profile.
4. Return to the panel, check the connection and refresh the available models.

The CAD MCP profile is shared across drawings under
`%LOCALAPPDATA%/CadMcp/providers/kimi`. The CLI stores and refreshes its own login
there; CAD MCP does not copy credentials out of another Kimi profile. Each
working folder has a separate stable workspace beneath that profile. The
integration preserves the CLI's login/model settings and maintains its own
CAD-tool configuration. The user's ordinary Kimi home and project configuration
are not edited. Only models advertised under the CLI's `kimi-code/` subscription
provider can be selected.

The [official command reference](https://www.kimi.com/code/docs/en/kimi-code-cli/reference/kimi-command.html)
describes `login`, `acp` and the configurable Kimi home.

## Shared behavior

- Models are read from authenticated ACP session metadata. Refresh and connection
  checks do not send a prompt to a model. The default choice uses the CLI's
  current model; an unavailable saved model produces an error before the task.
- Session IDs are saved with the chat. Each subsequent turn loads that CLI
  session, including after a model change. History replay is not displayed as a
  new answer and cannot authorize a new CAD operation.
- CAD tool calls use the active owner, CAD session and drawing. Existing host
  policies, revision checks, operation receipts and rollback rules still apply.
  Only exact tools from the supplied `cad` MCP server receive per-call ACP
  approval. The integration does not grant permanent tool permissions.
- Built-in shell and filesystem tools, other MCP servers, automatic helpers and
  Computer Use are not exposed through these adapters. The reference readers
  supplied by CAD MCP handle local PDF and DWG/DXF paths.
- Text attachments are included in the prompt. Images are sent only when the CLI
  advertises image support. The user's task, included attachment data and CAD
  results are processed by the chosen provider under its account terms.
- **Stop** cancels the active ACP turn and requests cancellation of the chat's
  accepted CAD operations. A native operation may need to reach a safe boundary.
  The adapter does not resubmit a model prompt after an uncertain result.
- Additions during an active turn are disabled for these two providers. Send the
  next instruction after completion. Context occupancy reported by ACP is shown
  as context, not billed tokens or remaining subscription quota.

On Windows, standard npm `.cmd`/`.bat` launchers are resolved to their installed
package entry point and invoked through Node with an argument list. A shell is
not used. If the launcher does not have the expected installed package layout,
the adapter reports a configuration error instead of guessing a command.

### Transport retries and drawing changes

The current Kimi CLI can [retry an MCP tool call after a transport failure](https://github.com/MoonshotAI/kimi-code/blob/main/packages/agent-core-v2/src/agent/mcp/tools/mcp.ts),
including after reconnecting the host. It retains the same tool arguments.
All five CAD mutation tools require `operation_id`; the worker records that ID
durably before admitting the operation. The same ID and request return its
existing receipt. An unfinished or unknown receipt returns a pending/unknown
status and is not admitted to the execution queue again. Changed content with
the same ID is rejected. Restarting the MCP host does not discard the CAD
worker's journal.

This receipt mechanism, rather than an unsupported promise that Kimi never
retries a transport call, prevents a repeated request from applying the same
CAD edit twice. After an uncertain result, inspect `cad_operation_status` and
the drawing; do not replace the operation ID to force a new execution.

## DeepSeek subscription request

No DeepSeek subscription adapter is included in this change. The official
[DeepSeek Harness terms](https://www.deepseek.com/en/harness/terms-of-use/) and
[Codex integration guide](https://api-docs.deepseek.com/quick_start/agent_integrations/codex/)
describe metered Open Platform/API access. That is a different billing mode from
the requested subscription integration. Supporting DeepSeek through a particular
subscription requires identifying that service and its supported agent/login
interface; browser credentials are not used as an undocumented API.

## Validation

Offline ACP fixtures exercise streaming, model selection, authenticated metadata,
session continuation, history and foreign-session filtering, attachments,
per-call CAD permissions, denied client execution, cancellation, incomplete
streams and failed session restoration. They do not use paid model requests.
The current official Qwen CLI has also completed an unauthenticated ACP
initialization smoke check with the adapter's configuration.

Windows CI builds the WPF panel and runs the managed/MCP test suite. Account
entitlement, a complete authenticated model turn and actual AutoCAD geometry
still require an end-to-end check with the user's installed CLI and account.
