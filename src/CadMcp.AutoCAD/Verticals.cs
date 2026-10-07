using System.Collections;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

/// <summary>Read-only adapters for optional product APIs; no vendor DLL is copied into base AutoCAD.</summary>
internal static class Verticals
{
    private static readonly string[] ScalarNames = ["Name", "Description", "StyleName", "SurfaceName", "AlignmentName",
        "ProfileName", "StartStation", "EndStation", "StartingStation", "EndingStation", "Length", "Area",
        "MinimumElevation", "MaximumElevation", "Elevation", "NumberOfPoints", "NumberOfTriangles",
        "RimElevation", "SumpElevation", "InnerDiameterOrWidth", "InnerHeight", "IsReferenceObject"];

    public static (ObjectId Id, object Detail) EditTin(Database db, Transaction tr, System.Text.Json.JsonElement operation)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AeccDbMgd");
        var type = assembly?.GetType("Autodesk.Civil.DatabaseServices.TinSurface");
        if (CivilDocument(db) is null || type is null) throw new CadFault("CIVIL3D_REQUIRED", "Civil 3D managed API is not loaded");
        var points = new Point3dCollection();
        foreach (var item in operation.GetProperty("vertices").EnumerateArray())
        {
            var p = EditPlan.Point(item); points.Add(new Point3d(p[0], p[1], p[2]));
        }
        if (operation.GetProperty("op").GetString() == "civil_tin_create")
        {
            bool planarCollinear = true;
            var first = points[0];
            for (int i = 1; i < points.Count && planarCollinear; i++)
                for (int j = i + 1; j < points.Count; j++)
                    if (Math.Abs((points[i].X - first.X) * (points[j].Y - first.Y) -
                                 (points[j].X - first.X) * (points[i].Y - first.Y)) > 1e-10)
                    { planarCollinear = false; break; }
            if (planarCollinear) throw new CadFault("TIN_POINTS_COLLINEAR", "TIN surface needs three non-collinear XY points");
        }
        try
        {
            ObjectId id;
            if (operation.GetProperty("op").GetString() == "civil_tin_create")
            {
                var create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, [typeof(Database), typeof(string)], null)
                    ?? throw new CadFault("CIVIL_API_UNAVAILABLE", "TinSurface.Create(Database, String) is unavailable");
                id = (ObjectId)create.Invoke(null, [db, EditPlan.RequiredText(operation, "name")])!;
            }
            else
            {
                string handle = EditPlan.RequiredText(operation, "handle");
                if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, null, out var h) ||
                    !db.TryGetObjectId(new Handle(h), out id) || id.IsErased)
                    throw new CadFault("SURFACE_NOT_FOUND", handle);
            }
            var surface = tr.GetObject(id, OpenMode.ForWrite);
            if (!type.IsInstanceOfType(surface)) throw new CadFault("SURFACE_REQUIRED", "Handle is not a Civil 3D TIN surface");
            var add = type.GetMethod("AddVertices", [typeof(Point3dCollection)])
                ?? throw new CadFault("CIVIL_API_UNAVAILABLE", "TinSurface.AddVertices is unavailable");
            add.Invoke(surface, [points]);
            return (id, new { handle = id.Handle.ToString(), name = Scalar(surface, "Name"),
                added_vertices = points.Count, coordinate_system = "WCS", verification = "Civil 3D database readback before transaction commit" });
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { throw new CadFault("CIVIL_API_ERROR", e.InnerException.Message); }
    }

    public static object Catalog(Database db, Transaction tr)
    {
        var civil = CivilDocument(db);
        var project = MapProject(db);
        return new {
            civil3d = civil is null ? new { available = false, reason = "Civil 3D managed API is not loaded in this product" } as object
                : new { available = true, surfaces = CivilCollection(civil, "GetSurfaceIds", tr),
                    alignments = CivilCollection(civil, "GetAlignmentIds", tr),
                    profiles = CivilProfiles(civil, tr),
                    pipe_networks = CivilCollection(civil, "GetPipeNetworkIds", tr),
                    sites = CivilCollection(civil, "GetSiteIds", tr),
                    styles = CivilStyles(civil, tr), label_sets = CivilLabelSets(civil, tr), parts_lists = CivilPartsLists(civil, tr),
                    naming = "Operations accept these names instead of handles: style, label_set, layer, parts_list, family, size" },
            map3d = project is null ? new { available = false, reason = "Map 3D managed API is not loaded in this product" } as object
                : new { available = true, coordinate_system = Scalar(project, "Projection"),
                    vertical_coordinate_system = Scalar(project, "VerticalProjection"),
                    object_data_tables = MapTableNames(project) }
        };
    }

    private static readonly string[] StyleCollections = ["AlignmentStyles", "ProfileStyles", "ProfileViewStyles", "SurfaceStyles", "PipeStyles", "StructureStyles",
        "PipeRuleSetStyles", "StructureRuleSetStyles", "PointStyles", "FeatureLineStyles", "CorridorStyles", "SampleLineStyles", "SectionStyles"];

    private static object[] Names(object? collection, Transaction tr, int limit = 100) =>
        VerticalEditing.Named(collection, tr).Take(limit).Select(n => (object)new { name = n.Name, handle = n.Id.Handle.ToString() }).ToArray();

    private static object? CivilStyles(object civil, Transaction tr)
    {
        try
        {
            if (VendorReflection.TryGet(civil, "Styles") is not { } root) return null;
            var result = new SortedDictionary<string, object[]>(StringComparer.Ordinal);
            foreach (var name in StyleCollections)
                if (VendorReflection.TryGet(root, name) is { } collection) result[name] = Names(collection, tr);
            return result;
        }
        catch (System.Exception e) when (e is CadFault or Autodesk.AutoCAD.Runtime.Exception or TargetInvocationException) { return new { error = e.Message }; }
    }

    /// <summary>Every label-set collection of the drawing, discovered from the LabelSetStyles root of this release.</summary>
    private static object? CivilLabelSets(object civil, Transaction tr)
    {
        try
        {
            if (VendorReflection.TryGet(civil, "Styles") is not { } styles || VendorReflection.TryGet(styles, "LabelSetStyles") is not { } root) return null;
            var result = new SortedDictionary<string, object[]>(StringComparer.Ordinal);
            foreach (var property in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
                if (VendorReflection.TryGet(root, property.Name) is IEnumerable collection and not string && Names(collection, tr) is { Length: > 0 } names)
                    result[property.Name] = names;
            return result;
        }
        catch (System.Exception e) when (e is CadFault or Autodesk.AutoCAD.Runtime.Exception or TargetInvocationException) { return new { error = e.Message }; }
    }

    private static object? CivilPartsLists(object civil, Transaction tr)
    {
        try
        {
            if (VendorReflection.TryGet(civil, "Styles") is not { } styles || VendorReflection.TryGet(styles, "PartsListSet") is not { } set) return null;
            return VerticalEditing.Named(set, tr).Take(30).Select(list => (object)new
            {
                name = list.Name, handle = list.Id.Handle.ToString(),
                families = VerticalEditing.Children(tr, tr.GetObject(list.Id, OpenMode.ForRead), "PartFamilyCount").Take(50).Select(family => new
                {
                    family = Convert.ToString(VendorReflection.TryGet(family.Object, "Description")), handle = family.Id.Handle.ToString(),
                    domain = Convert.ToString(VendorReflection.TryGet(family.Object, "Domain")),
                    sizes = VerticalEditing.Children(tr, family.Object, "PartSizeCount").Take(60)
                        .Select(size => new { size = VerticalEditing.SizeName(size.Object), handle = size.Id.Handle.ToString() }).ToArray()
                }).ToArray()
            }).ToArray();
        }
        catch (System.Exception e) when (e is CadFault or Autodesk.AutoCAD.Runtime.Exception or TargetInvocationException) { return new { error = e.Message }; }
    }

    public static object Inspect(Database db, Transaction tr, string handle, double[][]? samplePoints)
    {
        if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, null, out var value) || value <= 0)
            throw new CadFault("INVALID_HANDLE", "Expected a positive hexadecimal handle");
        if (!db.TryGetObjectId(new Handle(value), out var id) || id.IsErased)
            throw new CadFault("ENTITY_NOT_FOUND", handle);
        var obj = tr.GetObject(id, OpenMode.ForRead);
        var type = obj.GetType();
        bool civil = type.FullName?.StartsWith("Autodesk.Civil.", StringComparison.Ordinal) == true;
        var project = MapProject(db);
        var objectData = obj is Entity && project is not null ? MapObjectData(project, id) : null;
        if (!civil && objectData is null)
            throw new CadFault("VERTICAL_OBJECT_NOT_FOUND", "Handle has no Civil 3D object or readable Map 3D object data");
        object? elevations = null;
        if (samplePoints is not null)
        {
            if (!civil || !type.Name.EndsWith("Surface", StringComparison.Ordinal))
                throw new CadFault("SURFACE_REQUIRED", "Elevation sampling requires a Civil 3D surface handle");
            var method = type.GetMethod("FindElevationAtXY", [typeof(double), typeof(double)]);
            if (method is null) throw new CadFault("SURFACE_SAMPLING_UNAVAILABLE", type.FullName ?? type.Name);
            elevations = samplePoints.Select(p => {
                try { return new { point = p, elevation = (double?)Convert.ToDouble(method.Invoke(obj, [p[0], p[1]])), error = (string?)null }; }
                catch (System.Exception e) { return new { point = p, elevation = (double?)null,
                    error = (string?)(e is TargetInvocationException { InnerException: { } inner } ? inner : e).Message }; }
            }).ToArray();
        }
        // Surface point/triangle counts and elevation range live in GetGeneralProperties/GetTinProperties,
        // not on the surface object itself.
        var statistics = civil && type.Name.EndsWith("Surface", StringComparison.Ordinal)
            ? VendorReflection.Statistics(obj, ["GetGeneralProperties", "GetTinProperties", "GetTerrainProperties"]) : null;
        return new { handle, class_name = type.FullName, civil3d = civil,
            properties = Properties(obj), statistics = statistics is { Count: > 0 } ? statistics : null, children = civil ? Children(obj, tr) : null,
            map_object_data = objectData, surface_elevations = elevations,
            limitations = new[] { "vendor_geometry_requires_product_API", "object_data_record_limit_100_per_entity" } };
    }

    /// <summary>
    /// The Civil document of this database, so reads of a background drawing never list the active one.
    /// CivilApplication.ActiveDocument is used only when the database is the active drawing's.
    /// </summary>
    internal static object? CivilDocument(Database db)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AeccDbMgd");
            if (assembly is null) return null;
            var get = assembly.GetType("Autodesk.Civil.ApplicationServices.CivilDocument")
                ?.GetMethod("GetCivilDocument", BindingFlags.Public | BindingFlags.Static, [typeof(Database)]);
            if (get?.Invoke(null, [db]) is { } document) return document;
            var active = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Database;
            if (active is null || active.UnmanagedObject != db.UnmanagedObject) return null;
            return assembly.GetType("Autodesk.Civil.ApplicationServices.CivilApplication")
                ?.GetProperty("ActiveDocument", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }
        catch (System.Exception) { return null; }
    }

    private static object CivilCollection(object civil, string methodName, Transaction tr)
    {
        try
        {
            if (civil.GetType().GetMethod(methodName, Type.EmptyTypes)?.Invoke(civil, null) is not IEnumerable ids)
                return new { available = false, reason = methodName + " unavailable" } as object;
            var result = new List<object>(); int count = 0;
            foreach (var candidate in ids)
            {
                count++;
                if (result.Count == 200) continue;
                if (candidate is ObjectId id && !id.IsNull && !id.IsErased)
                {
                    try { result.Add(Summary(tr.GetObject(id, OpenMode.ForRead))); }
                    catch (System.Exception e) { result.Add(new { handle = id.Handle.ToString(), error = e.Message }); }
                }
            }
            return new { available = true, count, items = result, truncated = count > result.Count };
        }
        catch (System.Exception e) { return new { available = false, reason = e.Message }; }
    }

    private static object Summary(DBObject obj) => new { handle = obj.Handle.ToString(), class_name = obj.GetType().FullName,
        properties = Properties(obj) };

    private static object CivilProfiles(object civil, Transaction tr)
    {
        try
        {
            if (civil.GetType().GetMethod("GetAlignmentIds", Type.EmptyTypes)?.Invoke(civil, null) is not IEnumerable alignments)
                return new { available = false, reason = "GetAlignmentIds unavailable" } as object;
            var profiles = new List<object>(); int count = 0;
            foreach (var candidate in alignments)
            {
                if (candidate is not ObjectId alignmentId || alignmentId.IsNull || alignmentId.IsErased) continue;
                var alignment = tr.GetObject(alignmentId, OpenMode.ForRead);
                if (alignment.GetType().GetMethod("GetProfileIds", Type.EmptyTypes)?.Invoke(alignment, null) is not IEnumerable ids) continue;
                foreach (var profile in ids)
                {
                    if (profile is not ObjectId profileId || profileId.IsNull || profileId.IsErased) continue;
                    count++;
                    if (profiles.Count < 200) profiles.Add(Summary(tr.GetObject(profileId, OpenMode.ForRead)));
                }
            }
            return new { available = true, count, items = profiles, truncated = count > profiles.Count };
        }
        catch (System.Exception e) { return new { available = false, reason = e.Message }; }
    }

    private static Dictionary<string, object> Properties(object obj) => VendorReflection.Scalars(obj, ScalarNames);

    // Vendor classes hide inherited members (StyleBase.Name), so lookup walks the type hierarchy.
    private static object? Scalar(object obj, string name) => VendorReflection.Scalar(VendorReflection.TryGet(obj, name));

    private static object? Children(object obj, Transaction tr)
    {
        string[] methods = obj.GetType().Name switch
        {
            "Alignment" => ["GetProfileIds", "GetSampleLineGroupIds"],
            "Network" => ["GetPipeIds", "GetStructureIds"],
            _ => []
        };
        if (methods.Length == 0) return null;
        return methods.ToDictionary(name => name, name => CivilCollection(obj, name, tr));
    }

    internal static object? MapProject(Database db)
    {
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            var assembly = loaded.FirstOrDefault(a => a.GetName().Name == "ManagedMapApi");
            var commandLine = Environment.GetCommandLineArgs();
            bool mapProduct = loaded.Any(a => a.GetName().Name?.StartsWith("AcMap", StringComparison.OrdinalIgnoreCase) == true) ||
                Enumerable.Range(0, Math.Max(0, commandLine.Length - 1)).Any(i =>
                    commandLine[i].Equals("/product", StringComparison.OrdinalIgnoreCase) &&
                    commandLine[i + 1].Equals("MAP", StringComparison.OrdinalIgnoreCase));
            if (assembly is null && mapProduct)
            {
                var root = Path.GetDirectoryName(typeof(Database).Assembly.Location);
                var path = root is null ? null : Path.Combine(root, "Map", "ManagedMapApi.dll");
                if (path is not null && File.Exists(path)) assembly = Assembly.LoadFrom(path);
            }
            var type = assembly?.GetType("Autodesk.Gis.Map.HostMapApplicationServices") ?? assembly?.GetType("Autodesk.Gis.Map.MapApplication");
            var application = type?.GetProperty("Application", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var method = application?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetProjectForDB" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Database)));
            return method?.Invoke(application, [db]);
        }
        catch (System.Exception) { return null; }
    }

    private static object MapTableNames(object project)
    {
        try
        {
            var tables = project.GetType().GetProperty("ODTables")?.GetValue(project);
            var names = tables?.GetType().GetMethod("GetTableNames", Type.EmptyTypes)?.Invoke(tables, null) as IEnumerable;
            return names is null ? Array.Empty<string>() : names.Cast<object>().Select(x => x.ToString() ?? "").Take(200).ToArray();
        }
        catch (System.Exception e) { return new { error = e.Message }; }
    }

    private static object? MapObjectData(object project, ObjectId id)
    {
        try
        {
            var tables = project.GetType().GetProperty("ODTables")?.GetValue(project);
            if (tables is null) return null;
            var methods = tables.GetType().GetMethods().Where(m => m.Name == "GetObjectRecords");
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (!parameters.Any(p => p.ParameterType == typeof(ObjectId))) continue;
                var args = new object?[parameters.Length];
                bool supported = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    var type = parameters[i].ParameterType;
                    // The record offset is uint in the Map API; a boxed int would not bind.
                    if (type == typeof(ObjectId)) args[i] = id;
                    else if (type.IsEnum) args[i] = Enum.GetNames(type).Contains("OpenForRead") ? Enum.Parse(type, "OpenForRead") : Enum.GetValues(type).GetValue(0);
                    else if (!VendorReflection.TryDefault(parameters[i], out args[i])) supported = false;
                }
                if (!supported) continue;
                var records = method.Invoke(tables, args);
                if (records is not IEnumerable list) continue;
                try
                {
                    var items = new List<object>(); int count = 0;
                    foreach (var record in list)
                    {
                        if (record is null) continue;
                        count++;
                        if (items.Count < 100)
                        {
                            var tableName = Scalar(record, "TableName") as string;
                            var table = tableName is null ? null : tables.GetType().GetProperty("Item", [typeof(string)])?.GetValue(tables, [tableName]);
                            var definitions = table?.GetType().GetProperty("FieldDefinitions")?.GetValue(table);
                            items.Add(new { table = tableName, values = MapValues(record, definitions) });
                        }
                    }
                    return new { count, records = items, truncated = count > items.Count };
                }
                finally { (records as IDisposable)?.Dispose(); }
            }
        }
        catch (System.Exception e) { return new { error = VendorReflection.Describe(e) }; }
        return null;
    }

    private static object MapValues(object record, object? definitions)
    {
        try
        {
            var countValue = VendorReflection.TryGet(record, "Count") ?? (definitions is null ? null : VendorReflection.TryGet(definitions, "Count"));
            if (countValue is null) return new { available = false };
            int count = Convert.ToInt32(countValue, System.Globalization.CultureInfo.InvariantCulture);
            var values = new List<object>();
            for (int i = 0; i < Math.Min(count, 100); i++)
            {
                var value = VendorReflection.Index(record, i);
                object? field = null;
                if (definitions is not null) try { field = VendorReflection.Index(definitions, i); } catch (CadFault) { }
                if (value is not null) values.Add(new { index = i, name = field is null ? null : Scalar(field, "Name"),
                    type = Scalar(value, "Type"),
                    text = Scalar(value, "StrValue"), integer = Scalar(value, "Int32Value"),
                    number = Scalar(value, "DoubleValue") });
            }
            return values;
        }
        catch (System.Exception e) { return new { error = VendorReflection.Describe(e) }; }
    }
}
