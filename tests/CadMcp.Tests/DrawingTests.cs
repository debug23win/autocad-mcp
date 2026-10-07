using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void Snapshot_tracks_revision_and_document()
    {
        var store = new SnapshotStore(); var s = store.Add("d1", 4, [Wire.Element(new { handle = "A", layer = "Сеть", text = "Колодец" })], true);
        Assert.Throws<CadFault>(() => store.Query(s.Id, "d2", 4, Wire.Element(new { })));
        var historical = Wire.Element(store.Query(s.Id, "d1", 5, Wire.Element(new { })));
        Assert.True(historical.GetProperty("historical").GetBoolean() && historical.GetProperty("captured_revision").GetInt64() == 4, "Changed DWG did not mark the snapshot as historical");
        Assert.Throws<CadFault>(() => store.Query(s.Id, "d1", 4, Wire.Element(new { limit = -1 })));
        var result = Wire.Element(store.Query(s.Id, "d1", 4, Wire.Element(new { text = "колод", limit = 1 })));
        Assert.Equal(1, result.GetProperty("entities").GetArrayLength());
        Assert.False(result.GetProperty("historical").GetBoolean());
        Assert.True(result.GetProperty("pagination").GetProperty("snapshot_truncated").GetBoolean(), "Hidden truncation");
    }

    [Fact]
    public void Pagination_is_stable_and_retention_bounded()
    {
        var store = new SnapshotStore(); var items = Enumerable.Range(0, 5).Select(i => Wire.Element(new { handle = i.ToString(), layer = "A" }));
        var s = store.Add("d", 1, items, false);
        var page = Wire.Element(store.Query(s.Id, "d", 1, Wire.Element(new { offset = 2, limit = 2 })));
        Assert.Equal("2", page.GetProperty("entities")[0].GetProperty("handle").GetString());
        Assert.Equal(4, page.GetProperty("pagination").GetProperty("next_offset").GetInt32());
        for (int i = 0; i < 4; i++) store.Add("d", 1, [], false);
        Assert.Throws<CadFault>(() => store.Query(s.Id, "d", 1, Wire.Element(new { })));
    }

    [Fact]
    public void Read_caches_separate_revisions_documents_spaces_and_limits()
    {
        var cache = new RevisionCache<string>(2); cache.Put("d1", "catalog", 3, "old");
        Assert.True(cache.TryGet("d1", "catalog", 3, out var value) && value == "old");
        Assert.False(cache.TryGet("d1", "catalog", 4, out _) || cache.TryGet("d2", "catalog", 3, out _), "Stale cache returned");
        cache.Put("d1", "catalog", 4, "new"); Assert.False(cache.TryGet("d1", "catalog", 3, out _), "Old revision survived replacement");
        var store = new SnapshotStore(); var s = store.Add("d", 1, [], false, "model", 100);
        Assert.Equal(s.Id, store.Reuse("d", "model", 1, 100)?.Id);
        Assert.True(store.Reuse("d", "paper", 1, 100) is null && store.Reuse("d", "model", 2, 100) is null && store.Reuse("d", "model", 1, 200) is null, "Snapshot mixed contexts");
    }
}

public sealed class VerificationTests
{
    [Fact]
    public void Acceptance_detects_wrong_geometry_units_and_missing_measurements()
    {
        var entity = Wire.Element(new { handle = "A", type = "Line", layer = "Сеть", length = 99d, start = new[] { 0, 0, 0 }, end = new[] { 99, 0, 0 }, bounds = new { min = new[] { 0, 0, 0 }, max = new[] { 99, 0, 0 } } });
        var plan = DrawingVerification.Parse("""{"units":"Millimeters","entity_count":1,"checks":[{"target":"beam","property":"length","expected":100,"tolerance":0.1},{"handle":"A","property":"layer","expected":"Сеть"},{"handle":"A","property":"radius","expected":5}]}""");
        var result = DrawingVerification.Evaluate([entity], "Millimeters", plan, new Dictionary<string, string> { { "beam", "A" } });
        Assert.True(result.Failed == 1 && result.Passed == 3 && result.Unverified == 1, "Actual drift/missing measurement was accepted");
        var distance = DrawingVerification.Parse("""{"checks":[{"property":"distance","first":{"handle":"A","point":"start"},"second":{"handle":"A","point":"end"},"expected":99,"tolerance":0.0001}]}""");
        Assert.Equal("passed", DrawingVerification.Evaluate([entity], "Millimeters", distance).State);
        Assert.Equal(2, DrawingVerification.Evaluate([entity], "Inches", plan, new Dictionary<string, string> { { "beam", "A" } }).Failed);
        Assert.Throws<CadFault>(() => DrawingVerification.Parse("""{"checks":[{"handle":"A","property":"length","expected":99,"tolerance":-1}]}"""));
    }

