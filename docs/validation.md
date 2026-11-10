# Validation

This describes current validation procedures. Release-specific evidence is in [CHANGELOG.md](../CHANGELOG.md).

## Automated tests

Run `scripts/build.ps1`, then `scripts/test.ps1` (or `dotnet test tests/CadMcp.Tests`). xUnit tests in `tests/CadMcp.Tests` cover:

- named-pipe framing, broker startup, graceful shutdown, and stale-version replacement;
- operation journals, lost-response recovery without duplicate writes, large-result archives, cancellation isolation;
- edit/acceptance parsing, geometry checks, image calibration, AutoLISP wrappers;
- chat history/attachments, Markdown, Word export;
- Codex/Claude protocols with fake processes in `tests/CadMcp.TestCli`: streaming, follow-ups, cancellation, model selection, refusal of interactive requests, command-line length;
- real MCP SDK over built `CadMcp.Host` stdio: tool listing/read-only annotations, drawing pinning, assistant restrictions.

These tests do not run AutoCAD, real CLIs, or models. Tests run serially because they launch processes/brokers and measure time.

## DWG safety after transaction-policy changes

Automated tests cover retaining completed receipts after journal-write failure/cache eviction and rejecting mixed batches with out-of-transaction actions. They do not prove native rollback.

On Windows with installed AutoCAD, use a separate test DWG only:

1. Submit ordinary geometry with an impossible enforced criterion. Verify created entities are absent after failure and after reopening.
2. Submit `layout_create` plus `line` in both orders. Both must return `SINGLE_OPERATION_REQUIRED` without creating either object.
3. Run `layout_create` alone. Verify `checkpoint.path` exists/opens, SHA-256 matches the receipt, and the new layout is absent from the checkpoint. Open it separately.
4. Apply an impossible enforced criterion to an out-of-transaction action. Require `partial_changes_possible` and checkpoint path; inspect actual DWG state without automatic replay.
5. In an isolated profile, deny writes to recovery. A new out-of-transaction action must return `CHECKPOINT_FAILED` before modifying the DWG. Restore access afterward.
6. Check Undo/Redo and reopening after a successful ordinary batch. Finish Undo, leave chat open at least 10 seconds for background context/document polls, then Redo must restore the whole batch. Compare with chat closed. `CADMCPGUISTABILITY` explicitly polls `cad_context`, `cad_catalog`, `cad_snapshot`, and documents between separate Undo/Redo commands, including linked tables. Repeat on installed AutoCAD 2027 and Civil/Map: base AutoCAD 2025 does not establish their behavior.

`ForRead` does not require document locking. [Autodesk lockDocument documentation](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-RefGuide/files/OARX-RefGuide-AcApDocManager__lockDocument_AcApDocument__AcAp__DocLockMode_ACHAR__ACHAR__bool.html) notes that locking counts as starting a command. Ordinary reads therefore run on the CAD thread without write locks; edits and `cad_edit_preview` retain them. This correction requires live Redo validation; compilation/unit tests are insufficient.

## CI

`.github/workflows/ci.yml` runs on Windows for pull requests and `main`:

1. Installs .NET 8/10 and checksum-verified AutoCAD 2025 reference assemblies from NuGet (`scripts/autocad-references.ps1`). References are excluded from payloads/installers.
2. Restores packages in locked mode.
3. Builds every `.github/ci-projects.txt` project, including .NET 10 adapters/native probes, with warnings as errors.
4. Runs tests and retains TRX results.

## Releases

`.github/workflows/release.yml` builds Windows installers through `scripts/build-installer.ps1`:

1. Repeats CI checks on the release commit.
2. Downloads/checks official Codex 0.159.0 (`scripts/prepare-codex.ps1`).
3. Compiles with Inno Setup 6.3 or newer.
4. Verifies every payload file against its manifest.
5. Writes `validation.json` and `SHA256SUMS.txt`.

PRs changing release tooling/version only build an installer artifact. Public publishing is manually dispatched on `main` (Actions → Release → Run workflow) with the `Directory.Build.props` version, creating pre-release `v<version>`. Notes come from that version's CHANGELOG section. Workflows do not run AutoCAD checks; their results are added to CHANGELOG manually.

## AutoCAD probes

Run manually on a machine with AutoCAD, without working drawings:

