namespace CadMcp.Core;

/// <summary>
/// Plan of a horizontal alignment from PI points with circular curves: tangent lengths, deflections and the
/// expected total length. A curve that does not fit between its neighbours is rejected before Civil 3D is
/// asked to build it, and the built alignment is compared with this plan before the transaction commits.
/// </summary>
public static class AlignmentGeometry
{
    public sealed record Curve(int Pi, double Radius, double DeflectionDeg, double TangentLength, double ArcLength);
    public sealed record Plan(IReadOnlyList<double> SegmentLengths, IReadOnlyList<Curve> Curves, double ExpectedLength);

    public static Plan Compute(IReadOnlyList<(double X, double Y)> pis, IReadOnlyList<double>? radii)
    {
        if (pis.Count < 2) throw new CadFault("INVALID_POINTS", "An alignment needs at least two PI points");
        if (radii is not null && radii.Count != Math.Max(0, pis.Count - 2))
            throw new CadFault("INVALID_RADII", "radii needs one value per interior PI (" + Math.Max(0, pis.Count - 2) + "), 0 for no curve");
        var lengths = new double[pis.Count - 1];
        for (int i = 0; i + 1 < pis.Count; i++)
        {
            lengths[i] = Math.Sqrt(Math.Pow(pis[i + 1].X - pis[i].X, 2) + Math.Pow(pis[i + 1].Y - pis[i].Y, 2));
            if (lengths[i] < 1e-6) throw new CadFault("INVALID_POINTS", "PI points " + i + " and " + (i + 1) + " coincide");
        }
        var tangents = new double[pis.Count];
        var curves = new List<Curve>();
        double expected = lengths.Sum();
        for (int i = 1; i + 1 < pis.Count; i++)
        {
            double radius = radii?[i - 1] ?? 0;
            if (!double.IsFinite(radius) || radius < 0) throw new CadFault("INVALID_RADII", "Radii must be zero or positive");
            if (radius == 0) continue;
            double ax = pis[i].X - pis[i - 1].X, ay = pis[i].Y - pis[i - 1].Y, bx = pis[i + 1].X - pis[i].X, by = pis[i + 1].Y - pis[i].Y;
            double cos = Math.Clamp((ax * bx + ay * by) / (lengths[i - 1] * lengths[i]), -1, 1);
            double deflection = Math.Acos(cos);
            if (deflection < 1e-9) throw new CadFault("INVALID_CURVE", "PI " + i + " has no deflection; use radius 0 there");
            if (Math.PI - deflection < 1e-6) throw new CadFault("INVALID_CURVE", "PI " + i + " reverses the direction; no curve can join the tangents");
            double tangent = radius * Math.Tan(deflection / 2), arc = radius * deflection;
            tangents[i] = tangent;
            expected -= 2 * tangent - arc;
            curves.Add(new(i, radius, deflection * 180 / Math.PI, tangent, arc));
        }
        for (int i = 0; i + 1 < pis.Count; i++)
            if (tangents[i] + tangents[i + 1] > lengths[i] + 1e-9)
                throw new CadFault("CURVE_DOES_NOT_FIT", "Tangent " + i + "-" + (i + 1) + " is " + lengths[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                    " long but its curves need " + (tangents[i] + tangents[i + 1]).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "; reduce the radii");
        return new(lengths, curves, expected);
    }
}
