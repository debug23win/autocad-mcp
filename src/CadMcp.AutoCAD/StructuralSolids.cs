using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
using Region=Autodesk.AutoCAD.DatabaseServices.Region;
namespace CadMcp.AutoCAD;

internal static class StructuralSolids
{
    internal static Solid3d IBeam(double length,double width,double height,double web,double flange,double radius)
    {
        if(radius<0 || radius>(width-web)/2 || radius>(height-2*flange)/2)throw new CadFault("INVALID_PROFILE","Root fillets do not fit the section");
        using var section=new Polyline();double b=width/2,s=web/2,r=radius;double arc=Math.Tan(Math.PI/8);
        var points=new List<(double X,double Y,double Bulge)>();
        void V(double x,double y,double bulge=0){points.Add((x,y,bulge));}
        V(-b,0);V(b,0);V(b,flange);
        if(r>0){V(s+r,flange,-arc);V(s,flange+r);V(s,height-flange-r,-arc);V(s+r,height-flange);}else{V(s,flange);V(s,height-flange);}
        V(b,height-flange);V(b,height);V(-b,height);V(-b,height-flange);
        if(r>0){V(-s-r,height-flange,-arc);V(-s,height-flange-r);V(-s,flange+r,-arc);V(-s-r,flange);}else{V(-s,height-flange);V(-s,flange);}
        V(-b,flange);
        for(int n=0;n<points.Count;n++)section.AddVertexAt(n,new(points[n].X,points[n].Y),points[n].Bulge,0,0);
        section.Closed=true;var regions=Region.CreateFromCurves(new DBObjectCollection{section});
        try
        {
            var solid=new Solid3d();try{solid.CreateExtrudedSolid((Region)regions[0],new Vector3d(0,0,length),new SweepOptions());
                solid.TransformBy(Matrix3d.AlignCoordinateSystem(Point3d.Origin,Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis,Point3d.Origin,Vector3d.YAxis,Vector3d.ZAxis,Vector3d.XAxis));return solid;}
            catch{solid.Dispose();throw;}
        }
        finally{foreach(DBObject region in regions)region.Dispose();}
    }
    internal static Solid3d Tube(double length,double diameter,double wall)
    {
        if(wall*2>=diameter)throw new CadFault("INVALID_PROFILE","Tube wall consumes the bore");
        var tube=new Solid3d();try{tube.CreateFrustum(length,diameter/2,diameter/2,diameter/2);using var bore=new Solid3d();bore.CreateFrustum(length*1.01,diameter/2-wall,diameter/2-wall,diameter/2-wall);tube.BooleanOperation(BooleanOperationType.BoolSubtract,bore);tube.TransformBy(Matrix3d.Displacement(new(0,0,length/2)));return tube;}catch{tube.Dispose();throw;}
    }
    internal static Solid3d HelicalBlade(double shaft,double diameter,double pitch,double thickness,int steps=48)
    {
        if(thickness>=pitch || diameter<=shaft)throw new CadFault("INVALID_PILE","Blade thickness must be below pitch and blade diameter above shaft");
        var sections=new List<Entity>();var solid=new Solid3d();
        try
        {
            for(int n=0;n<=steps;n++)
            {
                double angle=2*Math.PI*n/steps,z=pitch*n/steps,c=Math.Cos(angle),s=Math.Sin(angle);
                var profile=new Polyline3d(Poly3dType.SimplePoly,new Point3dCollection(new[]{
                    new Point3d(shaft/2*c,shaft/2*s,z),new Point3d(diameter/2*c,diameter/2*s,z),
                    new Point3d(diameter/2*c,diameter/2*s,z+thickness),new Point3d(shaft/2*c,shaft/2*s,z+thickness)}),true);sections.Add(profile);
            }
            using var options=new LoftOptionsBuilder{Ruled=true}.ToLoftOptions();
            solid.CreateLoftedSolid(sections.ToArray(),[],null,options);
            // Ruled inner faces form chords inside the circular shaft. Trim them to prevent double-counted material.
            using(var shaftCut=new Solid3d())
            {
                shaftCut.CreateFrustum(pitch+thickness+2,shaft/2,shaft/2,shaft/2);
                shaftCut.TransformBy(Matrix3d.Displacement(new(0,0,(pitch+thickness)/2)));
                solid.BooleanOperation(BooleanOperationType.BoolSubtract,shaftCut);
            }
            if(solid.MassProperties.Volume<=0)throw new CadFault("INVALID_PILE","Native blade has no positive volume");return solid;
        }
        catch{solid.Dispose();throw;}
        finally{foreach(var section in sections)section.Dispose();}
    }
    internal static Solid3d Bend(PathBend bend,double barRadius)
    {
        if(bend.Radius<=barRadius)throw new CadFault("INVALID_REBAR","Centerline bend radius must exceed half the bar diameter");
        Point3d P(double[] v)=>new(v[0],v[1],v[2]);var center=P(bend.Center);var normal=new Vector3d(bend.Normal[0],bend.Normal[1],bend.Normal[2]);var x=(P(bend.Start)-center).GetNormal();var y=normal.CrossProduct(x);
        using var path=new Arc(Point3d.Origin,Vector3d.ZAxis,bend.Radius,0,bend.Angle);
        path.TransformBy(Matrix3d.AlignCoordinateSystem(Point3d.Origin,Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis,center,x,y,normal));
        using var profile=new Circle(P(bend.Start),y,barRadius);var solid=new Solid3d();
        try{solid.CreateSweptSolid(profile,path,new SweepOptions());return solid;}catch{solid.Dispose();throw;}
    }
}