- `tests/CadMcp.CoreProbe` / `scripts/test-autocad-core.ps1`: Core Console; `scripts/test-dwg-read.ps1`: DWG reading.
- `tests/CadMcp.NativeProbe` / `scripts/test-autocad-gui.ps1`: AutoCAD/Map GUI (`-Product MAP`).
- `tests/CadMcp.GuiProbe`: multiple-DWG/layout-reading commands, loaded through NETLOAD.
- `tests/CadMcp.ChatPanelProbe`: standalone WPF panel.
- [Live checklist](live-checklist.md).

For 0.11.1 translation/replacement regressions, build `tests/CadMcp.CoreProbe`, then:

```powershell
./scripts/test-autocad-core.ps1 -TextRegressionOnly -DynamicTextFixture 'C:\Program Files\Autodesk\AutoCAD 2025\Sample\ru-RU\Dynamic Blocks\Annotation - Metric.dwg'
```

This starts a separate hidden Core Console with temporary profile/DWG. The standard sample is read-only and cloned into the fixture. Other locales require the corresponding local dynamic-block sample. Missing samples mean unperformed validation, not success. Checks/errors are saved to the probe's `result.json`; success is copied to `artifacts/core-probe-latest.json`.

For 0.11.2 crashes, build `tests/CadMcp.NativeProbe`; installed AutoCAD 2025/2026 is required:

```powershell
./scripts/test-autocad-gui.ps1 -Command CADMCPSHEETREGEN -TimeoutSeconds 1200
```

The probe recreates A1 batches in fresh drawings: frame, table, cross-table formula, notes, combined, after preview, `render_then_edit`, and `frame_explode`. Each step is recorded to `.progress` before execution. `-Variants` selects variants; `-Plan` replays an operation-receipt plan. Results: `artifacts/gui-probe-latest.json`.

## Real CLI probes

Optional subscription-backed probes use a recording CAD fixture and do not modify drawings. After a Release build, run from the repository root:

- `CadMcp.TestCli.exe --live-subagent-test codex|claude <cli>`: read-only assistant;
- `CadMcp.TestCli.exe --live-codex-model-test <cli> [--resume-conversation <id>]`: model selection/conversation continuity;
- `CadMcp.TestCli.exe --live-codex-approval-test <cli>`: Codex CAD permissions;
- `CadMcp.TestCli.exe --cli-config-check <cli>`: MCP/assistant-role configuration acceptance.

## Agent evaluation

`benchmarks/` grades actual post-run DWG state using hidden checks, hard failures (false success, unrequested deletion, out-of-scope edits, fabricated tools, unsupported writes, replay of unknown edits, bypassing Lisp denial), three runs per task, empty-run grader validation, and comparison against the previous release. See [benchmarks/README.md](../benchmarks/README.md); commands: `CadMcp.Host.exe --capture-evidence`, `--grade`, `--grade-selfcheck`.

## Review safety regression suite

Managed/MCP tests in `ReviewRegressionTests`, `ProviderWatchdogTests`, and `ReferenceDownloadTests` cover ambiguous transport recovery, historical save provenance, C# replay before compilation, incomplete verification targets, streaming snapshot budgets, table component selection, Map acceptance rejection before worker calls, checkpoint file evidence, silent Claude processes and stalled download bodies.

On Windows with installed AutoCAD, build `tests/CadMcp.CoreProbe` and run a separate owned Core Console fixture:

```powershell
./scripts/test-autocad-core.ps1 -ReviewSafetyOnly
```

`CADMCPREVIEWSAFETY` refuses interactive AutoCAD and nonempty/out-of-directory drawings. It checks a 3000-vertex mesh's pages, manual removal of a linked table source followed by an unrelated edit, rejection of affected broken dependencies, explicit repair, incomplete C# verification targets, and survival/hash of the first of eleven recovery copies. Success removes only the copies created by this fixture; failures retain their paths in `result.json`. Compile success does not mean the probe ran.

Run GUI Undo/Redo probes separately, including an open idle MCP chat; Core Console does not validate GUI command history. Test each supported installed product/runtime: AutoCAD 2025 .NET 8, updated 2025/2026 .NET 10, 2027, and available Civil/Map variants. Include a controlled post-commit disconnect and reopen checks. Record exact product/update and skipped prerequisites; a missing product is unperformed coverage.

`ConservativeReviewFixTests` exercises actual stdio MCP Host diagnostics, compact archived C# status with persisted replay identity, untouched valid xref-stream PDFs, rejected invalid PDFs, and unchanged/repairable classic PDFs. PDF normalization is linked directly from its production source into the managed test project. Queue admission now uses the same gate for reads and writes, preserving durable journal acceptance before enqueue; installed-AutoCAD concurrency remains a native check. Added disposal of temporary native collections requires the existing geometry probes in installed products.
