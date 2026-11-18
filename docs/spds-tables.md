# Native tables, formulas, and SPDS drafting

Since 0.8.0, the native C# adapter performs these operations. Tables remain `Table`, formulas remain native DWG formulas/fields, dimensions remain `Dimension`, and frames/axes/levels are blocks of ordinary entities. New entities do not require SPDS GraphiCS. Codex and Claude share `cad_spds_help`, `cad_edit_help`, `cad_table_get`, and `cad_edit`.

## Tables

`table_create` creates a matrix; `table_cells` edits values/formulas; `table_merge` merges/unmerges cells; `table_rows`/`table_columns` insert/delete rows/columns; `table_recalculate` recalculates dependents. Cell addresses use A1; range/insertion indices are zero-based. `cad_table_get` returns values, formulas, field codes, merged ranges, sizes, and layout name, up to 500 cells per page. Edits target current space; explicitly pass `layout` for another paper layout.

### Reading table pages

Start a complete read with `first_row=0`, `first_column=0`. Both counts default to
20. Positive oversized counts are capped at **100 rows, 50 columns and 500 cells**;
the cell budget can reduce the effective row count further. For example, a request
for 100 rows and 50 columns returns at most 10 rows by 50 columns. The `pagination`
object reports requested and effective counts, limits, returned/total cells and
whether this single page covers the whole table. Counts of zero or less, negative
offsets and offsets past the actual table boundary return `INVALID_TABLE_RANGE`.
An offset exactly at a boundary returns an empty page without a continuation.

To continue, copy **all four fields of `next_page`** into the next request, retaining
the same handle/session/document. It reads column stripes across a fixed row band,
then resets `first_column` to zero for the next row band. Stop when `next_page` is
absent/null. The older `next_row` remains a vertical hint; following it alone can
miss columns. `next_column` is a horizontal hint. Keep the response revision
consistent across pages before claiming a complete table read; if the drawing
changes, the collected pages are not one consistent snapshot. A nonzero initial
offset leaves the preceding cells unread.

`rows`, `columns`, `width` and `height` describe the whole table. The arrays
`row_heights` and `column_widths` describe only the returned page (`sizes_scope` is
`page`); their global origins are `row_heights_first_row` and
`column_widths_first_column`. Each returned cell retains its global A1 address,
value, formula, field code and complete merged range. Merge lookup and size reads
are bounded by the page. Read pagination does not change table creation/edit limits.

Example `operations_json` batch:

```json
[
  {"op":"table_create","id":"source","position":[0,100],"rows":3,"columns":3,
   "cells":[{"cell":"A1","value":4},{"cell":"B1","value":2.5},{"cell":"C1","formula":"=A1*B1"}]},
  {"op":"table_create","id":"total","position":[120,100],"rows":3,"columns":2,
   "cells":[{"cell":"A1","formula":"={{mass}}*2","precision":3,
             "references":[{"name":"mass","table_target":"source","cell":"C1"}]}]}
]
```

The second table receives 20 through a native `AcExpr` field, not a copied value. Existing sources use `table_handle` instead of `table_target`. After source changes, run `table_recalculate` for dependents and reread actual values. Avoid cycles. After row/column insertion/deletion, verify cross-table references: they store cell addresses, not row meaning. Deleted sources invalidate references; the plugin does not replace dependency review.

Per-object limits: 500 rows, 50 columns, 5000 cells. Up to 1000 cell changes per action; 100 actions/65536 characters per batch. Pre-commit failure rolls back the batch. Edit a merged range through its upper-left cell. AutoCAD may normalize formulas with additional parentheses on readback.

## Forms and sheets

`spds_table` accepts `template`, `position`, row array `data`, and optional `title`, `scale`, `style`, `layout`. Paper-space sizes are paper millimeters; model-space `scale` converts them into DWG units. AutoCAD increases row height for long content: 8 mm is a minimum, not a forced clipping height.

| Template | Purpose | Form status |
|---|---|---|
| `sheet_register` | Working-drawing register | Form 1: 185 mm, columns 15/140/30. |
| `document_register` | Document register | Form 2: same columns; first is Designation. |
| `specification` | Specification | Form 7: 15/60/65/10/15/20 mm; header 15 mm. |
| `rebar_schedule` | Reinforcement schedule (RC) | Project template, `VerifiedForm=false`. |
| `steel_consumption` | Steel consumption (RC) | Project template, `VerifiedForm=false`. |
| `km_members` | Steel-member schedule | Project template, `VerifiedForm=false`. |

Forms 1/2/7 were checked against published GOST R 21.101-2026. Form 2 uses the Designation heading per B.5; Rosstandart's August 24, 2026 letter explains the printed-heading discrepancy. This does not establish implementation of every form or standard requirement.

`spds_sheet` places a frame and Form 3 title block (185×55 mm) on an existing A0–A4 paper layout. Margins: 20 mm left, 5 mm elsewhere. Designation, title, organization, stage, sheet numbers, and signatures are editable attributes. Edit attributes on another layout with `set`, `layout`, and `attributes`. Text is not automatically clipped; check long titles and additional columns. Layout creation, media/plot settings, viewports, and PDF publishing are separate native actions.

## Dimensions and RC/steel geometry

`spds_dimstyle` creates a new style. `drawing_scale` is the scale denominator; the millimeter factor derives from `INSUNITS` for mm/cm/m or explicit `measurement_factor`. Linear dimensions omit unit suffixes and use ticks; radial/diameter dimensions use arrows. `dimension_rotated`, `dimension_radius`, and `dimension_diameter` complement `dimension_aligned`. `spds_axis` builds a chain-line axis with markers; `spds_level` reports meter elevations to three decimals.

For RC/steel work, use existing C# geometry (lines, polylines, blocks, hatches, solids), dimensions/symbols, specifications/schedules, frames, and layouts. Profiles, marks, reinforcement, details, and design inputs come from the task/drawing. Templates neither calculate structural capacity nor certify project compliance. GOST 2.304 fonts, additional columns, details, and completeness require project-specific review.

Sources: [GOST R 21.101-2026 — Rosstandart](https://protect.gost.ru/gost/details/17bc12e8-6579-4145-b141-56855e772e7f), [Rosstandart clarification](https://base.garant.ru/414842117/), [GOST 21.501-2018](https://protect.gost.ru/gost/details/a241e289-aac3-4ee2-ad44-3259f9774799), [Autodesk cross-table formulas](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Using-cell-references-from-other-tables-in-table-formulas.html).
