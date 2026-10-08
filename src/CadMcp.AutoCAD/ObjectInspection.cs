using System.Runtime.InteropServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadMcp.Core;
#if !CORE_CONSOLE
using Autodesk.AutoCAD.Internal.PropertyInspector;
#endif

namespace CadMcp.AutoCAD;

/// <summary>
/// Reading objects beyond the fixed geometry of cad_entity_get: the properties the Properties palette (OPM) shows for any
/// object, including properties that verticals and object enablers add (cad_properties), and proxy objects left by
/// applications that are not loaded (cad_proxies).
/// </summary>
internal static class ObjectInspection
{
    private static string H(ObjectId id) => id.Handle.ToString();

    private static ObjectId[] Handles(Database db, JsonElement data, int max)
    {
        var json = data.Text("handles_json") ?? throw new CadFault("MISSING_FIELD", "handles_json");
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() < 1 || parsed.RootElement.GetArrayLength() > max)
            throw new CadFault("INVALID_HANDLES", "handles_json is an array of 1.." + max + " hexadecimal handles");
        return parsed.RootElement.EnumerateArray().Select(v => Edits.Handle(db, v.GetString() ?? "")).Distinct().ToArray();
    }

    /// <summary>cad_properties: the Properties palette of up to 20 objects, by category, with plain values.</summary>
    internal static object Properties(Database db, Transaction tr, JsonElement data, CancellationToken ct)
    {
        int limit = DraftingPlan.Integer(data, "max_properties", 1, 1000, 300);
        var objects = new List<object>();
        foreach (var id in Handles(db, data, 20))
        {
            ct.ThrowIfCancellationRequested();
            var obj = tr.GetObject(id, OpenMode.ForRead);
            objects.Add(new
            {
                handle = H(id), type = obj.GetType().Name, dxf_name = id.ObjectClass.DxfName, layer = (obj as Entity)?.Layer,
                proxy = obj switch { ProxyEntity p => Proxy(p.OriginalClassName, p.OriginalDxfName, p.ApplicationDescription, p.ProxyFlags, true), ProxyObject p => Proxy(p.OriginalClassName, p.OriginalDxfName, p.ApplicationDescription, p.ProxyFlags, false), _ => null },
                palette = Palette(id, limit)
            });
        }
        return new { objects, note = "Values are as the Properties palette shows them in this AutoCAD, including names in its language; objects shown as their own object references are left out" };
    }

    private static object Palette(ObjectId id, int limit)
    {
#if CORE_CONSOLE
        return new { error = "The Properties palette is not available in the AutoCAD core console" };
#else
        var categories = new List<object>();
        int count = 0;
        bool truncated = false;
        IntPtr unknown = IntPtr.Zero;
        try
        {
            string? name = null;
            try { name = ObjectPropertyManagerProperties.GetDisplayName(id); } catch (System.Exception) { }
            unknown = ObjectPropertyManagerPropertyUtility.GetIUnknownFromObjectId(id);
            if (unknown == IntPtr.Zero) return new { display_name = name, error = "The object has no COM wrapper for the Properties palette" };
            using var vector = ObjectPropertyManagerProperties.GetProperties(id, false, false);
            if (vector is null) return new { display_name = name, error = "The Properties palette returned no categories" };
            int categoryCount = vector.Count();
            for (int c = 0; c < categoryCount && !truncated; c++)
            {
                if (vector.Item(c) is not CategoryCollectable category) continue;
                var properties = new List<object>();
                var items = category.Properties;
                int itemCount = items?.Count() ?? 0;
                for (int p = 0; p < itemCount; p++)
                {
                    if (count >= limit) { truncated = true; break; }
                    try
                    {
                        if (items!.Item(p) is not PropertyCollectable property) continue;
                        object? value = null;
                        if (!property.GetValue(unknown, ref value)) continue;
                        if (Plain(value) is not { } plain) continue;
                        properties.Add(new { name = property.Name, value = plain });
                        count++;
                    }
                    catch (System.Exception) { }
                }
                if (properties.Count > 0) categories.Add(new { category = category.Name, properties });
            }
            return new { display_name = name, categories, properties = count, truncated = truncated ? true : (bool?)null };
        }
        catch (System.Exception e) { return new { categories, error = e.GetType().Name + ": " + e.Message }; }
        finally { if (unknown != IntPtr.Zero) Marshal.Release(unknown); }
#endif
    }

    /// <summary>A palette value as JSON can carry it; COM objects (colours, sub-objects) are left out.</summary>
    private static object? Plain(object? value, int depth = 0) => value switch
    {
        null or DBNull => null,
        string text => text.Length <= 1000 ? text : text[..1000] + "…",
        bool or int or short or long or byte or uint or ushort or ulong or sbyte => value,
        double d => double.IsFinite(d) ? Math.Round(d, 9) : d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        float f => double.IsFinite(f) ? Math.Round(f, 6) : f.ToString(System.Globalization.CultureInfo.InvariantCulture),
        decimal m => m,
        Array array when depth == 0 && array.Length <= 64 => array.Cast<object?>().Select(item => Plain(item, 1)).ToArray(),
        Array => null,
        _ when Marshal.IsComObject(value) => null,
        _ => value.ToString()
    };

    // Proxy flags as ObjectARX dbproxy.h defines them; the raw value is reported too.
    private static readonly (int Bit, string Name)[] EntityFlags =
        [(0x1, "erase"), (0x2, "transform"), (0x4, "color"), (0x8, "layer"), (0x10, "linetype"), (0x20, "linetype_scale"), (0x40, "visibility"), (0x80, "cloning"), (0x100, "lineweight"), (0x200, "plot_style"), (0x800, "material")];
    private static readonly (int Bit, string Name)[] ObjectFlags = [(0x1, "erase"), (0x80, "cloning")];

    private static object Proxy(string className, string dxfName, string application, int flags, bool entity) => new
    {
        original_class = className, original_dxf_name = dxfName, application,
        flags = "0x" + flags.ToString("X"), allowed = (entity ? EntityFlags : ObjectFlags).Where(f => (flags & f.Bit) != 0).Select(f => f.Name).ToArray(),
        warning_disabled = (flags & 0x400) != 0 ? true : (bool?)null
    };

    private sealed class Group
    {
        public required string Kind, ClassName, DxfName, Application;
        public int Count;
        public readonly SortedDictionary<string, int> Owners = new(StringComparer.Ordinal), Graphics = new(StringComparer.Ordinal);
        public readonly List<string> Samples = new();
        public int Flags = -1;
    }

    /// <summary>
    /// cad_proxies: every proxy entity and proxy object of the drawing, grouped by original class and application, found by
    /// a scan of all handles; with handles_json, details of up to 20 proxies, their graphics exploded in memory.
    /// </summary>
    internal static object Proxies(Database db, Transaction tr, JsonElement data, CancellationToken ct)
    {
        if (data.Text("handles_json") is not null) return ProxyDetails(db, tr, data, ct);
        int samples = DraftingPlan.Integer(data, "max_samples", 1, 100, 20);
        var entityClass = RXObject.GetClass(typeof(ProxyEntity));
        var objectClass = RXObject.GetClass(typeof(ProxyObject));
        var groups = new Dictionary<(string, string, string, string), Group>();
        long scanned = 0, last = db.Handseed.Value, limit = 20_000_000;
        bool truncated = last - 1 > limit;
        for (long value = 1; value < Math.Min(last, limit + 1); value++)
        {
            if ((++scanned & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (!db.TryGetObjectId(new Handle(value), out var id) || id.IsNull || id.IsErased) continue;
            var rx = id.ObjectClass;
            bool isEntity = rx.IsDerivedFrom(entityClass);
            if (!isEntity && !rx.IsDerivedFrom(objectClass)) continue;
            var obj = tr.GetObject(id, OpenMode.ForRead, true);
            string className, dxfName, application, graphics = "";
            int flags;
            if (obj is ProxyEntity entity) { className = entity.OriginalClassName; dxfName = entity.OriginalDxfName; application = entity.ApplicationDescription; flags = entity.ProxyFlags; graphics = entity.GraphicsMetafileType.ToString(); }
            else if (obj is ProxyObject proxy) { className = proxy.OriginalClassName; dxfName = proxy.OriginalDxfName; application = proxy.ApplicationDescription; flags = proxy.ProxyFlags; }
            else continue;
            var key = (isEntity ? "entity" : "object", className, dxfName, application);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new() { Kind = key.Item1, ClassName = className, DxfName = dxfName, Application = application };
            group.Count++;
            group.Flags = group.Flags < 0 ? flags : group.Flags & flags;
            string owner = Owner(tr, obj.OwnerId);
            group.Owners[owner] = group.Owners.GetValueOrDefault(owner) + 1;
            if (graphics.Length > 0) group.Graphics[graphics] = group.Graphics.GetValueOrDefault(graphics) + 1;
            if (group.Samples.Count < samples) group.Samples.Add(H(id));
        }
        var ordered = groups.Values.OrderByDescending(g => g.Count).ThenBy(g => g.ClassName, StringComparer.Ordinal).ToArray();
        return new
        {
            proxy_entities = ordered.Where(g => g.Kind == "entity").Sum(g => g.Count), proxy_objects = ordered.Where(g => g.Kind == "object").Sum(g => g.Count),
            groups = ordered.Select(g => new
            {
                kind = g.Kind, original_class = g.ClassName, original_dxf_name = g.DxfName, application = g.Application, count = g.Count,
                owners = g.Owners.OrderByDescending(o => o.Value).Take(20).ToDictionary(o => o.Key, o => o.Value), graphics = g.Graphics.Count == 0 ? null : g.Graphics,
                common_flags = Proxy(g.ClassName, g.DxfName, g.Application, Math.Max(0, g.Flags), g.Kind == "entity"), sample_handles = g.Samples
            }).ToArray(),
            handles_scanned = scanned, truncated = truncated ? true : (bool?)null,
            note = ordered.Length == 0 ? "No proxy objects: every object's application is loaded"
                : "Proxies stand for objects of applications that are not loaded; install their object enabler to edit them. common_flags lists the operations every proxy of the group allows"
        };
    }

    private static string Owner(Transaction tr, ObjectId ownerId)
    {
        if (ownerId.IsNull || ownerId.IsErased) return "none";
        try
        {
            return tr.GetObject(ownerId, OpenMode.ForRead) switch
            {
                BlockTableRecord { IsLayout: true } space => space.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase) ? "model"
                    : "layout " + ((Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead)).LayoutName,
                BlockTableRecord block => "block " + block.Name,
                DBDictionary => "dictionary",
                var other => other.GetType().Name
            };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return "unreadable owner"; }
    }

    private static object ProxyDetails(Database db, Transaction tr, JsonElement data, CancellationToken ct)
    {
        var details = new List<object>();
        foreach (var id in Handles(db, data, 20))
        {
            ct.ThrowIfCancellationRequested();
            var obj = tr.GetObject(id, OpenMode.ForRead, true);
            switch (obj)
            {
                case ProxyEntity entity:
                    details.Add(new
                    {
                        handle = H(id), kind = "entity", proxy = Proxy(entity.OriginalClassName, entity.OriginalDxfName, entity.ApplicationDescription, entity.ProxyFlags, true),
                        graphics = entity.GraphicsMetafileType.ToString(), layer = entity.Layer, owner = Owner(tr, entity.OwnerId), extents = Extents(entity), exploded = Exploded(entity)
                    });
                    break;
                case ProxyObject proxy:
                    details.Add(new
                    {
                        handle = H(id), kind = "object", proxy = Proxy(proxy.OriginalClassName, proxy.OriginalDxfName, proxy.ApplicationDescription, proxy.ProxyFlags, false),
                        owner = Owner(tr, proxy.OwnerId), key = DictionaryKey(tr, proxy)
                    });
                    break;
                default:
                    details.Add(new { handle = H(id), kind = "not_a_proxy", type = obj.GetType().Name });
                    break;
            }
        }
        return new { details };
    }

    private static double[][]? Extents(Entity entity)
    {
        try { var box = entity.GeometricExtents; return [[box.MinPoint.X, box.MinPoint.Y, box.MinPoint.Z], [box.MaxPoint.X, box.MaxPoint.Y, box.MaxPoint.Z]]; }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }

    /// <summary>The proxy's stored graphics, exploded into temporary objects that are read and disposed, never added to the drawing.</summary>
    private static object Exploded(ProxyEntity entity)
    {
        var parts = new DBObjectCollection();
        try
        {
            entity.Explode(parts);
            var types = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var texts = new List<string>();
            Extents3d? bounds = null;
            foreach (DBObject part in parts)
            {
                types[part.GetType().Name] = types.GetValueOrDefault(part.GetType().Name) + 1;
                if (part is DBText text && texts.Count < 50) texts.Add(CadText.Normalize(text.TextString));
                else if (part is MText mtext && texts.Count < 50) texts.Add(CadText.Normalize(mtext.Contents, true));
                if (part is Entity piece)
                    try { var box = piece.GeometricExtents; if (bounds is { } b) { b.AddExtents(box); bounds = b; } else bounds = box; }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
            }
            return new
            {
                parts = parts.Count, types, texts = texts.Count == 0 ? null : texts,
                extents = bounds is { } e ? new[] { new[] { e.MinPoint.X, e.MinPoint.Y, e.MinPoint.Z }, new[] { e.MaxPoint.X, e.MaxPoint.Y, e.MaxPoint.Z } } : null
            };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception e) { return new { error = "The proxy graphics cannot be exploded: " + e.ErrorStatus }; }
        finally { foreach (DBObject part in parts) part.Dispose(); parts.Dispose(); }
    }

    private static string? DictionaryKey(Transaction tr, DBObject obj)
    {
        try
        {
            if (obj.OwnerId.IsNull || tr.GetObject(obj.OwnerId, OpenMode.ForRead) is not DBDictionary dictionary) return null;
            foreach (DBDictionaryEntry entry in dictionary) if (entry.Value == obj.ObjectId) return entry.Key;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        return null;
    }
}
