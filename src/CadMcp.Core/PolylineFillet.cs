namespace CadMcp.Core;

/// <summary>A vertex of a lightweight polyline in its own plane, with the bulge and widths of the segment that starts there.</summary>
public readonly record struct PolylineVertex(double X, double Y, double Bulge = 0, double StartWidth = 0, double EndWidth = 0);

/// <summary>Vertices after rounding; Filleted and Skipped name vertices by their index before the change.</summary>
public sealed record PolylineFilletResult(IReadOnlyList<PolylineVertex> Vertices, IReadOnlyList<int> Filleted, IReadOnlyList<PolylineFilletSkip> Skipped);

public sealed record PolylineFilletSkip(int Vertex, string Reason);

/// <summary>
/// Rounds corners of a lightweight polyline with arcs of one radius, as the Polyline option of FILLET: a corner between
/// two straight segments becomes its two tangent points joined by an arc (a bulge). Ends of an open polyline, corners next
/// to an arc segment, straight or doubled-back corners, and corners whose arcs do not fit on a segment are left as they are
/// and reported. Two rounded corners that would overlap on the segment between them are both left, so the result never
/// depends on the order of the vertices.
/// </summary>
public static class PolylineFillet
{
    public static PolylineFilletResult Apply(IReadOnlyList<PolylineVertex> vertices, bool closed, double radius, IReadOnlyCollection<int>? at = null, double tolerance = 1e-9)
    {
        if (!double.IsFinite(radius) || radius <= 0) throw new CadFault("INVALID_PARAMETER", "radius must be positive");
        int n = vertices.Count;
        if (n < 2) throw new CadFault("INVALID_POLYLINE", "The polyline has fewer than two vertices");
        if (vertices.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y) || !double.IsFinite(v.Bulge))) throw new CadFault("INVALID_POLYLINE", "The polyline has a vertex that is not a finite number");
        var requested = (at ?? Enumerable.Range(0, n).ToArray()).Distinct().Order().ToArray();
        if (requested.FirstOrDefault(i => i < 0 || i >= n, -1) is int outside and >= 0)
            throw new CadFault("INVALID_VERTEX", "vertex " + outside + " is outside the polyline's 0.." + (n - 1));
        bool explicitList = at is not null;

        // Tangent length of each corner that can be rounded on its own; the rest are skipped with the reason.
        var tangent = new double[n];
        var corner = new double[n];
        var turn = new double[n];
        var skipped = new List<PolylineFilletSkip>();
        int Previous(int i) => i > 0 ? i - 1 : closed ? n - 1 : -1;
        int Next(int i) => i < n - 1 ? i + 1 : closed ? 0 : -1;
        double Length(int from) { var a = vertices[from]; var b = vertices[Next(from)]; return Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)); }
        foreach (int i in requested)
        {
            int p = Previous(i), q = Next(i);
            string? reason = null;
            if (p < 0 || q < 0) reason = "end of an open polyline";
            else if (Math.Abs(vertices[p].Bulge) > tolerance || Math.Abs(vertices[i].Bulge) > tolerance) reason = "next to an arc segment";
            else if (n == 2) reason = "no corner";
            else
            {
                double l1 = Length(p), l2 = Length(i);
                if (l1 <= tolerance || l2 <= tolerance) reason = "next to a segment of zero length";
                else
                {
                    var b = vertices[i];
                    double u1x = (vertices[p].X - b.X) / l1, u1y = (vertices[p].Y - b.Y) / l1, u2x = (vertices[q].X - b.X) / l2, u2y = (vertices[q].Y - b.Y) / l2;
                    double theta = Math.Acos(Math.Clamp(u1x * u2x + u1y * u2y, -1, 1));
                    if (Math.PI - theta < 1e-9) reason = "no corner: the segments are collinear";
                    else if (theta < 1e-9) reason = "the segments double back";
                    else
                    {
                        tangent[i] = radius / Math.Tan(theta / 2);
                        corner[i] = theta;
                        // Left turn (counter-clockwise arc) for a positive cross product of the incoming and outgoing directions.
                        turn[i] = Math.Sign(-u1x * u2y + u1y * u2x);
                        if (tangent[i] > Math.Min(l1, l2) + tolerance) { reason = "the arc does not fit: a segment is shorter than " + Format(tangent[i]); tangent[i] = 0; }
                    }
                }
            }
            if (reason is not null && (explicitList || reason != "end of an open polyline" && !reason.StartsWith("no corner", StringComparison.Ordinal)))
                skipped.Add(new(i, reason));
        }
        // Two rounded corners share the segment between them: both stay when their arcs overlap.
        var overlapping = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            int q = Next(i);
            if (q < 0 || q == i || tangent[i] <= 0 || tangent[q] <= 0) continue;
            if (tangent[i] + tangent[q] > Length(i) + tolerance) { overlapping.Add(i); overlapping.Add(q); }
        }
        foreach (int i in overlapping.Order())
        {
            skipped.Add(new(i, "the arcs of this corner and its neighbour overlap on the segment between them"));
            tangent[i] = 0;
        }

        // Width along a straight segment, interpolated between its start and end widths.
        double Width(int from, double distance)
        {
            var v = vertices[from];
            double length = Length(from);
            return length <= 0 ? v.StartWidth : v.StartWidth + (v.EndWidth - v.StartWidth) * Math.Clamp(distance / length, 0, 1);
        }
        var result = new List<PolylineVertex>(n * 2);
        var filleted = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var v = vertices[i];
            if (tangent[i] <= 0) { result.Add(v); continue; }
            int p = Previous(i), q = Next(i);
            double d = tangent[i], l1 = Length(p), l2 = Length(i);
            var a = vertices[p]; var c = vertices[q];
            double t1x = v.X + (a.X - v.X) / l1 * d, t1y = v.Y + (a.Y - v.Y) / l1 * d, t2x = v.X + (c.X - v.X) / l2 * d, t2y = v.Y + (c.Y - v.Y) / l2 * d;
            // The segment from the previous vertex now ends at the first tangent point: its end width is the width there.
            double w1 = Width(p, l1 - d), w2 = Width(i, d);
            if (result.Count > 0) result[^1] = result[^1] with { EndWidth = w1 };
            // The arc turns by the deflection, pi minus the corner angle; a bulge is the tangent of a quarter of it.
            double bulge = turn[i] * Math.Tan((Math.PI - corner[i]) / 4);
            result.Add(new(t1x, t1y, bulge, w1, w2));
            result.Add(new(t2x, t2y, 0, w2, v.EndWidth));
            filleted.Add(i);
        }
        // The closing segment of a closed polyline ends at vertex 0: its end width follows a rounded vertex 0.
        if (closed && tangent[0] > 0 && result.Count > 2) result[^1] = result[^1] with { EndWidth = Width(Previous(0), Length(Previous(0)) - tangent[0]) };
        return new(RemoveZeroSegments(result, closed, tolerance), filleted, skipped.OrderBy(s => s.Vertex).ToArray());
    }

    /// <summary>
    /// Drops a straight segment of zero length left where an arc takes a whole segment: its start vertex coincides with the
    /// next and carries no bulge, so the geometry does not change.
    /// </summary>
    private static List<PolylineVertex> RemoveZeroSegments(List<PolylineVertex> vertices, bool closed, double tolerance)
    {
        for (int i = vertices.Count - 1; i >= 0 && vertices.Count > 2; i--)
        {
            int next = i + 1 < vertices.Count ? i + 1 : closed ? 0 : -1;
            if (next < 0 || next == i) continue;
            var v = vertices[i]; var w = vertices[next];
            if (Math.Abs(v.Bulge) <= tolerance && Math.Abs(v.X - w.X) <= tolerance && Math.Abs(v.Y - w.Y) <= tolerance) vertices.RemoveAt(i);
        }
        return vertices;
    }

    private static string Format(double value) => value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
}
