using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class ModifyPlanTests
{
    private static string Code(string plan) => Assert.Throws<CadFault>(() => EditPlan.Parse(plan)).Code;

    [Fact]
    public void Everyday_operations_parse_with_handles_and_earlier_ids()
    {
        var plan = EditPlan.Parse("""
            [{"op":"line","id":"a","start":[0,0],"end":[10,0]},
             {"op":"line","id":"b","start":[10,0],"end":[10,10]},
             {"op":"offset","id":"a2","target":"a","distance":5,"side_point":[0,10]},
             {"op":"fillet","id":"arc","target":"a","other_target":"b","radius":2},
             {"op":"chamfer","handle":"1F","other_handle":"20","distance":1,"other_distance":2},
             {"op":"join","target":"a","others":["b","2A"]},
             {"op":"array_rect","items":["a","2B"],"rows":2,"columns":3,"row_spacing":10,"column_spacing":-5,"angle_deg":30},
             {"op":"array_polar","items":["b"],"center":[0,0],"count":6},
             {"op":"trim","target":"a2","boundaries":["b"],"pick_point":[1,1]},
             {"op":"extend","handle":"3C","boundaries":["b"],"end":"start"},
             {"op":"explode","handle":"4D","keep_original":true,"attributes_as_text":false},
             {"op":"dimension_angular","id":"ang","center":[0,0],"first":[10,0],"second":[0,10],"position":[5,5]},
             {"op":"mleader","points":[[0,0],[5,5],[8,5]],"text":"Поз. 1","height":2.5},
             {"op":"field_text","position":[0,-5],"object_target":"a","property":"length","precision":1,"prefix":"L=","suffix":" мм"},
             {"op":"text_replace","find":"Ø108","replace":"Ø114","scope":"all","layers":["ВК*"],"include":["text","mtext","tables"],"whole_word":true},
             {"op":"layer_merge","mapping":{"Старый":"Сеть","Tmp":"0"},"create_missing":true,"purge":false},
             {"op":"xdata_set","target":"a","app":"CADMCP_TEST","values":[{"type":"string","value":"труба"},{"type":"int16","value":3},{"type":"point","value":[1,2,3]}]},
             {"op":"xrecord_set","key":"settings","values":[{"type":"real","value":1.5}]},
             {"op":"xrecord_set","handle":"5E","dictionary":"Мой","key":"k","delete":true}]
            """);
        Assert.Equal(19, plan.Length);
        foreach (var kind in ModifyPlan.Fields.Keys.Except(["xref_attach", "block_import"]))
            Assert.Contains(plan, op => op.Text("op") == kind);
    }

    [Theory]
    [InlineData("""[{"op":"offset","handle":"1F"}]""", "MISSING_FIELD")]
    [InlineData("""[{"op":"offset","handle":"1F","distance":0}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"offset","handle":"1F","target":"x","distance":1}]""", "UNKNOWN_TARGET")]
    [InlineData("""[{"op":"offset","distance":1}]""", "INVALID_TARGET")]
    [InlineData("""[{"op":"explode","handle":"1F","id":"parts"}]""", "INVALID_ALIAS")]
    [InlineData("""[{"op":"join","handle":"1F","others":["nope"]}]""", "UNKNOWN_TARGET")]
    [InlineData("""[{"op":"join","handle":"1F","others":["2A","2a"]}]""", "INVALID_REFERENCES")]
    [InlineData("""[{"op":"array_rect","items":["1F"],"rows":1,"columns":1}]""", "INVALID_ARRAY")]
    [InlineData("""[{"op":"array_rect","items":["1F"],"rows":2,"columns":1}]""", "MISSING_FIELD")]
    [InlineData("""[{"op":"array_rect","items":["1F","20","21"],"rows":30,"columns":30,"row_spacing":1,"column_spacing":1}]""", "ARRAY_TOO_LARGE")]
    [InlineData("""[{"op":"array_polar","items":["1F"],"center":[0,0],"count":4,"angle_deg":0}]""", "INVALID_ARRAY")]
    [InlineData("""[{"op":"array_polar","items":["1F"],"center":[0,0],"count":1}]""", "INVALID_DRAFTING_PLAN")]
    [InlineData("""[{"op":"fillet","handle":"1F","radius":1}]""", "INVALID_TARGET")]
    [InlineData("""[{"op":"fillet","handle":"1F","other_handle":"20","radius":-1}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"chamfer","handle":"1F","other_handle":"20","distance":0}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"trim","handle":"1F","boundaries":["20"]}]""", "MISSING_FIELD")]
    [InlineData("""[{"op":"extend","handle":"1F","boundaries":["20"],"end":"middle"}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"mleader","points":[[0,0]],"text":"x"}]""", "INVALID_POINTS")]
    [InlineData("""[{"op":"field_text","position":[0,0],"object_handle":"1F","property":"Volume"}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"field_text","position":[0,0],"property":"Area"}]""", "INVALID_TARGET")]
    [InlineData("""[{"op":"text_replace","find":"","replace":"x"}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"text_replace","find":"a","replace":"b","scope":"layout"}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"text_replace","find":"a","replace":"b","handles":["1F"],"scope":"model"}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"text_replace","find":"a","replace":"b","include":["text","viewports"]}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"xref_attach","path":"relative/plan.dwg","position":[0,0]}]""", "INVALID_PATH")]
    [InlineData("""[{"op":"block_import","path":"C:/x/blocks.dxf","names":["A"]}]""", "INVALID_PATH")]
    [InlineData("""[{"op":"layer_merge","mapping":{"A":"a"}}]""", "INVALID_MAPPING")]
    [InlineData("""[{"op":"layer_merge","mapping":{}}]""", "INVALID_MAPPING")]
    [InlineData("""[{"op":"xdata_set","handle":"1F","app":"APP","values":[{"type":"string","value":"a\nb"}]}]""", "INVALID_VALUES")]
    [InlineData("""[{"op":"xdata_set","handle":"1F","app":"APP","values":[{"type":"int16","value":40000}]}]""", "INVALID_VALUES")]
    [InlineData("""[{"op":"xdata_set","handle":"1F","app":"APP","values":[{"type":"color","value":1}]}]""", "INVALID_VALUES")]
    [InlineData("""[{"op":"xdata_set","handle":"1F","app":"A/B","values":[]}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"xrecord_set","key":"k"}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"xrecord_set","key":"k","delete":true,"values":[{"type":"real","value":1}]}]""", "INVALID_PARAMETER")]
    [InlineData("""[{"op":"offset","handle":"1F","distance":1,"color_index":300}]""", "INVALID_DRAFTING_PLAN")]
    [InlineData("""[{"op":"offset","handle":"1F","distance":1,"unknown":1}]""", "UNKNOWN_FIELD")]
    public void Invalid_operations_fail_before_any_transaction(string plan, string code) => Assert.Equal(code, Code(plan));

    [Fact]
    public void XData_size_is_bounded()
    {
        var value = new string('x', 255);
        var values = string.Join(",", Enumerable.Repeat("{\"type\":\"string\",\"value\":\"" + value + "\"}", 70));
        Assert.Equal("XDATA_TOO_LARGE", Code("[{\"op\":\"xdata_set\",\"handle\":\"1F\",\"app\":\"APP\",\"values\":[" + values + "]}]"));
    }

    [Fact]
    public void Outside_transaction_operations_are_marked_for_preview()
    {
        Assert.Contains("xref_attach", ModifyPlan.OutsideTransaction);
        Assert.Contains("block_import", ModifyPlan.OutsideTransaction);
        Assert.DoesNotContain("text_replace", ModifyPlan.OutsideTransaction);
        Assert.All(ModifyPlan.Aliasable, kind => Assert.True(ModifyPlan.Supports(kind)));
    }

    [Fact]
    public void Plan_hash_binds_the_exact_plan_expectations_drawing_and_revision()
    {
        string plan = """[{"op":"line","start":[0,0],"end":[1,1]}]""";
        byte[] key = [1, 2, 3, 4], otherKey = [4, 3, 2, 1];
        string hash = EditPlan.Hash(plan, null, "doc", 7, key);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, EditPlan.Hash(plan, "", "doc", 7, key));
        Assert.NotEqual(hash, EditPlan.Hash(plan, """{"entity_count":1}""", "doc", 7, key));
        Assert.NotEqual(hash, EditPlan.Hash(plan.Replace("1,1", "1,2"), null, "doc", 7, key));
        Assert.NotEqual(hash, EditPlan.Hash(plan, null, "other", 7, key));
        Assert.NotEqual(hash, EditPlan.Hash(plan, null, "doc", 8, key));
        // Only the worker that previewed knows its key, so a hash cannot be made up without a preview.
        Assert.NotEqual(hash, EditPlan.Hash(plan, null, "doc", 7, otherKey));
        EditPlan.RequirePreviewed(Wire.Element(new { operations_json = plan }), "doc", 7, key);
        EditPlan.RequirePreviewed(Wire.Element(new { operations_json = plan, preview_hash = hash.ToUpperInvariant() }), "doc", 7, key);
        Assert.Equal("PREVIEW_MISMATCH", Assert.Throws<CadFault>(() =>
            EditPlan.RequirePreviewed(Wire.Element(new { operations_json = plan, expectations_json = """{"entity_count":1}""", preview_hash = hash }), "doc", 7, key)).Code);
        Assert.Equal("PREVIEW_MISMATCH", Assert.Throws<CadFault>(() => EditPlan.RequirePreviewed(Wire.Element(new { operations_json = plan, preview_hash = hash }), "doc", 8, key)).Code);
        Assert.Equal("PREVIEW_MISMATCH", Assert.Throws<CadFault>(() => EditPlan.RequirePreviewed(Wire.Element(new { operations_json = plan, preview_hash = hash }), "doc", null, key)).Code);
    }

    [Theory]
    [InlineData("ACAD", true)]
    [InlineData("ACAD_GROUP", true)]
    [InlineData("AcDbBlockRepETag", true)]
    [InlineData("AcadAnnotative", true)]
    [InlineData("AEC_DISP_PROPS", true)]
    [InlineData("AeccDbSurface", true)]
    [InlineData("AECOM_DATA", false)]
    [InlineData("ACADEMY", false)]
    [InlineData("CADMCP", false)]
    public void Only_names_owned_by_Autodesk_products_are_reserved(string name, bool reserved) => Assert.Equal(reserved, ModifyPlan.Reserved(name));

    [Theory]
    [InlineData("""[{"op":"xref_attach","path":"{dwg}","name":"A","position":[0,0]},{"op":"line","start":[0,0],"end":[1,1]}]""", "SINGLE_OPERATION_REQUIRED")]
    [InlineData("""[{"op":"line","start":[0,0],"end":[1,1]},{"op":"block_import","path":"{dwg}","names":["B"]}]""", "SINGLE_OPERATION_REQUIRED")]
    [InlineData("""[{"op":"xdata_set","handle":"2A","app":"ACAD","values":[]}]""", "RESERVED_NAME")]
    [InlineData("""[{"op":"xrecord_set","dictionary":"ACAD_LAYOUT","key":"Layout1","delete":true}]""", "RESERVED_NAME")]
    [InlineData("""[{"op":"xdata_set","handle":"2A","app":"AcDbAttr","values":[]}]""", "RESERVED_NAME")]
    public void Operations_that_could_damage_the_drawing_are_refused(string plan, string code)
    {
        // An absolute path on the test machine (C:\... on Windows, /tmp/... elsewhere).
        plan = plan.Replace("{dwg}", System.Text.Json.JsonEncodedText.Encode(Path.Combine(Path.GetTempPath(), "a.dwg")).ToString());
        Assert.Equal(code, Assert.Throws<CadFault>(() => EditPlan.Parse(plan)).Code);
    }

    [Theory]
    [InlineData("Труба Ø108", "Ø108", "Ø114", false, "Труба Ø114", 1)]
    [InlineData("Труба %%c108", "Ø108", "Ø114", false, "Труба %%c114", 1)]
    [InlineData("Труба %%C108 и Ø108", "ø108", "Ø114", false, "Труба %%c114 и Ø114", 2)]
    [InlineData("УГОЛ 90%%d", "90°", "45°", false, "УГОЛ 45%%d", 1)]
    [InlineData("Сеть В1, сеть В1.1", "В1", "В2", false, "Сеть В2, сеть В2.1", 2)]
    [InlineData("Сеть В1, сеть В1.1", "Сеть", "Линия", true, "Линия В1, сеть В1.1", 1)]
    [InlineData("путь C:\\dir", "C:\\dir", "D:\\x", false, "путь D:\\x", 1)]
    public void Plain_text_replacement(string raw, string find, string replace, bool matchCase, string expected, int count)
    {
        var (text, replaced) = CadText.Replace(raw, find, replace, matchCase);
        Assert.Equal(expected, text);
        Assert.Equal(count, replaced);
    }

    [Fact]
    public void Whole_words_respect_letters_and_digits()
    {
        Assert.Equal(("К12 и К12", 2), CadText.Replace("К1 и К1", "К1", "К12", wholeWord: true));
        Assert.Equal(1, CadText.Replace("К1 и К12", "К1", "К2", wholeWord: true).Count);
        Assert.Equal(("_К1 К1_ К1", 1), CadText.Replace("_К1 К1_ К1", "К1", "К1", wholeWord: true));
        Assert.Equal("К2 и К12", CadText.Replace("К1 и К12", "К1", "К2", wholeWord: true).Text);
    }

    [Fact]
    public void MText_replacement_keeps_formatting_codes()
    {
        string contents = "{\\fArial|b1|i0|c204|p34;Труба}\\P\\C1;Ø108 x 4\\PL=12\\~м {\\H2.5x;Труба}";
        var (text, count) = CadText.Replace(contents, "Труба", "Трубопровод", mtext: true);
        Assert.Equal(2, count);
        Assert.Equal("{\\fArial|b1|i0|c204|p34;Трубопровод}\\P\\C1;Ø108 x 4\\PL=12\\~м {\\H2.5x;Трубопровод}", text);
        // Codes are never matched: P, C1, H2.5x and the font name stay intact.
        Assert.Equal(0, CadText.Replace(contents, "Arial", "Times", mtext: true).Count);
        Assert.Equal(0, CadText.Replace(contents, "P", "Q", matchCase: true, mtext: true).Count);
        Assert.Equal(("\\S1^2;", false), CadText.MTextSegments("\\S1^2;").Single());
        Assert.Equal(new[] { ("a", true), ("\\U+00D8", false), ("b", true), ("\\\\", false), ("c", true) }, CadText.MTextSegments("a\\U+00D8b\\\\c").ToArray());
    }

    [Fact]
    public void A_match_split_by_formatting_is_left_alone()
    {
        string contents = "{\\C1;Ø}108";
        Assert.Equal(0, CadText.Replace(contents, "Ø108", "Ø114", mtext: true).Count);
        Assert.Equal("Ø108", CadText.Normalize(contents, mtext: true));
    }
}