    [Fact]
    public void Whole_task_bounds_type_counts_mass_range_and_visual_review_use_actual_evidence()
    {
        var entities = new[] { Wire.Element(new { handle = "A", type = "BlockReference", assembly = new { solid_mass_kg = 12d, profile_code = "20Б1" }, bounds = new { min = new[] { 0, 0, 0 }, max = new[] { 100, 20, 30 } } }), Wire.Element(new { handle = "B", type = "Line", length = 200d, bounds = new { min = new[] { 100, 0, 0 }, max = new[] { 300, 0, 0 } } }) };
        var plan = DrawingVerification.Parse("""{"task":"Frame","units":"Millimeters","bounds_size":[300,20,30],"type_counts":{"BlockReference":1,"Line":1},"checks":[{"handle":"A","property":"assembly.solid_mass_kg","minimum":11.9,"maximum":12.1},{"handle":"A","property":"assembly.profile_code","expected":"20Б1"}],"review_views":["front","isometric"],"visual_requirements":["silhouette"]}""");
        var report = DrawingVerification.Evaluate(entities, "Millimeters", plan);
        Assert.True(report.State == "passed" && report.OverallState == "visual_review_required" && report.Passed == 6, "Measured contract lost a constraint or passed unreviewed visual requirements");
        Assert.True(DrawingVerification.Evaluate(entities.Take(1), "Millimeters", plan).Failed >= 2, "Subset verification accepted missing task geometry");
    }

    [Theory]
    [InlineData("""{"type_counts":{"Line":"1"}}""")]
    [InlineData("""{"checks":[{"handle":"A","property":"length","minimum":2,"maximum":1}]}""")]
    [InlineData("""{"checks":[{"handle":"A","property":"length","expected":1,"minimum":0}]}""")]
    [InlineData("""{"review_views":["made-up"]}""")]
    [InlineData("""{"bounds_size":[1,-2,3]}""")]
    public void Invalid_task_contracts_fail_before_native_calls(string json) => Assert.Throws<CadFault>(() => DrawingVerification.Parse(json));

    [Fact]
    public void Steel_catalog_converts_millimetres_independently_of_material_grade()
    {
        Assert.True(SteelSections.Get("20б1").RootRadiusMm == 11 && Math.Abs(SteelSections.Get("PIPE-108x4").AreaMm2 - Math.PI * (54 * 54 - 50 * 50)) < 1e-8, "Nominal section data is incorrect");
        Assert.True(SteelSections.MetersPerUnit("Meters") == 1 && SteelSections.MetersPerUnit("Millimeters") == .001, "Catalog unit conversion is incorrect");
        Assert.Throws<CadFault>(() => SteelSections.Get("guessed-profile"));
        Assert.Throws<CadFault>(() => SteelSections.MetersPerUnit("Undefined"));
    }

    [Fact]
    public void Rounded_rebar_rejects_overlapping_bends_and_keeps_spatial_tangent_length()
    {
        var path = BendPath.Create([new[] { 0d, 0, 0 }, new[] { 100d, 0, 0 }, new[] { 100d, 0, 100 }], 10);
        Assert.True(path.Bends.Count == 1 && Math.Abs(path.Length - (180 + 5 * Math.PI)) < 1e-8, "Spatial bend centerline length is incorrect");
        Assert.Throws<CadFault>(() => BendPath.Create([new[] { 0d, 0, 0 }, new[] { 10d, 0, 0 }, new[] { 10d, 10, 0 }, new[] { 20d, 10, 0 }], 8));
    }

