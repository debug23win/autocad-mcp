using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class TextTranslationTests
{
    [Fact]
    public void Unit_keys_name_texts_cells_and_paragraphs()
    {
        Assert.Equal("1F;-1;-1", Normalize(TextTranslation.ParseUnit("1F")));
        Assert.Equal("2A;3;4", Normalize(TextTranslation.ParseUnit("2A@3,4")));
        Assert.Equal("1F,20,21;-1;-1", Normalize(TextTranslation.ParseUnit("1F+20+21")));
        Assert.Equal("2A@3,4", TextTranslation.CellUnit("2A", 3, 4));
        foreach (var bad in new[] { "", " ", "zz", "1F@x,1", "1F@1", "1F+1f", "1F++20", string.Join("+", Enumerable.Range(1, 201).Select(i => i.ToString("X"))) })
            Assert.Equal("INVALID_UNIT", Assert.Throws<CadFault>(() => TextTranslation.ParseUnit(bad)).Code);
        static string Normalize((string[] Handles, int Row, int Column) unit) => string.Join(",", unit.Handles) + ";" + unit.Row + ";" + unit.Column;
    }

    [Theory]
    [InlineData("Plan\nof floor", "Plan of floor")]
    [InlineData("Slope 50%", "Slope 50%")]
    [InlineData("100%% wide", "100%%%%%% wide")]
    public void Text_takes_one_line_without_accidental_codes(string text, string stored)
    {
        Assert.Equal(stored, TextTranslation.ForText(text));
        Assert.Equal(text.Replace('\n', ' '), CadText.Normalize(TextTranslation.ForText(text)));
    }

    [Theory]
    [InlineData("{\\fArial|b1;\\C1;Hello world}", "Привет мир", "{\\fArial|b1;\\C1;Привет мир}", false)]
    [InlineData("\\A1;Line one\\PLine two", "Строка 1\nСтрока 2", "\\A1;Строка 1\\PСтрока 2", false)]
    [InlineData("{\\C1;red} and {\\C3;green}", "красный и зелёный", "{\\C1;красный и зелёный}", true)]
    [InlineData("Plain", "a{b}\\c", "a\\{b\\}\\\\c", false)]
    [InlineData("", "Новый", "Новый", false)]
    [InlineData("\\PTop", "Верх", "Верх", false)]
    public void MText_keeps_its_opening_format_and_escapes_the_new_text(string contents, string text, string expected, bool simplified)
    {
        Assert.Equal(expected, TextTranslation.ForMText(contents, text, out bool lost));
        Assert.Equal(simplified, lost);
        Assert.Equal(text.Replace("\n", "\n"), CadText.Normalize(expected, true));
    }

    [Fact]
    public void Absolute_heights_scale_and_relative_ones_stay()
    {
        Assert.Equal("{\\H1.25;Text \\H0.8x;small}", TextTranslation.ScaleHeights("{\\H2.5;Text \\H0.8x;small}", 0.5));
        Assert.Equal("No codes", TextTranslation.ScaleHeights("No codes", 0.5));
    }

    [Fact]
    public void Stacked_lines_form_paragraphs()
    {
        TextLine Line(double x, double y, double width, double height = 2.5, double rotation = 0, string layer = "Text") => new(x, y, rotation, height, width, "Standard", layer);
        var lines = new List<TextLine>
        {
            Line(0, 10, 40), Line(0, 6.5, 35), Line(0, 3, 20),          // 0..2: a left-aligned paragraph
            Line(100, 10, 20),                                           // 3: elsewhere on the sheet
            Line(0, 20, 30, height: 5),                                  // 4: a heading in a larger height
            Line(-5, -10, 50), Line(5, -13.5, 30),                       // 5..6: centred lines
            Line(0, 50, 30), Line(0, 46.5, 30), Line(0, 41, 30),         // 7..9: the third spacing differs, so 9 stands alone
            Line(0, 70, 30, layer: "Other"), Line(0, 66.5, 30)           // 10..11: different layers
        };
        // A rotated paragraph far from the origin.
        double r = Math.PI / 2;
        lines.Add(new(1e6, 2e6, r, 2.5, 40, "Standard", "Text"));
        lines.Add(new(1e6 + 3.5, 2e6, r, 2.5, 40, "Standard", "Text"));
        var groups = TextTranslation.Paragraphs(lines).Select(g => string.Join(",", g)).ToHashSet();
        Assert.Contains("0,1,2", groups);
        Assert.Contains("3", groups);
        Assert.Contains("4", groups);
        Assert.Contains("5,6", groups);
        Assert.Contains("7,8", groups);
        Assert.Contains("9", groups);
        Assert.Contains("10", groups);
        Assert.Contains("11", groups);
        Assert.Contains("12,13", groups);
        Assert.Equal(lines.Count, TextTranslation.Paragraphs(lines).Sum(g => g.Length));
    }

    [Fact]
    public void Fitting_shrinks_width_factor_then_height_then_reports_overflow()
    {
        static double Measure(string s) => s.Length;
        var fits = TextTranslation.FitLines("hello world", 1, 11, 1, 0.7, 0.6, Measure);
        Assert.Equal(new[] { "hello world" }, fits.Lines);
        Assert.Equal((1.0, 1.0, false), (fits.WidthFactor, fits.HeightScale, fits.Overflow));
        var narrower = TextTranslation.FitLines("hello world", 1, 10, 1, 0.7, 0.6, Measure);
        Assert.Equal(10.0 / 11, narrower.WidthFactor, 3);
        Assert.Equal(1, narrower.HeightScale);
        var smaller = TextTranslation.FitLines("hello world", 1, 7, 1, 0.7, 0.6, Measure);
        Assert.Equal(0.7, smaller.WidthFactor, 6);
        Assert.Equal(7 / (11 * 0.7), smaller.HeightScale, 3);
        var overflow = TextTranslation.FitLines("hello world", 1, 4, 1, 0.7, 0.6, Measure);
        Assert.True(overflow.Overflow);
        Assert.Equal(new[] { "hello world" }, overflow.Lines);
        // A width factor already below the floor is never raised.
        Assert.Equal(0.5, TextTranslation.FitLines("hello world", 1, 20, 0.5, 0.7, 0.6, Measure).WidthFactor);
    }

    [Fact]
    public void Wrapping_fills_the_lines_of_a_paragraph()
    {
        static double Measure(string s) => s.Length;
        Assert.Equal(new[] { "one two", "three four" }, TextTranslation.FitLines("one two three four", 2, 10, 1, 0.7, 0.6, Measure).Lines);
        Assert.Equal(new[] { "short", "", "" }, TextTranslation.FitLines("short", 3, 10, 1, 0.7, 0.6, Measure).Lines);
        // Ideographs wrap between characters, without spaces.
        Assert.Equal(new[] { "漢字漢", "字漢字" }, TextTranslation.FitLines("漢字漢字漢字", 2, 3, 1, 0.7, 0.6, Measure).Lines);
        // An explicit break needs a line of its own; with one line the text overflows onto it.
        var broken = TextTranslation.FitLines("a\nb", 1, 10, 1, 0.7, 0.6, Measure);
        Assert.True(broken.Overflow);
        Assert.Equal(new[] { "a b" }, broken.Lines);
        Assert.Equal(new[] { "a", "b" }, TextTranslation.FitLines("a\nb", 2, 10, 1, 0.7, 0.6, Measure).Lines);
    }
}
