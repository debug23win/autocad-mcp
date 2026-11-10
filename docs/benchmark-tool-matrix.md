# Independent full-coverage benchmark design

This is a design, not an executed run. Inventory: 49 MCP tools from current CadTools attributes, including cad_csharp.

| Tool | Fixture | Required assertions | Prerequisites and side effects |
|---|---|---|---|
| cad_sessions | Broker/current worker; protocol fixture for second worker | Current session/process found; stale descriptors never treated as live; pin returns expected context | Without a second real worker, its branch is SKIPPED; never fabricate two sessions. |
| cad_documents | User sentinel DWG and two new benchmark DWGs | Unique document_id, correct active flag, stable project_key after fixture reopen | Never close user documents. |
| cad_context | mm/Model fixture and paper layout | Exact scope/revision/INSUNITS/units; active/inactive editor availability; stale revision accepted for reads | Unit/Model changes block further writes. |
| cad_catalog | Known layer/block/attribute/style/linetype and two layouts | Exact names/handles/settings, dictionary enumeration; read preserves revision/DBMOD | Empty catalog over nonempty fixture cannot PASS. |
| cad_runtime_status | Controlled queued task or Lisp awaiting approval | Own operation_id/phase/cancel flag agree with receipt; available while UI thread busy | Do not run dangerous/heavy kernel operations to create busy state. |
| cad_diagnostics | Risky fixture operation requiring checkpoint | Phase/version/operation_id/path agree; checkpoint exists and hash matches | Never modify old user recovery files. |
| cad_cancel | Two queued jobs with different OwnerIds behind fixture gate | Only own ID/owner cancelled; own receipt CANCELLED before startup, other task executes | Deterministic gate required; create/cancel races are unreliable; unavailable gate means SKIPPED. |
| cad_operation_status | Own completed/failed/cancelled/queued operations | Exact original request/ID/document; state/result agree; recovery never uses new ID | queued/unknown/not_found never prove no changes. |
| cad_operation_list | Three own operations with different outcomes | Expected IDs, owner/document filtering, limit/since/order; receipts are not fresh geometry reads | Historical/unreadable receipts explicitly marked. |
| cad_edit_help | Actual Host edit-help resource | Valid JSON/schema; operations/fields match EditPlan.Fields | Nonempty text alone cannot PASS. |
| cad_spds_help | Actual Host spds-help resource | Table/formula/SPDS templates and exact dimensions match code | Help does not prove GOST-compliant output. |
| cad_steel_catalog | Fixed 20Б1 section | Exact code/dimensions/root radius/mm/density 7850; unique codes | Nominal mass is not structural capacity. |
| cad_edit | Isolated 2D/3D/annotation/block/table families | Independent family readbacks; exact replay adds no duplicates; conflicting ID/stale revision rejected before writes | 48/49 MCP tools do not cover all EditPlan operations; maintain a separate operation map. |
| cad_edit_preview | Contour and existing text_replace | Correct temporary readback; no entities/edits remain; exact hash commits, altered plan/revision rejected | Preview may set DBMOD; layout/Civil/Map prohibited up front. |
| cad_csharp | Fixed source in fresh fixture; exact runtime references | Actual compilation; Run receives correct Database/Transaction/ct; independent line/circle reads; checkpoint/receipt; compile/signature/throw/cancel negatives | Full process trust, not sandboxed. Only reviewed fixed code; no files/processes/manual commit. Checkpoints cannot roll back external effects. |
| cad_verify | Known line/circle/solid/erased handles and native operation ID | Actual length/radius/volume/position; wrong expected fails; erased/missing not live; receipt aliases correct | Do not grade only a final success boolean. |
| cad_entity_get | Model line, paper viewport, dimension, Xrecord | Geometry/type/layer; paper read without layout switch; formatted dimension; extension dictionary | Nested handle must fail; use search path/root_handle for it. |
| cad_snapshot | N known entities, limit=N-1 | Count/coverage/captured_revision/truncated; immutable after fixture edit | Empty snapshots do not cover truncation. |
| cad_query | Layer/type/text snapshot with multiple pages | Exact filter sets; no duplicate/missing pages; edit produces historical=true with original data | Reject another document's snapshot. |
| cad_search | Model/paper/nested block/xref/view/selection/viewport | Every scope/filter/bounds; WCS/path/root_handle; exact page sets and bounds exclusion | Current runner covers only model/text/nested/paging. |
| cad_result_get | 200 MText × 2000 chars, batches of 20; >512 KiB search | Real archive_id; reconstruct complete JSON; exactly 200 markers/handles; captured_revision/historical | Respect caps; archive_id alone does not prove contents. |
| cad_focus | Temporarily activated fixture and top-level line | Context selection exactly requested handle; unchanged geometry/revision/DBMOD | Activate fixture because mutations currently restore user active DWG; restore active document afterward. |
| cad_render | Known model/paper geometry, explicit top view | Decodable/nonblank PNG, dimensions/image_id/path; attach modes/content blocks; unchanged camera/revision/DBMOD | Nested handle fails, root_handle succeeds; complex visual fidelity requires separate review. |
| cad_image_register | Real render image_id and three noncollinear pairs | Affine matrix, RMS/max residual, corners match synthetic mapping | Reject degenerate/nonplanar/foreign images; math tests do not establish real visual registration. |
| cad_image_point | Same registration, independent midpoint | Both directions roundtrip <=1e-6; edit yields historical=true and old captured_revision | Exact errors for out-of-image/unknown registration. |
| cad_reference_calibrate | Owned 100×100 PNG, known hash | Similarity: (10,90)→(0,0), (90,90)→(80,0), (50,50)→(40,40); separate projective test; zero residual | Host-only; reject degenerate/nonplanar anchors. |
| cad_reference_point | reference_id and independent checkpoint | Roundtrip/WCS z; modifying owned PNG after fit produces hash mismatch | Modify fixture files only, never user attachments. |
| cad_reference_compare | Two fixture PNGs with known rectangle contours | Same contour: IoU=1/deviation=0; half-width shift: IoU=1/3; correct hashes/dimensions | Supplied-contour comparison, not automatic vision or 3D proof. |
| cad_takeoff | Contour, two layers, block attributes, model/paper | Lengths/areas/count/by_type/blocks/attrs/CSV quoting; scopes/layer filters | Current runner checks aggregate length/area only; do not merge hatch/boundary area without by_type. |
| cad_text_units | TEXT paragraph, MTEXT, attribute, table, mleader | Exact full text/keys/frame/group_lines/pagination; translation source guard | Never fabricate OCR/translation; locked layers explicitly skipped. |
| cad_outline | Model, two layouts, block/text/style | Exact model count/types/sheets/tab order/layer counts/styles/text sample; deterministic repeats | Known 0.11.3 DictionaryEntry failure remains FAIL until fixed, never fallback-and-pass. |
| cad_file_inspect | Saved fixture DWG/DXF, then unsaved editor marker | Side database sees saved content without marker; coords/text/dimensions/attributes/scope/pages; unchanged source hash | Never SaveAs the working DWG; owned reference files only. |
| cad_changes | Capture revision; add/move/erase fixture handles | Net added/modified/erased, author/source/revision/since/include_non_entities, complete=true | Incomplete journals cannot prove absence of foreign edits. |
| cad_review | Known duplicate/self-intersection/text-fit/solid collision and clean control | Expected problems at correct handles/severity; clean control has no errors | Nonempty report alone is insufficient; match codes/categories to implementation. |
| cad_solid_get | 10×20×30 mm box | Volume=6000, nonempty topology IDs/current revision; reread IDs after topology edits | Missing kernel/solid capability explicitly fails/skips; never infer volume solely from inputs. |
| cad_assembly_get | Catalog-profile beam, length 1000 mm | Recipe/mark/material/profile/length; mass/volume match catalog/units; reread updates | Geometry/mass is not a strength calculation. |
| cad_table_get | Linked tables, merged/resized/paged cells | Numeric values/formulas/field codes; sizes/merged ranges/offset pages | Current runner covers four cell values, not the full contract. |
| cad_table_dependencies | Same linked tables with stable cell IDs | Exact graph nodes/edges/expressions; source update; cycle rejection preserves graph; resize preserves identity | table_get does not replace graph-tool checks. |
| cad_properties | 37.25-unit line on known layer | Real nonempty OPM palette and semantic Length/layer values, accounting for locale | Unavailable OPM means SKIPPED; error/empty palette cannot PASS. |
| cad_proxies | Proxy-free baseline and licensed known-proxy DWG | Baseline counts=0; positive class/app/flags/owner/dictionary key, bounded samples/graphics | Missing real proxy fixture means positive branch SKIPPED; zero counts do not cover inspection. |
| cad_release_check | Valid sheet and deliberately bad ####/plot setup | Valid has no errors; bad has expected codes/handles; distinguish warnings; layout scope | Missing PC3/fonts skips affected valid variant; test expected refusal separately. |
| cad_export | Unique output directory, known layout/device | Actual DXF/PDF bytes/hash; one PDF page/marker; collision refusal without overwrite; reference readback | Missing PDF device skips PDF; still run DXF. |
| cad_publish | Two paper layouts, unique empty directory | Two PDFs in order; real JSON/CSV hashes/bytes; collision refusal; accurate partial manifest | Missing PDF device skips; partial output is not overall PASS. |
| cad_lisp | Owned fixture layer; bounded entmake line/circle | Queued→terminal, independent geometry; deny/decline/cancel/error; unknown stops writes; separate approval timing | Never change policy/bypass denial; missing approval UI means SKIPPED. |
| cad_vertical_capabilities | Actually loaded Civil/Map assemblies | Runtime versions/signatures; explicit unavailable without API | Unavailability contract may pass; positive vertical cases stay SKIPPED. |
| cad_vertical_catalog | Real TIN/alignment/profile/network/OD fixtures | Exact names/handles/API versions; bounded child collections | Missing API/fixture skips positives; empty catalog proves nothing. |
| cad_vertical_get | Real z=x+y TIN, alignment/profile/network, Map OD | (1,2)→3; exact child/OD scalar fields; per-point error outside TIN | Requires real licensed products/fixtures; otherwise SKIPPED, never substituted DBObjects. |
| cad_pdf_read | Deterministic two-page text PDF, raster page, encrypted PDF | Exact page/text offsets/text; actual decoded/nonblank PNG/dimensions; attach modes; unchanged original hash | render_error cannot pass rendering even with IsError=false/text; images cannot bypass encryption. |
| cad_web_download | Owned loopback HTTP server, known bytes/hash/redirect; optional pinned HTTPS | Real GET, bytes/hash, unique path/read_with; redirect/oversize/cancel cleanup/no overwrite | Loopback does not test external TLS; unavailable network skips the external variant. |

