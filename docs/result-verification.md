# Result verification, operation recovery, and photo references

Introduced in 0.7.0-preview. Codex and Claude Code share contracts/instructions.

## Dimensional acceptance

`cad_edit` accepts optional `expectations_json`. Checks use actual database readback inside the transaction before commit, not requested coordinates. By default, failed or unavailable criteria cause `ACCEPTANCE_FAILED` and batch rollback. `enforce:false` retains the diagnostic report without enforcing rollback.

Example for `line` with `id:"beam"`:

```json
{
  "units":"Millimeters",
  "entity_count":1,
  "checks":[
    {"label":"Длина балки","target":"beam","property":"length","expected":5000,"tolerance":0.01},
    {"target":"beam","property":"layer","expected":"БАЛКИ"}
  ]
}
```

`entity_count` counts live entities affected by this batch, not the whole DWG. Units match AutoCAD `Insunits` names (`Millimeters`, `Meters`, etc.). Tolerance is absolute in the property's units. Coordinates require full XYZ. Supported properties include length, radius, area, volume, dimension measurement, layer, type, closed state, vertex/face counts, points, and bounds. Availability depends on entity type; unsupported measurements are `unverified`.

Distance between accessible points:

```json
{"checks":[{"property":"distance","first":{"handle":"A1","point":"start"},"second":{"handle":"A2","point":"center"},"expected":3050,"tolerance":0.1}]}
```

`cad_verify` takes a completed `operation_id` or `handles_json` (1–500 entities) and rereads the current database. Operation aliases/original criteria are retained; callers may supply new criteria. Results include `passed/failed/unverified`, actual properties, overall bounds, and save state. An old operation result cannot replace fresh verification.

The panel's footer separately reports completed/failed/pending operations, reread entities, criteria for the latest completed native edit, and persistence. It never declares an entire project verified from its last batch. The primary agent defines/checks task-wide final criteria. Complex results also require visual review. Missing criteria or unavailable fresh verification are disclosed.

Save state uses current `DBMOD`, filename, and file metadata. Dirty DWGs are marked unsaved; verification does not save automatically.

## Quality and release checks

Per-edit quality review and `cad_review` also check:

- dimension text hiding/contradicting measurement, accounting for DIMLFAC/DIMRND/precision, and duplicate diameter symbols;
- missing font glyphs: ordinary SHX without Cyrillic displays "?"; regular/Unicode SHX are checked, TrueType is advisory, big fonts and inline MTEXT font switches are not checked;
- duplicates, zero-length curves, overlapping segments, and self-intersections on XY-parallel planes, separately per elevation. Vertical lines/3D wireframes are not errors.

Review is descriptive: failed checks are marked unverified (`REVIEW_FAILED`, `*_UNVERIFIED`) and do not undo the edit. Unreadable curves or excessively dense geometry yield `unverified`, not a clean bill.

`info` findings do not change overall state. `cad_review` with `options_json` checks topology over current space/model/layout, with layer wildcards `` * ? # , ~ ``. Other characters, including `.`, `@`, `[ ]`, are literal; `` ` `` escapes the next character. Checks include dangling endpoints, near-gaps up to `gap_tolerance`, T-junctions, and unconnected crossings. Arc/spline approximation has known chord deviation, so endpoints near an approximated arc count as touches; straight segments in arc-containing polylines remain exact. A multilevel grid avoids all-pairs comparison of short/long segments, allowing large plans to run in seconds. Work is bounded to several seconds: extremely dense crossings or coordinates around 10²⁹ yield `TOPOLOGY_LIMIT` rather than hanging AutoCAD.

`cad_release_check` adds unresolved fields ("####"), inconsistent/insufficient title-block sheet totals, numbering gaps (advisory), Fit plotting, and Display plot area. Non-1:1 scaling and non-layout plot areas are `notes`.

PDF `cad_export` compares page size with layout media and detects blank pages. Parser failure leaves the file and marks checks unverified; wrong page count is `failed`. DXF is reopened in a separate database and compared by entity count per layout/type/layer. `cad_publish` applies the PDF checks to every output (`pdf_review`: `passed`, `review_required`, `failed`).

