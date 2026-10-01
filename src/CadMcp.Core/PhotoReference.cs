using System.Text.Json;

namespace CadMcp.Core;

/// <summary>A calibrated plane in a photograph. This does not infer hidden 3D geometry.</summary>
public sealed class PhotoReference
{
    public double[] Matrix { get; }
    public double Z { get; }
    public double RmsError { get; }
    public double MaxError { get; }
    public int Width { get; }
    public int Height { get; }
    public string Model { get; }
    private PhotoReference(double[] matrix, double z, double rms, double max, int width, int height, string model)
    { Matrix = matrix; Z = z; RmsError = rms; MaxError = max; Width = width; Height = height; Model = model; }
    private static double N(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n)
        ? n : throw new CadFault("INVALID_REFERENCE", "Reference coordinates must be finite");
    public static PhotoReference Fit(JsonElement controls, int width, int height, string model, bool invertPixelY = true)
    {
        if (width is < 1 or > 100000 || height is < 1 or > 100000) throw new CadFault("INVALID_IMAGE_SIZE", "Invalid reference pixel dimensions");
        if (model is not ("similarity" or "projective")) throw new CadFault("INVALID_REFERENCE_MODEL", "Use similarity for an orthographic view, projective for a known coplanar surface");
        int required = model == "similarity" ? 2 : 4;
        if (controls.ValueKind != JsonValueKind.Array || controls.GetArrayLength() < required || controls.GetArrayLength() > 20)
            throw new CadFault("INVALID_REFERENCE", $"Supply {required}..20 known pixel/world anchors");
        var pairs = controls.EnumerateArray().Select(p => {
            var px = p.GetProperty("pixel"); if (px.ValueKind != JsonValueKind.Array || px.GetArrayLength() != 2) throw new CadFault("INVALID_REFERENCE", "pixel:[x,y] required");
            var w = EditPlan.Point(p.GetProperty("world")); double x = N(px[0]), y = N(px[1]);
            if (x < 0 || x > width || y < 0 || y > height) throw new CadFault("INVALID_REFERENCE", "Pixel anchor is outside the image");
            return (px: x, py: y, x: w[0], y: w[1], z: w[2]);
        }).ToArray();
        double z = pairs[0].z;
        if (pairs.Any(p => Math.Abs(p.z - z) > 1e-8)) throw new CadFault("NONPLANAR_REFERENCE", "All anchors must lie on one known world XY plane");
        double pcx = width / 2d, pcy = height / 2d, ps = Math.Max(width, height);
        double wx = pairs.Average(p => p.x), wy = pairs.Average(p => p.y);
        double ws = Math.Sqrt(pairs.Average(p => (p.x - wx) * (p.x - wx) + (p.y - wy) * (p.y - wy)));
        if (ws < 1e-10) throw new CadFault("REFERENCE_SINGULAR", "World anchors must differ");
        int size = model == "similarity" ? 4 : 8;
        var a = new double[size, size]; var b = new double[size];
        void Row(double[] row, double value)
        { for (int i = 0; i < size; i++) { b[i] += row[i] * value; for (int j = 0; j < size; j++) a[i,j] += row[i] * row[j]; } }
        foreach (var p in pairs)
        {
            double u = (p.px - pcx) / ps, v = (p.py - pcy) / ps * (model == "similarity" && invertPixelY ? -1 : 1), x = (p.x - wx) / ws, y = (p.y - wy) / ws;
            if (model == "similarity") { Row([u, -v, 1, 0], x); Row([v, u, 0, 1], y); }
            else { Row([u, v, 1, 0, 0, 0, -x*u, -x*v], x); Row([0, 0, 0, u, v, 1, -y*u, -y*v], y); }
        }
        var h = Solve(a, b);
        double[] norm = model == "similarity" ? [h[0], -h[1], h[2], h[1], h[0], h[3], 0, 0, 1]
            : [h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1];
        double ySign=model=="similarity"&&invertPixelY?-1:1;
        var matrix = Multiply([ws,0,wx, 0,ws,wy, 0,0,1], Multiply(norm, [1/ps,0,-pcx/ps, 0,ySign/ps,-ySign*pcy/ps, 0,0,1]));
        Inverse(matrix);
        var denominators = new[] { matrix[8], matrix[6]*width+matrix[8], matrix[7]*height+matrix[8], matrix[6]*width+matrix[7]*height+matrix[8] };
        if (denominators.Min() <= 0 && denominators.Max() >= 0) throw new CadFault("REFERENCE_HORIZON", "Projective horizon crosses the image; use a smaller planar crop");
        var errors = pairs.Select(p => { var w = Transform(matrix, p.px, p.py); return Math.Sqrt((w[0]-p.x)*(w[0]-p.x)+(w[1]-p.y)*(w[1]-p.y)); }).ToArray();
        return new(matrix,z,Math.Sqrt(errors.Average(e=>e*e)),errors.Max(),width,height,model);
    }
    public double[] PixelToWorld(double x, double y)
    {
        if (x < 0 || x > Width || y < 0 || y > Height) throw new CadFault("POINT_OUTSIDE_IMAGE", "Pixel is outside the calibrated image");
        var p = Transform(Matrix,x,y); return [p[0],p[1],Z];
    }
    public double[] WorldToPixel(double x, double y) => Transform(Inverse(Matrix),x,y);
    private static double[] Transform(double[] m,double x,double y)
    {
        double d=m[6]*x+m[7]*y+m[8]; if(Math.Abs(d)<1e-12) throw new CadFault("REFERENCE_HORIZON","Point lies on the projective horizon");
        var p=new[]{(m[0]*x+m[1]*y+m[2])/d,(m[3]*x+m[4]*y+m[5])/d};
        if(p.Any(v=>!double.IsFinite(v))) throw new CadFault("INVALID_REFERENCE","Mapping overflow"); return p;
    }
    private static double[] Multiply(double[] a,double[] b) => Enumerable.Range(0,9).Select(i=>Enumerable.Range(0,3).Sum(k=>a[(i/3)*3+k]*b[k*3+i%3])).ToArray();
    private static double[] Inverse(double[] a)
    {
        double[] c=[a[4]*a[8]-a[5]*a[7],a[2]*a[7]-a[1]*a[8],a[1]*a[5]-a[2]*a[4],
            a[5]*a[6]-a[3]*a[8],a[0]*a[8]-a[2]*a[6],a[2]*a[3]-a[0]*a[5],
            a[3]*a[7]-a[4]*a[6],a[1]*a[6]-a[0]*a[7],a[0]*a[4]-a[1]*a[3]];
        double d=a[0]*c[0]+a[1]*c[3]+a[2]*c[6];
        if(Math.Abs(d)<1e-20) throw new CadFault("REFERENCE_SINGULAR","Reference has no inverse"); return c.Select(v=>v/d).ToArray();
    }
    private static double[] Solve(double[,] a,double[] b)
    {
        int n=b.Length;
        for(int c=0;c<n;c++)
        {
            int p=c; for(int r=c+1;r<n;r++) if(Math.Abs(a[r,c])>Math.Abs(a[p,c])) p=r;
            if(Math.Abs(a[p,c])<1e-11) throw new CadFault("REFERENCE_SINGULAR","Anchors are degenerate or poorly conditioned; add well-spaced known points");
            for(int j=c;j<n;j++) (a[c,j],a[p,j])=(a[p,j],a[c,j]); (b[c],b[p])=(b[p],b[c]);
            double d=a[c,c]; for(int j=c;j<n;j++) a[c,j]/=d; b[c]/=d;
            for(int r=0;r<n;r++) if(r!=c) { d=a[r,c]; for(int j=c;j<n;j++) a[r,j]-=d*a[c,j]; b[r]-=d*b[c]; }
        }
        return b;
    }
}

