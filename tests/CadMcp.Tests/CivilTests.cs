using System.Text.RegularExpressions;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class CivilTests
{
    private static Dictionary<string, HashSet<string>> Signatures()
    {
        var releases = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string>? current = null;
        foreach (var line in File.ReadLines(Path.Combine(TestEnvironment.RepositoryRoot, "tests", "CadMcp.Tests", "Fixtures", "civil-api-signatures.txt")))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[')) { releases[line.Trim('[', ']')] = current = new(StringComparer.Ordinal); continue; }
            current!.Add(line);
        }
        return releases;
    }

    [Fact]
    public void Every_reflected_Civil_member_exists_in_each_supported_release()
    {
        var releases = Signatures();
        Assert.Equal(new[] { "2024", "2025", "2026" }, releases.Keys.Order(StringComparer.Ordinal));
        foreach (var (release, members) in releases)
        {
            var missing = CivilApiContract.Members.Where(m => !members.Contains(m)).ToArray();
            Assert.True(missing.Length == 0, "Civil 3D " + release + " lacks: " + string.Join("; ", missing));
        }
    }

    [Fact]
    public void Member_names_in_the_Civil_code_are_in_the_contract()
    {
        static string Source(string file) => File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "src", "CadMcp.AutoCAD", file));
        var editing = Source("VerticalEditing.cs");
        // The Civil part of VerticalEditing; its Map part and the CS-MAP check are outside the Civil contract.
        var civil = editing[editing.IndexOf("internal static object Civil(", StringComparison.Ordinal)..editing.IndexOf("internal static object Capabilities(", StringComparison.Ordinal)];
        var literals = Regex.Matches(civil + Source("Verticals.cs"), "\"([A-Z][A-Za-z0-9]*)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var members = CivilApiContract.Members.Select(m => Regex.Match(m, @"\|[MP] (?:static )?\S+ (\w+)").Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var types = CivilApiContract.Members.Select(m => m.Split('|')[0].Split('.')[^1]).ToHashSet(StringComparer.Ordinal);
        string[] notMembers =
        [
            "AeccDbMgd", "ManagedMapApi", "AcMap", "MAP", "Map", "WCS", // assembly names and product markers
            "Compound", "PrtSN", "Point", "Id", "AlignmentLine", "Surface", // enum value, catalog field key, name suffix checks, type names
            "Application", "DoubleValue", "FieldDefinitions", "GetObjectRecords", "GetProjectForDB", "GetTableNames", "Int32Value", // Map 3D API
            "ODTables", "OpenForRead", "Projection", "StrValue", "TableName", "Type", "VerticalProjection"
        ];
        var unexplained = literals.Where(l => !members.Contains(l) && !types.Contains(l) && !CivilApiContract.Unchecked.ContainsKey(l) && !notMembers.Contains(l)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unexplained.Length == 0, "Add these names to CivilApiContract.Members (or explain them in Unchecked): " + string.Join(", ", unexplained));
        Assert.DoesNotContain(CivilApiContract.Unchecked.Keys, members.Contains);
    }

    [Fact]
    public void The_contract_catches_a_wrong_overload()
    {
        // The defect fixed earlier: alignments take Point3d for fixed lines, profiles take Point2d for tangents.
        var members = Signatures()["2025"];
        Assert.DoesNotContain("Autodesk.Civil.DatabaseServices.AlignmentEntityCollection|M AlignmentLine AddFixedLine(Point2d,Point2d)", members);
        Assert.Contains("Autodesk.Civil.DatabaseServices.ProfileEntityCollection|M ProfileTangent AddFixedTangent(Point2d,Point2d)", members);
    }

    [Fact]
    public void Alignment_plan_subtracts_tangents_and_adds_arcs()
    {
        // An L-shaped alignment 100 + 100 with a radius-20 curve at the corner (90° deflection).
        var plan = AlignmentGeometry.Compute([(0, 0), (100, 0), (100, 100)], [20]);
        var curve = Assert.Single(plan.Curves);
        Assert.Equal(90, curve.DeflectionDeg, 9);
        Assert.Equal(20, curve.TangentLength, 9);
        Assert.Equal(10 * Math.PI, curve.ArcLength, 9);
        Assert.Equal(200 - 40 + 10 * Math.PI, plan.ExpectedLength, 9);
        Assert.Empty(AlignmentGeometry.Compute([(0, 0), (100, 0), (100, 100)], [0]).Curves);
        Assert.Equal(200, AlignmentGeometry.Compute([(0, 0), (100, 0), (100, 100)], null).ExpectedLength, 9);
    }

    [Theory]
    [InlineData(150.0, "CURVE_DOES_NOT_FIT")]
    [InlineData(-1.0, "INVALID_RADII")]
    public void Curves_that_cannot_be_built_are_rejected(double radius, string code) =>
        Assert.Equal(code, Assert.Throws<CadFault>(() => AlignmentGeometry.Compute([(0, 0), (100, 0), (100, 100)], [radius])).Code);

    [Fact]
    public void Degenerate_alignments_are_rejected()
    {
        Assert.Equal("INVALID_CURVE", Assert.Throws<CadFault>(() => AlignmentGeometry.Compute([(0, 0), (50, 0), (100, 0)], [10])).Code);
        Assert.Equal("INVALID_CURVE", Assert.Throws<CadFault>(() => AlignmentGeometry.Compute([(0, 0), (50, 0), (0, 0.0000001)], [10])).Code);
        // Collinear points on a diagonal: acos of the rounded cosine used to report a tiny deflection.
        Assert.Equal("INVALID_CURVE", Assert.Throws<CadFault>(() => AlignmentGeometry.Compute([(0, 0), (1, 1), (2, 2)], [10])).Code);
        Assert.Equal("INVALID_POINTS", Assert.Throws<CadFault>(() => AlignmentGeometry.Compute([(0, 0), (0, 0)], null)).Code);
        Assert.Equal("INVALID_RADII", Assert.Throws<CadFault>(() => AlignmentGeometry.Compute([(0, 0), (10, 0), (10, 10), (20, 10)], [1])).Code);
        // Two curves sharing one tangent must fit on it together.
        Assert.Equal("CURVE_DOES_NOT_FIT", Assert.Throws<CadFault>(() => AlignmentGeometry.Compute([(0, 0), (100, 0), (100, 30), (200, 30)], [20, 20])).Code);
    }

    [Fact]
    public void Civil_operations_accept_names_instead_of_handles()
    {
        var plan = EditPlan.Parse("""
            [{"op":"civil_alignment_create","name":"Ось 1","layer":"C-ROAD","style":"Basic","label_set":"Major","points":[[0,0],[100,0],[100,100]],"radii":[20]},
             {"op":"civil_profile_create","name":"П1","alignment_handle":"2A","layer":"C-PROF","style":"Design","label_set":"Complete","points":[[0,10],[100,12]]},
             {"op":"civil_profile_from_surface","name":"Земля","alignment_handle":"2A","surface_handle":"3B","layer_handle":"1F","style":"Existing","label_set_handle":"20"},
             {"op":"civil_network_create","name":"К1","parts_list":"Standard"},
             {"op":"civil_network_add_pipe","handle":"4C","family":"Concrete Pipe","size":"300 mm Concrete Pipe","start":[0,0,10],"end":[50,0,9.5]},
             {"op":"civil_network_add_structure","handle":"4C","family_handle":"5D","size":"Ø1000","position":[0,0,12]},
             {"op":"map_coordinate_system","code":"EPSG:2154","force":false}]
            """);
        Assert.Equal(7, plan.Length);
        // An empty site makes a siteless alignment.
        EditPlan.Parse("""[{"op":"civil_alignment_create","name":"A","layer":"L","style":"S","label_set":"M","site":"","points":[[0,0],[100,0]]}]""");
        var many = string.Join(",", Enumerable.Range(0, 501).Select(i => "[" + i + "," + (i % 2) + "]"));
        Assert.Equal("INVALID_POINTS", Assert.Throws<CadFault>(() => EditPlan.Parse("[{\"op\":\"civil_alignment_create\",\"name\":\"A\",\"layer\":\"L\",\"style\":\"S\",\"label_set\":\"M\",\"points\":[" + many + "]}]")).Code);
    }

    [Theory]
    [InlineData("""[{"op":"civil_profile_create","name":"П","alignment_handle":"2A","layer":"L","layer_handle":"1F","style":"S","label_set":"C","points":[[0,0],[1,1]]}]""", "MISSING_FIELD")]
    [InlineData("""[{"op":"civil_profile_create","name":"П","alignment_handle":"2A","style":"S","label_set":"C","points":[[0,0],[1,1]]}]""", "MISSING_FIELD")]
    [InlineData("""[{"op":"civil_network_add_pipe","handle":"4C","family":"F","start":[0,0,0],"end":[1,0,0]}]""", "MISSING_FIELD")]
    [InlineData("""[{"op":"civil_network_create","name":"К1","parts_list":""}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"civil_alignment_create","name":"A","layer":"L","style":"S","label_set":"M","points":[[0,0],[100,0],[100,100]],"radii":[150]}]""", "CURVE_DOES_NOT_FIT")]
    [InlineData("""[{"op":"civil_alignment_create","name":"A","layer":"L","style":"S","label_set":"M","points":[[0,0],[100,0],[100,100]],"radii":[1,2]}]""", "INVALID_RADII")]
    [InlineData("""[{"op":"civil_alignment_create","name":"A","layer":"L","style":"S","label_set":"M","points":[[0,0],[100,0],[100,100]],"radii":["20"]}]""", "INVALID_RADII")]
    [InlineData("""[{"op":"map_coordinate_system","code":"LL84","force":"yes"}]""", "INVALID_BOOLEAN")]
    [InlineData("""[{"op":"civil_alignment_create","name":"A","layer":"L","style":"S","label_set":"M","site":5,"points":[[0,0],[100,0]]}]""", "INVALID_PARAMETER")]
    public void Invalid_Civil_operations_fail_before_any_transaction(string plan, string code) =>
        Assert.Equal(code, Assert.Throws<CadFault>(() => EditPlan.Parse(plan)).Code);
}
