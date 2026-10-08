using System.Text;
using CadMcp.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace CadMcp.Tests;

public sealed class QualityTests
{
    [Theory]
    [InlineData("%%c20", "Ø20")]
    [InlineData("90%%d", "90°")]
    [InlineData("%%p0.5", "±0.5")]
    [InlineData("%%uПодчёркнуто%%u и %%oнадчёркнуто", "Подчёркнуто и надчёркнуто")]
    [InlineData("100%%%", "100%")]
    [InlineData("%%176", "°")]
    [InlineData("\\U+00D8 30", "Ø 30")]
    [InlineData("%%C%%D%%P", "Ø°±")]
    [InlineData("50%", "50%")]
    public void Control_codes_become_displayed_characters(string raw, string displayed) => Assert.Equal(displayed, CadText.Normalize(raw));

    [Theory]
    [InlineData("{\\fArial|b0|i0|c204|p34;Труба}\\PL=12", "Труба\nL=12")]
    [InlineData("\\H2.5x;Крупно", "Крупно")]
    [InlineData("\\S1^2; дюйма", "1/2 дюйма")]
    [InlineData("50\\~мм", "50 мм")]
    [InlineData("\\LПодчёркнуто\\l", "Подчёркнуто")]
    [InlineData("\\\\server\\{x\\}", "\\server{x}")]
    [InlineData("{\\C1;%%c}108", "Ø108")]
    public void MText_formatting_is_removed(string contents, string displayed) => Assert.Equal(displayed, CadText.Normalize(contents, mtext: true));

    [Fact]
    public void Search_compares_displayed_text()
    {
        Assert.True(CadText.Contains("Труба %%c108x4", "Ø108"));
        Assert.True(CadText.Contains("{\\fArial;%%C108}", "ø108", mtext: true));
        Assert.False(CadText.Contains("%%c108", "%%c109"));
        Assert.True(CadText.Contains("ПОЗ. 1", "поз. 1"));
    }

    [Fact]
    public void Numbers_accept_comma_decimals_and_grouped_thousands()
    {
        Assert.Equal(new[] { 12.5, 3.0 }, CadText.Numbers("L=12,5 м; 3 шт"));
        Assert.Contains(1200.0, CadText.Numbers("1 200"));
        Assert.Contains(1200.0, CadText.Numbers("1\u00A0200"));
        Assert.DoesNotContain(12000.0, CadText.Numbers("1 2000"));
    }

    [Theory]
    [InlineData(null, 10.0, 2, 0.0, null)]
    [InlineData("", 10.0, 2, 0.0, null)]
    [InlineData("<> мм", 10.0, 2, 0.0, null)]
    [InlineData("12", 12.004, 2, 0.0, "DIMENSION_TEXT_FIXED")]
    [InlineData("%%c12", 12.0, 0, 0.0, "DIMENSION_TEXT_FIXED")]
    [InlineData("R5", 4.996, 2, 0.0, "DIMENSION_TEXT_FIXED")]
    [InlineData("12.5", 12.4, 1, 0.5, "DIMENSION_TEXT_FIXED")]
    [InlineData("1 200", 1200.0, 0, 0.0, "DIMENSION_TEXT_FIXED")]
    [InlineData("15", 12.0, 0, 0.0, "DIMENSION_TEXT_MISMATCH")]
    [InlineData("2x45%%d", 10.0, 0, 0.0, "DIMENSION_TEXT_MISMATCH")]
    [InlineData("12,6", 12.4, 1, 0.0, "DIMENSION_TEXT_MISMATCH")]
    [InlineData("См. план", 12.0, 0, 0.0, "DIMENSION_TEXT_REPLACED")]
    public void Dimension_overrides_are_classified(string? text, double measurement, int precision, double rounding, string? expected) =>
        Assert.Equal(expected, CadText.DimensionOverride(text, measurement, precision, rounding));

    [Theory]
    [InlineData("%%c%%c20", true)]
    [InlineData("Ø Ø20", true)]
    [InlineData("{\\fArial;%%c}%%c20", true)]
    [InlineData("%%c20", false)]
    [InlineData("Ø20 x Ø10", false)]
    public void Repeated_diameter_signs_are_found(string text, bool repeated) => Assert.Equal(repeated, CadText.RepeatedDiameter(text, mtext: true));