public static class ReferenceComparison
{
    public static object Compare(JsonElement reference, JsonElement rendered, double minimumIou, double maximumDeviation)
    {
        if (!double.IsFinite(minimumIou) || minimumIou < 0 || minimumIou > 1 || !double.IsFinite(maximumDeviation) || maximumDeviation < 0 || maximumDeviation > 1)
            throw new CadFault("INVALID_COMPARISON", "Use IoU and deviation thresholds in 0..1");
        double[][] Polygon(JsonElement data)
        {
            if(data.ValueKind!=JsonValueKind.Array || data.GetArrayLength() is <3 or >1000) throw new CadFault("INVALID_COMPARISON","Supply 3..1000 normalized silhouette points in comparable views");
            return data.EnumerateArray().Select(p=> { var xy=EditPlan.Point(p); if(xy[0]<0||xy[0]>1||xy[1]<0||xy[1]>1||xy[2]!=0) throw new CadFault("INVALID_COMPARISON","Silhouette coordinates must be normalized [0..1,0..1]"); return xy; }).ToArray();
        }
        var a=Polygon(reference); var b=Polygon(rendered);
        bool Inside(double[][] p,double x,double y) { bool hit=false; for(int i=0,j=p.Length-1;i<p.Length;j=i++) if((p[i][1]>y)!=(p[j][1]>y) && x<(p[j][0]-p[i][0])*(y-p[i][1])/(p[j][1]-p[i][1])+p[i][0]) hit=!hit; return hit; }
        int intersection=0,union=0;
        const int grid=256;
        for(int y=0;y<grid;y++) for(int x=0;x<grid;x++) { bool pa=Inside(a,(x+.5)/grid,(y+.5)/grid),pb=Inside(b,(x+.5)/grid,(y+.5)/grid); if(pa&&pb)intersection++; if(pa||pb)union++; }
        if(union==0) throw new CadFault("INVALID_COMPARISON","Silhouette area is empty at comparison resolution");
        double Segment(double[] p,double[] u,double[] v) { double dx=v[0]-u[0],dy=v[1]-u[1],d=dx*dx+dy*dy; double t=d==0?0:Math.Clamp(((p[0]-u[0])*dx+(p[1]-u[1])*dy)/d,0,1); return Math.Sqrt(Math.Pow(p[0]-u[0]-t*dx,2)+Math.Pow(p[1]-u[1]-t*dy,2)); }
        double Deviation(double[][] from,double[][] to) => Enumerable.Range(0,from.Length).SelectMany(i=>Enumerable.Range(0,9).Select(s=>new[]{
            from[i][0]+(from[(i+1)%from.Length][0]-from[i][0])*s/8d,from[i][1]+(from[(i+1)%from.Length][1]-from[i][1])*s/8d}))
            .Max(p=>Enumerable.Range(0,to.Length).Min(i=>Segment(p,to[i],to[(i+1)%to.Length])));
        double iou=(double)intersection/union, deviation=Math.Max(Deviation(a,b),Deviation(b,a));
        return new { state=iou>=minimumIou&&deviation<=maximumDeviation?"passed":"failed", silhouette_iou=iou,
            maximum_sampled_boundary_deviation=deviation, minimum_iou=minimumIou, maximum_allowed_deviation=maximumDeviation,
            grid_resolution=grid, evidence="agent_supplied_contours_in_comparable_views",
            limitations=new[]{"contours_must_be_checked_against_actual_images","silhouette_does_not_verify_surface_detail_or_hidden_geometry","no_automatic_3D_reconstruction"} };
    }
}
