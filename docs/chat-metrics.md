# Time and tokens in answers

By default, the primary agent shows readable progress comments about reading, editing, and verification. Show progress disables recording new comments. Assistant-to-assistant messages and technical logs are hidden; Copy copies the final answer. Provider, model, and reasoning preferences for Codex/Claude are saved separately and applied on the next launch and in new tabs.

Each CAD MCP answer ends with task duration and token counts from CLI events. Timing starts when the request is sent and ends after plugin verification, including automatic preview. Provider, approval, and drawing-verification waits are included. Follow-ups sent during execution belong to the same task; its last answer receives the metrics footer.

Codex reports the primary agent's cumulative-token difference between task start and end. Resumed-chat history is subtracted using its replay event; without that baseline, usage is unavailable. Assistant counters are excluded. Repeated usage events replace the value rather than add to it. Cached input and reasoning tokens are already included in Codex's main counters and are not added again.

Claude reports `usage` from the last `result` event, explicitly labeled "last Claude result". Input includes `input_tokens`, `cache_read_input_tokens`, and `cache_creation_input_tokens`; output uses `output_tokens`. Multiple results caused by follow-ups are not summed because the provider may report cumulative usage.

Unreliable/missing counters display "no data". On failure or cancellation, available counters are marked incomplete. These figures are neither cost estimates nor the token count of visible answer text alone. The fixed benchmark runs without a model and reports 0 tokens.

The Codex protocol was checked against pinned CLI [0.159.0](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/app-server-protocol/schema/typescript/v2/ThreadTokenUsage.ts) and [resume replay](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/app-server/src/request_processors/token_usage_replay.rs).

Codex allowance comes from account/rateLimits/read and account/rateLimits/updated notifications. The panel shows actual remaining percentages, window duration (such as 5 h / 7 d), and reset time in local Windows time. It refreshes when a request is sent. Missing values display "no data": allowance is not inferred from tokens, and a percentage does not guarantee model availability.

Codex and Claude allow 90 seconds for task startup and five minutes without any events during execution. Any protocol activity, including hidden service messages, refreshes the connection indicator. On timeout, the CLI process stops without resubmitting an edit; accepted AutoCAD operations still require receipt verification. This does not forcibly interrupt a CAD command. Claude uses the active-event deadline after init or the first assistant/user/stream event, including older streams without init. Tool events carry structured tool and phase fields; localized display strings do not decide whether a write completed.

Since 0.11.6, tool failures appear immediately in the primary answer regardless of Show progress: stage/tool, actual error code/text, operation_id/request_id when present, and confirmed or unknown write outcome. Only selected diagnostic fields are shown, without argument, source-code, or image dumps. Messages persist in history and are copied with the answer. A Stopped result and disabled Stop button distinguish a finished turn from ongoing DWG verification.
