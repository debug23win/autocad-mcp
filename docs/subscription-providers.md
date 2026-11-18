# Qwen Code and Kimi Code subscriptions

The CAD MCP panel includes **Qwen Code** and **Kimi Code**. Both adapters use the
official CLI's account login and Agent Client Protocol (ACP). They do not require
an API key and do not switch to metered API access when a subscription is missing
or exhausted. The CLI and the account's subscription determine model access and
usage limits.

These CLIs are installed separately from CAD MCP. Use the current official
installation instructions for [Qwen Code](https://qwenlm.github.io/qwen-code-docs/en/users/overview/)
or [Kimi Code](https://www.kimi.com/code/docs/en/kimi-code-cli/guides/getting-started.html).
The recommended official installers provide standalone distributions. Global
npm installations are also supported. Use the current Kimi Code CLI: the older
Python `kimi-cli` protocol is not supported by this adapter.

## Install and find the CLI on Windows

1. Select **Qwen Code** or **Kimi Code** and open its connection settings.
2. If the CLI is not installed, click **Install CLI (Установить CLI)**. The panel
   opens a visible PowerShell window with that provider's official installer:
   [Qwen](https://qwen-code-assets.oss-cn-hangzhou.aliyuncs.com/installation/install-qwen-standalone.ps1)
   or [Kimi](https://code.kimi.com/kimi-code/install.ps1). Complete the installation
   in that window and check its result.
3. Return to CAD MCP and click **Find CLI (Найти CLI)**. This searches again,
   including the current Windows user and machine `PATH` values, so an installer
   can add its command after AutoCAD has already started. Restarting AutoCAD is
   not required for this discovery step. You can also select the installed CLI
   explicitly in the executable field.
4. Use **Sign in**, complete the provider's account flow, then check the
   connection and refresh the model list.

The installation button is available for Qwen and Kimi. It does not silently
sign in or select an account; login remains a separate step. A successful
installation does not establish subscription access.

Discovery includes these official Windows installation locations as well as
commands on `PATH`:

| CLI | Official standalone layout |
| --- | --- |
| Qwen | The default installation root is `%LOCALAPPDATA%\qwen-code`. The outer launcher is `bin\qwen.cmd`; the packaged launcher is `qwen-code\bin\qwen.cmd` beneath that root. Both locations are recognized. |
| Kimi | `%USERPROFILE%\.kimi-code\bin\kimi.exe`, or `%KIMI_INSTALL_DIR%\bin\kimi.exe` when the installer root is overridden. |

`KIMI_INSTALL_DIR` controls installation. `KIMI_CODE_HOME` controls the Kimi
profile and data location; it does not locate the executable. For a custom
installation elsewhere, select its actual launcher or executable rather than
renaming a file or copying a wrapper away from its installed package.

## Qwen Code

1. Use the official standalone installer above, or install
   `@qwen-code/qwen-code` through npm. The standalone archive includes its
   runtime; an npm installation, including an installer fallback to npm,
   requires Node.js 22 or later and npm on `PATH`.
2. Select **Qwen Code** in CAD MCP and click **Find CLI**. The command name is
   `qwen`; a valid npm `qwen.cmd` is also supported.
3. Use the panel's sign-in button to open the official CLI. Complete `/auth` with
   **Qwen Account with Subscription**, using the account that has the subscription.
4. Return to CAD MCP, check the connection, refresh the model list, and send a task.

The adapter explicitly selects `qwen-subscription` authentication. It starts
Qwen in bare ACP mode, with the local CAD MCP server supplied for the current
drawing. Account credentials remain managed by Qwen Code. See the official
[authentication instructions](https://qwenlm.github.io/qwen-code-docs/en/users/configuration/auth/).

## Kimi Code

1. Use the official standalone installer above, which provides `kimi.exe`
   without requiring a pre-installed Node.js. Alternatively, install
   `@moonshot-ai/kimi-code` through npm with Node.js 22.19.0 or later. Install
   Git for Windows before the first launch, as described in the official guide.
2. Select **Kimi Code** in CAD MCP and click **Find CLI**. Both the native
   `kimi.exe` and a valid npm `kimi.cmd` are supported.
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

### Launchers and installation errors

CAD MCP recognizes the official Qwen standalone wrappers and starts their
bundled Node or Bun runtime with `lib/cli-entry.js` directly. Native Kimi runs
as `kimi.exe`. Standard npm `.cmd`/`.bat` launchers are resolved to the installed
package's declared entry point and invoked through Node with an argument list.
These CLI launches do not pass task arguments through `cmd.exe`. The separate,
visible installation window uses PowerShell to run the official installer.

- **CLI not found:** install it, click **Find CLI** after installation, or select
  the existing executable explicitly. A saved bare command such as `kimi.cmd`
  can be replaced by the newly detected native `kimi.exe`.
- **Package, runtime or entry point missing:** the launcher was found, but its
  installation is incomplete or does not have a supported official layout.
  Repair or reinstall that CLI and run **Find CLI** again. This is distinct from
  a missing command; changing subscription credentials will not repair it.
- **Login or subscription error:** complete the provider's sign-in flow and
  check the account's entitlement. Reinstalling the executable does not grant
  account access.

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
