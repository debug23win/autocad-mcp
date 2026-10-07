namespace CadMcp.Core;

/// <summary>
/// A curve projected to the WCS XY plane as a polyline; arcs and splines are sampled by the caller.
/// Deviation is the largest distance between the sampled chords and the true curve, so a point on an
/// arc between two samples still counts as touching it.
/// </summary>
public sealed record TopologyCurve(string Handle, string Layer, IReadOnlyList<double[]> Points, bool Closed, double Deviation = 0);

/// <summary>
/// Tolerance: points closer than this coincide. GapTolerance: an endpoint closer than this to other
/// geometry, but not touching it, is a near miss (0 disables the check).
/// </summary>
public sealed record TopologyOptions(double Tolerance, double GapTolerance = 0, bool Endpoints = true, bool Crossings = true,
    bool Duplicates = true, bool SelfIntersections = true, int MaxFindings = 500);

public sealed record TopologyFinding(string Code, string Severity, string[] Handles, double[] Point, double? Distance, int Occurrences, string Message);

public sealed record TopologyReport(string State, int Curves, int Segments, IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<TopologyFinding> Findings, bool Truncated, string[] Limitations);

/// <summary>
/// Independent 2D topology check of drawn linework: dangling ends, near misses, T-junctions,
/// crossings without a node, overlapping or duplicate geometry, self-intersections and zero-length
/// curves. A uniform grid keeps it near-linear in the number of segments.
/// </summary>
public static class Topology
{
    public const string Warning = "warning", Info = "info", Unverified = "unverified";
    private const int MaxSegments = 200_000, MaxRegistrations = 4_000_000;

    private readonly record struct Segment(int Curve, int Index, double Ax, double Ay, double Bx, double By)
    {
        public double Length => Math.Sqrt((Bx - Ax) * (Bx - Ax) + (By - Ay) * (By - Ay));
    }

