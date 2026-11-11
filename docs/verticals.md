# Civil 3D and Map 3D

`cad_vertical_catalog` calls the running vertical product's API and reports availability. Civil catalogs surfaces, alignments, profiles, pipe networks, sites, styles (alignment/profile/surface/pipe/structure/etc.), label sets, and parts lists with families/sizes. `cad_vertical_get` reads handle-based object properties, child profiles/network parts, and surface statistics (point/triangle counts, elevation range); surfaces can sample 1–100 WCS points. Vertical reads do not activate drawing tabs.

## Civil 3D editing

| Operation | Behavior |
|---|---|
| `civil_tin_create`, `civil_tin_add_points` | Creates TIN surfaces and adds vertices. |
| `civil_alignment_create` | Alignment through 2–500 PI points. `radii` (one per internal PI, 0 means no curve) adds circular tangent curves. Radii that cannot fit are rejected before Civil calls; tangent/curve counts, radii, and length are checked before commit. Omit/empty `site` for a siteless alignment. |
| `civil_alignment_add_line`, `civil_profile_add_tangent` | Adds an alignment tangent using Point3d, or a profile tangent using station/elevation. |
| `civil_profile_create`, `civil_profile_from_surface` | Profile from points or a surface. |
| `civil_network_create`, `civil_network_add_pipe`, `civil_network_add_structure` | Network, centerline pipes, and structures. |
| `civil_set` | Restricted property allowlist. |

Data-shortcut objects cannot be edited; unreadable reference status also rejects the edit.

Specify style, label set, profile layer, network parts list, family, and pipe/structure size by name instead of handle (`style`, `label_set`, `layer`, `parts_list`, `family`, `size`). Size names follow the Civil parts catalog; other textual values in a size record also match.

Every reflected Civil member is listed in `CivilApiContract` and unit-tested against Civil 3D 2024–2027 signature dumps (`tests/CadMcp.Tests/Fixtures/civil-api-signatures.txt`). `scripts/civil-signatures.ps1` extracts metadata from official Autodesk Civil3D.NET SDK packages; Autodesk binaries are not committed. Invalid overloads, such as Point2d instead of Point3d, are caught without running Civil. A second test checks member names used in Civil code against the contract. Data-reference status, part-size records, pipe/structure properties, and surface statistics also match dumps. Only generic optional Civil properties (styles/display properties) have reasoned exceptions; their absence does not fail an operation.

## Map 3D

Catalogs read horizontal/vertical CRS codes and object-data tables; `cad_vertical_get` reads up to 100 OD records/100 fields. `map_coordinate_system` takes a CS-MAP code (such as `LL84`) or EPSG number (`2154`, `EPSG:2154`), validates it against the catalog, and compares drawing bounds with the CRS area of use, including antimeridian crossings. Mismatches require `force:true`. Non-geographic systems (`XY-M`, etc.) and unavailable longitude/latitude conversion report unverified compatibility, not mismatch. If CS-MAP is unavailable, code assignment proceeds with a warning; EPSG input is rejected because it cannot be translated without the catalog. Assigning a CRS never moves geometry. OD operations: `map_od_table`, `map_od_add`, `map_od_update`.

Autodesk DLLs are not bundled; APIs load from the installed product.

## Validation limits

No working Civil 3D was available on the development machine. Civil and Map OD operations are **built, signature-checked, and unit-tested, but not validated on real vertical objects**. Agents must disclose this and never present ordinary DWG geometry as Civil/Map objects.