## Independent verification architecture

1. Derive canonical inventory from actual Host `tools/list` or `McpServerToolAttribute` reflection; compare with declarative `tool -> case IDs`. New tools without scenarios must be `UNCOVERED`, not removed from totals or SKIPPED as unimplemented. There are 49 tools including `cad_csharp`; internal `benchmark_create`/compile-reference handshakes are excluded.
2. Separate worker-native and actual Host-tool executors. Direct worker `Func<Request,...>` calls miss Host-only tools, `IsError`, content blocks/PNG, pin/helper rules, and MCP JSON normalization. Use a dedicated fixture-pinned host; `benchmark_create` remains internal setup. Never reuse a chat host pinned to a user DWG.
3. Distinguish invocation, checked assertions, nominal PASSED, expected-refusal PASSED, conditional nominal SKIPPED, and UNKNOWN. Civil API refusal cannot pass real-surface reads. Report `tests passed/total`, `tools with nominal evidence/49`, `negative contracts checked`, `skipped prerequisites`, and `uncovered` separately.
4. Mutate only current run_id registry fixtures. Focus/view/selection require explicit safe fixture activation and restoration of the user active document; background reads must not add write locks/Undo marks. Check session/document/units/Model before writes except prepared sheet cases. Record sentinel name/revision/DBMOD/selection/view before/after; never save/close/clean its data. This historical sentinel design is superseded for full native runs by 0.11.7's separate-process/no-open-DWG requirement.
5. Files belong to a unique run root: seed DWG, DXF/PDF/CSV/JSON, images, downloads, encrypted fixtures. Never mutate a user attachment. Never change SECURELOAD/TRUSTEDPATHS, Lisp policy, registry, or templates. PC3/fonts/vendor APIs are explicit SKIPPED prerequisites when missing.
6. `cad_cancel` needs a safe deterministic gate for accepted tasks, not create/cancel races, heavy kernels, or infinite code. Own cancellation and foreign-owner isolation are separate assertions. An already completed job/empty queue does not cover positive cancellation.
7. PDF/data/download fixtures must be synthetic with pinned bytes/hash/text. Loopback tests real HTTP without external credentials; HTTPS is conditional. Grade PDF text, page/text pagination, and raster separately; text does not compensate for render_error.
8. Archive cases must obtain a real oversized result, never invent an ID. Bounded setup: 200 MText × 2000 characters in batches of 20, details search, then complete JSON reconstruction. If thresholds change, use measured size and a fixed upper limit, never unbounded generation.
9. Persist raw Request/Response or MCP result/content before normalization, scope/revision, input/output hash, case/tool ID. Expected values must be fixture constants/independent calculations, not `receipt.acceptance=passed` or actual filled from expected.
10. After an accepted mutation loses its reply, consult only the original operation_id receipt. UNKNOWN stops writes and cleanup. Independent conditional read failures need not stop other host/read cases in this design; actual 0.11.7 runner stops on the first unexpected failure. Scope/owner/session changes stop all writes.