    [Theory]
    [InlineData("В1-Сети", "В1*", true)]
    [InlineData("в1-сети", "В1-СЕТИ", true)]
    [InlineData("К2", "В?,К?", true)]
    [InlineData("К12", "К#", false)]
    [InlineData("К1", "К#", true)]
    [InlineData("0", "~0", false)]
    [InlineData("Оси", "~0", true)]
    [InlineData("a.b", "a?b", true)]
    [InlineData("a+b", "a+b", true)]
    [InlineData("ab", "a+b", false)]
    // . @ and [ ] match themselves, so an exact layer name never takes in its neighbours.
    [InlineData("КЖ.Арматура", "КЖ.Арматура", true)]
    [InlineData("КЖ_Арматура", "КЖ.Арматура", false)]
    [InlineData("Ось[1]", "Ось[1]", true)]
    [InlineData("C1", "[AB]*", false)]
    [InlineData("[a-Z]x", "[a-Z]*", true)]
    [InlineData("@5", "@#", true)]
    [InlineData("Ж5", "@#", false)]
    [InlineData("Wall.1", "Wall.#", true)]
    [InlineData("Wall-1", "Wall.#", false)]
    [InlineData("A*B", "A`*B", true)]
    [InlineData("AxB", "A`*B", false)]
    [InlineData("B2", "A* , B*", true)]
    [InlineData("x", "A[", false)]
    public void Layer_patterns_follow_AutoCAD_wildcards(string layer, string pattern, bool matches) => Assert.Equal(matches, CadText.Like(layer, pattern));

    [Fact]
    public void Non_ascii_digits_never_break_the_checks()
    {
        // Full-width digits typed with an Asian input method are text, not numbers to parse.
        Assert.Equal("DIMENSION_TEXT_REPLACED", CadText.DimensionOverride("３００", 300, 0));
        Assert.Empty(CadText.Numbers("３００"));
        Assert.Equal("%%１２３", CadText.Normalize("%%１２３"));
        // Text below the dimension line (\X) is still part of the displayed text.
        Assert.Equal("DIMENSION_TEXT_FIXED", CadText.DimensionOverride("2 HOLES\\X%%c20", 20, 0));
        Assert.Equal("a\ufffdb", CadText.Normalize("a\\M+18140b", mtext: true));
        // A needle that displays as nothing matches nothing.
        Assert.False(CadText.Contains("any text", "%%u"));
    }

    [Theory]
    [InlineData("Труба %%c108", "C", "С", false, "Труба %%c108", 0)]
    [InlineData("Угол 90%%d", "d", "x", false, "Угол 90%%d", 0)]
    [InlineData("Труба %%c108", "Ø108", "Ø114", false, "Труба %%c114", 1)]
    // %%% is the stored form of a single %: it is replaced as a whole, never cut through.
    [InlineData("50%%%", "%", "x", false, "50x", 1)]
    [InlineData("Уклон 50%%%", "50%", "60%", false, "Уклон 60%%%", 1)]
    [InlineData("%%ccc", "cc", "dd", false, "%%cdd", 1)]
    [InlineData("100%% wide", "%%", "pct", false, "100pct wide", 1)]
    // A symbol written as a code next to a literal %, in any mix.
    [InlineData("i=%%p0.5%", "±0.5%", "±1%", false, "i=%%p1%%%", 1)]
    [InlineData("%%c50%", "Ø50%", "Ø60%", false, "%%c60%%%", 1)]
    [InlineData("45%%d 10%", "45° 10%", "30° 10%", false, "30%%d 10%%%", 1)]
    [InlineData("{\\fArial|b0;%%p5%}", "±5%", "±6%", true, "{\\fArial|b0;%%p6%%%}", 1)]
    // A search written with codes works on the stored text and writes the new text as typed.
    [InlineData("%%c20", "%%c", "⌀", false, "⌀20", 1)]
    // A stored %%% is one %, so the letter after it is plain text, not the code of a symbol.
    [InlineData("5%%%d", "5%°", "6%°", false, "5%%%d", 0)]
    [InlineData("%%%c", "%Ø", "X", false, "%%%c", 0)]
    [InlineData("{\\fArial|b0;5%%%d}", "5%°", "6%°", true, "{\\fArial|b0;5%%%d}", 0)]
    [InlineData("5%%%d", "5%d", "6%d", false, "6%%%d", 1)]
    [InlineData("\\U+00D8108", "U", "u", false, "\\U+00D8108", 0)]
    [InlineData("{\\fArial|b0;Path}", "Path", "C:\\Data\\{New}", true, "{\\fArial|b0;C:\\\\Data\\\\\\{New\\}}", 1)]
    public void Replacement_never_cuts_through_control_codes(string raw, string find, string replace, bool mtext, string expected, int count)
    {
        var (text, replaced) = CadText.Replace(raw, find, replace, mtext: mtext);
        Assert.Equal(expected, text);
        Assert.Equal(count, replaced);
    }

