# Perplexity in CAD MCP

Select **Perplexity API** in the CAD MCP chat panel. This provider uses the official
[Perplexity Agent API](https://docs.perplexity.ai/docs/agent-api/quickstart), with
the existing local CAD MCP host executing CAD tools.

## Connect

1. Create an API key and add API credits in the
   [Perplexity API console](https://console.perplexity.ai). Sign-up credits alone
   do not unlock the Agent API.
2. Enter the key in the password field under connection settings, or set the
   `PERPLEXITY_API_KEY` environment variable before launching AutoCAD.
3. Select a model and, if needed, a reasoning level. The default model is shown
   in the selector. Refresh obtains the available model IDs from Perplexity.
4. Check the CAD connection and send a task as with the other providers.

The password field keeps the key only for the lifetime of that panel. The key is
not written into chat history, preferences, exported conversations or the
adapter/session identifier. A user environment variable is an alternative for
subsequent launches; restart AutoCAD after changing it.

API usage is billed separately from a Perplexity subscription. The account
button opens the API console; refreshing the model list does not generate an
answer. See the official [billing documentation](https://docs.perplexity.ai/docs/getting-started/projects).

## CAD execution and conversation

The adapter starts `CadMcp.Host` over local stdio with the active CAD session,
drawing and operation owner. It obtains the host's actual tool schemas, sends
them as custom function definitions, and executes returned function calls
through the same host. The host retains its drawing/revision checks, operation
receipts, C# and AutoLISP policies, and recovery behavior. No inbound web server
or public CAD endpoint is needed.

An explicit model, optional `reasoning.effort`, CAD functions and Perplexity web
search are sent to the API. Top-level effort presets are not used: those presets
can add other remote tools. Model availability and supported reasoning settings
are controlled by Perplexity; a service rejection is reported rather than
silently switching the model.

The provider keeps bounded local conversation history for subsequent turns and
model changes. Tool results are recorded before continuing the model loop.
Interrupted calls with an unknown outcome are not automatically replayed.
**Stop** requests cancellation of this chat's accepted CAD operations and closes
its provider connection; a native operation may still need time to reach a safe
boundary. Check its receipt and the drawing before asking for another edit.

Text attachments are included in the prompt, and supported images are sent as
image inputs. PDF and DWG/DXF paths are interpreted by the existing local CAD
reference tools. An ordinary local path does not give Perplexity independent
access to the filesystem.

Search sources are collected across the response and shown with links. Token
counts are accumulated from the completed API responses in the current task;
they are not a monetary estimate. Missing counters and interrupted work remain
explicitly marked as unavailable or incomplete.

## Limits and validation

This adapter does not provide the Codex Computer Use integration or separate
helper agents. User prompts, included attachment data, tool definitions and tool
results are sent to Perplexity as part of the conversation. Local continuation
does not imply zero server retention: see the provider's
[conversation-state documentation](https://docs.perplexity.ai/docs/agent-api/conversation-state)
and data policies.

Automated provider tests use simulated HTTP and MCP responses, without an API
key or paid requests. They exercise tool execution, conversation continuation,
stream completion, cancellation, errors and accounting. They cannot establish
model quality, account entitlement or correctness of native AutoCAD geometry.
An authenticated end-to-end check inside the supported AutoCAD installation is
still required before relying on this provider for production drawing edits.

Protocol references:

- [Custom functions](https://docs.perplexity.ai/docs/agent-api/tools/custom-functions)
- [Agent request and response schema](https://docs.perplexity.ai/api-reference/agent-post)
- [Model catalog](https://docs.perplexity.ai/api-reference/models-get)
- [Migration from Sonar](https://docs.perplexity.ai/docs/agent-api/migrate-from-sonar/overview)