## Timing and C#/AutoLISP

- Use Stopwatch for case/total wall time. Separately report reliable approval_wait_ms, network_wait, compile_ms (real `cad_csharp`), execution, and verification; never invent CPU time or subtract unknown waits.
- Compare three explicit generators with identical geometry/independent assertions: native `cad_edit`, real source `cad_csharp`, and `cad_lisp`. A legacy C#-named cad_edit case does not cover compilation/Run.
- Refused/unavailable Lisp approval stays SKIPPED. C# compile errors/invalid signatures are expected negatives, not substitutes for successful compilation/execution.
- One fast call does not prove performance. Repeats need separate run IDs/summaries; never combine failed runs and successful continuations into one clean run.

## Independent extended-runner negative checks

- Missing coverage map entries, duplicate case/tool IDs, empty checks, counting invocation as assertion coverage.
- False positives from empty palettes/proxies/vertical collections; missing APIs/PC3/fonts/renderers/network must yield explicit SKIPPED prerequisites.
- Incorrect bytes/hash, empty/invalid PNG, render_error with valid text, swapped pages/text offsets, incomplete/truncated/archive evidence without reconstruction.
- Shifted geometry, wrong nested root handles, revision mismatch, duplicate pages/next_offset, duplicated entities replacing missing ones.
- Path escapes, overwrite collisions, partial files after cancellation, reference inspection reading unsaved editor content.
- Read-only-helper mutations/focus/cancel; pin violations; foreign-owner cancellation; writes after unknown.
- C# compilation/signature/live-DBObject serialization failures; Lisp denial/refusal/not-started/terminal mismatch. None can pass generation.