    public static TopologyReport Analyze(IReadOnlyList<TopologyCurve> curves, TopologyOptions options)
    {
        if (!double.IsFinite(options.Tolerance) || options.Tolerance <= 0) throw new CadFault("INVALID_TOLERANCE", "tolerance must be a positive drawing-unit distance");
        if (!double.IsFinite(options.GapTolerance) || options.GapTolerance < 0) throw new CadFault("INVALID_TOLERANCE", "gap_tolerance must be zero or a positive drawing-unit distance");
        double tol = options.Tolerance, gap = Math.Max(options.GapTolerance, 0);
        var findings = new List<TopologyFinding>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        bool truncated = false;
        void Add(string code, string severity, string[] handles, double x, double y, double? distance, int occurrences, string message)
        {
            counts[code] = counts.GetValueOrDefault(code) + 1;
            if (findings.Count >= options.MaxFindings) { truncated = true; return; }
            findings.Add(new(code, severity, handles, [x, y], distance, occurrences, message));
        }

        // Clean the polylines: drop repeated vertices, detect implicit closure, measure length.
        var points = new List<(double X, double Y)[]>(curves.Count);
        var closed = new bool[curves.Count];
        var live = new bool[curves.Count];
        for (int c = 0; c < curves.Count; c++)
        {
            var cleaned = new List<(double X, double Y)>();
            foreach (var p in curves[c].Points)
            {
                if (p.Length < 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1])) throw new CadFault("INVALID_TOPOLOGY_POINT", curves[c].Handle);
                if (cleaned.Count == 0 || Distance(cleaned[^1], (p[0], p[1])) > tol) cleaned.Add((p[0], p[1]));
            }
            bool isClosed = curves[c].Closed;
            if (!isClosed && cleaned.Count > 2 && Distance(cleaned[0], cleaned[^1]) <= tol) { isClosed = true; cleaned.RemoveAt(cleaned.Count - 1); }
            if (isClosed && cleaned.Count > 1 && Distance(cleaned[0], cleaned[^1]) <= tol) cleaned.RemoveAt(cleaned.Count - 1);
            points.Add(cleaned.ToArray()); closed[c] = isClosed;
            double length = 0;
            for (int i = 1; i < cleaned.Count; i++) length += Distance(cleaned[i - 1], cleaned[i]);
            if (isClosed && cleaned.Count > 1) length += Distance(cleaned[^1], cleaned[0]);
            live[c] = cleaned.Count >= 2 && length > tol;
            if (!live[c]) Add("ZERO_LENGTH", Warning, [curves[c].Handle], cleaned.Count > 0 ? cleaned[0].X : 0, cleaned.Count > 0 ? cleaned[0].Y : 0, length, 1,
                "Curve has no measurable length; erase it or redraw the intended geometry");
        }

        var segments = new List<Segment>();
        for (int c = 0; c < curves.Count; c++)
        {
            if (!live[c]) continue;
            var p = points[c];
            for (int i = 1; i < p.Length; i++) segments.Add(new(c, i - 1, p[i - 1].X, p[i - 1].Y, p[i].X, p[i].Y));
            if (closed[c] && p.Length > 2) segments.Add(new(c, p.Length - 1, p[^1].X, p[^1].Y, p[0].X, p[0].Y));
        }
        var limitations = new[] { "XY projection: Z is ignored", "arcs, ellipses and splines are sampled by the caller within tolerance", "top-level entities only; blocks are not exploded" };
        if (segments.Count > MaxSegments)
        {
            Add("TOPOLOGY_LIMIT", Unverified, [], 0, 0, null, segments.Count, "Too many segments (" + segments.Count + "); narrow the scope by layer or bounds");
            return Report();
        }

        // Grid sized to the typical segment, never smaller than the gap, so a 3x3 neighbourhood covers every candidate.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, total = 0;
        foreach (var s in segments)
        {
            minX = Math.Min(minX, Math.Min(s.Ax, s.Bx)); minY = Math.Min(minY, Math.Min(s.Ay, s.By));
            maxX = Math.Max(maxX, Math.Max(s.Ax, s.Bx)); maxY = Math.Max(maxY, Math.Max(s.Ay, s.By)); total += s.Length;
        }
        double cell = segments.Count == 0 ? 1 : Math.Max(Math.Max(total / segments.Count, 2 * Math.Max(gap, tol)), Math.Max(maxX - minX, maxY - minY) / 4096);
        var grid = new Dictionary<long, List<int>>();
        long registrations = 0;
        long Key(long ix, long iy) => (ix << 32) ^ (iy & 0xffffffffL);
        long Cell(double v, double origin) => (long)Math.Floor((v - origin) / cell);
        for (int i = 0; i < segments.Count && registrations <= MaxRegistrations; i++)
        {
            var s = segments[i]; var registered = new HashSet<long>();
            int steps = Math.Max(1, (int)Math.Ceiling(s.Length / (cell / 2)));
            for (int k = 0; k <= steps; k++)
            {
                double t = (double)k / steps, x = s.Ax + (s.Bx - s.Ax) * t, y = s.Ay + (s.By - s.Ay) * t;
                long cx = Cell(x, minX), cy = Cell(y, minY);
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++)
                {
                    long key = Key(cx + dx, cy + dy);
                    if (!registered.Add(key)) continue;
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new();
                    list.Add(i); registrations++;
                }
            }
        }
        if (registrations > MaxRegistrations)
        {
            Add("TOPOLOGY_LIMIT", Unverified, [], 0, 0, null, segments.Count, "Geometry too dense or too large for one check; narrow the scope by layer or bounds");
            return Report();
        }
        IEnumerable<int> Near(double x, double y) => grid.TryGetValue(Key(Cell(x, minX), Cell(y, minY)), out var list) ? list : [];
        double Deviation(int curve) => double.IsFinite(curves[curve].Deviation) ? Math.Clamp(curves[curve].Deviation, 0, cell / 4) : 0;
        bool Adjacent(Segment a, Segment b)
        {
            if (a.Curve != b.Curve) return false;
            int n = points[a.Curve].Length, last = closed[a.Curve] && n > 2 ? n - 1 : n - 2;
            int d = Math.Abs(a.Index - b.Index);
            return d <= 1 || closed[a.Curve] && d == last;
        }

        // Duplicate curves (identical geometry in either direction or rotation) are reported once, not as overlaps.
        var duplicates = new HashSet<(int, int)>();
        if (options.Duplicates)
        {
            var buckets = new Dictionary<(int, long, long, long), List<int>>();
            for (int c = 0; c < curves.Count; c++)
            {
                if (!live[c]) continue;
                var p = points[c];
                var key = (p.Length, (long)Math.Round(p.Min(v => v.X) / (tol * 4)), (long)Math.Round(p.Min(v => v.Y) / (tol * 4)), closed[c] ? 1L : 0L);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new();
                foreach (int other in list)
                    if (SameGeometry(points[other], closed[other], p, closed[c], tol))
                    {
                        duplicates.Add((other, c));
                        Add("DUPLICATE_GEOMETRY", Warning, [curves[other].Handle, curves[c].Handle], p[0].X, p[0].Y, 0, 1, "Identical curves lie on top of each other; keep one");
                    }
                list.Add(c);
            }
        }

        // Pairwise segment relations from shared grid cells.
        var pairs = new Dictionary<(string Code, int A, int B), (double X, double Y, int Count)>();
        var seen = new HashSet<long>();
        foreach (var list in grid.Values)
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    int a = Math.Min(list[i], list[j]), b = Math.Max(list[i], list[j]);
                    if (!seen.Add(((long)a << 32) | (uint)b)) continue;
                    var s = segments[a]; var t = segments[b];
                    if (s.Curve == t.Curve && (!options.SelfIntersections || Adjacent(s, t))) continue;
                    if (s.Curve != t.Curve && duplicates.Contains((Math.Min(s.Curve, t.Curve), Math.Max(s.Curve, t.Curve)))) continue;
                    var relation = Relate(s, t, tol);
                    if (relation is null) continue;
                    var (kind, x, y) = relation.Value;
                    string? code = s.Curve == t.Curve
                        ? kind == "overlap" ? "SELF_OVERLAP" : kind == "cross" ? "SELF_INTERSECTION" : null
                        : kind == "overlap" ? "OVERLAPPING_SEGMENTS" : kind == "cross" && options.Crossings ? "UNNODED_CROSSING" : null;
                    if (code is null) continue;
                    var pairKey = (code, Math.Min(s.Curve, t.Curve), Math.Max(s.Curve, t.Curve));
                    pairs[pairKey] = pairs.TryGetValue(pairKey, out var prior) ? (prior.X, prior.Y, prior.Count + 1) : (x, y, 1);
                }
        foreach (var ((code, a, b), (x, y, count)) in pairs.OrderBy(p => p.Key.A).ThenBy(p => p.Key.B).ThenBy(p => p.Key.Code, StringComparer.Ordinal))
        {
            string[] handles = a == b ? [curves[a].Handle] : [curves[a].Handle, curves[b].Handle];
            var (severity, message) = code switch
            {
                "SELF_INTERSECTION" => (Warning, "Curve crosses itself; closed outlines used for areas or hatches must not self-intersect"),
                "SELF_OVERLAP" => (Warning, "Curve runs back over itself"),
                "OVERLAPPING_SEGMENTS" => (Warning, "Collinear segments of different curves overlap; merge or trim them"),
                _ => (Info, "Curves cross without a common vertex; add a node if they must connect (networks, outlines)")
            };
            Add(code, severity, handles, x, y, null, count, message);
        }

        // Endpoint relations of open curves.
        if (options.Endpoints)
        {
            var endpoints = new List<(int Curve, double X, double Y)>();
            for (int c = 0; c < curves.Count; c++)
            {
                if (!live[c] || closed[c]) continue;
                endpoints.Add((c, points[c][0].X, points[c][0].Y)); endpoints.Add((c, points[c][^1].X, points[c][^1].Y));
            }
            var endpointGrid = new Dictionary<long, List<int>>();
            for (int e = 0; e < endpoints.Count; e++)
            {
                long key = Key(Cell(endpoints[e].X, minX), Cell(endpoints[e].Y, minY));
                if (!endpointGrid.TryGetValue(key, out var list)) endpointGrid[key] = list = new();
                list.Add(e);
            }
            var reportedGaps = new HashSet<(int, int)>();
            for (int e = 0; e < endpoints.Count; e++)
            {
                var (c, x, y) = endpoints[e];
                double nearestEnd = double.MaxValue; int nearestEndCurve = -1, nearestEndIndex = -1;
                long cx = Cell(x, minX), cy = Cell(y, minY);
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++)
                    if (endpointGrid.TryGetValue(Key(cx + dx, cy + dy), out var list))
                        foreach (int other in list)
                        {
                            if (other == e || endpoints[other].Curve == c) continue;
                            double d = Distance((x, y), (endpoints[other].X, endpoints[other].Y));
                            if (d < nearestEnd) { nearestEnd = d; nearestEndCurve = endpoints[other].Curve; nearestEndIndex = other; }
                        }
                if (nearestEnd <= tol) continue;
                double nearestSegment = double.MaxValue; int nearestSegmentCurve = -1;
                foreach (int index in Near(x, y))
                {
                    var s = segments[index];
                    // A curve's own incident segments always touch its endpoint.
                    if (s.Curve == c && (s.Index == 0 || s.Index == points[c].Length - 2)) continue;
                    double d = Math.Max(0, PointToSegment(x, y, s) - Deviation(s.Curve));
                    if (d < nearestSegment) { nearestSegment = d; nearestSegmentCurve = s.Curve; }
                }
                if (nearestSegment <= tol)
                {
                    Add("T_JUNCTION", Info, nearestSegmentCurve == c ? [curves[c].Handle] : [curves[c].Handle, curves[nearestSegmentCurve].Handle], x, y, nearestSegment, 1,
                        "Endpoint meets another curve between its vertices; split that curve if the joint must be a node");
                    continue;
                }
                double nearest = Math.Min(nearestEnd, nearestSegment);
                int nearestCurve = nearestEnd <= nearestSegment ? nearestEndCurve : nearestSegmentCurve;
                // Two ends that stop short of each other form one gap, reported once.
                if (gap > 0 && nearest <= gap && nearestCurve >= 0 && nearestEnd <= nearestSegment && !reportedGaps.Add((Math.Min(e, nearestEndIndex), Math.Max(e, nearestEndIndex)))) continue;
                if (gap > 0 && nearest <= gap && nearestCurve >= 0)
                    Add("NEAR_MISS", Warning, [curves[c].Handle, curves[nearestCurve].Handle], x, y, nearest, 1,
                        "Endpoint stops short of other geometry by " + nearest.ToString("G6", System.Globalization.CultureInfo.InvariantCulture) + " drawing units; snap it or confirm the gap is intended");
                else Add("DANGLING_END", Info, [curves[c].Handle], x, y, null, 1, "Endpoint is not connected to other geometry");
            }
        }
        return Report();

        TopologyReport Report()
        {
            var ordered = findings.OrderBy(f => f.Severity == Warning ? 0 : f.Severity == Unverified ? 1 : 2).ToArray();
            string state = counts.Keys.Any(k => k == "TOPOLOGY_LIMIT") ? "unverified"
                : findings.Any(f => f.Severity == Warning) || truncated && counts.Any(p => p.Key is not ("DANGLING_END" or "T_JUNCTION" or "UNNODED_CROSSING")) ? "review_required" : "passed";
            return new(state, curves.Count, segments.Count, counts, ordered, truncated, limitations);
        }
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double PointToSegment(double x, double y, Segment s)
    {
        double dx = s.Bx - s.Ax, dy = s.By - s.Ay, length2 = dx * dx + dy * dy;
        double t = length2 <= 0 ? 0 : Math.Clamp(((x - s.Ax) * dx + (y - s.Ay) * dy) / length2, 0, 1);
        double px = s.Ax + dx * t - x, py = s.Ay + dy * t - y;
        return Math.Sqrt(px * px + py * py);
    }

    /// <summary>"cross" for a proper interior crossing, "overlap" for a collinear overlap longer than the tolerance, else null.</summary>
    private static (string Kind, double X, double Y)? Relate(Segment s, Segment t, double tol)
    {
        double ux = s.Bx - s.Ax, uy = s.By - s.Ay, vx = t.Bx - t.Ax, vy = t.By - t.Ay;
        double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
        if (lu <= tol || lv <= tol) return null;
        // Signed distances of each segment's ends from the other's supporting line.
        double d1 = (ux * (t.Ay - s.Ay) - uy * (t.Ax - s.Ax)) / lu, d2 = (ux * (t.By - s.Ay) - uy * (t.Bx - s.Ax)) / lu;
        double d3 = (vx * (s.Ay - t.Ay) - vy * (s.Ax - t.Ax)) / lv, d4 = (vx * (s.By - t.Ay) - vy * (s.Bx - t.Ax)) / lv;
        if (Math.Abs(d1) <= tol && Math.Abs(d2) <= tol)
        {
            // Collinear: overlap of the projections onto s.
            double ta = ((t.Ax - s.Ax) * ux + (t.Ay - s.Ay) * uy) / lu, tb = ((t.Bx - s.Ax) * ux + (t.By - s.Ay) * uy) / lu;
            double start = Math.Max(0, Math.Min(ta, tb)), end = Math.Min(lu, Math.Max(ta, tb));
            if (end - start <= tol) return null;
            double mid = (start + end) / 2;
            return ("overlap", s.Ax + ux / lu * mid, s.Ay + uy / lu * mid);
        }
        if ((d1 > tol && d2 < -tol || d1 < -tol && d2 > tol) && (d3 > tol && d4 < -tol || d3 < -tol && d4 > tol))
        {
            double k = d1 / (d1 - d2);
            return ("cross", t.Ax + vx * k, t.Ay + vy * k);
        }
        return null;
    }

    private static bool SameGeometry((double X, double Y)[] a, bool closedA, (double X, double Y)[] b, bool closedB, double tol)
    {
        if (a.Length != b.Length || closedA != closedB) return false;
        int n = a.Length;
        bool Match(int start, int direction)
        {
            for (int i = 0; i < n; i++)
            {
                int j = ((start + direction * i) % n + n) % n;
                if (Distance(a[i], b[j]) > tol) return false;
            }
            return true;
        }
        if (!closedA) return Match(0, 1) || Match(n - 1, -1);
        for (int start = 0; start < n; start++)
            if (Distance(a[0], b[start]) <= tol && (Match(start, 1) || Match(start, -1))) return true;
        return false;
    }
}

