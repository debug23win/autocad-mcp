namespace CadMcp.Core;

public sealed record PathBend(double[] Start,double[] End,double[] Center,double[] Normal,double Radius,double Angle);
public sealed record RoundedPath(IReadOnlyList<(double[] Start,double[] End)> Lines,IReadOnlyList<PathBend> Bends,double Length);

/// <summary>Circular centerline fillets; validates all tangency setbacks before invoking the native kernel.</summary>
public static class BendPath
{
    private static double[] Sub(double[] a,double[] b)=>a.Zip(b,(x,y)=>x-y).ToArray();
    private static double[] Add(double[] a,double[] b)=>a.Zip(b,(x,y)=>x+y).ToArray();
    private static double[] Scale(double[] a,double k)=>a.Select(x=>x*k).ToArray();
    private static double Dot(double[] a,double[] b)=>a.Zip(b,(x,y)=>x*y).Sum();
    private static double Len(double[] a)=>Math.Sqrt(Dot(a,a));
    public static RoundedPath Create(IReadOnlyList<double[]> points,double radius)
    {
        if(points.Count is <2 or >100 || !double.IsFinite(radius)||radius<=0 || points.Any(p=>p.Length!=3||p.Any(v=>!double.IsFinite(v))))throw new CadFault("INVALID_REBAR","2..100 finite XYZ points and positive bend_radius required");
        var lengths=points.Zip(points.Skip(1),(a,b)=>Len(Sub(b,a))).ToArray();
        if(lengths.Any(l=>l<1e-8))throw new CadFault("INVALID_REBAR","Repeated centerline points");
        var starts=points.Take(points.Count-1).Select(p=>p.ToArray()).ToArray();var ends=points.Skip(1).Select(p=>p.ToArray()).ToArray();
        var setbacks=new double[points.Count];var bends=new List<PathBend>();
        for(int i=1;i<points.Count-1;i++)
        {
            var incoming=Scale(Sub(points[i],points[i-1]),1/lengths[i-1]);var outgoing=Scale(Sub(points[i+1],points[i]),1/lengths[i]);
            double angle=Math.Acos(Math.Clamp(Dot(incoming,outgoing),-1,1));
            if(angle<1e-8)continue;
            if(Math.PI-angle<1e-6)throw new CadFault("INVALID_REBAR","A reversal needs explicit intermediate bend points");
            double setback=radius*Math.Tan(angle/2);setbacks[i]=setback;
            double[] normal=[incoming[1]*outgoing[2]-incoming[2]*outgoing[1],incoming[2]*outgoing[0]-incoming[0]*outgoing[2],incoming[0]*outgoing[1]-incoming[1]*outgoing[0]];normal=Scale(normal,1/Len(normal));
            double[] inward=[normal[1]*incoming[2]-normal[2]*incoming[1],normal[2]*incoming[0]-normal[0]*incoming[2],normal[0]*incoming[1]-normal[1]*incoming[0]];
            var start=Add(points[i],Scale(incoming,-setback));var end=Add(points[i],Scale(outgoing,setback));
            ends[i-1]=start;starts[i]=end;bends.Add(new(start,end,Add(start,Scale(inward,radius)),normal,radius,angle));
        }
        for(int i=0;i<lengths.Length;i++)if(setbacks[i]+setbacks[i+1]>=lengths[i]-1e-8)throw new CadFault("BEND_DOES_NOT_FIT","Adjacent tangent setbacks exceed a centerline segment");
        var lines=starts.Zip(ends,(s,e)=>(Start:s,End:e)).ToArray();
        double length=lines.Sum(l=>Len(Sub(l.End,l.Start)))+bends.Sum(b=>b.Radius*b.Angle);
        return new(lines,bends,length);
    }
}