    [Fact]
    public void A_search_of_many_percent_signs_stays_fast()
    {
        // Each % of the search has one way to match, so a miss is found without trying every split of %%%.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (text, count) = CadText.Replace(new string('%', 600), new string('%', 20) + "x", "y");
        Assert.Equal(0, count);
        Assert.Equal(new string('%', 600), text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "Replace took " + watch.Elapsed);
    }

    [Fact]
    public void Unresolved_fields_are_found()
    {
        Assert.True(CadText.HasUnresolvedField("Площадь #### м²"));
        Assert.False(CadText.HasUnresolvedField("Поз. #12"));
        Assert.False(CadText.HasUnresolvedField(null));
    }

    private static byte[] Shapes(params int[] codes)
    {
        var data = new List<byte>(Encoding.ASCII.GetBytes("AutoCAD-86 shapes 1.0\r\n")) { 0x1A };
        void U16(int value) { data.Add((byte)(value & 0xFF)); data.Add((byte)(value >> 8)); }
        var all = new[] { 0 }.Concat(codes).ToArray();
        U16(all.Min()); U16(all.Max()); U16(all.Length);
        foreach (int code in all) { U16(code); U16(2); }
        foreach (int _ in all) { data.Add(0); data.Add(0); }
        return data.ToArray();
    }

    private static byte[] Unifont(params int[] codes)
    {
        var data = new List<byte>(Encoding.ASCII.GetBytes("AutoCAD-86 unifont 1.0\r\n")) { 0x1A };
        void U16(int value) { data.Add((byte)(value & 0xFF)); data.Add((byte)(value >> 8)); }
        int count = codes.Length + 1;
        data.AddRange(BitConverter.GetBytes(count));
        byte[] definition = [.. Encoding.ASCII.GetBytes("test"), 0, 8, 2, 0, 0];
        U16(definition.Length); data.AddRange(definition);
        foreach (int code in codes) { U16(code); U16(3); data.AddRange(new byte[] { 1, 2, 0 }); }
        return data.ToArray();
    }

    [Fact]
    public void Regular_shx_fonts_are_checked_through_the_drawing_code_page()
    {
        var latin = ShxGlyphs.Parse(Shapes('A', 'B', '1', '2'));
        Assert.NotNull(latin);
        Assert.False(latin!.Unicode);
        Assert.DoesNotContain(0, latin.Codes);
        Assert.Equal(new[] { 'Б', 'Т' }, latin.Missing("AБ 12Т", 1251));
        // Without a known code page, characters outside ASCII cannot be judged; symbols are drawn by AutoCAD.
        Assert.Empty(latin.Missing("AБ Ø°±", 0));
        var cyrillic = ShxGlyphs.Parse(Shapes('A', 0xC1));
        Assert.Empty(cyrillic!.Missing("AБ", 1251));
    }

    [Fact]
    public void Unicode_shx_fonts_are_checked_by_code_point()
    {
        var font = ShxGlyphs.Parse(Unifont('A', 'Б'));
        Assert.NotNull(font);
        Assert.True(font!.Unicode);
        Assert.Equal(new[] { 'В' }, font.Missing("AБВ", 1251));
    }