    [Fact]
    public void Result_handles_include_the_whole_response_and_exclude_failed_edits()
    {
        var operations = Wire.Element(new[] { new { state = "completed", operation = "cad_edit", handles = new[] { "A", "B" } }, new { state = "completed", operation = "cad_edit", handles = new[] { "a", "C" } }, new { state = "failed", operation = "cad_edit", handles = new[] { "D" } } });
        Assert.Equal(new[] { "A", "B", "C" }, CadResultSummary.ChangedHandles(operations));
    }

    [Fact]
    public void Result_summary_distinguishes_checked_geometry_and_an_unsaved_drawing()
    {
        var list = Wire.Element(new { operations = new[] { new { state = "completed", operation = "cad_edit" } } });
        var report = Wire.Element(new { verification = new { state = "passed", entity_count = 4, erased_count = 0, passed = 2, failed = 0, unverified = 0 }, document_state = new { disk_save = "unsaved" } });
        var text = CadResultSummary.Describe(list, report);
        Assert.True(text.Contains("объектов: 4") && text.Contains("несохранённые изменения") && !text.Contains("Выполняю cad_"), "User result is misleading or technical");
    }

    [Fact]
    public void Photo_calibration_recovers_perspective_scale_and_inverse_and_rejects_degenerate_anchors()
    {
        double[] World(double u, double v) { double d = 1 + .0002 * u + .0001 * v; return [(100 + 2 * u + .2 * v) / d, (200 - .1 * u + 1.5 * v) / d, 7]; }
        var pixels = new[] { new[] { 0d, 0d }, new[] { 1000d, 0d }, new[] { 1000d, 800d }, new[] { 0d, 800d }, new[] { 200d, 300d } };
        var fit = PhotoReference.Fit(Wire.Element(pixels.Select(p => new { pixel = p, world = World(p[0], p[1]) })), 1000, 800, "projective");
        var expected = World(350, 275); var actual = fit.PixelToWorld(350, 275);
        Assert.True(fit.RmsError < 1e-7 && Math.Abs(expected[0] - actual[0]) < 1e-7 && Math.Abs(expected[1] - actual[1]) < 1e-7, "Perspective calibration lost precision");
        var inverse = fit.WorldToPixel(actual[0], actual[1]); Assert.True(Math.Abs(inverse[0] - 350) < 1e-7 && Math.Abs(inverse[1] - 275) < 1e-7, "Photo inverse failed");
        var similarity = PhotoReference.Fit(Wire.Element(new[] { new { pixel = new[] { 0, 0 }, world = new[] { 100, 200, 0 } }, new { pixel = new[] { 100, 0 }, world = new[] { 300, 200, 0 } } }), 1000, 800, "similarity");
        var point = similarity.PixelToWorld(20, 30); Assert.True(Math.Abs(point[0] - 140) < 1e-7 && Math.Abs(point[1] - 140) < 1e-7, "Top-left photo Y convention is wrong");
        Assert.Throws<CadFault>(() => PhotoReference.Fit(Wire.Element(Enumerable.Range(0, 4).Select(i => new { pixel = new[] { i * 10, 0 }, world = new[] { i * 20, 0, 0 } })), 100, 100, "projective"));
    }

    [Fact]
    public void Silhouette_comparison_rejects_mismatching_shapes_and_reports_its_evidence()
    {
        var a = Wire.Element(new[] { new[] { .1, .1 }, new[] { .8, .1 }, new[] { .8, .8 }, new[] { .1, .8 } });
        var b = Wire.Element(new[] { new[] { .5, .5 }, new[] { .9, .5 }, new[] { .9, .9 }, new[] { .5, .9 } });
        Assert.Equal("passed", Wire.Element(ReferenceComparison.Compare(a, a, .95, .01)).Text("state"));
        var result = Wire.Element(ReferenceComparison.Compare(a, b, .85, .05));
        Assert.Equal("failed", result.Text("state"));
        Assert.Equal("agent_supplied_contours_in_comparable_views", result.Text("evidence"));
    }

