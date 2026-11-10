# DWG reading and caching

`cad_context` returns session_id, document_id, revision, name, units, space, selection handles, UCS, view, and AutoCAD/.NET versions. Writes require exact expected_revision. Reads accept stale revisions and return the actual current revision; session and document must still match.

`cad_search` accepts options_json, for example:

```json
{"scope":"model","text":"Колодец","expand_blocks":true,"max_depth":6,"limit":100,"offset":0,"details":false}
```

Scopes: current/model/layouts/all/selection/view. Filters: layer, type (.NET or DXF name), text, bounds with WCS min/max. limit: 1..500; max_depth: 0..12. Continue through result.pagination.next_offset. A bounded page's count is not the total. Bounds use bounding-box intersection; view uses a conservative WCS envelope. Perspective views require selection or explicit bounds.

Results: result.entities and cached. Summaries include handles, instance path, root_handle, type, layer, text, bounds, and depth; details=true adds geometry. path distinguishes a definition entity appearing in multiple inserts; use root_handle for display. Nested definitions and xrefs cannot be edited as current-space entities. Unsupported class transforms return local coordinates and a limitation; world_transform is retained. Unloaded xrefs and special properties are not considered fully read.

`cad_snapshot` / `cad_query` provide a bounded current-space snapshot. `cad_query` reports captured_revision and historical; old snapshots remain readable but are unsuitable as current editing state. `cad_entity_get` reads a top-level current-space entity. Dimensions expose measurement, text_override, dimension_style, text_position, and available geometry points, not fully evaluated display text with every style rule.

Catalogs, snapshots, and up to eight search pages are keyed by document, revision, and space. Database changes, Undo/Redo, and plugin operations increment revision. Synchronous `ObjectModified` events caused by our own reads do not. A tested working-DWG copy produced 24 such events while DBMOD stayed 0. Search view/selection is uncached. Summaries do not clone full geometry; type/layer filters precede detailed reads.

Results over 512 KiB are archived under results/<worker-session>. `cad_result_get` takes archive_id, offset, and limit (1..32000 characters), returning JSON text and next_offset. Concatenate pages before parsing. Data is historical with captured_revision; refresh context before editing. Limits: 64 files / 64 MiB per archive, 16 MiB per result; older results can expire.

## Outlines, quantities, and changes

`cad_outline` returns a deterministic drawing summary: units, model entity types/bounds, layouts with paper/device/viewports/title-block attributes, layers by entity count, blocks by insert count, xrefs, text/dimension styles, and text samples. Identical drawings produce identical summaries. `cad_file_inspect` reads the same outline for a DWG on a local/mapped drive without opening a tab (separate in-memory database, up to 200 MB). UNC paths such as `\\server\share` and device paths are rejected to avoid Windows authenticating to a remote server. For a file open in AutoCAD, the response warns that it reflects the last saved state.

`cad_properties` reads the Properties palette (OPM) for 1–20 handles: categorized properties, including Civil 3D, Map 3D, SPDS, and other enabler properties, with values/names as displayed by that AutoCAD locale. Use it for custom, vertical, and proxy objects only partially covered by `cad_entity_get`. It uses internal `Autodesk.AutoCAD.Internal.PropertyInspector`; object COM pointers are released after reading. AutoCAD Core Console lacks the palette and reports it unavailable.

`cad_proxies` finds objects belonging to unloaded applications. Proxy entities/non-graphical objects are grouped by original class, DXF name, and application, with counts, owners (model/layout/block/dictionary), saved-graphics type, and allowed-operation flags. `handles_json` requests details for 1–20 proxies: saved graphics are exploded in memory into component types, text, and bounds without adding drawing entities. Editing requires the application's object enabler.

`cad_text_units` prepares text for translation/rewriting: TEXT, MTEXT, block attributes, mleaders, table cells, and optionally block-definition texts become keyed units with display text and frames. Stacked TEXT lines are grouped as paragraphs when style/layer/height/rotation match, spacing is 0.8–2.5 text heights, and left/center/right alignment is shared. Width uses the style font; unavailable fonts yield a flagged estimate. Fields and locked layers are marked, including an attribute's block-insert layer. Tables are limited to the first 500 rows/50 columns; larger ones appear in `tables_truncated`. Results paginate with `offset`/`limit`.

`cad_takeoff` totals curve lengths and closed-contour areas (including polylines ending at their first point without a closed flag) by layer and type, separating hatches from their boundaries. It counts block inserts by effective name, including dynamic blocks, and optionally lists attributes. `format=csv` adds semicolon-separated tables. Geometry inside blocks is not expanded.

`cad_changes` reports net changes since a revision: added/modified/erased/restored, type, layer, and author (`operation_id`, `table_recalculation`, or `user`, including external programs/UNDO). Objects created then undone or rolled back disappear from net results. This captures manual edits between turns and actual `cad_lisp` changes. The in-memory journal retains the last 20,000 events of the current CAD session; `complete=false` means earlier changes were evicted.

`cad_search` matches stored and displayed text: Ø108 finds `%%c108` and formatted MTEXT. `cad_entity_get` and edit results expose per-application XData and extension-dictionary contents.

`cad_render` with `attach=false` saves PNG and returns only path/metadata; each result estimates image tokens in `image_cost.approx_tokens`. Prefer text tools before images and crop by handles/bounds.

These tools are built and MCP-contract tested; their initial implementation had not yet been validated in running AutoCAD. See [current validation](validation.md) and release-specific evidence.
