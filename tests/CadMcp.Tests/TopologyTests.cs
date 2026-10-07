using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class TopologyTests
{
    private static TopologyCurve Line(string handle, double x1, double y1, double x2, double y2) => new(handle, "0", [[x1, y1], [x2, y2]], false);
    private static TopologyCurve Poly(string handle, bool closed, params double[] xy) =>
        new(handle, "0", Enumerable.Range(0, xy.Length / 2).Select(i => new[] { xy[2 * i], xy[2 * i + 1] }).ToArray(), closed);
    private static TopologyReport Check(double gap, params TopologyCurve[] curves) => Topology.Analyze(curves, new TopologyOptions(0.001, gap));
    private static string[] Codes(TopologyReport report) => report.Findings.Select(f => f.Code).ToArray();

    [Fact]
    public void Connected_lines_report_only_their_free_ends()
    {
        var report = Check(0.1, Line("A", 0, 0, 10, 0), Line("B", 10, 0, 10, 10));
        Assert.Equal("passed", report.State);
        Assert.Equal(new[] { "DANGLING_END", "DANGLING_END" }, Codes(report));
        Assert.Empty(Check(0.1, Poly("R", true, 0, 0, 10, 0, 10, 5, 0, 5)).Findings);
    }

    [Fact]
    public void Near_misses_are_warnings_with_their_gap()
    {
        var report = Check(0.1, Line("A", 0, 0, 10, 0), Line("B", 10.05, 0, 10.05, 10));
        var miss = Assert.Single(report.Findings, f => f.Code == "NEAR_MISS");
        Assert.Equal(Topology.Warning, miss.Severity);
        Assert.Equal(0.05, miss.Distance!.Value, 6);
        Assert.Contains("A", miss.Handles); Assert.Contains("B", miss.Handles);
        Assert.Equal("review_required", report.State);
        // Without a gap tolerance the same ends are only dangling.
        Assert.DoesNotContain("NEAR_MISS", Codes(Check(0, Line("A", 0, 0, 10, 0), Line("B", 10.05, 0, 10.05, 10))));
    }

    [Fact]
    public void T_junctions_crossings_and_overlaps_are_classified()
    {
        Assert.Contains("T_JUNCTION", Codes(Check(0.1, Line("A", 0, 0, 10, 0), Line("B", 5, 0, 5, 5))));
        var cross = Check(0.1, Line("A", 0, 0, 10, 10), Line("B", 0, 10, 10, 0));
        var crossing = Assert.Single(cross.Findings, f => f.Code == "UNNODED_CROSSING");
        Assert.Equal(5, crossing.Point[0], 6); Assert.Equal(5, crossing.Point[1], 6);
        Assert.Equal(Topology.Info, crossing.Severity);
        var overlap = Check(0.1, Line("A", 0, 0, 10, 0), Line("B", 5, 0, 15, 0));
        Assert.Contains(overlap.Findings, f => f.Code == "OVERLAPPING_SEGMENTS" && f.Severity == Topology.Warning);
        Assert.DoesNotContain(Topology.Analyze([Line("A", 0, 0, 10, 10), Line("B", 0, 10, 10, 0)], new TopologyOptions(0.001, 0, Crossings: false)).Findings, f => f.Code == "UNNODED_CROSSING");
    }

    [Fact]
    public void Duplicates_are_reported_once_instead_of_as_overlaps()
    {
        var report = Check(0.1, Poly("A", false, 0, 0, 10, 0, 10, 10), Poly("B", false, 10, 10, 10, 0, 0, 0));
        Assert.Single(report.Findings, f => f.Code == "DUPLICATE_GEOMETRY");
        Assert.DoesNotContain("OVERLAPPING_SEGMENTS", Codes(report));
        var rotated = Check(0.1, Poly("R1", true, 0, 0, 4, 0, 4, 4, 0, 4), Poly("R2", true, 4, 4, 0, 4, 0, 0, 4, 0));
        Assert.Single(rotated.Findings, f => f.Code == "DUPLICATE_GEOMETRY");
    }

    [Fact]
    public void Self_intersections_and_zero_length_curves_are_warnings()
    {
        var bowtie = Check(0, Poly("Z", true, 0, 0, 10, 10, 10, 0, 0, 10));
        Assert.Contains(bowtie.Findings, f => f.Code == "SELF_INTERSECTION" && f.Handles.SequenceEqual(new[] { "Z" }));
        var zero = Check(0, Line("P", 3, 3, 3, 3));
        Assert.Equal("ZERO_LENGTH", Assert.Single(zero.Findings).Code);
        Assert.Equal("review_required", zero.State);
        Assert.Equal("INVALID_TOLERANCE", Assert.Throws<CadFault>(() => Topology.Analyze([], new TopologyOptions(0))).Code);
    }

    [Fact]
    public void Large_drawings_stay_fast_and_findings_are_capped()
    {
        var random = new Random(7);
        var curves = Enumerable.Range(0, 20000).Select(i =>
        {
            double x = random.NextDouble() * 1000, y = random.NextDouble() * 1000;
            return Line(i.ToString("X"), x, y, x + random.NextDouble() * 5, y + random.NextDouble() * 5);
        }).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = Topology.Analyze(curves, new TopologyOptions(0.001, 0.5, MaxFindings: 200));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "Topology check is too slow: " + watch.Elapsed);
        Assert.True(report.Truncated && report.Findings.Count == 200);
        Assert.True(report.Counts.Values.Sum() > 200);
    }

    [Fact]
    public void An_endpoint_on_a_sampled_arc_touches_it_within_the_chord_deviation()
    {
        // A coarse quarter circle of radius 100: a line ending on the true arc midway between two samples
        // lies off the chord; with the reported deviation it is a T-junction, not a near miss.
        int segments = 4;
        var arc = Enumerable.Range(0, segments + 1).Select(i => { double a = Math.PI / 2 * i / segments; return new[] { 100 * Math.Cos(a), 100 * Math.Sin(a) }; }).ToArray();
        double between = Math.PI / 2 / segments / 2;
        var line = Line("L", 0, 0, 100 * Math.Cos(between), 100 * Math.Sin(between));
        double sagitta = ArcSampling.Sagitta(100, Math.PI / 2, segments);
        Assert.True(sagitta > 0.5, "The test needs a visible chord deviation");
        var sampled = new TopologyCurve("A", "0", arc, false, sagitta);
        var exact = Topology.Analyze([sampled, line], new TopologyOptions(0.001, 5));
        Assert.Contains(exact.Findings, f => f.Code == "T_JUNCTION" && f.Handles.Contains("L"));
        Assert.DoesNotContain(exact.Findings, f => f.Code == "NEAR_MISS");
        var coarse = Topology.Analyze([sampled with { Deviation = 0 }, line], new TopologyOptions(0.001, 5));
        Assert.Contains(coarse.Findings, f => f.Code == "NEAR_MISS");
    }

    [Fact]
    public void Bulge_samples_end_exactly_on_the_next_vertex()
    {
        var points = ArcSampling.Bulge(0.1, 0.2, 7.3, 1.9, 0.37, 0.0001);
        Assert.Equal(7.3, points[^1][0]);
        Assert.Equal(1.9, points[^1][1]);
        Assert.Equal(0, ArcSampling.Sagitta(5, 0, 4), 12);
        Assert.Equal(5 * (1 - Math.Cos(Math.PI / 8)), ArcSampling.Sagitta(5, Math.PI, 4), 12);
    }
}
