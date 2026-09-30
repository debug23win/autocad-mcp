using System.Text.Json;

namespace CadMcp.Core;

/// <summary>Affine registration of top-left-origin image pixels to a WCS XY plane.</summary>
public sealed class ImageRegistration
{
    public double[] X { get; }
    public double[] Y { get; }
    public double Z { get; }
    public double RmsError { get; }
    public double MaxError { get; }
    public int ControlPointCount { get; }
    public double Determinant => X[1] * Y[2] - X[2] * Y[1];

    private ImageRegistration(double[] x, double[] y, double z, double rms, double max, int count)
    { X = x; Y = y; Z = z; RmsError = rms; MaxError = max; ControlPointCount = count; }

    public double[] PixelToWorld(double pixelX, double pixelY) =>
        [X[0] + X[1] * pixelX + X[2] * pixelY,
         Y[0] + Y[1] * pixelX + Y[2] * pixelY, Z];

    public double[] WorldToPixel(double worldX, double worldY)
    {
        if (Math.Abs(Determinant) < 1e-30) throw new CadFault("IMAGE_REGISTRATION_SINGULAR", "Image registration has no inverse");
        var dx = worldX - X[0]; var dy = worldY - Y[0];
        return [(dx * Y[2] - dy * X[2]) / Determinant,
                (dy * X[1] - dx * Y[1]) / Determinant];
    }

    public static ImageRegistration Fit(JsonElement controls, int width, int height)
    {
        if (width < 1 || height < 1) throw new CadFault("INVALID_IMAGE_SIZE", "Image dimensions must be positive");
        if (controls.ValueKind != JsonValueKind.Array || controls.GetArrayLength() is < 3 or > 20)
            throw new CadFault("INVALID_CONTROL_POINTS", "Supply 3..20 pixel/WCS control point pairs");
        var pairs = new List<(double px, double py, double x, double y, double z)>();
        foreach (var item in controls.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("pixel", out var pixel) ||
                pixel.ValueKind != JsonValueKind.Array || pixel.GetArrayLength() != 2 ||
                !item.TryGetProperty("world", out var world))
                throw new CadFault("INVALID_CONTROL_POINTS", "Each point needs pixel:[x,y] and world:[x,y,z]");
            double px = Number(pixel[0]), py = Number(pixel[1]);
            if (px < 0 || px > width || py < 0 || py > height)
                throw new CadFault("CONTROL_POINT_OUTSIDE_IMAGE", "Control point pixels must lie inside the image");
            var w = EditPlan.Point(world);
            pairs.Add((px, py, w[0], w[1], w[2]));
        }
        double z = pairs[0].z;
        if (pairs.Any(p => Math.Abs(p.z - z) > 1e-8))
            throw new CadFault("NONPLANAR_IMAGE", "Raster control points must lie on one WCS Z plane");
        double scale = Math.Max(width, height);
        var normal = new double[3, 3]; var xRhs = new double[3]; var yRhs = new double[3];
        foreach (var p in pairs)
        {
            var v = new[] { 1d, (p.px - width / 2d) / scale, (p.py - height / 2d) / scale };
            for (int i = 0; i < 3; i++)
            {
                xRhs[i] += v[i] * p.x; yRhs[i] += v[i] * p.y;
                for (int j = 0; j < 3; j++) normal[i, j] += v[i] * v[j];
            }
        }
        var sx = Solve(normal, xRhs); var sy = Solve(normal, yRhs);
        double[] x = [sx[0] - (sx[1] * width + sx[2] * height) / (2 * scale), sx[1] / scale, sx[2] / scale];
        double[] y = [sy[0] - (sy[1] * width + sy[2] * height) / (2 * scale), sy[1] / scale, sy[2] / scale];
        double determinant = x[1] * y[2] - x[2] * y[1];
        double widthVector = Math.Sqrt(x[1] * x[1] + y[1] * y[1]);
        double heightVector = Math.Sqrt(x[2] * x[2] + y[2] * y[2]);
        if (widthVector < 1e-12 || heightVector < 1e-12 ||
            Math.Abs(determinant) < widthVector * heightVector * 1e-8)
            throw new CadFault("IMAGE_REGISTRATION_SINGULAR", "Control points collapse the image into a line");
        double sum = 0, max = 0;
        foreach (var p in pairs)
        {
            double dx = x[0] + x[1] * p.px + x[2] * p.py - p.x;
            double dy = y[0] + y[1] * p.px + y[2] * p.py - p.y;
            double error = Math.Sqrt(dx * dx + dy * dy); sum += error * error; max = Math.Max(max, error);
        }
        return new(x, y, z, Math.Sqrt(sum / pairs.Count), max, pairs.Count);
    }

    private static double Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) && double.IsFinite(n)
            ? n : throw new CadFault("INVALID_CONTROL_POINTS", "Pixel coordinates must be finite numbers");

    private static double[] Solve(double[,] matrix, double[] rhs)
    {
        var a = (double[,])matrix.Clone(); var b = (double[])rhs.Clone();
        for (int c = 0; c < 3; c++)
        {
            int pivot = c;
            for (int row = c + 1; row < 3; row++) if (Math.Abs(a[row, c]) > Math.Abs(a[pivot, c])) pivot = row;
            if (Math.Abs(a[pivot, c]) < 1e-10) throw new CadFault("IMAGE_CONTROL_POINTS_COLLINEAR", "Control points must not lie on one image line");
            for (int j = c; j < 3; j++) (a[c, j], a[pivot, j]) = (a[pivot, j], a[c, j]);
            (b[c], b[pivot]) = (b[pivot], b[c]);
            double d = a[c, c];
            for (int j = c; j < 3; j++) a[c, j] /= d;
            b[c] /= d;
            for (int row = 0; row < 3; row++)
            {
                if (row == c) continue;
                double factor = a[row, c];
                for (int j = c; j < 3; j++) a[row, j] -= factor * a[c, j];
                b[row] -= factor * b[c];
            }
        }
        return b;
    }
}