Production files were unchanged during this design review. This document proposes coverage; its real native/host/side-effect/conditional scenarios had not yet been executed at that stage.

## Concrete implementation payloads

Document calls use actual fixture-registry `session_id`/`document_id`; writes need fresh `expected_revision` and unique `operation_id`. Handles `"2A"`, `"2B"`, and `"2C"` are typed placeholders for preceding real results, never usable IDs. Real MCP `*_json` parameters are **JSON strings**; decoded arrays below aid readability. Validate every `operations_json` with `EditPlan.Parse` before execution. Revision-required reads must also supply expected_revision per tools/list.

### SOLID — cad_edit / cad_solid_get / cad_verify

```json
{
  "operations_json": [
    {"op":"layer","name":"B_SOLID"},
    {"op":"box","id":"solid","center":[10000,0,0],"length":10,"width":20,"height":30,"layer":"B_SOLID"}
  ],
  "expectations_json":{"entity_count":1,"enforce":true}
}
```

Extract the `solid` handle. `cad_solid_get(handle=<solid>)` must return volume=6000, nonzero faces/edges, and current revision. `cad_verify(handles_json=[<solid>], expectations_json={"entity_count":1,"checks":[{"handle":<solid>,"property":"volume","expected":6000,"tolerance":0.000001}]})` independently rereads it. For topology-ID instability, select a real edge ID, apply `solid_fillet` radius=1, then reread IDs; never reuse stale IDs. Unsupported filleting fails that topology case, not skips the baseline box-volume check.

### ASSEMBLY — cad_edit / cad_assembly_get / cad_steel_catalog

A geometry oracle independent of external catalogs:

```json
{
  "operations_json":[
    {"op":"layer","name":"B_ASSEMBLY"},
    {"op":"assembly_create","id":"beam","kind":"beam","position":[12000,0,0],"parameters":{"length":1000,"width":100,"height":200,"web_thickness":5,"flange_thickness":8,"root_radius":0},"mark":"B-1","material":"S235","density":7850,"show_annotations":false,"layer":"B_ASSEMBLY"}
  ]
}
```

