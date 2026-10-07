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
    public void Long_polylines_and_distant_strays_stay_fast()
    {
        // 50000 vertices in one zigzag: pairs of neighbouring segments once hashed badly and took minutes.
        var zigzag = new TopologyCurve("Z", "0", Enumerable.Range(0, 50_000).Select(i => new[] { i * 1.0, i % 2 * 0.5 }).ToArray(), false);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = Topology.Analyze([zigzag], new TopologyOptions(0.001));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Zigzag check is too slow: " + watch.Elapsed);
        Assert.DoesNotContain(report.Findings, f => f.Code == "TOPOLOGY_LIMIT");
        // A cluster of short lines plus one line a million units away must not crowd every cell.
        var random = new Random(3);
        var cluster = Enumerable.Range(0, 5000).Select(i =>
        {
            double x = random.NextDouble() * 100, y = random.NextDouble() * 100;
            return Line("C" + i, x, y, x + 1, y + 0.5);
        }).Append(Line("FAR", 1_000_000, 0, 1_000_010, 0)).Append(Line("LONG", -50, 50, 150, 50)).ToArray();
        watch.Restart();
        report = Topology.Analyze(cluster, new TopologyOptions(0.001, 0.01));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Clustered check is too slow: " + watch.Elapsed);
        Assert.DoesNotContain(report.Findings, f => f.Code == "TOPOLOGY_LIMIT");
        // The long line crossing the cluster is still compared with the short lines it crosses.
        Assert.Contains(report.Findings, f => f.Code == "UNNODED_CROSSING" && f.Handles.Contains("LONG"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Topology.Analyze(cluster, new TopologyOptions(0.001), cancelled.Token));
    }

    [Fact]
    public void Tiny_segments_next_to_long_lines_and_arcs_keep_full_checks()
    {
        // A 100 x 60 m plan in millimetres: 20000 strokes of 1-3 mm (exploded text) beside 3000 walls of 1-10 m and a
        // road arc of R = 20 m. Long lines used to be compared with everything and reached the work limit; tiny
        // cells clipped the arc's chord deviation, so a line ending on the arc became a near miss.
        var random = new Random(11);
        var curves = new List<TopologyCurve>();
        for (int i = 0; i < 20000; i++)
        {
            double x = random.NextDouble() * 100000, y = random.NextDouble() * 60000, a = random.NextDouble() * Math.PI, length = 1 + random.NextDouble() * 2;
            curves.Add(Line("S" + i, x, y, x + Math.Cos(a) * length, y + Math.Sin(a) * length));
        }
        for (int i = 0; i < 3000; i++)
        {
            double x = random.NextDouble() * 100000, y = random.NextDouble() * 60000, length = 1000 + random.NextDouble() * 9000;
            curves.Add(random.Next(2) == 0 ? Line("W" + i, x, y, x + length, y) : Line("W" + i, x, y, x, y + length));
        }
        double sampling = Math.Sqrt(100000.0 * 100000 + 60000.0 * 60000) * 1e-5;
        var arc = ArcSampling.Arc(50000, 30000, 20000, 0, Math.PI / 2, sampling);
        curves.Add(new("ARC", "0", arc, false, ArcSampling.Sagitta(20000, Math.PI / 2, arc.Length - 1)));
        double angle = Math.PI / 2 * (10.5 / (arc.Length - 1));
        double ex = 50000 + 20000 * Math.Cos(angle), ey = 30000 + 20000 * Math.Sin(angle);
        curves.Add(Line("ON_ARC", ex + 3000 * Math.Cos(angle), ey + 3000 * Math.Sin(angle), ex, ey));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = Topology.Analyze(curves, new TopologyOptions(1e-4, 1, MaxFindings: 100000));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "Mixed check is too slow: " + watch.Elapsed);
        Assert.DoesNotContain(report.Findings, f => f.Code == "TOPOLOGY_LIMIT");
        Assert.Contains(report.Findings, f => f.Code == "T_JUNCTION" && f.Handles[0] == "ON_ARC");
        Assert.DoesNotContain(report.Findings, f => f.Code == "NEAR_MISS" && f.Handles[0] == "ON_ARC");
        Assert.Contains(report.Findings, f => f.Code == "UNNODED_CROSSING" && f.Handles.Any(h => h.StartsWith('W')));
    }

    [Fact]
    public void One_arc_or_a_fan_of_long_lines_does_not_coarsen_the_whole_check()
    {
        // 20000 legend strokes in a 400 x 150 mm box of an 800 x 600 m site, 2000 walls, and one road arc of R = 50 m:
        // the arc's chord deviation once set the cell size of everything and the check hit its work limit.
        var random = new Random(5);
        var curves = new List<TopologyCurve>();
        for (int i = 0; i < 20000; i++)
        {
            double x = 1000 + random.NextDouble() * 400, y = 1000 + random.NextDouble() * 150, a = random.NextDouble() * Math.PI, length = 1 + random.NextDouble() * 2;
            curves.Add(Line("S" + i, x, y, x + Math.Cos(a) * length, y + Math.Sin(a) * length));
        }
        for (int i = 0; i < 2000; i++)
        {
            double x = random.NextDouble() * 800000, y = random.NextDouble() * 600000, length = 1000 + random.NextDouble() * 9000;
            curves.Add(random.Next(2) == 0 ? Line("W" + i, x, y, x + length, y) : Line("W" + i, x, y, x, y + length));
        }
        double sampling = Math.Sqrt(800000.0 * 800000 + 600000.0 * 600000) * 1e-5;
        var arc = ArcSampling.Arc(400000, 300000, 50000, 0, Math.PI / 2, sampling);
        curves.Add(new("ARC", "0", arc, false, ArcSampling.Sagitta(50000, Math.PI / 2, arc.Length - 1)));
        // 1000 rays of 5 m from one point next to the strokes: long segments meeting in one place.
        for (int i = 0; i < 1000; i++)
        {
            double a = 2 * Math.PI * i / 1000;
            curves.Add(Line("R" + i, 1200, 1075, 1200 + 5000 * Math.Cos(a), 1075 + 5000 * Math.Sin(a)));
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = Topology.Analyze(curves, new TopologyOptions(1e-3, 0, MaxFindings: 1000));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "Check is too slow: " + watch.Elapsed);
        Assert.DoesNotContain(report.Findings, f => f.Code == "TOPOLOGY_LIMIT");
        // Coordinates no grid level can hold are reported instead of exhausting memory.
        var absurd = Topology.Analyze([Line("A", 0, 0, 1, 1), Line("B", 0, 0, 2, 0), Line("HUGE", 0, 0, 1e40, 0)], new TopologyOptions(1e-3));
        Assert.Contains(absurd.Findings, f => f.Code == "TOPOLOGY_LIMIT");
    }

    [Fact]
    public void Chord_deviation_applies_only_to_curved_segments()
    {
        // A polyline whose first segment is straight and second is a coarsely sampled arc; a line stops 0.008
        // short of the straight part: that is a near miss, whatever the arc's chords deviate.
        var polyline = new TopologyCurve("P", "0", [[0, 0], [100, 0], [100, 50]], false, 0.5, [0, 0.5]);
        var line = Line("L", 50, 10, 50, 0.008);
        var report = Topology.Analyze([polyline, line], new TopologyOptions(0.001, 0.05));
        Assert.Contains(report.Findings, f => f.Code == "NEAR_MISS" && f.Handles.Contains("L"));
        // Without per-segment values the whole curve counts as curved, as before.
        var uniform = Topology.Analyze([polyline with { SegmentDeviations = null }, line], new TopologyOptions(0.001, 0.05));
        Assert.DoesNotContain(uniform.Findings, f => f.Code == "NEAR_MISS");
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