Text parsing, SHX, topology, and PDF checks have unit tests; initial additions still required live AutoCAD runs.

## Lost responses

Before queuing a mutation, the journal persists `operation_id`, full original request, and acceptance time. Exact replay returns its receipt/status; different payloads sharing an ID conflict. Transport timeout/Stop after acceptance does not delete the operation. Requests expiring before execution finish with `DEADLINE_EXPIRED` without starting edits.

After a lost response, the server reads `cad_operation_status` once without resubmitting. Completed receipts return to the client; `queued/running` requires waiting, while `unknown/not_found` requires drawing inspection rather than reconstruction. Status and `cad_operation_list` read only the journal outside AutoCAD's main thread, remaining available during long geometry work. Journal writes are concurrency-safe.

If the old CAD session disappears, the broker can read disk receipts marked `historical:true`. Completed historical receipts prove prior execution, not current geometry or persistence. Unfinished entries become `unknown` after restart. A crash window remains between DWG modification and completed receipt persistence; it does not permit safe automatic replay.

## Images and geometry

Both providers receive attachments with actual pixel size, EXIF orientation, and SHA-256. `cad_reference_calibrate` reads a real local PNG/JPEG/GIF/WebP up to 7 MiB and registers it to WCS XY:

- `similarity`: 2–20 known pixel/XYZ pairs for scale/rotation/translation of an orthographic view. Pixel Y defaults downward, WCS Y upward.
- `projective`: 4–20 known coplanar pairs for a perspective image of a plane. Degenerate anchors and a horizon crossing the image are rejected.

Results include matrix, RMS/max anchor error, and calibration ID. `cad_reference_point` converts both directions and checks source-file immutability. Anchor residuals do not prove unknown-dimension accuracy. One known length in an arbitrary perspective photo does not determine a complete 3D model.

`cad_render` can temporarily frame handles and select `front/back/left/right/top/isometric` or a viewing direction; the original view is restored. Results include the local image path. Changing direction without handles retains the current framing scale. Use `cad_image_register/cad_image_point` for DWG-capture coordinates.

`cad_reference_compare` takes two real files and agent-traced contours from comparable views, normalized to [0,1]. It estimates IoU on a 256×256 grid and bidirectional boundary deviation using segment samples. Tool defaults do not override user requirements. Evidence explicitly states `agent_supplied_contours_in_comparable_views`: supplied-contour checking, not automatic image recognition. It does not verify surface detail or hidden geometry. The agent must compare traced contours with images and distinctive details.

Recommended photo workflow: identify known dimensions/assumptions → calibrate suitable views → construct silhouette/main volumes with C# → verify dimensions → capture comparable views → compare silhouette/details → refine → show final image and DWG state.

## Text and scrolling

HTML updates only changed messages; completed text/images/formulas retain their DOM. Sequence numbers discard stale updates. While reading history, the first visible message and its position anchor scrolling; near the end, the chat follows new output. Image/formula height changes use the same anchor. Images reserve space using known dimensions. Content, tables, code, and math have explicit text colors in both themes. Streaming updates batch every 120 ms to reduce redraws.

## Recovery provenance and unsupported acceptance

Worker transport loss is `WORKER_TRANSPORT_LOST`. Mutation recovery also reconciles ambiguous legacy `INTERNAL_ERROR`, timeout and oversized-response failures by reading the original receipt once, never by resending the write. Status requests preserve document/session/owner scope. A foreign receipt is not accepted.

Recovered historical results keep `historical:true`, `worker_reachable:false`, `current_state_verified:false` and `requires_reconciliation:true`. Previous save evidence is moved to `historical_document_state`; current `document_state.disk_save` is `unknown` and no current revision is asserted.

Map actions with nonempty geometry acceptance criteria fail `UNSUPPORTED_MAP_EXPECTATIONS` before Map execution, including diagnostic criteria with `enforce:false`. Map API changes have no native geometry acceptance implementation; independent product-specific readback is required. An empty criteria object or `enforce` alone does not assert geometry acceptance.

Implicitly changed linked/schedule tables participate in the batch's returned entities and live `entity_count`. Counts must include these actual dependency updates. Verification refuses incomplete C# target lists before reading any prefix.