Independent area: `2*100*8 + (200-2*8)*5 = 2520 mm²`. At 1000 mm, expect 2520000 mm³ and 19.782 kg. Check explicit solid volume/mass, not an unqualified approximate annotation. `cad_assembly_get(handle=<beam>)`: kind=beam, mark=B-1, material=S235, recipe.length=1000, explicit mass scope. After `assembly_update(handle=<beam>,parameters={...same dimensions...,"length":2000})`, read back doubled volume/mass. Separate catalog variant: `parameters={"length":1000,"profile":"20Б1"}` with dimensions/root radius from `cad_steel_catalog`; never mix profile and explicit dimensions.

### RENDER / IMAGE — cad_render, cad_image_register, cad_image_point

```json
{
  "width":512,"height":384,"view_name":"top","handles_json":["2A"],"attach":false
}
```

`2A` must be a real top-level box/polyline in **one** model space. Decode PNG and check dimensions/nonblank content. Missing files/`preview_file_warning` cannot pass saved-file checks. `attach=false` with an existing file returns text only; `attach=true` adds ImageContentBlock. Preserve fixture camera/DBMOD.

Use actual returned width/height/image_id. For a mathematical registration test:

```json
{
  "image_id":"<returned image_id>",
  "control_points_json":[
    {"pixel":[0,0],"world":[0,0,0]},
    {"pixel":[512,0],"world":[100,0,0]},
    {"pixel":[0,384],"world":[0,100,0]}
  ]
}
```

`cad_image_point(points_json=[[256,192]],direction="pixel_to_world")` → [50,50,0]; reverse [[50,50]] → [256,192]. Evidence must state that synthetic anchors check math, not proven visual georegistration. After editing, require historical=true with unchanged captured_revision.

### HOST REFERENCE — calibrate / point / compare

Create an owned 100×100 PNG with known hash in run root. `cad_reference_calibrate` payload:

```json
{
  "image_path":"<run-root>/reference.png",
  "model":"similarity",
  "invert_pixel_y":true,
  "control_points_json":[{"pixel":[10,90],"world":[0,0,0]},{"pixel":[90,90],"world":[80,0,0]}]
}
```

`cad_reference_point(reference_id=<returned>,points_json=[[50,50]],direction="pixel_to_world")` → [40,40,0]; reverse → [50,50]. Replace only this owned PNG with another valid same-size PNG and require source-hash refusal; use a new file/ID afterward.

For `cad_reference_compare`, A=[[0,0],[0.5,0],[0.5,1],[0,1]] compared with itself gives IoU=1/deviation=0. B=[[0.25,0],[0.75,0],[0.75,1],[0.25,1]] gives IoU=1/3 with discrete-sampling tolerance. Count positive/negative threshold checks separately; these are supplied contours.

### ARCHIVE — cad_result_get

On `B_ARCHIVE`, create 200 MText in ten batches of 20: `height=1,width=200,text="ARCHIVE-<i>:" + "x"*2000`, position [20000+100*(i%20),100*(i/20),0], i=0..199. Check actual serialization size under the 65536-character plan limit. Generate bounded strings/coordinates in runner C#, not a model/Lisp.

```json
{"options_json":{"scope":"model","layer":"B_ARCHIVE","details":true,"limit":500,"offset":0}}
```

If archive_id appears, do not skip as partial. Read `cad_result_get(archive_id=<id>,offset=0,limit=32000)` using actual `next_offset` to EOF. Concatenate **text**, parse JSON, and verify 200 unique handles/all markers. Full reconstruction is the only permitted partial transport for archive-aware helpers; reconstructed evidence must be complete. Limit to 200 pages/8 MiB; excess fails. After edits, require historical with unchanged captured_revision. If 200 MText unexpectedly misses threshold, allow one bounded extra setup batch/reread within a predefined fixture cap; never loop indefinitely.

### EXPORT / PUBLISH / RELEASE_CHECK

Create B_SHEET_1/B_SHEET_2 only in the benchmark DWG, each through a **separate** `layout_create`. Separate `layout_configure` actions use `device="DWG To PDF.pc3"`, `media_name="ISO_full_bleed_A3_(420.00_x_297.00_MM)"`, `paper_units="millimeters"`, `paper_rotation=0`. Missing device/media preserves exact prerequisite error and skips positive PDF/export/publish, not DXF/side inspection. Never substitute an arbitrary printer.

Place `spds_sheet` with format=A3, designation=BENCH-1/BENCH-2, drawing_title="Benchmark", sheet="1"/"2", sheets="2", plus distinct known geometry. Check paper/frame/plot compatibility with `cad_release_check` and independent render; layout existence cannot pass publishing.