    [Fact]
    public void Image_control_points_fit_an_affine_transform_and_its_inverse()
    {
        var points = Wire.Element(new[] {
            new { pixel = new[] { 0, 0 }, world = new[] { 1000, 2000, 0 } },
            new { pixel = new[] { 1000, 0 }, world = new[] { 1100, 2020, 0 } },
            new { pixel = new[] { 0, 500 }, world = new[] { 1025, 1900, 0 } },
            new { pixel = new[] { 500, 250 }, world = new[] { 1062, 1960, 0 } } });
        var fit = ImageRegistration.Fit(points, 1000, 500);
        var world = fit.PixelToWorld(500, 250);
        var pixel = fit.WorldToPixel(world[0], world[1]);
        Assert.True(Math.Abs(world[0] - 1062.5) < 1 && Math.Abs(pixel[0] - 500) < 1e-6 && Math.Abs(pixel[1] - 250) < 1e-6 && fit.RmsError > 0, "Image affine fit/inverse incorrect");
        var fault = Assert.Throws<CadFault>(() => ImageRegistration.Fit(Wire.Element(new[] {
            new { pixel = new[] { 0, 0 }, world = new[] { 0, 0, 0 } },
            new { pixel = new[] { 1, 1 }, world = new[] { 1, 1, 0 } },
            new { pixel = new[] { 2, 2 }, world = new[] { 2, 2, 0 } } }), 1000, 500));
        Assert.Equal("IMAGE_CONTROL_POINTS_COLLINEAR", fault.Code);
    }
}

public sealed class EditPlanTests
{
    [Fact]
    public void Plan_supports_aliases_unicode_and_explicit_units()
    {
        var plan = EditPlan.Parse("""
            [{"op":"layer","name":"Сеть","color_index":3},
             {"op":"polyline","id":"pipe","points":[[0,0,10],[100,0,10]],"bulges":[0,0],"layer":"Сеть"},
             {"op":"move","target":"pipe","displacement":[0,50]},
             {"op":"text","position":[0,50],"text":"Колодец ✓","height":2.5}]
            """);
        Assert.Equal(4, plan.Length);
        Assert.Equal(0, EditPlan.Point(plan[2].GetProperty("displacement"))[2]);
    }

    [Fact]
    public void Native_geometry_blocks_and_layouts_validate_before_touching_the_drawing()
    {
        var plan = EditPlan.Parse("""
            [{"op":"point","position":[1,2]},
             {"op":"ellipse","center":[0,0],"major_axis":[10,0],"radius_ratio":0.5},
             {"op":"block_define","name":"STAIRS","base_point":[0,0],"handles":["A","B"]},
             {"op":"layout_create","name":"План А3"}]
            """);
        Assert.Equal(4, plan.Length);
        foreach (var invalid in new[] { "[{\"op\":\"ellipse\",\"center\":[0,0],\"major_axis\":[10,0],\"radius_ratio\":2}]",
            "[{\"op\":\"block_define\",\"name\":\"X\",\"base_point\":[0,0],\"handles\":[\"NOT_HEX\"]}]" })
            Assert.Throws<CadFault>(() => EditPlan.Parse(invalid));
    }

    [Fact]
    public void Spatial_paths_and_meshes_validate_face_topology()
    {
        var plan = EditPlan.Parse("""
            [{"op":"polyline3d","points":[[0,0,0],[10,0,5],[10,10,10]]},
             {"op":"mesh","vertices":[[0,0,0],[10,0,0],[0,10,0],[0,0,10]],
              "faces":[[0,2,1],[0,1,3],[1,2,3],[2,0,3]]}]
            """);
        Assert.Equal(2, plan.Length);
        foreach (var invalid in new[] {
            "[{\"op\":\"polyline3d\",\"points\":[[0,0],[1,1,1]]}]",
            "[{\"op\":\"mesh\",\"vertices\":[[0,0,0],[1,0,0],[0,1,0]],\"faces\":[[0,1,3]]}]",
            "[{\"op\":\"mesh\",\"vertices\":[[0,0,0],[1,0,0],[0,1,0]],\"faces\":[[0,1,1]]}]",
            "[{\"op\":\"mesh\",\"vertices\":[[0,0,0],[1,0,0],[2,0,0]],\"faces\":[[0,1,2]]}]"
        }) Assert.Throws<CadFault>(() => EditPlan.Parse(invalid));
    }