/// <summary>Polyline approximations of arcs within a chord tolerance, for the topology check.</summary>
public static class ArcSampling
{
    /// <summary>Segment count for a circular sweep so that no chord deviates more than <paramref name="tolerance"/>.</summary>
    public static int Segments(double radius, double sweep, double tolerance, int minimum = 4, int maximum = 512)
    {
        sweep = Math.Abs(sweep);
        if (radius <= 0 || sweep <= 0) return 1;
        double step = tolerance >= radius ? Math.PI / 2 : 2 * Math.Acos(1 - tolerance / radius);
        if (!double.IsFinite(step) || step <= 0) return maximum;
        return Math.Clamp((int)Math.Ceiling(sweep / step), minimum, maximum);
    }

    /// <summary>Largest distance between a circular arc and its chords when the sweep is split into equal segments.</summary>
    public static double Sagitta(double radius, double sweep, int segments) =>
        segments <= 0 ? Math.Abs(radius) : Math.Abs(radius) * (1 - Math.Cos(Math.Min(Math.Abs(sweep), 2 * Math.PI) / segments / 2));

    /// <summary>Points of a counter-clockwise arc from start to end angle (radians), both ends included.</summary>
    public static double[][] Arc(double cx, double cy, double radius, double start, double end, double tolerance)
    {
        double sweep = end - start;
        while (sweep <= 0) sweep += 2 * Math.PI;
        int n = Segments(radius, sweep, tolerance);
        return Enumerable.Range(0, n + 1).Select(i => { double a = start + sweep * i / n; return new[] { cx + radius * Math.Cos(a), cy + radius * Math.Sin(a) }; }).ToArray();
    }

