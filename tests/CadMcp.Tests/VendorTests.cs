using System.Reflection;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class VendorReflectionTests
{
    public class StyleBase { public string Name => "style"; public double Elevation { get; set; } }
    // Civil StyleBase-like hiding: a set-only "new" Name on the derived class.
    public sealed class LabelStyle : StyleBase { public new string Name { set { } } }
    public class Overridden { public virtual string Name => "base"; }
    public sealed class Hiding : Overridden { public new string Name => "derived"; }
    public readonly record struct Point2(double X, double Y);
    public readonly record struct Point3(double X, double Y, double Z);
    public sealed class Entities
    {
        public string Last = "";
        public string AddFixedLine(Point3 start, Point3 end) => Last = "line " + start + end;
        public string AddFixedTangent(Point2 start, Point2 end) => Last = "tangent2";
        public string AddFixedTangent(Point3 start, Point3 end) => Last = "tangent3";
        public string Records(uint offset, string id, bool erased) => "records " + offset + id + erased;
        public string Measure(int value) => "int";
        public string Measure(long value) => "long";
        public static string Create(string name, ref int id) { id = 42; return name; }
        public string Fail() => throw new InvalidOperationException("vendor said no");
    }
    public sealed class Records { private readonly string[] values = ["a", "b"]; public string this[uint index] => values[index]; public int Count => values.Length; }
    public sealed class GeneralProperties { public int NumberOfPoints => 3; public double MaximumElevation => 12.5; public double Broken => double.NaN; }
    public sealed class Surface
    {
        public GeneralProperties GetGeneralProperties() => new();
        public object GetTinProperties() => throw new InvalidOperationException("Surface out of date");
    }
    public sealed class MapException(string message) : Exception(message) { public string ErrorCode => "ErrorCode(-12)"; }
    public sealed class MapApi { public void GetObjectTableRecords(uint offset, string id, bool openErased) { } }

    [Fact]
    public void Hidden_set_only_property_falls_back_to_the_readable_base_getter()
    {
        Assert.Equal("style", VendorReflection.TryGet(new LabelStyle(), "Name"));
        Assert.Equal("derived", VendorReflection.TryGet(new Hiding(), "Name"));
        Assert.Null(VendorReflection.TryGet(new LabelStyle(), "Missing"));
        Assert.Equal(new Dictionary<string, object> { ["Name"] = "style", ["Elevation"] = 0d }, VendorReflection.Scalars(new LabelStyle(), ["Name", "Elevation", "Missing"]));
    }

    [Fact]
    public void Overloads_bind_by_argument_type_with_lossless_numeric_conversion()
    {
        var entities = new Entities();
        // Alignment lines exist only with Point3d: a Point2d argument must not bind.
        Assert.Equal("VERTICAL_API_UNAVAILABLE", Assert.Throws<CadFault>(() => VendorReflection.Invoke(entities, "AddFixedLine", new Point2(0, 0), new Point2(1, 0))).Code);
        VendorReflection.Invoke(entities, "AddFixedLine", new Point3(0, 0, 0), new Point3(1, 0, 0));
        Assert.StartsWith("line", entities.Last);
        Assert.Equal("tangent2", VendorReflection.Invoke(entities, "AddFixedTangent", new Point2(0, 0), new Point2(1, 0)));
        // A Map record offset is uint: an int literal converts, a fractional number never truncates.
        Assert.Equal("records 0XFalse", VendorReflection.Invoke(entities, "Records", 0, "X", false));
        Assert.Throws<CadFault>(() => VendorReflection.Invoke(entities, "Records", -1, "X", false));
        Assert.Throws<CadFault>(() => VendorReflection.Invoke(entities, "Records", 1.5, "X", false));
        Assert.Equal("int", VendorReflection.Invoke(entities, "Measure", 5));
        Assert.Equal("long", VendorReflection.Invoke(entities, "Measure", 5L));
    }

    [Fact]
    public void Ref_results_are_copied_back_and_vendor_errors_are_described()
    {
        object?[] args = ["network", 0];
        Assert.Equal("network", VendorReflection.Invoke(typeof(Entities), "Create", args));
        Assert.Equal(42, args[1]);
        var failure = Assert.Throws<CadFault>(() => VendorReflection.Invoke(new Entities(), "Fail"));
        Assert.Equal("VERTICAL_API_ERROR", failure.Code);
        Assert.Contains("vendor said no", failure.Message);
        Assert.Equal("MapException (ErrorCode(-12))", VendorReflection.Describe(new TargetInvocationException(new MapException(""))));
    }

    [Fact]
    public void Indexers_setters_statistics_and_defaults_follow_vendor_types()
    {
        Assert.Equal("b", VendorReflection.Index(new Records(), 1));
        var style = new StyleBase();
        VendorReflection.Set(style, "Elevation", 7);
        Assert.Equal(7d, style.Elevation);
        Assert.Equal("VERTICAL_PROPERTY_UNAVAILABLE", Assert.Throws<CadFault>(() => VendorReflection.Set(style, "Name", "x")).Code);
        var statistics = VendorReflection.Statistics(new Surface(), ["GetGeneralProperties", "GetTinProperties", "GetTerrainProperties"]);
        var general = Assert.IsType<Dictionary<string, object>>(statistics["GetGeneralProperties"]);
        Assert.Equal(3, general["NumberOfPoints"]);
        Assert.False(general.ContainsKey("Broken"), "Non-finite statistics leaked");
        Assert.Contains("out of date", Wire.Element(statistics["GetTinProperties"]).GetProperty("error").GetString());
        Assert.False(statistics.ContainsKey("GetTerrainProperties"));
        var parameters = typeof(MapApi).GetMethod("GetObjectTableRecords")!.GetParameters();
        Assert.True(VendorReflection.TryDefault(parameters[0], out var offset) && offset is uint and 0);
        Assert.True(VendorReflection.TryDefault(parameters[2], out var erased) && erased is false);
        Assert.False(VendorReflection.TryDefault(parameters[1], out _), "A required reference argument was invented");
    }
}