    [Fact]
    public void Big_fonts_and_damaged_files_are_not_analyzed()
    {
        Assert.Null(ShxGlyphs.Parse(Encoding.ASCII.GetBytes("AutoCAD-86 bigfont 1.0\r\n\u001A\0\0\0\0")));
        Assert.Null(ShxGlyphs.Parse(Shapes('A')[..30]));
        Assert.Null(ShxGlyphs.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void Arc_sampling_follows_the_polyline_bulge()
    {
        static void OnCircle(double[][] points, double cx, double cy, double r) =>
            Assert.All(points, p => Assert.Equal(r, Math.Sqrt((p[0] - cx) * (p[0] - cx) + (p[1] - cy) * (p[1] - cy)), 9));
        // A positive bulge runs counter-clockwise: from (0,0) to (2,0) the half circle passes below the chord.
        var half = ArcSampling.Bulge(0, 0, 2, 0, 1, 0.001);
        OnCircle(half, 1, 0, 1);
        Assert.Equal(2, half[^1][0], 9); Assert.Equal(0, half[^1][1], 9);
        Assert.Contains(half, p => Math.Abs(p[0] - 1) < 0.05 && p[1] < -0.99);
        Assert.Contains(ArcSampling.Bulge(0, 0, 2, 0, -1, 0.001), p => Math.Abs(p[0] - 1) < 0.05 && p[1] > 0.99);
        // A quarter circle from (1,0) to (0,1) and a three-quarter circle from (1,0) to (0,-1) share the centre (0,0).
        OnCircle(ArcSampling.Bulge(1, 0, 0, 1, Math.Tan(Math.PI / 8), 0.001), 0, 0, 1);
        var major = ArcSampling.Bulge(1, 0, 0, -1, Math.Tan(3 * Math.PI / 8), 0.001);
        OnCircle(major, 0, 0, 1);
        Assert.Contains(major, p => p[0] < -0.99);
        Assert.Equal(new[] { new[] { 5.0, 5.0 } }, ArcSampling.Bulge(0, 0, 5, 5, 0, 0.001));
    }

    [Fact]
    public void Arc_sampling_respects_the_chord_tolerance()
    {
        int n = ArcSampling.Segments(100, Math.PI, 0.01);
        double sagitta = 100 * (1 - Math.Cos(Math.PI / n / 2));
        Assert.True(sagitta <= 0.01, "Chord deviation " + sagitta);
        Assert.Equal(4, ArcSampling.Segments(1, Math.PI / 2, 10));
        Assert.Equal(512, ArcSampling.Segments(1e9, 2 * Math.PI, 1e-9));
        var arc = ArcSampling.Arc(0, 0, 1, Math.PI * 1.5, 0, 0.01);
        Assert.Equal(0, arc[0][0], 9); Assert.Equal(-1, arc[0][1], 9);
        Assert.Equal(1, arc[^1][0], 9); Assert.Equal(0, arc[^1][1], 9);
    }

    [Theory]
    [InlineData(297.0, 210.0, 210.0, 297.0, true)]
    [InlineData(297.4, 210.2, 297.0, 210.0, true)]
    [InlineData(843.0, 594.0, 841.0, 594.0, true)]
    [InlineData(420.0, 297.0, 297.0, 210.0, false)]
    [InlineData(297.0, 297.0, 297.0, 210.0, false)]
    public void Paper_sizes_compare_in_either_orientation(double width, double height, double expectedWidth, double expectedHeight, bool same) =>
        Assert.Equal(same, PdfVerification.SameSize(width, height, expectedWidth, expectedHeight));

    private static string Pdf(Action<PdfDocumentBuilder> build)
    {
        var builder = new PdfDocumentBuilder();
        build(builder);
        var path = Path.Combine(Path.GetTempPath(), "cadmcp-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static string State(object report) => (string)report.GetType().GetProperty("state")!.GetValue(report)!;
    private static string[] Warnings(object report) =>
        ((IEnumerable<object>)report.GetType().GetProperty("warnings")!.GetValue(report)!).Select(w => (string)w.GetType().GetProperty("code")!.GetValue(w)!).ToArray();

    [Fact]
    public void Pdf_pages_are_checked_for_size_and_content()
    {
        var drawn = Pdf(b =>
        {
            var page = b.AddPage(PageSize.A4, false);
            page.AddText("Sheet 1", 12, new PdfPoint(40, 40), b.AddStandard14Font(Standard14Font.Helvetica));
            page.DrawLine(new PdfPoint(10, 10), new PdfPoint(200, 200), 1);
        });
        var empty = Pdf(b => b.AddPage(PageSize.A3, true));
        try
        {
            var ok = PdfVerification.Check(drawn, 1, (210, 297));
            Assert.Equal("passed", State(ok));
            Assert.Empty(Warnings(ok));
            var wrongPaper = PdfVerification.Check(drawn, 1, (420, 297));
            Assert.Equal("review_required", State(wrongPaper));
            Assert.Equal(new[] { "PDF_PAGE_SIZE_MISMATCH" }, Warnings(wrongPaper));
            Assert.Equal(new[] { "PDF_PAGE_EMPTY" }, Warnings(PdfVerification.Check(empty)));
            Assert.Equal("PDF_PAGE_COUNT", Assert.Throws<CadFault>(() => PdfVerification.Check(drawn, 2)).Code);
        }
        finally { File.Delete(drawn); File.Delete(empty); }
    }
}
