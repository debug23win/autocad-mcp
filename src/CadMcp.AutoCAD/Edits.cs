using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

// Native creation/transaction approach adapted from beiming183-cloud/AutoCAD-MCP CadDispatcher.cs (MIT).
// Copyright (c) 2024 AutoCAD MCP Server Contributors. See licenses/beiming-MIT.txt.
// Changes: WCS contract, current space, 2D objects, blocks/attributes, edits, strict validation and bounded readback.
internal static class Edits
{
    private static Point3d Point(JsonElement op, string field)
    { var p = EditPlan.Point(op.GetProperty(field)); return new(p[0], p[1], p[2]); }
    private static double N(JsonElement op, string field, double? fallback = null) => EditPlan.Numeric(op, field, fallback);
    private static double Angle(JsonElement op, string field, double fallback = 0) => N(op, field, fallback) * Math.PI / 180;
    private static string S(JsonElement op, string field) => EditPlan.RequiredText(op, field);
    private static bool Bool(JsonElement op, string field, bool fallback = false) => op.TryGetProperty(field, out var v) ? v.GetBoolean() : fallback;
    private static void Equal(Point3d a, Point3d b)
    { if (a.DistanceTo(b) < 1e-10) throw new CadFault("DEGENERATE_GEOMETRY", "Points must differ"); }
    public static object Execute(Document doc, JsonElement[] operations, CancellationToken ct)
    {
        using var undoGroup = new UndoGroup(doc);
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
        var aliases = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
        var touched = new HashSet<ObjectId>();
        var results = new List<object>();
        int index = 0;
        foreach (var op in operations)
        {
            ct.ThrowIfCancellationRequested();
            string kind = S(op, "op");
            if (kind == "layer")
            {
                var name = S(op, "name");
                var layers = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                LayerTableRecord record;
                bool created = !layers.Has(name);
                if (created)
                {
                    SymbolUtilityServices.ValidateSymbolName(name, false);
                    layers.UpgradeOpen(); record = new() { Name = name }; layers.Add(record); tr.AddNewlyCreatedDBObject(record, true);
                }
                else record = (LayerTableRecord)tr.GetObject(layers[name], OpenMode.ForWrite);
                if (record.IsDependent) throw new CadFault("XREF_LAYER", "Cannot edit a dependent layer");
                if (op.TryGetProperty("color_index", out var color))
                {
                    var c = color.GetInt32();
                    if (c is < 1 or > 255) throw new CadFault("INVALID_COLOR", "Layer ACI must be 1..255");
                    record.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, (short)c);
                }
                if (op.TryGetProperty("locked", out var locked)) record.IsLocked = locked.GetBoolean();
                if (op.TryGetProperty("off", out var off)) record.IsOff = off.GetBoolean();
                results.Add(new { index = index++, op = kind, created, name = record.Name, color_index = record.Color.ColorIndex, locked = record.IsLocked, off = record.IsOff });
                if (op.TryGetProperty("id", out _)) throw new CadFault("INVALID_ALIAS", "Layer operations cannot be entity targets");
                continue;
            }
            if (kind == "block_define")
            {
                var name = S(op, "name");
                var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                SymbolUtilityServices.ValidateSymbolName(name, false);
                if (table.Has(name)) throw new CadFault("BLOCK_EXISTS", name);
                var sources = op.GetProperty("handles").EnumerateArray().Select(x => Handle(doc.Database, x.GetString()!)).ToArray();
                if (sources.Distinct().Count() != sources.Length) throw new CadFault("DUPLICATE_SOURCE", "Block source handles must be unique");
                foreach (var source in sources) Editable(doc.Database, tr, source, false);
                table.UpgradeOpen();
                var definition = new BlockTableRecord { Name = name, Origin = Point(op, "base_point") };
                table.Add(definition); tr.AddNewlyCreatedDBObject(definition, true);
                var mapping = new IdMapping();
                doc.Database.DeepCloneObjects(new ObjectIdCollection(sources), definition.ObjectId, mapping, false);
                var cloned = sources.Select(source => mapping[source].Value).ToArray();
                foreach (var clone in cloned) touched.Add(clone);
                results.Add(new { index = index++, op = kind, name, handle = definition.Handle.ToString(),
                    source_handles = sources.Select(x => x.Handle.ToString()).ToArray(),
                    entity_handles = cloned.Select(x => x.Handle.ToString()).ToArray() });
                if (op.TryGetProperty("id", out _)) throw new CadFault("INVALID_ALIAS", "Block definitions cannot be entity targets");
                continue;
            }
            if (kind == "layout_create")
            {
                var name = S(op, "name");
                SymbolUtilityServices.ValidateSymbolName(name, false);
                var layouts = (DBDictionary)tr.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                if (layouts.Contains(name)) throw new CadFault("LAYOUT_EXISTS", name);
                var layoutId = LayoutManager.Current.CreateLayout(name);
                results.Add(new { index = index++, op = kind, name, handle = layoutId.Handle.ToString() });
                if (op.TryGetProperty("id", out _)) throw new CadFault("INVALID_ALIAS", "Layouts cannot be entity targets");
                continue;
            }
            if (kind is "layout_copy" or "layout_configure")
            {
                var detail = kind == "layout_copy" ? Sheets.Copy(doc.Database, tr, op) : Sheets.Configure(doc.Database, tr, op);
                results.Add(new { index = index++, op = kind, detail });
                if (op.TryGetProperty("id", out _)) throw new CadFault("INVALID_ALIAS", "Layout operations cannot be entity targets");
                continue;
            }
            if (kind is "civil_tin_create" or "civil_tin_add_points")
            {
                var (surfaceId, detail) = Verticals.EditTin(doc.Database, tr, op);
                touched.Add(surfaceId);
                results.Add(new { index = index++, op = kind, detail });
                if (op.TryGetProperty("id", out _)) throw new CadFault("INVALID_ALIAS", "Civil surfaces cannot be used as regular entity targets");
                continue;
            }
            if (kind == "solid_boolean")
            {
                var primaryId = Resolve(doc.Database, tr, op, aliases);
                var toolId = op.Text("tool_target") is { } toolAlias
                    ? aliases.TryGetValue(toolAlias, out var aliased) ? aliased : throw new CadFault("UNKNOWN_TARGET", toolAlias)
                    : Handle(doc.Database, S(op, "tool_handle"));
                if (primaryId == toolId) throw new CadFault("INVALID_SOLID_BOOLEAN", "Primary and tool must be different solids");
                if (Editable(doc.Database, tr, primaryId, true) is not Solid3d primary ||
                    Editable(doc.Database, tr, toolId, false) is not Solid3d tool)
                    throw new CadFault("INVALID_SOLID_BOOLEAN", "Both targets must be editable 3D solids in the current space");
                var booleanType = S(op, "operation") switch
                {
                    "union" => BooleanOperationType.BoolUnite,
                    "subtract" => BooleanOperationType.BoolSubtract,
                    "intersect" => BooleanOperationType.BoolIntersect,
                    _ => throw new CadFault("INVALID_SOLID_BOOLEAN", "Unknown Boolean operation")
                };
                using (var toolCopy = (Solid3d)tool.Clone()) primary.BooleanOperation(booleanType, toolCopy);
                bool keepTool = Bool(op, "keep_tool");
                if (!keepTool) { RequireUnlocked(tr, tool); tool.UpgradeOpen(); tool.Erase(); }
                touched.Add(primaryId); touched.Add(toolId);
                if (op.TryGetProperty("id", out var solidId)) aliases.Add(solidId.GetString()!, primaryId);
                results.Add(new { index = index++, op = kind, operation = S(op, "operation"), handle = primary.Handle.ToString(),
                    tool_handle = tool.Handle.ToString(), tool_erased = !keepTool });
                continue;
            }
            Entity entity;
            string? sourceHandle = null;
            object? imageRegistration = null;
            if (kind is "move" or "copy" or "rotate" or "rotate3d" or "scale" or "mirror" or "erase" or "set")
            {
                var sourceId = Resolve(doc.Database, tr, op, aliases);
                entity = Editable(doc.Database, tr, sourceId, kind != "copy");
                sourceHandle = entity.Handle.ToString();
                if (kind == "copy")
                {
                    var mapping = new IdMapping();
                    doc.Database.DeepCloneObjects(new ObjectIdCollection([sourceId]), space.ObjectId, mapping, false);
                    entity = (Entity)tr.GetObject(mapping[sourceId].Value, OpenMode.ForWrite);
                    if (op.TryGetProperty("layer", out var layer)) entity.LayerId = Layer(doc.Database, tr, layer.GetString()!);
                    RequireUnlocked(tr, entity);
                }
                touched.Add(entity.ObjectId);
                switch (kind)
                {
                    case "move": case "copy":
                        Transform(entity, Matrix3d.Displacement(Point(op, "displacement") - Point3d.Origin), tr); break;
                    case "rotate": Transform(entity, Matrix3d.Rotation(Angle(op, "angle_deg"), Vector3d.ZAxis, Point(op, "center")), tr); break;
                    case "rotate3d":
                        var axisStart = Point(op, "axis_start"); var axisEnd = Point(op, "axis_end"); Equal(axisStart, axisEnd);
                        Transform(entity, Matrix3d.Rotation(Angle(op, "angle_deg"), axisEnd - axisStart, axisStart), tr); break;
                    case "scale": Transform(entity, Matrix3d.Scaling(N(op, "factor"), Point(op, "center")), tr); break;
                    case "mirror":
                        var first = Point(op, "first"); var second = Point(op, "second"); Equal(first, second);
                        if (Math.Abs(first.Z - second.Z) > 1e-8) throw new CadFault("INVALID_MIRROR", "Mirror line must lie in a WCS XY plane");
                        // A vertical plane through the requested XY line mirrors 2D and 3D entities correctly.
                        using (var plane = new Plane(first, (second - first).CrossProduct(Vector3d.ZAxis))) Transform(entity, Matrix3d.Mirroring(plane), tr);
                        break;
                    case "erase": entity.Erase(); break;
                    case "set": Set(doc.Database, tr, entity, op); break;
                }
            }
            else
            {
                if (kind == "viewport") Sheets.InitializeLayoutView(S(op, "layout"));
                if (kind == "image_attach")
                {
                    var attached = RasterImages.Attach(doc.Database, tr, op);
                    entity = attached.Image; imageRegistration = attached.Registration;
                }
                else entity = kind == "viewport" ? new Viewport() : Create(doc.Database, tr, op, aliases);
                try
                {
                    entity.SetDatabaseDefaults(doc.Database);
                    if (entity is Viewport prepared) Sheets.ConfigureViewport(prepared, op);
                    entity.LayerId = Layer(doc.Database, tr, op.Text("layer") ?? "0");
                    if (op.TryGetProperty("color_index", out var c)) entity.ColorIndex = c.GetInt32();
                    var targetSpace = op.Text("layout") is { } layout ? Sheets.Space(doc.Database, tr, layout) : space;
                    targetSpace.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true);
                    if (entity is Polyline3d spatial) PopulatePolyline3d(spatial, op, tr);
                    if (entity is RasterImage raster) RasterImages.Associate(raster, tr);
                }
                catch { if (entity.ObjectId.IsNull) entity.Dispose(); throw; }
                if (entity is BlockReference block) Attributes(doc.Database, tr, block, op, true);
                if (entity is Viewport viewport) Sheets.TurnOnViewport(S(op, "layout"), viewport);
                if (entity is Hatch hatch) SetupHatch(doc.Database, tr, hatch, op, aliases);
                if (entity is Dimension dimension) dimension.RecomputeDimensionBlock(true);
                touched.Add(entity.ObjectId);
            }
            if (op.TryGetProperty("id", out var id)) aliases.Add(id.GetString()!, entity.ObjectId);
            results.Add(new { index = index++, op = kind, id = op.Text("id"), handle = entity.Handle.ToString(), source_handle = sourceHandle, erased = entity.IsErased,
                image_registration = imageRegistration });
        }
        // Read back the final database state while rollback is still possible. Failed readback aborts the transaction.
        var readback = touched.Select(id =>
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForRead, true);
            return entity.IsErased ? Wire.Element(new { handle = entity.Handle.ToString(), erased = true }) : Reader.Read(entity, tr);
        }).ToArray();
        var data = new { transaction = "committed", coordinate_system = "WCS", units = doc.Database.Insunits.ToString(), results, entities = readback,
            undo = undoGroup.Grouped ? "single_undo_group" : "transaction_only_undo_group_unavailable", verification = "database_readback", limitations = new[] { "special_objects_require_vendor_API" } };
        if (JsonSerializer.SerializeToUtf8Bytes(data, Wire.Json).Length > 512 * 1024) throw new CadFault("RESULT_TOO_LARGE", "Use a smaller edit batch; no changes were committed");
        ct.ThrowIfCancellationRequested();
        tr.Commit();
        // A display failure after commit must never be reported as an uncommitted transaction.
        try { doc.Editor.Regen(); } catch (System.Exception e) { System.Diagnostics.Trace.WriteLine("Committed edit; redraw failed: " + e.Message); }
        return data;
    }
    private static void Transform(Entity entity, Matrix3d transform, Transaction tr)
    {
        var attributes = new List<(AttributeReference Attribute, Point3d Position)>();
        if (entity is BlockReference block)
            foreach (ObjectId id in block.AttributeCollection)
            {
                var attribute = (AttributeReference)tr.GetObject(id, OpenMode.ForWrite);
                attributes.Add((attribute, attribute.Position));
            }
        entity.TransformBy(transform);
        foreach (var (attribute, position) in attributes)
        {
            // Some transform paths move subentities themselves; compare before applying to avoid a double translation.
            var expected = position.TransformBy(transform);
            if (!attribute.Position.IsEqualTo(expected, new Tolerance(1e-8, 1e-8))) attribute.TransformBy(transform);
            if (!attribute.Position.IsEqualTo(expected, new Tolerance(1e-8, 1e-8))) throw new CadFault("ATTRIBUTE_READBACK", "Block attribute transform did not match the requested transform");
        }
    }
    private static Entity Create(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        switch (S(op, "op"))
        {
            case "line": Equal(Point(op, "start"), Point(op, "end")); return new Line(Point(op, "start"), Point(op, "end"));
            case "circle": return new Circle(Point(op, "center"), Vector3d.ZAxis, N(op, "radius"));
            case "point": return new DBPoint(Point(op, "position"));
            case "ellipse":
                var major = Point(op, "major_axis") - Point3d.Origin;
                if (major.Length < 1e-10) throw new CadFault("DEGENERATE_ELLIPSE", "major_axis must be a nonzero vector");
                if (Math.Abs(major.Z) > 1e-8) throw new CadFault("INVALID_ELLIPSE", "Only WCS XY ellipses are supported");
                var start = Angle(op, "start_angle_deg"); var end = Angle(op, "end_angle_deg", 360);
                if (Math.Abs(start - end) < 1e-10) throw new CadFault("DEGENERATE_ELLIPSE", "Ellipse parameters must differ");
                return new Ellipse(Point(op, "center"), Vector3d.ZAxis, major, N(op, "radius_ratio"), start, end);
            case "arc":
                var a = Angle(op, "start_angle_deg"); var b = Angle(op, "end_angle_deg");
                if (Math.Abs(a - b) < 1e-10) throw new CadFault("DEGENERATE_ARC", "Arc angles must differ");
                return new Arc(Point(op, "center"), Vector3d.ZAxis, N(op, "radius"), a, b);
            case "polyline":
                var points = op.GetProperty("points").EnumerateArray().Select(EditPlan.Point).ToArray();
                var bulges = op.TryGetProperty("bulges", out var bs) ? bs.EnumerateArray().Select(x => x.GetDouble()).ToArray() : new double[points.Length];
                if (bulges.Length != points.Length) throw new CadFault("INVALID_BULGES", "One bulge per vertex is required");
                var poly = new Polyline(points.Length) { Elevation = points[0][2], Closed = Bool(op, "closed") };
                for (int i = 0; i < points.Length; i++) poly.AddVertexAt(i, new(points[i][0], points[i][1]), bulges[i], N(op, "width", 0), N(op, "width", 0));
                return poly;
            case "polyline3d": return new Polyline3d();
            case "spline":
                var fitPoints = new Point3dCollection();
                foreach (var value in op.GetProperty("fit_points").EnumerateArray())
                {
                    var fit = EditPlan.Point(value); fitPoints.Add(new Point3d(fit[0], fit[1], fit[2]));
                }
                var splineDegree = op.TryGetProperty("degree", out var requestedDegree) ? requestedDegree.GetInt32() : Math.Min(3, fitPoints.Count - 1);
                return new Spline(fitPoints, Bool(op, "closed"), KnotParameterizationEnum.Chord, splineDegree, 0);
            case "mesh":
                var meshVertices = new Point3dCollection();
                foreach (var value in op.GetProperty("vertices").EnumerateArray())
                {
                    var vertex = EditPlan.Point(value);
                    meshVertices.Add(new Point3d(vertex[0], vertex[1], vertex[2]));
                }
                var meshFaces = new Int32Collection();
                foreach (var face in op.GetProperty("faces").EnumerateArray())
                {
                    meshFaces.Add(face.GetArrayLength());
                    foreach (var vertex in face.EnumerateArray()) meshFaces.Add(vertex.GetInt32());
                }
                var mesh = new SubDMesh();
                try { mesh.SetSubDMesh(meshVertices, meshFaces, 0); return mesh; }
                catch { mesh.Dispose(); throw; }
            case "extrude":
                var profileId = Resolve(db, tr, op, aliases);
                var profile = Editable(db, tr, profileId, false);
                if (profile is not (Circle or Polyline) || profile is Polyline { Closed: false })
                    throw new CadFault("INVALID_EXTRUSION_PROFILE", "Use a closed planar polyline or circle in the current space");
                var direction = Point(op, "direction") - Point3d.Origin;
                if (direction.Length < 1e-8) throw new CadFault("INVALID_EXTRUSION_DIRECTION", "Extrusion direction must be nonzero");
                var extruded = new Solid3d();
                try { extruded.CreateExtrudedSolid(profile, direction, new SweepOptions()); return extruded; }
                catch { extruded.Dispose(); throw; }
            case "sweep":
                var sweepProfile = Editable(db, tr, Resolve(db, tr, op, aliases), false);
                if (sweepProfile is not (Circle or Polyline) || sweepProfile is Polyline { Closed: false })
                    throw new CadFault("INVALID_SWEEP_PROFILE", "Use a closed planar polyline or circle as the sweep profile");
                var pathId = op.Text("path_target") is { } pathAlias
                    ? aliases.TryGetValue(pathAlias, out var pathTarget) ? pathTarget : throw new CadFault("UNKNOWN_TARGET", pathAlias)
                    : Handle(db, S(op, "path_handle"));
                if (Editable(db, tr, pathId, false) is not Curve sweepPath)
                    throw new CadFault("INVALID_SWEEP_PATH", "Use a current-space line, arc, polyline or spline as the path");
                var swept = new Solid3d();
                try { swept.CreateSweptSolid(sweepProfile, sweepPath, new SweepOptions()); return swept; }
                catch { swept.Dispose(); throw; }
            case "revolve":
                var revolveProfile = Editable(db, tr, Resolve(db, tr, op, aliases), false);
                if (revolveProfile is not (Circle or Polyline) || revolveProfile is Polyline { Closed: false })
                    throw new CadFault("INVALID_REVOLVE_PROFILE", "Use a closed planar polyline or circle as the revolve profile");
                var revolveStart = Point(op, "axis_start"); var revolveEnd = Point(op, "axis_end"); Equal(revolveStart, revolveEnd);
                var revolveAngle = Angle(op, "angle_deg");
                if (Math.Abs(revolveAngle) < 1e-10 || Math.Abs(revolveAngle) > Math.PI * 2 + 1e-10)
                    throw new CadFault("INVALID_REVOLVE_ANGLE", "Revolve angle must be nonzero and at most 360 degrees");
                var revolved = new Solid3d();
                try { revolved.CreateRevolvedSolid(revolveProfile, revolveStart, revolveEnd - revolveStart, revolveAngle, 0, new RevolveOptions()); return revolved; }
                catch { revolved.Dispose(); throw; }
            case "rectangle":
                var p = Point(op, "first"); var q = Point(op, "second");
                if (Math.Abs(p.Z - q.Z) > 1e-8 || Math.Abs(p.X - q.X) < 1e-10 || Math.Abs(p.Y - q.Y) < 1e-10) throw new CadFault("DEGENERATE_RECTANGLE", "Opposite corners need different X/Y and the same Z");
                var rect = new Polyline(4) { Elevation = p.Z, Closed = true };
                rect.AddVertexAt(0, new(p.X, p.Y), 0, 0, 0); rect.AddVertexAt(1, new(q.X, p.Y), 0, 0, 0);
                rect.AddVertexAt(2, new(q.X, q.Y), 0, 0, 0); rect.AddVertexAt(3, new(p.X, q.Y), 0, 0, 0); return rect;
            case "text":
                return new DBText { Position = Point(op, "position"), TextString = op.GetProperty("text").GetString()!, Height = N(op, "height"), Rotation = Angle(op, "rotation_deg"), TextStyleId = TextStyle(db, tr, op.Text("style")) };
            case "mtext":
                return new MText { Location = Point(op, "position"), Contents = op.GetProperty("text").GetString()!, TextHeight = N(op, "height"), Width = N(op, "width", 0), Rotation = Angle(op, "rotation_deg"), TextStyleId = TextStyle(db, tr, op.Text("style")), Attachment = AttachmentPoint.TopLeft };
            case "block":
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead); var name = S(op, "name");
                if (!table.Has(name)) throw new CadFault("BLOCK_NOT_FOUND", name + "; read cad_catalog or create the definition with block_define");
                var definition = (BlockTableRecord)tr.GetObject(table[name], OpenMode.ForRead);
                if (definition.IsLayout || definition.IsFromExternalReference || definition.IsDependent) throw new CadFault("UNSUPPORTED_BLOCK", "Expected a local block definition");
                var scale = new[] { 1d, 1d, 1d };
                if (op.TryGetProperty("scale", out var s)) scale = s.ValueKind == JsonValueKind.Array ? EditPlan.Point(s) : [s.GetDouble(), s.GetDouble(), s.GetDouble()];
                return new BlockReference(Point(op, "position"), table[name]) { Rotation = Angle(op, "rotation_deg"), ScaleFactors = new(scale[0], scale[1], scale[2]) };
            case "dimension_aligned":
                var dimStyles = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                var dimStyle = op.Text("style") is { } style ? dimStyles.Has(style) ? dimStyles[style] : throw new CadFault("STYLE_NOT_FOUND", style) : db.Dimstyle;
                Equal(Point(op, "first"), Point(op, "second"));
                return new AlignedDimension(Point(op, "first"), Point(op, "second"), Point(op, "position"), op.Text("text") ?? "", dimStyle);
            case "hatch": return new Hatch();
            case "box":
                if (N(op, "width") <= 0) throw new CadFault("INVALID_PARAMETER", "Box width must be positive");
                var box = new Solid3d(); box.CreateBox(N(op, "length"), N(op, "width"), N(op, "height"));
                var boxBounds = box.GeometricExtents;
                var boxCenter = new Point3d((boxBounds.MinPoint.X + boxBounds.MaxPoint.X) / 2, (boxBounds.MinPoint.Y + boxBounds.MaxPoint.Y) / 2, (boxBounds.MinPoint.Z + boxBounds.MaxPoint.Z) / 2);
                box.TransformBy(Matrix3d.Displacement(Point(op, "center") - boxCenter)); return box;
            case "cylinder":
                var solid = new Solid3d(); solid.CreateFrustum(N(op, "height"), N(op, "radius"), N(op, "radius"), N(op, "radius"));
                var center = Point(op, "center");
                // Derive placement from the actual solid extents rather than assuming the factory origin.
                var bounds = solid.GeometricExtents;
                solid.TransformBy(Matrix3d.Displacement(new(center.X - (bounds.MinPoint.X + bounds.MaxPoint.X) / 2,
                    center.Y - (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2, center.Z - bounds.MinPoint.Z))); return solid;
            case "sphere":
                var sphere = new Solid3d(); sphere.CreateSphere(N(op, "radius"));
                PlaceSolid(sphere, Point(op, "center"), false); return sphere;
            case "cone":
                var cone = new Solid3d(); cone.CreateFrustum(N(op, "height"), N(op, "radius"), N(op, "radius"), 0);
                PlaceSolid(cone, Point(op, "center"), true); return cone;
            case "wedge":
                if (N(op, "width") <= 0) throw new CadFault("INVALID_PARAMETER", "Wedge width must be positive");
                var wedge = new Solid3d(); wedge.CreateWedge(N(op, "length"), N(op, "width"), N(op, "height"));
                PlaceSolid(wedge, Point(op, "center"), false); return wedge;
            case "torus":
                if (N(op, "minor_radius") >= N(op, "major_radius")) throw new CadFault("INVALID_TORUS", "minor_radius must be smaller than major_radius");
                var torus = new Solid3d(); torus.CreateTorus(N(op, "major_radius"), N(op, "minor_radius"));
                PlaceSolid(torus, Point(op, "center"), false); return torus;
            default: throw new CadFault("INVALID_OPERATION", S(op, "op"));
        }
    }
    private static void PlaceSolid(Solid3d solid, Point3d target, bool bottom)
    {
        var bounds = solid.GeometricExtents;
        var origin = new Point3d((bounds.MinPoint.X + bounds.MaxPoint.X) / 2,
            (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2,
            bottom ? bounds.MinPoint.Z : (bounds.MinPoint.Z + bounds.MaxPoint.Z) / 2);
        solid.TransformBy(Matrix3d.Displacement(target - origin));
    }
    private static ObjectId Layer(Database db, Transaction tr, string name)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!layers.Has(name)) throw new CadFault("LAYER_NOT_FOUND", name + "; add a layer operation first");
        var layer = (LayerTableRecord)tr.GetObject(layers[name], OpenMode.ForRead);
        if (layer.IsLocked || layer.IsDependent) throw new CadFault("LAYER_NOT_EDITABLE", name + " is locked or dependent");
        return layer.ObjectId;
    }
    private static ObjectId TextStyle(Database db, Transaction tr, string? name)
    {
        if (name is null) return db.Textstyle;
        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        return table.Has(name) ? table[name] : throw new CadFault("STYLE_NOT_FOUND", name);
    }
    internal static ObjectId Resolve(Database db, Transaction tr, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        if (op.Text("target") is { } target) return aliases.TryGetValue(target, out var id) ? id : throw new CadFault("UNKNOWN_TARGET", target);
        return Handle(db, S(op, "handle"));
    }
    private static ObjectId Handle(Database db, string value)
    {
        if (!long.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var h) || !db.TryGetObjectId(new Handle(h), out var id) || id.IsNull || id.IsErased)
            throw new CadFault("ENTITY_NOT_FOUND", value);
        return id;
    }
    private static void RequireUnlocked(Transaction tr, Entity e)
    { if (((LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead)).IsLocked) throw new CadFault("LAYER_LOCKED", e.Layer); }
    private static Entity Editable(Database db, Transaction tr, ObjectId id, bool write)
    {
        if (id.IsErased) throw new CadFault("ENTITY_ERASED", id.Handle.ToString());
        if (tr.GetObject(id, OpenMode.ForRead) is not Entity e || e.OwnerId != db.CurrentSpaceId) throw new CadFault("UNSUPPORTED_SCOPE", "Only current-space top-level entities can be edited");
        if (e.GetType().Assembly != typeof(Line).Assembly || e is not (Line or Circle or Arc or Polyline or Polyline3d or Spline or SubDMesh or DBPoint or DBText or MText or BlockReference or Dimension or Hatch or Solid3d or Ellipse))
            throw new CadFault("SPECIAL_OBJECT", "Special objects need a vendor API or explicitly targeted AutoLISP");
        if (e is BlockReference block && ((BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead)).IsFromExternalReference)
            throw new CadFault("XREF_OBJECT", "This path does not edit external references");
        if (write) { RequireUnlocked(tr, e); e.UpgradeOpen(); }
        return e;
    }
    private static void PopulatePolyline3d(Polyline3d polyline, JsonElement op, Transaction tr)
    {
        foreach (var value in op.GetProperty("points").EnumerateArray())
        {
            var point = EditPlan.Point(value);
            var vertex = new PolylineVertex3d(new Point3d(point[0], point[1], point[2]));
            polyline.AppendVertex(vertex);
            tr.AddNewlyCreatedDBObject(vertex, true);
        }
        polyline.Closed = Bool(op, "closed");
    }
    private static void Set(Database db, Transaction tr, Entity entity, JsonElement op)
    {
        foreach (var p in op.EnumerateObject())
        {
            switch (p.Name)
            {
                case "op": case "id": case "handle": case "target": break;
                case "layer": entity.LayerId = Layer(db, tr, p.Value.GetString()!); break;
                case "color_index": entity.ColorIndex = p.Value.GetInt32(); break;
                case "visible": entity.Visible = p.Value.GetBoolean(); break;
                case "linetype":
                    var types = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                    if (!types.Has(p.Value.GetString()!)) throw new CadFault("LINETYPE_NOT_FOUND", p.Value.GetString()!);
                    entity.LinetypeId = types[p.Value.GetString()!]; break;
                case "linetype_scale": entity.LinetypeScale = p.Value.GetDouble(); break;
                case "lineweight":
                    int weight = p.Value.GetInt32();
                    if (!Enum.IsDefined(typeof(LineWeight), weight)) throw new CadFault("INVALID_LINEWEIGHT", "Expected a supported LineWeight value in hundredths of mm, or -1/-2/-3");
                    entity.LineWeight = (LineWeight)weight; break;
                case "attributes" when entity is BlockReference block: Attributes(db, tr, block, op, false); break;
                case "text" when entity is DBText text: text.TextString = p.Value.GetString()!; break;
                case "text" when entity is MText mtext: mtext.Contents = p.Value.GetString()!; break;
                case "text" when entity is Dimension dimension: dimension.DimensionText = p.Value.GetString()!; dimension.RecomputeDimensionBlock(true); break;
                case "position" when entity is DBText text: text.Position = Point(op, "position"); text.AdjustAlignment(db); break;
                case "position" when entity is MText text: text.Location = Point(op, "position"); break;
                case "position" when entity is BlockReference block: block.Position = Point(op, "position"); TransformAttributes(tr, block); break;
                case "height" when entity is DBText text: text.Height = p.Value.GetDouble(); break;
                case "height" when entity is MText text: text.TextHeight = p.Value.GetDouble(); break;
                case "width" when entity is MText text: text.Width = p.Value.GetDouble(); break;
                case "rotation_deg" when entity is DBText text: text.Rotation = Angle(op, "rotation_deg"); break;
                case "rotation_deg" when entity is MText text: text.Rotation = Angle(op, "rotation_deg"); break;
                case "rotation_deg" when entity is BlockReference block: block.Rotation = Angle(op, "rotation_deg"); TransformAttributes(tr, block); break;
                case "start" when entity is Line line: line.StartPoint = Point(op, "start"); break;
                case "end" when entity is Line line: line.EndPoint = Point(op, "end"); break;
                case "center" when entity is Circle circle: circle.Center = Point(op, "center"); break;
                case "center" when entity is Arc arc: arc.Center = Point(op, "center"); break;
                case "radius" when entity is Circle circle: circle.Radius = p.Value.GetDouble(); break;
                case "radius" when entity is Arc arc: arc.Radius = p.Value.GetDouble(); break;
                case "closed" when entity is Polyline poly: poly.Closed = p.Value.GetBoolean(); break;
                default: throw new CadFault("PROPERTY_NOT_SUPPORTED", p.Name + " on " + entity.GetType().Name);
            }
        }
    }
    private static void TransformAttributes(Transaction tr, BlockReference block)
    {
        // Position/rotation through TransformBy is preferable: changing only the reference does not move existing attributes.
        if (block.AttributeCollection.Count > 0) throw new CadFault("ATTRIBUTE_TRANSFORM", "Use move/rotate for attributed blocks; set position/rotation would leave attributes behind");
    }
    private static void Attributes(Database db, Transaction tr, BlockReference block, JsonElement op, bool create)
    {
        var requested = op.TryGetProperty("attributes", out var a) ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (create)
        {
            var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad || ad.Constant) continue;
                var ar = new AttributeReference(); ar.SetAttributeFromBlock(ad, block.BlockTransform);
                ar.TextString = requested.TryGetValue(ad.Tag, out var value) ? value : ad.TextString;
                block.AttributeCollection.AppendAttribute(ar); tr.AddNewlyCreatedDBObject(ar, true);
                if (requested.ContainsKey(ad.Tag)) applied.Add(ad.Tag);
            }
        }
        else
        {
            foreach (ObjectId id in block.AttributeCollection)
            {
                var ar = (AttributeReference)tr.GetObject(id, OpenMode.ForRead);
                if (!requested.TryGetValue(ar.Tag, out var value)) continue;
                ar.UpgradeOpen(); ar.TextString = value; if (ar.IsMTextAttribute) ar.UpdateMTextAttribute(); applied.Add(ar.Tag);
            }
        }
        if (requested.Keys.Any(tag => !applied.Contains(tag))) throw new CadFault("ATTRIBUTE_NOT_FOUND", string.Join(", ", requested.Keys.Where(tag => !applied.Contains(tag))));
    }
    private static void SetupHatch(Database db, Transaction tr, Hatch hatch, JsonElement op, Dictionary<string, ObjectId> aliases)
    {
        hatch.SetHatchPattern(HatchPatternType.PreDefined, op.Text("pattern") ?? "SOLID");
        hatch.PatternScale = N(op, "scale", 1); hatch.PatternAngle = Angle(op, "angle_deg"); hatch.Associative = true;
        bool first = true;
        foreach (var boundary in op.GetProperty("boundaries").EnumerateArray())
        {
            string key = boundary.GetString()!;
            var id = aliases.TryGetValue(key, out var alias) ? alias : Handle(db, key);
            var e = Editable(db, tr, id, false);
            if (e is not Circle && (e is not Polyline p || !p.Closed)) throw new CadFault("INVALID_HATCH_BOUNDARY", "Expected a closed polyline or circle");
            hatch.AppendLoop(first ? HatchLoopTypes.Outermost : HatchLoopTypes.Default, new ObjectIdCollection([id])); first = false;
        }
        hatch.EvaluateHatch(true);
    }
}