```json
{"format":"dxf","path":"<run-root>/export/fixture.dxf"}
```

```json
{"format":"pdf","layout":"B_SHEET_1","path":"<run-root>/export/sheet1.pdf"}
```

```json
{"output_folder":"<run-root>/publish","layouts_json":["B_SHEET_1","B_SHEET_2"]}
```

Runner helpers create directories only within unique run root. Independently check every PDF/DXF through FileInfo/SHA256 against returned bytes/hash; read PDF with `cad_pdf_read` (page_count=1, expected sheet marker or actual PNG), DXF with `cad_file_inspect`. Exporting again to an existing **owned** path must refuse and preserve bytes/hash. Compare JSON/CSV manifests with actual files/input order; partial publication is not overall PASS.

After DXF export, create benchmark MText "UNSAVED_AFTER_EXPORT". Full pagination through `cad_file_inspect(path=<fixture.dxf>,scope=model,limit=5)` must not see it; live `cad_search` must. DWG variants require a separately saved fixture reference from an owned side database/safe fixture-save, not the blank seed as positive complex-geometry evidence.

### CANCEL / RUNTIME / RECEIPTS

Sending a short `cad_edit` then cancelling immediately cannot reliably require CANCELLED: a native edit may already finish. That is not a defect.

Use an internal **fixture-only deterministic gate** without blocking user drawings:

1. Register two queued jobs with owners `benchmark-<run>-A`/`benchmark-<run>-B`, each with known operation_id. Hold both before execution/cancellation checks. Existing owner guards need a verified opaque sub-owner token or explicit scheme; unsupported owners yielding scope errors do not test cancellation.
2. `cad_runtime_status(document_id=<fixture>)` must expose expected jobs/phases while gated.
3. Owner A calls `cad_cancel(document_id=<fixture>,operation_id=<A>)`; its status becomes cancelled with Error.Code=CANCELLED/no geometry. B remains uncancelled.
4. Release B, wait for completed, independently read its geometry. `cad_operation_list` includes both outcomes; check `since`/`limit` separately.
5. Expected negative: another owner cannot return/cancel A.

Without a deterministic gate, positive cancellation remains SKIPPED with reason; no-op cancellation on an empty queue is insufficient. Queued Lisp awaiting approval is a conditional alternative: policy=allow may execute immediately, and user policy must never be changed.

### CIVIL / MAP — conditional coverage

Always call `cad_vertical_capabilities`/`cad_vertical_catalog`. Missing APIs must be explicitly unavailable: a checked negative contract. Real positive `cad_vertical_get` requires Civil surfaces/networks and Map OD, not plain AutoCAD 2027.

Minimal Civil fixture: standalone `civil_tin_create(name="B_TIN",vertices=[[0,0,0],[10,0,10],[0,10,10]])`; `cad_vertical_get(handle=<returned surface>,sample_points_json=[[1,2],[2,1],[20,20]])` must return two elevations of 3 and a per-point outside-boundary error. Resolve layer/style/label/parts names from runtime catalogs/licensed fixtures, never guess "Standard"/"Basic". Missing prerequisites skip that positive variant, not pass it or stop all independent tools.

Minimal Map fixture: registered B_OD table with known string/numeric fields assigned only to benchmark entities; `cad_vertical_get` must return exact table/values. Select CRS from a verified runtime list; setters touch only owned fixtures and require checkpoints. Missing product/OD API means SKIPPED. Never substitute supported=true without loaded APIs.

## Validation of this matrix design

An independent reviewer matched all 49 rows against current `McpServerTool` attributes without omissions/duplicates. All nine fenced JSON templates parsed; both native `operations_json` (SOLID/ASSEMBLY) passed actual `EditPlan.Parse`, and SOLID criteria passed `DrawingVerification.Parse`. This verifies example contracts, not native execution or completion of the listed positive/conditional cases.

## Safety regression dimensions

Track three axes separately: tool contracts (the table above), individual `cad_edit` operation families, and native product/runtime scenarios. Managed/MCP evidence is not installed-AutoCAD evidence. Include transport loss after commit, historical receipts, C# exact replay, 500/501/2000 changed targets, large mesh pages/snapshot budgets, broken table sources and explicit repair, unsupported Map criteria, Claude startup/active silence, RU/EN runtime entry points, checkpoint retention/hash, and HTTP body stalls. See `ReviewRegressionTests` and `-ReviewSafetyOnly` in [validation](validation.md). The native probe and GUI/product matrix must be marked unperformed until their reports exist.
