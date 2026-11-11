# Historical phase 3B checklist (0.1, reading): working DWG requires agreement

Use a user-selected mixed-object DWG on a separate copy. Record original hash/DBMOD. Manually establish reference values for 5–10 objects, including overridden dimension text, arc polyline, block, and SPDS.

1. NETLOAD the DLL in AutoCAD 2025, open the palette, and check worker descriptor creation/removal.
2. Start broker/external MCP client; verify context name, units, space, and document_id.
3. Snapshot: compare top-level entity count with AutoCAD, availability/truncation, pages, and Russian text.
4. Read reference handles; compare coordinates/lengths. Check selection, invalid handle, DWG switching, and stale revision.
5. Capture PNG; compare arcs/text/dimensions/SPDS/layers with AutoCAD. Reject geometrically distorted previews. Verify no DWG change.
6. For SPDS, record class, display, ordinary properties, and special parameters. The generic reader does not replace this experiment.
7. Check official Codex/Claude authentication. One conversation each: model calls MCP, receives an image, explains the selected region. Record transmitted data and actual subscription limits.
8. Check Stop during model waits/busy CAD, concurrent clients, document closure, and CAD exit. Unavailable sessions must return clear errors.
9. Repeat basic reads under Map 3D. This does not validate Map vertical objects.
10. Compare DBMOD/saved copy. The 0.1 prototype has no write tools; separately note temporary selection and enabler-specific open behavior.

Deliver a log of versions, confirmed capabilities, errors, and response examples. Current scope is AutoCAD, Map 3D, and Civil 3D 2025–2027, excluding LT/earlier releases. One installation cannot validate others or full SPDS editing.

## 0.2.0 editing addendum

On a separate fixture DWG, test every entity type; layers/Russian TEXT/MTEXT; attributed-block insertion/copy/transform; dimensions/hatches; readback/Ctrl+Z; second-action failure rolling back the first; stale revision/exact operation_id replay; cache invalidation after manual edits; Lisp/error/Esc/status; chat restoration after restart.

`tests/CadMcp.NativeProbe` compiles the production executor into a separate DLL and creates a blank DWG without user files. Its old missing-profile startup failure was fixed. On September 29, 2026, AutoCAD 2025 and Map 3D 2025 each passed 20 GUI checks, including Undo, Lisp, Esc, and document switching. See [0.3.0 evidence](../CHANGELOG.md#030-preview). Mixed working DWGs, special SPDS/vertical objects, and other installations remain separate work.

Phase 3B above is historical; the no-write-tools statement applies to 0.1.

## 0.11.2 crash and Claude Code addendum

These 0.11.2 steps had not yet been run in AutoCAD. Use DWG copies.

1. Run `scripts/test-autocad-gui.ps1 -Command CADMCPSHEETREGEN -TimeoutSeconds 1200`; add `-AutoCADDir` for 2026. All variants, including `render_then_edit`/`frame_explode`, must pass. After a crash, the last `.progress` line identifies the step.
2. Replay the failed operation plan using `-Plan <receipt from %LOCALAPPDATA%\CadMcp\operations>`.
3. In chat, prepare an A1 sheet with SPDS frame, formula table, and notes. Each turn receives an automatic preview. Repeat three turns, then `REGEN` and switch layouts; no crashes.
4. `explode` an SPDS title block, undo with Ctrl+Z, then close without saving; no crashes.
5. Non-table/non-block edits must display immediately without `REGEN`; layers disabled by `layer` must turn off.
6. Claude: refresh models, select model/effort, verify answer attribution. Attach a PDF from a directory containing parentheses and ask a web-search question.

## 0.11.1 paragraph isolation and dynamic inserts

Core Console passed 22 checks on October 8, 2026; full GUI needs separate DWG-copy validation.

1. Create nearby matching-XY TEXT lines in model, two layouts, two block definitions, and different Z planes. `cad_text_units(scope: all, include: text,block_definitions)` must not group across spaces/blocks/elevations. Translate one valid paragraph; other text must remain unchanged.
2. Insert one dynamic block twice and alter the second insert's parameter. Replace definition TEXT with `text_replace`, `include: [text,block_definitions]`. Both inserts must show new text while retaining distinct parameters; Ctrl+Z restores text in one step.

## 0.11.0 properties, proxies, translation, and fillets

Use separate DWG copies. These steps had not yet been performed for 0.11.0.

1. Build `tests/CadMcp.CoreProbe`; run `scripts/test-autocad-core.ps1`. It creates a blank DWG and checks rectangle fillets, three-line TEXT paragraph translation, color-coded MTEXT, multiline attributes (text/width reread after `text_fit`), locked-layer skips, proxies, and properties.
2. In full AutoCAD, compare `cad_properties` for a line, MTEXT, dynamic block, and Civil/Map object with the Properties palette. Repeat 50 times, then trigger GC (`(gc)` or prolonged work); no crashes.
3. Open a Civil/other-enabler DWG in plain AutoCAD. `cad_proxies` must list groups and inspect graphics through `handles_json` without dirtying the drawing.
4. With TEXT paragraphs, formatted MTEXT, single/multiline attributes, mleaders, tables, and a dynamic block, run `cad_text_units`, then `text_translate` through `cad_edit_preview`/`cad_edit`. Visually verify frames, `%%c`/`%%d`, accurate `overflow`, translated anonymous dynamic copies, and one-step Ctrl+Z.
5. Compare `polyline_fillet` on variable-width/arc/short-segment polylines with FILLET's Polyline option at the same radius.
