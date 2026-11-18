# API key authentication

CAD MCP supports explicit API key authentication for **Codex (OpenAI)**,
**Claude Code (Anthropic)**, and **Perplexity API**. API usage is billed by the
provider separately from a ChatGPT, Claude, or Perplexity chat subscription.
Qwen Code and Kimi Code keep their existing official subscription login flow.

## Setup in the panel

1. Choose **Codex** or **Claude Code**.
2. Expand **Настройки подключения** (Connection settings).
3. Set **Авторизация** (Authentication) to **API key**.
4. Enter the corresponding key in the password field. An empty field reads
   `OPENAI_API_KEY` for Codex or `ANTHROPIC_API_KEY` for Claude from AutoCAD's
   process environment. Restart AutoCAD after changing a system environment
   variable so it can inherit the new value.
5. Choose **Модели API** or **Обновить список моделей**, then select a model and
   send the task. Refresh reads CLI metadata without starting a model turn;
   it does not guarantee that the key has inference permissions or remaining
   credit.

Codex is bundled with the installer. Claude Code must be installed separately;
explicit API key mode requires **Claude Code 2.1.300 or newer**. CAD MCP checks
`--version` without passing credentials before starting that mode. This is the
supported baseline for host-managed routing, not a claim about the first version
that accepted an API key. An unsupported Codex configuration is also rejected
before the key is sent.

To return to the CLI's own login/configuration, choose **Настройки CLI**.
That mode retains the CLI's existing authentication behavior, including any API
credentials the user has already configured outside CAD MCP. It does not force
subscription billing or silently choose a different account.

Perplexity has its own key field and `PERPLEXITY_API_KEY` fallback. See
[Perplexity setup](perplexity.md).

## Keys and sessions

- Keys entered in the panel remain in memory for that panel. They are not saved
  in preferences, exported chat JSON, command-line arguments, or CAD MCP logs.
  Reopening the panel requires entering a key again or using the environment.
- Preferences remember only the selected authentication mode and model choices.
  Importing a chat history does not enable API billing or import credentials.
- Changing the key or authentication mode clears the provider session and its
  cached model catalog. The visible conversation remains available. A provider
  session from exported history is not resumed in explicit API key mode.
- The same key and mode are used for model discovery and the subsequent chat.
  Codex subscription quota messages are hidden in API mode; token usage reported
  for the answer remains visible.

## Provider implementation

**Codex:** CAD MCP selects the official OpenAI provider and verifies the
effective app-server configuration. Credentials must use the `ephemeral` store;
custom catalog endpoints or static catalog files are rejected in explicit API
key mode. Only after that check does CAD MCP send `account/login/start` over the
private process input stream. The key is not written to `auth.json`, a platform
credential store, or `.codex/config.toml`. Existing CLI login policy restrictions
remain in force. The bundled CLI is 0.169.1.

**Claude Code:** the key is passed in the child process environment as
`ANTHROPIC_API_KEY`. Host-managed provider routing selects the official Anthropic
endpoint and prevents settings files from substituting routing or authentication
variables. The panel does not invoke persistent `auth login` in API mode or
change the user's settings files. Managed permission restrictions still apply.

This feature uses the official provider endpoints. A custom gateway needs a
separate integration; entering an official API key does not authorize sending it
to an arbitrary endpoint from a project configuration.

## References

- [Codex authentication and API billing](https://learn.chatgpt.com/docs/auth)
- [Codex app-server account/login/start](https://learn.chatgpt.com/docs/app-server)
- [Claude Code authentication](https://code.claude.com/docs/en/authentication)
- [Claude Code environment variables](https://code.claude.com/docs/en/env-vars)
- [Claude Code changelog, including host-managed routing fixes in 2.1.300](https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md)

Regression tests use synthetic keys and fake CLI processes. They do not make
paid inference requests or validate a user's real key.
