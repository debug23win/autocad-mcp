using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.BoundaryRepresentation;
using CadMcp.Core;
using App=Autodesk.AutoCAD.ApplicationServices.Core.Application;
namespace CadMcp.AutoCAD;
internal static class SolidModeling
{
    internal static object Inspect(Solid3d solid)
    {
        using var brep=new Brep(new FullSubentityPath(new[]{solid.ObjectId},new SubentityId(SubentityType.Null,IntPtr.Zero)));var edges=new List<object>();var faces=new List<object>();
        foreach(var edge in brep.Edges){using(edge){using var curve=solid.CopyEdge(edge.SubentityPath.SubentId);edges.Add(new{index=edge.SubentityPath.SubentId.IndexPtr.ToInt64(),type=curve.GetType().Name,bounds=Bounds(curve)});if(edges.Count>=1000)break;}}
        foreach(var face in brep.Faces){using(face){using var entity=solid.CopyFace(face.SubentityPath.SubentId);faces.Add(new{index=face.SubentityPath.SubentId.IndexPtr.ToInt64(),type=entity.GetType().Name,bounds=Bounds(entity)});if(faces.Count>=1000)break;}}
        return new{handle=solid.Handle.ToString(),volume=solid.MassProperties.Volume,edges,faces,validity="Ids valid only at this revision; refresh after topology changes"};
    }
    private static object? Bounds(Entity e){try{var x=e.GeometricExtents;return new{min=new[]{x.MinPoint.X,x.MinPoint.Y,x.MinPoint.Z},max=new[]{x.MaxPoint.X,x.MaxPoint.Y,x.MaxPoint.Z}};}catch{return null;}}
    private static SubentityId[] Ids(Solid3d solid,JsonElement op,string field,SubentityType type)
    {
        using var brep=new Brep(new FullSubentityPath(new[]{solid.ObjectId},new SubentityId(SubentityType.Null,IntPtr.Zero)));var valid=new HashSet<long>();
        if(type==SubentityType.Edge)foreach(var e in brep.Edges){using(e)valid.Add(e.SubentityPath.SubentId.IndexPtr.ToInt64());}
        else foreach(var f in brep.Faces){using(f)valid.Add(f.SubentityPath.SubentId.IndexPtr.ToInt64());}
        var ids=op.GetProperty(field).EnumerateArray().Select(v=>v.GetInt64()).ToArray();
        if(ids.Distinct().Count()!=ids.Length||ids.Any(i=>!valid.Contains(i)))throw new CadFault("SUBENTITY_STALE","Unknown/duplicate edge or face id; refresh cad_solid_get");
        return ids.Select(i=>new SubentityId(type,new IntPtr(i))).ToArray();
    }
    internal static Entity Execute(Database db,Transaction tr,JsonElement op,Dictionary<string,ObjectId> aliases)
    {
        string kind=op.Text("op")!;
        if(kind=="solid_loft")
        {
            var clones=new List<Entity>();
            Entity Get(JsonElement target){var id=Edits.Resolve(db,tr,target,aliases);var source=tr.GetObject(id,OpenMode.ForRead) as Curve??throw new CadFault("INVALID_LOFT","Curve required");var clone=(Entity)source.Clone();clones.Add(clone);return clone;}
            var solid=new Solid3d();
            try
            {
                var sections=op.GetProperty("sections").EnumerateArray().Select(Get).ToArray();
                if(sections.Any(e=>e is Curve c&&!c.Closed))throw new CadFault("INVALID_LOFT","Solid sections must be closed curves");
                var guides=op.TryGetProperty("guides",out var gs)?gs.EnumerateArray().Select(Get).ToArray():[];
                var path=op.TryGetProperty("path",out var p)?Get(p):null;
                if(guides.Length>0&&path is not null)throw new CadFault("INVALID_LOFT","Use guide curves or path, not both");
                var builder=new LoftOptionsBuilder{Ruled=op.TryGetProperty("ruled",out var ruled)&&ruled.GetBoolean()};
                using var options=builder.ToLoftOptions();solid.CreateLoftedSolid(sections,guides,path,options);return solid;
            }
            catch{solid.Dispose();throw;}
            finally{foreach(var clone in clones)clone.Dispose();}
        }
        var id=Edits.Resolve(db,tr,op,aliases);var target=Edits.Editable(db,tr,id,kind!="solid_section") as Solid3d??throw new CadFault("INVALID_SOLID","Native Solid3d required");
        if(kind=="solid_section")
        {
            var origin=EditPlan.Point(op.GetProperty("origin"));var normal=EditPlan.Point(op.GetProperty("normal"));var vector=new Vector3d(normal[0],normal[1],normal[2]);
            if(vector.Length<1e-10)throw new CadFault("INVALID_PLANE","Nonzero normal required");
            using var plane=new Plane(new Point3d(origin[0],origin[1],origin[2]),vector);
            return target.GetSection(plane)??throw new CadFault("NO_SECTION","Plane does not intersect solid");
        }
        object prior=App.GetSystemVariable("SOLIDCHECK");
        try
        {
            App.SetSystemVariable("SOLIDCHECK",1);
            if(kind=="solid_fillet")
            {
                var edges=Ids(target,op,"edges",SubentityType.Edge);if(edges.Length==0)throw new CadFault("INVALID_EDGES","Choose at least one edge");
                target.FilletEdges(edges,new DoubleCollection(Enumerable.Repeat(EditPlan.Numeric(op,"radius"),edges.Length).ToArray()),new DoubleCollection(new double[edges.Length]),new DoubleCollection(new double[edges.Length]));
            }
            else if(kind=="solid_chamfer")
            {
                var edges=Ids(target,op,"edges",SubentityType.Edge);var baseFace=Ids(target,Wire.Element(new{faces=new[]{op.GetProperty("base_face").GetInt64()}}),"faces",SubentityType.Face).Single();
                target.ChamferEdges(edges,baseFace,EditPlan.Numeric(op,"base_distance"),EditPlan.Numeric(op,"other_distance"));
            }
            else target.ShellBody(Ids(target,op,"faces",SubentityType.Face),EditPlan.Numeric(op,"offset"));
            _=target.MassProperties.Volume;return target;
        }
        finally{App.SetSystemVariable("SOLIDCHECK",prior);}
    }
}
