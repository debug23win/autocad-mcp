# Built-in benchmark

Since 0.11.5-preview, **Benchmark** runs fixed checks without manual scenario prompts or model calls. Other chat tasks must finish first. Since 0.11.7, full native runs require a separate AutoCAD process with no open DWGs.

Start a separate AutoCAD instance, open CAD MCP from it, then close its initial empty drawing. Press Benchmark and do not open other DWGs until completion. The plugin never closes/saves user documents. Core checks for zero documents before setup; the worker repeats the check on the CAD thread before file/fixture creation. Subsequent native calls and actual deferred Lisp startup require only the owned fixture to be open. Other AutoCAD processes are unaffected.

The working drawing is never the fixture. The plugin creates a new millimeter DWG named `CADMCP_Benchmark_<id>.dwg`; the runner verifies its document_id was absent before startup. The worker binds run_id to that document and rejects benchmark requests targeting others. Tab switching cannot change the run target. The fixture remains open for inspection; geometry is not automatically saved or cleaned up.

## Checks

| Group | Independently read assertions |
|---|---|
| C#: contour | Closed polyline, four vertices, length 600, area 20000; independent takeoff. |
| C#: Unicode | Exact Ø108 → Ø114 replacement in two TEXT and one MTEXT. |
| C#: blocks | Two inserts, 0/90° rotations, one 100-unit and one 50-unit line, WCS coordinates. |
| C#: transforms | Copy/rotate/scale; moved original read before erase, then remaining copy checked. |
| C#: tables | Local/linked formulas: 10/20 → 15/30 after source update. |
| C#: search | 12 points, 5/5/2 pages, offsets, unique handles, types/coordinates. |
| C# generation | Native `cad_edit` creates a 100-unit line and radius-10 circle; search rereads geometry. |
| AutoLISP generation | Fixed `entmake` code creates equivalent geometry; independent search follows the receipt. |

The baseline C# case uses native cad_edit. Additional cad_csharp compiles full C# source and executes it in the worker: line/circle, volume-6000 box, duplicate control, and 200 long MText entities for archiving. Evidence includes source/plans/Lisp. Generation is fixed; no model is called.

Lisp follows current `CADMCPLISP` policy. AutoCAD shows normal approval when needed. The benchmark never disables prompts, changes policy, or bypasses refusal. `queued` is not success: the runner polls and verifies receipt ID/state/document before reading geometry. Denial/unavailable approval is SKIPPED; uncertain outcomes are UNKNOWN and stop further writes.

## Report

