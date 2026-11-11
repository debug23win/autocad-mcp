# Native modeling through C#

`cad_edit` runs an operation batch through AutoCAD's standard .NET API in one transaction. It is a compiled adapter with enumerated operations, not an arbitrary C# console. Read `cad_edit_help` before editing. Use AutoLISP only for a specific operation missing from the help that cannot be composed from available C# actions.

For photo-based models, combine `line`, `polyline3d`, `spline`, `mesh`, solid primitives, `extrude`, `sweep`, `revolve`, and `solid_boolean`. Meshes take `[x,y,z]` vertices and triangular/quadrilateral faces with zero-based indices. They become ordinary unsmoothed `SubDMesh` objects. Example:

```json
[
  {"op":"mesh","id":"panel","vertices":[[0,0,0],[100,0,0],[100,0,50],[0,0,50]],"faces":[[0,1,2,3]]},
  {"op":"polyline3d","points":[[0,0,0],[100,0,25],[200,0,50]]}
]
```

A closed profile can produce a solid; the source profile remains in the DWG. `solid_boolean` modifies the first solid and erases the second by default; `keep_tool:true` retains it. `rotate3d` takes two axis points and an angle in degrees. All coordinates use WCS and actual drawing units.

```json
[
  {"op":"rectangle","id":"section","first":[0,0,0],"second":[100,20,0]},
  {"op":"extrude","id":"beam","target":"section","direction":[0,0,30]},
  {"op":"cylinder","id":"hole","center":[50,10,0],"radius":5,"height":30},
  {"op":"solid_boolean","target":"beam","tool_target":"hole","operation":"subtract"}
]
```

Also available: `sphere`, `cone`, `wedge`, `torus`, spatial splines, sweep along an existing curve, and revolve of a closed profile around an axis. Place/orient a sweep profile appropriately at the path start. Invalid geometry triggers a modeling-kernel error and rolls back the batch. Results contain handles, volumes, and coordinates for verification.

Reference-based modeling requires comparing the finished model against the original image from a similar viewpoint: silhouette, proportions, major surfaces, and recognizable details. A mesh or a similar object category in the DWG does not prove fidelity. Label schematic results as drafts and list differences. One perspective cannot determine hidden surfaces or precise scale; accurate reproduction needs the original 3D model, orthographic views, or at least overall/key dimensions.

Automatic photo-to-mesh reconstruction, whole-model watertightness/self-intersection checks, BREP face/edge editing, materials, and automatic model-to-image matching are not implemented. Add these separately with geometric tests. Specialized Civil 3D/Map 3D objects require APIs from the running vertical product.
