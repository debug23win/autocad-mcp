# Geometric image registration

## Raster in DWG

`cad_edit` operation `image_attach` accepts an absolute `path` to PNG, JPEG, TIFF, or BMP and `control_points`: 3–20 pairs of `{ "pixel": [x,y], "world": [x,y,z] }`. Pixel origin is the image's upper-left corner, X right, Y down. DWG coordinates use WCS/current drawing units; all control points must share the same Z. Points must not be collinear. The plugin fits a 2D affine transform, returns coefficients and mean/maximum residual, then inserts a standard AutoCAD `RasterImage` with that orientation. The raster remains an external DWG file dependency: transfer it with the drawing.

Example:

```json
[{"op":"image_attach","name":"План_съемки","path":"C:\\Проект\\plan.png","control_points":[{"pixel":[0,0],"world":[500000,6200000,0]},{"pixel":[2000,0],"world":[500100,6200000,0]},{"pixel":[0,1000],"world":[500000,6199950,0]}]}]
```

Three points determine the transform exactly; additional points reveal residuals. Three points alone cannot establish registration quality: critical drawings need independent checkpoints. The affine model supports translation, scale, rotation, and shear. Perspective or relief photographs require separate orthorectification. WCS numbers do not automatically assign a Map 3D coordinate system; the CRS code must be known separately.

## AutoCAD view image for the agent

`cad_render` returns a PNG, actual pixel dimensions, and `image_id`. Register exact pixel-to-DWG correspondence with `cad_image_register` using 3–20 known points from **that exact image**. Results include `pixel_to_world_x/y` coefficients, image corners in WCS, residuals, and DWG revision. `cad_image_point` converts lists between pixels and WCS. After a view change, capture and register a new image. Stale images are marked `historical`.

`ViewTableRecord` alone cannot guarantee exact pixel mapping: `CapturePreviewImage` does not document the finished PNG's cropping/scaling. Before calibration, use structured DWG geometry for precise construction and the image only for visual understanding.