The fixture chat receives a case table and `passed/total`; use the normal Copy button. JSON at `%LOCALAPPDATA%\CadMcp\benchmarks\<run_id>\report.json` includes requests, responses, assertions, errors, and timing. Fixture DWG: `%LOCALAPPDATA%\CadMcp\benchmarks\<worker_session>\<run_id>\`.

`Stopwatch` measures each case and the entire run, including setup, CAD calls, and Lisp approval waits. `approval_wait_ms` separately counts observed receipt approval phases, limited by polling frequency. Times describe the whole pipeline, not pure C#/Lisp CPU speed.

Counters satisfy `total = passed + failed + skipped + unknown`; full success requires `passed == total`. Skipped Lisp prevents full PASS even with no failures. Incomplete/truncated/archived reads are not proof. Never resubmit an accepted operation with unknown outcome under a new ID.

**Stop benchmark** ends waiting and future scheduling. Already accepted CAD operations cannot be assumed cancelled; the report retains UNKNOWN. Decline any remaining Lisp prompt if execution is no longer wanted; stop running Lisp through AutoCAD. Closing the fixture chat stops waiting but does not close the DWG.

## Implementation verification

An independent agent added `CadBenchmarkTests`: positive control and negatives for document isolation, geometry, incomplete reads, duplicate pages/offsets, receipts, scope/unit/space changes, cancellation, duplicate starts, and Lisp. These use simulated CAD protocols. Native installed-AutoCAD verification, especially 2027/Lisp dialogs, remains separate. `CADMCPGUISTABILITY` also checks fixture-creation refusal with open drawings and write refusal outside the owned fixture.

## Full MCP coverage in 0.11.5

The main button runs eight functional groups plus 49 scenarios from real tools/list inventory. Calls use a dedicated stdio MCP host, exercising public schemas, pin/owner, JSON, and image content. Only fixture creation/activation and a controlled cancellation gate are internal worker requests. Advertised tools without cases produce BENCHMARK_CASE_MISSING.

Coverage includes native/actual C#/Lisp generation, independent dimensions/solid topology, assembly mass/recipe, Unicode/table graph, snapshot/query, real >512 KiB archives to EOF, side-file DWG/DXF with unchanged hashes, PDF text/page/image, loopback HTTP download/SHA-256, render/registration, positive/negative contour/topology controls, checkpoints/receipts/pre-execution cancellation, PDF publishing and actual file rereading. PDF devices/media come from catalog; user printers are not used.

Reports separate tests from tools with checked assertions/total. Reusing baseline evidence does not duplicate call counts. Partial data, inconsistent expected/actual, substituted handles, and incomplete archives cannot pass. Requests have bounded waits; unknown outcomes stop new writes.

Civil/Map and proprietary-proxy positive branches need the appropriate product/object. Discovery/ordinary-read contracts run where available; missing positive fixtures are SKIPPED. Empty proxy catalogs/unavailable Civil APIs do not count as positive coverage. Missing PDF devices/media are also SKIPPED. All reduce coverage and prevent full success.

JSON includes result, mcp_inventory, and mcp_calls with arguments, JSON evidence, and PNG hashes/decoding. Scenario files live in benchmarks/<run_id>/files, apart from the working DWG. CadCalls counts logical runner requests including setup, not extra host/tool-internal service calls.

The [independent matrix](benchmark-tool-matrix.md) defines 49 tools, prerequisites, and verification limits. Protocol tests and real C# payload compilation against official SDK references do not constitute native AutoCAD execution.

## Native crash investigation

A separate DWG in a working AutoCAD process cannot isolate other files from a fatal process-wide native crash. Hence 0.11.7 requires a separate instance with no documents. The first unexpected failure stops later cases. Lost transport/session yields UNKNOWN, never permission to continue writes. Expected negative checks and explicitly unavailable prerequisites may finish without stopping the suite.

All reads supply mandatory revisions to the real MCP schema. CI runs the complete baseline through actual stdio host, and required fields are checked against tools/list before invocation. report.json preserves requests/responses. Adjacent native-trace.jsonl durably records native begin/end boundaries, snapshot read stages, and actual Lisp execution. It excludes arguments/source/geometry. Begin without end identifies missing confirmation, not crash causality or rollback; consult existing receipts/WorkSafety for commit state.

The reported AutoCAD 2027 fatal crash has not been reproduced in development. A lost connection during the cad_query case does not establish Query as the cause; native OPM/plotting ran earlier. Localization requires report.json, native-trace.jsonl from a new run, the Autodesk error, and any available crash dump. Managed/MCP tests cannot replace native validation.

Coverage must be reported on three independent axes: MCP tools with positive/negative contracts, native edit-operation families, and installed product/runtime failure scenarios. A tool invocation or `cad_edit` PASS does not establish all 98 edit operations. The current report's tool coverage is only the first axis; native safety checks remain separate, using `-ReviewSafetyOnly` and GUI probes described in [validation](validation.md). Do not combine them into a single unsupported full-coverage percentage.


The benchmark tab shows the current case, each CAD request and its result, and a running wait timer. Each event is appended to `benchmarks/<run_id>/progress.log` before waiting for the next response; the log contains request IDs but no request payloads. The final summary remains visible even while the drawing list refreshes. In 0.11.11 the AutoCAD 2027 adapter disables internal OPM palette reads as a temporary crash workaround: `cad_properties` is explicitly skipped. Archive coverage uses a 100-cell table range whose combined response exceeds 512 KiB, then verifies complete reconstruction through `cad_result_get`.
