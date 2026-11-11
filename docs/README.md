# Documentation and source map

Repository instructions are maintained in English to keep agent context compact. Documentation language does not select the application's interface or assistant response language; those follow the installation language.

| Location | Contents |
|---|---|
| [src/CadMcp.AutoCAD](../src/CadMcp.AutoCAD) | AutoCAD plugin, native geometry, document dispatch, WPF chat and HTML assets. |
| [src/CadMcp.Core](../src/CadMcp.Core) | Shared contracts, broker/transport, journals, edit plans, geometry checks, benchmark runner. |
| [src/CadMcp.Host](../src/CadMcp.Host) | MCP tool server, C# compilation, PDF readers and web downloads. |
| [src/CadMcp.Providers](../src/CadMcp.Providers) | Codex/Claude adapters, agent instructions, history, attachments, metrics and Word export. |
| [src/CadMcp.Client](../src/CadMcp.Client) | Standalone chat application reusing the plugin's UI. |
| [src/CadMcp.AutoCAD2027](../src/CadMcp.AutoCAD2027) | AutoCAD 2027 adapter project. |
| [src/CadMcp.AutoCAD2025Net10](../src/CadMcp.AutoCAD2025Net10), [src/CadMcp.AutoCAD2026Net10](../src/CadMcp.AutoCAD2026Net10) | Adapter projects for updated .NET 10 products. |
| [tests](../tests) | Automated tests and native/GUI/CLI probes. |
| [scripts](../scripts), [installer](../installer) | Build/validation/release scripts and installer configuration. |
| [benchmarks](../benchmarks) | Agent-evaluation scenarios; the built-in benchmark is in Core. |

Start with [capabilities](capabilities.md), [reading](reading.md), [editing](editing.md), and [validation](validation.md). See [built-in benchmark](in-app-benchmark.md) for dedicated-process safety requirements and [localization](localization.md) for installation-language behavior. Runtime prompt sources are [cad-agent.md](../src/CadMcp.Providers/Resources/cad-agent.md) and [server-instructions.md](../src/CadMcp.Host/Resources/server-instructions.md).
