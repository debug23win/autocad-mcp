using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class PolylineFilletTests
{
    private static readonly double QuarterBulge = Math.Tan(Math.PI / 8);

    private static PolylineVertex[] Path(params double[] xy) => Enumerable.Range(0, xy.Length / 2).Select(i => new PolylineVertex(xy[2 * i], xy[2 * i + 1])).ToArray();

    /// <summary>Length of a polyline with arc segments: an arc of bulge b on chord c has the included angle 4·atan(b).</summary>
    private static double Length(IReadOnlyList<PolylineVertex> vertices, bool closed)
    {
        double total = 0;
        int segments = closed ? vertices.Count : vertices.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            var a = vertices[i]; var b = vertices[(i + 1) % vertices.Count];
            double chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (Math.Abs(a.Bulge) < 1e-15) { total += chord; continue; }
            double angle = 4 * Math.Atan(Math.Abs(a.Bulge));
            total += chord / (2 * Math.Sin(angle / 2)) * angle;
        }
        return total;
    }

    [Fact]
    public void A_rectangle_gets_four_quarter_arcs_turning_its_way()
    {
        var rectangle = Path(0, 0, 20, 0, 20, 10, 0, 10);
        var result = PolylineFillet.Apply(rectangle, closed: true, radius: 2);
        Assert.Equal([0, 1, 2, 3], result.Filleted);
        Assert.Empty(result.Skipped);
        Assert.Equal(8, result.Vertices.Count);
        // Vertex 0 takes the closing segment as its incoming one.
        Assert.Equal((0.0, 2.0, QuarterBulge), (result.Vertices[0].X, result.Vertices[0].Y, result.Vertices[0].Bulge), new Tolerant());
        Assert.Equal((2.0, 0.0, 0.0), (result.Vertices[1].X, result.Vertices[1].Y, result.Vertices[1].Bulge), new Tolerant());
        Assert.Equal((18.0, 0.0, QuarterBulge), (result.Vertices[2].X, result.Vertices[2].Y, result.Vertices[2].Bulge), new Tolerant());
        Assert.Equal((20.0, 2.0, 0.0), (result.Vertices[3].X, result.Vertices[3].Y, result.Vertices[3].Bulge), new Tolerant());
        Assert.Equal(60 - 16 + 4 * Math.PI, Length(result.Vertices, true), 9);

        // Clockwise, the arcs turn the other way.
        var clockwise = PolylineFillet.Apply(rectangle.Reverse().ToArray(), closed: true, radius: 2);
        Assert.All(clockwise.Vertices.Where(v => v.Bulge != 0), v => Assert.Equal(-QuarterBulge, v.Bulge, 12));
    }

    [Fact]
    public void An_open_polyline_rounds_inner_corners_and_names_skipped_ends_only_when_asked()
    {
        var l = Path(0, 0, 10, 0, 10, 10);
        var all = PolylineFillet.Apply(l, closed: false, radius: 2);
        Assert.Equal([1], all.Filleted);
        Assert.Empty(all.Skipped);
        Assert.Equal(4, all.Vertices.Count);
        Assert.Equal((8.0, 0.0, QuarterBulge), (all.Vertices[1].X, all.Vertices[1].Y, all.Vertices[1].Bulge), new Tolerant());
        Assert.Equal((10.0, 2.0), (all.Vertices[2].X, all.Vertices[2].Y), new Tolerant());

        var asked = PolylineFillet.Apply(l, closed: false, radius: 2, at: [0, 1]);
        Assert.Equal([1], asked.Filleted);
        Assert.Equal("end of an open polyline", Assert.Single(asked.Skipped).Reason);
    }

    [Fact]
    public void An_arc_that_takes_whole_segments_leaves_no_zero_length_segment()
    {
        // A 10 x 10 square with radius 5 becomes a circle of four arcs through the side midpoints.
        var circle = PolylineFillet.Apply(Path(0, 0, 10, 0, 10, 10, 0, 10), closed: true, radius: 5);
        Assert.Equal(4, circle.Filleted.Count);
        Assert.Equal(4, circle.Vertices.Count);
        Assert.All(circle.Vertices, v => Assert.Equal(QuarterBulge, v.Bulge, 12));
        Assert.Equal(2 * Math.PI * 5, Length(circle.Vertices, true), 9);

        // Radius 6 does not fit next to a rounded neighbour: every corner stays, whatever their order.
        var tooLarge = PolylineFillet.Apply(Path(0, 0, 10, 0, 10, 10, 0, 10), closed: true, radius: 6);
        Assert.Empty(tooLarge.Filleted);
        Assert.Equal(4, tooLarge.Skipped.Count);
        Assert.All(tooLarge.Skipped, s => Assert.Contains("overlap", s.Reason));
        // One corner alone has room.
        Assert.Equal([1], PolylineFillet.Apply(Path(0, 0, 10, 0, 10, 10, 0, 10), closed: true, radius: 6, at: [1]).Filleted);
        // A corner whose arc is longer than its segments is reported on its own.
        var single = PolylineFillet.Apply(Path(0, 0, 3, 0, 3, 10), closed: false, radius: 5);
        Assert.Contains("does not fit", Assert.Single(single.Skipped).Reason);
    }

    [Fact]
    public void Arc_segments_straight_vertices_and_widths()
    {
        // The middle segment is an arc: both of its corners stay and say why.
        var withArc = new[] { new PolylineVertex(0, 0), new PolylineVertex(10, 0, 0.5), new PolylineVertex(20, 0), new PolylineVertex(20, 10) };
        var arcResult = PolylineFillet.Apply(withArc, closed: false, radius: 1);
        Assert.Empty(arcResult.Filleted);
        Assert.Equal([1, 2], arcResult.Skipped.Select(s => s.Vertex));
        Assert.All(arcResult.Skipped, s => Assert.Equal("next to an arc segment", s.Reason));

        // A vertex in a straight run is no corner and is not reported unless asked for.
        var straight = PolylineFillet.Apply(Path(0, 0, 5, 0, 10, 0, 10, 10), closed: false, radius: 1);
        Assert.Equal([2], straight.Filleted);
        Assert.Empty(straight.Skipped);

        // Tapered segments keep their taper: the widths at the tangent points are interpolated.
        var tapered = new[] { new PolylineVertex(0, 0, 0, 0, 2), new PolylineVertex(10, 0, 0, 2, 4), new PolylineVertex(10, 10, 0, 4, 4) };
        var widths = PolylineFillet.Apply(tapered, closed: false, radius: 2).Vertices;
        Assert.Equal(4, widths.Count);
        Assert.Equal((0.0, 1.6), (widths[0].StartWidth, widths[0].EndWidth), new Tolerant());
        Assert.Equal((1.6, 2.4), (widths[1].StartWidth, widths[1].EndWidth), new Tolerant());
        Assert.Equal((2.4, 4.0), (widths[2].StartWidth, widths[2].EndWidth), new Tolerant());
    }

    [Fact]
    public void Invalid_input_is_refused()
    {
        var l = Path(0, 0, 10, 0, 10, 10);
        Assert.Equal("INVALID_PARAMETER", Assert.Throws<CadFault>(() => PolylineFillet.Apply(l, false, 0)).Code);
        Assert.Equal("INVALID_PARAMETER", Assert.Throws<CadFault>(() => PolylineFillet.Apply(l, false, double.NaN)).Code);
        Assert.Equal("INVALID_VERTEX", Assert.Throws<CadFault>(() => PolylineFillet.Apply(l, false, 1, at: [3])).Code);
        Assert.Equal("INVALID_POLYLINE", Assert.Throws<CadFault>(() => PolylineFillet.Apply([new PolylineVertex(0, 0)], false, 1)).Code);
    }

    [Fact]
    public void Rounding_shortens_a_polygon_by_exactly_the_cut_corners()
    {
        // Random convex polygons: each rounded corner replaces two tangent lengths by an arc of radius r and angle pi - theta.
        var random = new Random(11);
        for (int run = 0; run < 300; run++)
        {
            int n = random.Next(3, 12);
            double scale = Math.Pow(10, random.Next(-2, 6));
            var angles = Enumerable.Range(0, n).Select(_ => random.NextDouble() * 2 * Math.PI).Order().ToArray();
            var polygon = angles.Select(a => new PolylineVertex(scale * Math.Cos(a), scale * Math.Sin(a))).ToArray();
            double radius = scale * random.NextDouble() * 0.2;
            if (radius <= 0) continue;
            var result = PolylineFillet.Apply(polygon, closed: true, radius, tolerance: scale * 1e-12);
            double expected = Length(polygon, true);
            foreach (int i in result.Filleted)
            {
                var p = polygon[(i + n - 1) % n]; var v = polygon[i]; var q = polygon[(i + 1) % n];
                double ux = p.X - v.X, uy = p.Y - v.Y, wx = q.X - v.X, wy = q.Y - v.Y;
                double theta = Math.Acos(Math.Clamp((ux * wx + uy * wy) / Math.Sqrt((ux * ux + uy * uy) * (wx * wx + wy * wy)), -1, 1));
                expected += -2 * radius / Math.Tan(theta / 2) + radius * (Math.PI - theta);
            }
            Assert.Equal(expected, Length(result.Vertices, true), 9 - (int)Math.Log10(scale) - 3);
            Assert.Equal(n, result.Filleted.Count + result.Skipped.Count);
        }
    }

    private sealed class Tolerant : IEqualityComparer<(double, double, double)>, IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double, double) a, (double, double, double) b) => Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9 && Math.Abs(a.Item3 - b.Item3) < 1e-9;
        public int GetHashCode((double, double, double) value) => 0;
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9;
        public int GetHashCode((double, double) value) => 0;
    }
}
