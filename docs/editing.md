# DWG editing

The panel grants local CAD MCP tools on every Codex process launch, including resume. `approvalPolicy=never` disables extra dialogs; `mcp_servers.cad.default_tools_approval_mode="approve"` and `cad_edit`/`cad_lisp`/`cad_focus` settings authorize editing tools. The original implementation used a `read-only` filesystem sandbox; current primary C# sessions use [workspace-write](csharp-execution.md). Global user configuration is unchanged. These settings apply to our panel; external MCP clients use their own access policies.

Settings follow the [official OpenAI configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

Get session/document from fresh `cad_context`. Native edits take `operation_id` and `operations_json`. Coordinates are WCS, values use DWG units, and angles are degrees around WCS Z. Read the contract through `cad_edit_help`.

```json
[
  {"op":"layer","name":"Сеть","color_index":3},
  {"op":"line","id":"pipe","start":[0,0,0],"end":[100,0,0],"layer":"Сеть"},
  {"op":"text","position":[0,5,0],"text":"Трубопровод","height":2.5,"layer":"Сеть"},
  {"op":"move","target":"pipe","displacement":[0,50,0]}
]
```

98 operations are supported, including `polyline3d`, `spline`, `mesh`, `sphere`, `cone`, `wedge`, `torus`, `extrude`, `sweep`, `revolve`, `solid_boolean`, and `rotate3d` for 3D. `cad_edit_help` returns the current complete list and fields.

## Everyday 2D operations

Ordinary geometry runs in one transaction: failure before commit rolls back the batch. Operations with external effects are listed below; complete rollback is not guaranteed for them.

`layout_create`, `layout_copy`, `layout_configure`, `viewport`, `image_attach`, `xref_attach`, `block_import`, and all `civil_*`/`map_*` operations must run individually. Mixed batches fail before execution with `SINGLE_OPERATION_REQUIRED`. A separate DWG checkpoint is mandatory; `CHECKPOINT_FAILED` prevents execution. These operations are unavailable in `cad_edit_preview`.

Failures report `partial_changes_possible: true`, `checkpoint` details if created, and `outside_transaction_outcome_unknown` or `map_api_outcome_unknown`. Acceptance checks cannot guarantee rollback of external effects either. Never retry under a new ID: inspect the receipt and drawing. For recovery, open `checkpoint.path` separately without automatically overwriting the working DWG. Database checkpoints do not preserve external-file/service contents.

| Operation | Behavior |
|---|---|
| `text_replace` | Replaces TEXT, MTEXT, attributes, and optionally dimensions/tables/block-definition text. MTEXT replacement touches text spans between formatting codes, preserving fonts, colors, fractions, and paragraphs. Ø/°/± also match `%%c`/`%%d`/`%%p` in mixed strings (±0.5% matches `%%p0.5%`); encoded source receives encoded replacement. Searches written as codes (`%%c`) match stored text and insert the replacement unchanged. Matches never occur inside codes (`%%c`, `%%nnn`, `\U+XXXX`). Fields, formulas, hidden merged-table cells, and locked-layer objects are skipped/counted. |
| `text_translate` | Applies `cad_text_units` translations through `units`: `{unit, text, source}`. A changed `source` is rejected with `TEXT_CHANGED`. TEXT stays single-line; grouped paragraphs redistribute over the original lines, counting unused lines made empty. MTEXT, multiline attributes, mleaders, and table cells retain leading formatting; mixed inline word colors/fonts may be lost and are reported as `formatting_simplified`. Default `fit: "shrink"` first reduces width factor to `min_width_factor` (0.7), then height to `min_height_ratio` (0.6); remaining excess is `overflow`. Fields/formulas and locked layers, including an attribute's block-insert layer, appear in `skipped`. Editing a dynamic-block definition refreshes anonymous copies, like saving Block Editor (`dynamic_blocks_updated`). |
| `text_fit` | Fits existing text without changing content. TEXT/attributes reduce width factor then height to fit `width`; MTEXT wraps at `width` and reduces text height to fit optional `height`. AutoCAD handles Fit/Aligned justification. |
| `offset` | Offsets a line, arc, circle, polyline, ellipse, or spline. `side_point` selects the side relative to the nearest curve point; impossible offsets on that side are rejected. |
| `explode` | Explodes entities. Block attribute values become text (BURST behavior); otherwise attribute definitions remain (EXPLODE behavior). Nested blocks retain attribute values except nonuniformly scaled inserts, counted in `nested_attributes_not_copied`. |
| `join` | Joins end-to-end curves; a base line/arc becomes a polyline. |
| `array_rect`, `array_polar` | Rectangular/polar arrays of ordinary copies, at most 2000. Polar arrays without rotation translate the whole selection around its shared center. |
| `fillet`, `chamfer` | Fillet (radius 0 makes a sharp corner) and chamfer of two XY-plane lines. |
| `polyline_fillet` | Adds radius arcs to lightweight-polyline corners like FILLET's Polyline option, preserving the handle. `vertices` selects original vertex indices; default: all corners. Open endpoints, corners adjacent to arcs, and corners where arcs cannot fit (including neighboring arcs overlapping on a shared segment) remain unchanged and appear in `skipped` with reasons. Variable width is interpolated. No applied corners yields `FILLET_NOT_APPLIED`. |
| `trim`, `extend` | TRIM-style boundary trimming removes only the section between intersections surrounding the pick point. Extend reaches the nearest boundary and measures added distance along the curve. |
| `dimension_angular`, `mleader` | Three-point angular dimension and text multileader. |
| `field_text` | MTEXT with a live length/area/radius/other-property field. `factor` converts units (0.000001 converts mm² to m²). |
| `xref_attach`, `block_import` | Attaches an xref or imports block definitions from another DWG. Effects exceed the edit transaction; separate requests are mandatory (`SINGLE_OPERATION_REQUIRED`). |
| `layer_merge` | Moves entities/attributes from source to target layers throughout layouts and block definitions, removing emptied layers. Layer 0 inside blocks is untouched. |
| `xdata_set`, `xrecord_set` | Application XData (up to 16 KB, strings up to 255 characters without newlines) and XRecords in entity/drawing dictionaries. AutoCAD/vertical namespaces (`ACAD*`, `ACDB*`, `AEC*`, `ADSK*`, `AUTODESK*`, `ACMAP*`) fail with `RESERVED_NAME`. Key deletion erases only the data entry. |

`solid_boolean` reports before/after volumes and rejects an empty result with `EMPTY_SOLID_RESULT`. Bulk/destructive operations (`text_replace`, `layer_merge`, `explode`, `trim`, `join`, `block_import`) first create a DWG checkpoint.

Batches affecting over 250 objects are no longer rejected by quality review: reports use `REVIEW_SKIPPED` and may shorten readback (`entities_truncated`). Acceptance still checks all objects; `changed_handles` lists all changes for operation-based `cad_verify` and receipts. If even that list is too large, it is omitted (`changed_handles_omitted`), and `cad_verify` requests smaller chunks. Review is descriptive: internal failure produces `REVIEW_FAILED` and never cancels the edit.

## Dry run

`cad_edit_preview` runs the same plan in a transaction that always aborts, returning results, readback, quality review, and `expectations_json` checks. Failed criteria are displayed rather than aborting preview. Geometry stays unchanged and no Undo step is added; returned handles are temporary. The response includes `plan_hash`: `cad_edit` with the same plan/criteria, `preview_hash`, and `expected_revision` equal to `previewed_revision` executes exactly that preview. Changed plans, documents, or revisions yield `PREVIEW_MISMATCH`. The CAD worker signs the hash with its secret, so only preview can issue one. Preview activates its drawing, like edits. Layouts, viewports, rasters, xrefs, block imports, and Civil/Map cannot be previewed because their effects exceed the transaction. DBMOD may still mark the drawing dirty; the original state is returned as `document_state_before_preview`. Read-only assistants cannot preview.

Code/help: `src/CadMcp.Core/ModifyPlan.cs`, `src/CadMcp.AutoCAD/ModifyOperations.cs`, `cad_edit_help`. Plan parsing, replacement, and hashing have unit tests; initial native operations still required live AutoCAD validation.

`id` links a created entity to later `target` actions in a batch; existing entities use `handle`. Read layers, blocks, styles, and linetypes through `cad_catalog`. Block inserts use existing definitions; `block_define` creates definitions from existing entities. Attributes use `{ "TAG":"Значение" }`. Use transform tools to move/rotate attributed blocks. The native path does not edit xrefs; only explicitly listed special Civil/Map operations are available.

Pre-commit native failures roll back the entire ordinary batch. Final entities are read before commit; an oversized result rolls back with advice to reduce the batch. Use the returned revision next. When Undo and the COM document are available, StartUndoMark/EndUndoMark groups the batch without closing an existing user group. Responses report actual grouping availability. Refresh context after manual Undo.

## AutoLISP: user-controlled execution

Before `cad_lisp`, a scanner flags process/COM creation, file/registry writes, code loading (`load`, `NETLOAD`, `APPLOAD`), dynamic code (`eval`/`read`), and reactors. Commands opening, closing, or switching drawings are always rejected. Scanning is only advisory: blocklists are bypassable, so the user decides.

Choose policy with `CADMCPLISP` or the chat panel; it persists in `%LOCALAPPDATA%/CadMcp/settings.json`. Agents cannot change it: scripts mentioning `CADMCPLISP` are rejected, the command refuses while an agent script runs, and command-based relaxation requires a confirmation-window button.

| Policy | Behavior |
|---|---|
| `ask` (default) | AutoCAD displays code/risks in a separate dialog. The agent receives `waiting_for_user_confirmation_in_AutoCAD` and waits through `cad_operation_status`. Decline: `LISP_DENIED`; no answer for 10 minutes: `LISP_APPROVAL_TIMEOUT`. |
| `auto_safe` | Unflagged code runs immediately; other code requires confirmation. |
| `allow` | Runs without confirmation. |
| `deny` | Rejects with `LISP_DISABLED`. |

`CAD_MCP_LISP_POLICY` overrides persisted policy for automated runs. Do not ask until AutoCAD restarts enables `auto_safe` for the session or until the next policy selection, including the chat panel. Scripts temporarily suppress object snaps (OSMODE bit 16384) and set FILEDIA/CMDDIA to 0. Original settings are restored; successful scripts retain only values they explicitly changed.

Approved scripts start at the next AutoCAD idle point with the target active and no command running. Changes during the wait cause `REVISION_CONFLICT` before execution. Pending approval blocks other writes with `BUSY_LISP`. Options such as `"_Close"`, `"_New"`, and `"_eXit"` after an unfinished command are flagged as possible drawing switches rather than blocked. First commands (including earlier-executing `defun`/`lambda` bodies), `command-s`, dotted/dashed forms (`"_.CLOSE"`), and `vla-open` on Documents remain rejected. Pass a command's prompt responses within its same `(command)` call. The scanner also flags acad.pgp external commands (`START`, `DEL`, etc.), file-writing commands (`SAVEAS`, `WBLOCK`, `PLOT`…), `atoms-family`, `SendCommand`/`PostCommand`, document methods via `vlax-invoke`, and changes to `SECURELOAD`/`TRUSTEDPATHS`.

`cad_lisp` accepts up to 65536 characters inside `progn`, multiple expressions, and Russian strings. A managed callback supplies code after rechecking document/revision before startup. Use fully specified `command-s` with underscore-prefixed global command/option names (`"_.-LAYER"`, `"_C"`); Russian AutoCAD rejects unprefixed English names. Convert UCS command coordinates through `trans` when needed.

`queued` confirms scheduling only. `cad_operation_status` reports queued/running/completed/failed/not_found and returns results. After Lisp, independently read changed entities or inspect produced files. A callback does not establish geometric correctness. Failures may leave partial changes; scripts can alter Undo groups and active DWG. No universal rollback of external effects is promised.

Stopping the model does not cancel an already queued script. Use Esc in AutoCAD for running Lisp. CAD command cancellation/error marks the operation failed with a partial-changes warning. Without a callback/completion event, a receipt can remain unfinished; no automatic retry occurs.

Since 0.3.0, receipts persist under `%LOCALAPPDATA%/CadMcp/operations/<worker-session>`: queued before execution, then running and result. Recent completed entries are cached; the old 128-operation cap was removed. Exact replay with the same `operation_id`, payload, and `expected_revision` returns the prior result; changed requests sharing an ID conflict. Post-commit journal failure is never described as rollback: the response carries journal_warning and stays in memory while disk retains an unfinished entry.

New CAD sessions/document IDs differ after restart. Old journals are audit records; not_found in a new session does not prove no change occurred. Corrupt persisted entries prevent confirming safe replay. Inspect the drawing before retrying any unknown outcome.

## Table dependency isolation and recovery copies

Linked formulas are recalculated only in connected components reached from tables changed by the batch (including refreshed schedules). An unrelated ordinary entity edit does not rebind the whole formula graph. Missing sources still fail affected edits; unrelated components remain intact and may be reported as warnings when table dependencies are inspected. To remove a broken formula explicitly, assign a constant to its dependent cell with `table_cells`; this is a requested repair, not automatic deletion. Automatically updated tables are added to readback and acceptance counts.

Recovery DWGs are retained until explicit user cleanup; the plugin no longer deletes old copies after ten newer ones. Storage therefore grows. Inspect `%LOCALAPPDATA%\CadMcp\recovery` and retain copies needed by unknown/failed operations before manually cleaning up. Checkpoint failure, including insufficient space, prevents the requested protected operation. `cad_diagnostics(verify_checkpoints=true)` checks available checkpoint size/hash without opening or changing drawings. Omit `document_id` to inspect previous worker sessions. A receipt does not guarantee that a user has not removed its file.