    [Fact]
    public void Solid_modeling_plans_validate_references_and_sizes()
    {
        var plan = EditPlan.Parse("""
            [{"op":"circle","id":"profile","center":[0,0,0],"radius":5},
             {"op":"extrude","id":"solid","target":"profile","direction":[0,0,10]},
             {"op":"sphere","id":"tool","center":[0,0,5],"radius":2},
             {"op":"solid_boolean","target":"solid","tool_target":"tool","operation":"subtract"}]
            """);
        Assert.Equal(4, plan.Length);
        foreach (var invalid in new[] {
            "[{\"op\":\"extrude\",\"target\":\"future\",\"direction\":[0,0,10]}]",
            "[{\"op\":\"solid_boolean\",\"handle\":\"A\",\"operation\":\"union\"}]",
            "[{\"op\":\"torus\",\"center\":[0,0,0],\"major_radius\":-1,\"minor_radius\":1}]",
            "[{\"op\":\"solid_boolean\",\"handle\":\"A\",\"tool_handle\":\"B\",\"operation\":\"explode\"}]"
        }) Assert.Throws<CadFault>(() => EditPlan.Parse(invalid));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"op\":\"unlisted\"}]")]
    [InlineData("[{\"op\":\"circle\",\"center\":[0,0],\"radius\":-1}]")]
    [InlineData("[{\"op\":\"line\",\"start\":[0],\"end\":[1,2]}]")]
    [InlineData("[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,2],\"unexpected\":true}]")]
    [InlineData("[{\"op\":\"polyline\",\"points\":[[0,0,0],[1,1,2]]}]")]
    [InlineData("[{\"op\":\"move\",\"target\":\"future\",\"displacement\":[0,1]}]")]
    [InlineData("[{\"op\":\"move\",\"handle\":\"ZZ\",\"displacement\":[0,1]}]")]
    [InlineData("[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1],\"start\":[2,2]}]")]
    [InlineData("[{\"op\":\"block\",\"name\":\"A\",\"position\":[0,0],\"scale\":[1,1]}]")]
    [InlineData("[{\"op\":\"line\",\"id\":\"x\",\"start\":[0,0],\"end\":[1,1]},{\"op\":\"circle\",\"id\":\"x\",\"center\":[0,0],\"radius\":1}]")]
    public void Ambiguous_and_invalid_plans_are_rejected_before_mutation(string json) => Assert.Throws<CadFault>(() => EditPlan.Parse(json));

    [Fact]
    public void Drafting_contract_validates_formulas_bounded_cells_and_verified_form_widths()
    {
        EditPlan.Parse("""[{"op":"table_create","id":"a","position":[0,0],"rows":2,"columns":2,"cells":[{"cell":"A1","value":3}]},{"op":"table_create","position":[100,0],"rows":2,"columns":2,"cells":[{"cell":"A1","formula":"={{n}}*2","references":[{"name":"n","table_target":"a","cell":"A1"}]}]}]""");
        Assert.Throws<CadFault>(() => EditPlan.Parse("""[{"op":"table_create","position":[0,0],"rows":2,"columns":2,"cells":[{"cell":"C1","value":1}]}]"""));
        Assert.Throws<CadFault>(() => EditPlan.Parse("""[{"op":"table_create","position":[0,0],"rows":2,"columns":2,"merges":[[0,0,3,1]]}]"""));
        Assert.Throws<CadFault>(() => EditPlan.Parse("""[{"op":"table_cells","handle":"AB","cells":[{"cell":"A1","formula":"={{missing}}*2"}]}]"""));
        foreach (var form in SpdsTemplates.Tables.Where(f => f.VerifiedForm)) Assert.True(form.Widths.Sum() == 185 && form.HeaderHeight == 15 && form.RowHeight >= 8, "Wrong verified GOST form dimensions");
        Assert.Equal((11, 26), DraftingPlan.Address("$AA$12"));
        Assert.Equal("AA12", DraftingPlan.Address(11, 26));
    }

    [Fact]
    public void Object_cards_parse_tool_results_and_keep_document_identity()
    {
        var cards = CadCards.Parse("{\"session_id\":\"s\",\"document_id\":\"d\",\"data\":{\"entities\":[{\"handle\":\"AB\",\"type\":\"Line\"}]}}");
        Assert.True(cards.Count == 1 && cards[0].Session == "s" && cards[0].Document == "d" && cards[0].Handle == "AB", "Card lost drawing identity");
    }
}
