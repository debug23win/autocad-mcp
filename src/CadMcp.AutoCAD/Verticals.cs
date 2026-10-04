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
        if (CivilDocument() is null || type is null) throw new CadFault("CIVIL3D_REQUIRED", "Civil 3D managed API is not loaded");
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
        var civil = CivilDocument();
        var project = MapProject(db);
        return new {
            civil3d = civil is null ? new { available = false, reason = "Civil 3D managed API is not loaded in this product" } as object
                : new { available = true, surfaces = CivilCollection(civil, "GetSurfaceIds", tr),
                    alignments = CivilCollection(civil, "GetAlignmentIds", tr),
                    profiles = CivilProfiles(civil, tr),
                    pipe_networks = CivilCollection(civil, "GetPipeNetworkIds", tr) },
            map3d = project is null ? new { available = false, reason = "Map 3D managed API is not loaded in this product" } as object
                : new { available = true, coordinate_system = Scalar(project, "Projection"),
                    vertical_coordinate_system = Scalar(project, "VerticalProjection"),
                    object_data_tables = MapTableNames(project) }
        };
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
        return new { handle, class_name = type.FullName, civil3d = civil,
            properties = Properties(obj), children = civil ? Children(obj, tr) : null,
            map_object_data = objectData, surface_elevations = elevations,
            limitations = new[] { "vendor_geometry_requires_product_API", "object_data_record_limit_100_per_entity" } };
    }

    internal static object? CivilDocument()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AeccDbMgd");
            var type = assembly?.GetType("Autodesk.Civil.ApplicationServices.CivilApplication");
            return type?.GetProperty("ActiveDocument", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
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

    private static Dictionary<string, object> Properties(object obj)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (string name in ScalarNames)
        {
            var value = Scalar(obj, name);
            if (value is not null) result[name] = value;
        }
        return result;
    }

    private static object? Scalar(object obj, string name)
    {
        try
        {
            var property = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetMethod is null || property.GetIndexParameters().Length != 0) return null;
            var value = property.GetValue(obj);
            if (value is string s) return s.Length <= 500 ? s : s[..500];
            if (value is bool or byte or short or int or long or float or double or decimal)
                return value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f) ? null : value;
            if (value is Enum) return value.ToString();
        }
        catch (System.Exception) { }
        return null;
    }

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
                    args[i] = type == typeof(ObjectId) ? id : type == typeof(int) ? 0 : type == typeof(bool) ? false
                        : type.IsEnum ? Enum.GetNames(type).Contains("OpenForRead") ? Enum.Parse(type, "OpenForRead") : Enum.GetValues(type).GetValue(0)
                        : parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                    if (args[i] is null && !parameters[i].HasDefaultValue) supported = false;
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
        catch (System.Exception e) { return new { error = e.Message }; }
        return null;
    }

    private static object MapValues(object record, object? definitions)
    {
        try
        {
            var count = record.GetType().GetProperty("Count")?.GetValue(record) as int?
                ?? definitions?.GetType().GetProperty("Count")?.GetValue(definitions) as int?;
            var indexer = record.GetType().GetProperty("Item", [typeof(int)]);
            if (count is null || indexer is null) return new { available = false };
            var values = new List<object>();
            var fieldIndexer = definitions?.GetType().GetProperty("Item", [typeof(int)]);
            for (int i = 0; i < Math.Min(count.Value, 100); i++)
            {
                var value = indexer.GetValue(record, [i]);
                var field = fieldIndexer?.GetValue(definitions, [i]);
                if (value is not null) values.Add(new { index = i, name = field is null ? null : Scalar(field, "Name"),
                    type = Scalar(value, "Type"),
                    text = Scalar(value, "StrValue"), integer = Scalar(value, "Int32Value"),
                    number = Scalar(value, "DoubleValue") });
            }
            return values;
        }
        catch (System.Exception e) { return new { error = e.Message }; }
    }
}