    /// <summary>Points of a polyline bulge segment from (x0,y0) to (x1,y1), excluding the start point.</summary>
    public static double[][] Bulge(double x0, double y0, double x1, double y1, double bulge, double tolerance)
    {
        double chord = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        if (Math.Abs(bulge) < 1e-12 || chord <= 0) return [[x1, y1]];
        double theta = 4 * Math.Atan(bulge), radius = chord / (2 * Math.Abs(Math.Sin(theta / 2)));
        // The centre lies on the chord bisector at (chord/2)/tan(theta/2) along the left normal; the sign of
        // theta (counter-clockwise positive) and of the tangent place it correctly for minor and major arcs.
        double mx = (x0 + x1) / 2, my = (y0 + y1) / 2, nx = -(y1 - y0) / chord, ny = (x1 - x0) / chord;
        double h = (chord / 2) / Math.Tan(theta / 2);
        double cx = mx + nx * h, cy = my + ny * h;
        double a0 = Math.Atan2(y0 - cy, x0 - cx);
        int n = Segments(radius, Math.Abs(theta), tolerance);
        // The last point is the next vertex itself, so consecutive segments share it exactly.
        return Enumerable.Range(1, n).Select(i => { if (i == n) return new[] { x1, y1 }; double a = a0 + theta * i / n; return new[] { cx + radius * Math.Cos(a), cy + radius * Math.Sin(a) }; }).ToArray();
    }
}
