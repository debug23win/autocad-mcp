using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
namespace CadMcp.AutoCAD;
internal static class StructuralAssemblies
{
    private const string Key="CADMCP_ASSEMBLY_V1";
    internal sealed record Recipe(string Kind,JsonElement Parameters,string Mark,string Material,double Density,bool Annotations,int GeometryVersion=1);
    private static double P(JsonElement p,string name,double? fallback=null)
    {double n=EditPlan.Numeric(p,name,fallback);if(n<=0||n>1e8)throw new CadFault("INVALID_ASSEMBLY",name+" must be positive drawing units");return n;}
    internal static BlockReference Create(Database db,Transaction tr,JsonElement op)
    {
        var recipe=new Recipe(op.Text("kind")!,op.GetProperty("parameters").Clone(),op.Text("mark")!,op.Text("material")!,EditPlan.Numeric(op,"density",7850),op.TryGetProperty("show_annotations",out var a)?a.GetBoolean():true,2);
        if(recipe.Density<=0)throw new CadFault("INVALID_ASSEMBLY","Positive density in kg/m3 required");
        var blocks=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
        var definition=new BlockTableRecord{Name="CADMCP_ASM_"+Guid.NewGuid().ToString("N")};blocks.Add(definition);tr.AddNewlyCreatedDBObject(definition,true);
        Build(db,tr,definition,recipe);Store(definition,tr,recipe);
        var p=EditPlan.Point(op.GetProperty("position"));return new BlockReference(new(p[0],p[1],p[2]),definition.ObjectId);
    }
    internal static object Update(Database db,Transaction tr,BlockReference reference,JsonElement op)
    {
        var definition=(BlockTableRecord)tr.GetObject(reference.BlockTableRecord,OpenMode.ForWrite);var old=Load(definition,tr)??throw new CadFault("NOT_PARAMETRIC_ASSEMBLY","Select an assembly created by CAD MCP");
        if(definition.GetBlockReferenceIds(true,false).Count>1)throw new CadFault("SHARED_ASSEMBLY","This definition has multiple instances; updating it would change all. Make it unique first.");
        var recipe=old with{Parameters=op.GetProperty("parameters").Clone(),Mark=op.Text("mark")??old.Mark,Material=op.Text("material")??old.Material,Density=EditPlan.Numeric(op,"density",old.Density),Annotations=op.TryGetProperty("show_annotations",out var a)?a.GetBoolean():old.Annotations,GeometryVersion=2};
        foreach(ObjectId id in definition){var entity=(Entity)tr.GetObject(id,OpenMode.ForWrite);entity.Erase();}
        Build(db,tr,definition,recipe);Store(definition,tr,recipe);reference.RecordGraphicsModified(true);
        return Inspect(db,tr,reference);
    }
    private static void Build(Database db,Transaction tr,BlockTableRecord definition,Recipe recipe)
    {
        var p=recipe.Parameters;double l=1,w=1,h=1;var created=new List<Entity>();
        void Add(Entity entity){try{entity.SetDatabaseDefaults(db);definition.AppendEntity(entity);tr.AddNewlyCreatedDBObject(entity,true);created.Add(entity);}catch{if(entity.ObjectId.IsNull)entity.Dispose();throw;}}
        void Box(double x,double y,double z,Point3d center){var solid=new Solid3d();try{solid.CreateBox(x,y,z);solid.TransformBy(Matrix3d.Displacement(center-Point3d.Origin));Add(solid);}catch{if(solid.ObjectId.IsNull)solid.Dispose();throw;}}
        void Bar(Point3d start,Point3d end,double radius)
        {var axis=end-start;if(axis.Length<1e-9)throw new CadFault("INVALID_ASSEMBLY","Bar endpoints coincide");var solid=new Solid3d();try{solid.CreateFrustum(axis.Length,radius,radius,radius);solid.TransformBy(Matrix3d.AlignCoordinateSystem(Point3d.Origin,Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis,Point3d.Origin,axis.GetNormal().GetPerpendicularVector(),axis.GetNormal().CrossProduct(axis.GetNormal().GetPerpendicularVector()),axis.GetNormal()));solid.TransformBy(Matrix3d.Displacement((start+(axis*.5))-Point3d.Origin));Add(solid);}catch{if(solid.ObjectId.IsNull)solid.Dispose();throw;}}
        switch(recipe.Kind)
        {
            case "beam":case "column":
                l=P(p,"length");
                if(p.Text("profile") is { } code)
                {
                    var section=SteelSections.Get(code);double scale=.001/SteelSections.MetersPerUnit(db.Insunits.ToString());
                    if(new[]{"width","height","web_thickness","flange_thickness","diameter","wall_thickness","root_radius"}.Any(name=>p.TryGetProperty(name,out _)))throw new CadFault("PROFILE_DIMENSION_CONFLICT","Use catalog profile or explicit dimensions, not both");
                    w=section.WidthMm*scale;h=section.HeightMm*scale;
                    if(section.Shape=="pipe"){var pipe=StructuralSolids.Tube(l,h,section.WebMm*scale);pipe.TransformBy(Matrix3d.Rotation(Math.PI/2,Vector3d.YAxis,Point3d.Origin));Add(pipe);}
                    else Add(StructuralSolids.IBeam(l,w,h,section.WebMm*scale,section.FlangeMm*scale,section.RootRadiusMm*scale));
                }
                else
                {
                    w=P(p,"width");h=P(p,"height");double tw=P(p,"web_thickness"),tf=P(p,"flange_thickness");
                    if(tw>=w||2*tf>=h)throw new CadFault("INVALID_PROFILE","Web/flanges exceed section size");
                    Add(StructuralSolids.IBeam(l,w,h,tw,tf,EditPlan.Numeric(p,"root_radius",0)));
                }
                if(recipe.Kind=="column")foreach(var e in created)e.TransformBy(Matrix3d.Rotation(-Math.PI/2,Vector3d.YAxis,Point3d.Origin));break;
            case "screw_pile":
                l=P(p,"length");double d=P(p,"diameter"),wall=P(p,"wall_thickness"),blade=P(p,"blade_diameter"),pitch=P(p,"pitch"),bt=P(p,"blade_thickness");
                if(wall*2>=d||blade<=d||pitch+bt>l||bt>=pitch)throw new CadFault("INVALID_PILE","Invalid shaft/blade dimensions");
                Add(StructuralSolids.Tube(l,d,wall));Add(StructuralSolids.HelicalBlade(d,blade,pitch,bt));
                w=h=blade;break;
            case "stairs":
                l=P(p,"run");h=P(p,"rise");w=P(p,"width");int count=DraftingPlan.Integer(p,"steps",1,200,0);double tread=P(p,"tread_thickness"),stringer=P(p,"stringer_diameter");
                for(int n=0;n<count;n++)Box(l/count,w,tread,new((n+.5)*l/count,0,(n+1)*h/count-tread/2));
                Bar(new(0,-w/2,0),new(l,-w/2,h),stringer/2);Bar(new(0,w/2,0),new(l,w/2,h),stringer/2);break;
            case "railing":
                l=P(p,"length");h=P(p,"height");double spacing=P(p,"post_spacing"),rd=P(p,"diameter");int posts=(int)Math.Ceiling(l/spacing)+1;
                if(posts>200)throw new CadFault("ASSEMBLY_TOO_LARGE","At most 200 posts");
                for(int n=0;n<posts;n++)Bar(new(n*l/(posts-1),0,0),new(n*l/(posts-1),0,h),rd/2);
                Bar(new(0,0,h),new(l,0,h),rd/2);Bar(new(0,0,h*.5),new(l,0,h*.5),rd/2);break;
            case "rebar":
                double radius=P(p,"diameter")/2;var path=p.GetProperty("points").EnumerateArray().Select(v=>{var a=EditPlan.Point(v);return new Point3d(a[0],a[1],a[2]);}).ToArray();
                if(path.Length is <2 or >100)throw new CadFault("INVALID_REBAR","2..100 path points required");
                double bendRadius=EditPlan.Numeric(p,"bend_radius",radius*4);
                var rounded=BendPath.Create(path.Select(v=>new[]{v.X,v.Y,v.Z}).ToArray(),bendRadius);
                foreach(var line in rounded.Lines)Bar(new(line.Start[0],line.Start[1],line.Start[2]),new(line.End[0],line.End[1],line.End[2]),radius);
                foreach(var bend in rounded.Bends)Add(StructuralSolids.Bend(bend,radius));
                l=rounded.Length;w=h=radius*2;break;
            case "steel_joint":
                l=P(p,"plate_length");w=P(p,"plate_width");h=P(p,"plate_thickness");double hole=P(p,"hole_diameter");
                var plate=new Solid3d();try{plate.CreateBox(l,w,h);plate.TransformBy(Matrix3d.Displacement(new(l/2,0,h/2)));
                    foreach(var point in p.GetProperty("holes").EnumerateArray()){var a=EditPlan.Point(point);if(a[0]<=hole/2||a[0]>=l-hole/2||Math.Abs(a[1])>=w/2-hole/2)throw new CadFault("INVALID_HOLE","Bolt hole outside plate");using var cut=new Solid3d();cut.CreateFrustum(h*2,hole/2,hole/2,hole/2);cut.TransformBy(Matrix3d.Displacement(new(a[0],a[1],h/2)));plate.BooleanOperation(BooleanOperationType.BoolSubtract,cut);}Add(plate);}catch{if(plate.ObjectId.IsNull)plate.Dispose();throw;}break;
            default:throw new CadFault("ASSEMBLY_KIND_UNSUPPORTED","Use beam,column,screw_pile,stairs,railing,rebar,steel_joint");
        }
        if(recipe.Annotations)
        {
            double textHeight=Math.Max(Math.Min(l,h)/20,.01);
            Add(new DBText{Position=new(0,-w*.65,0),Height=textHeight,TextString=recipe.Mark+" · "+recipe.Material});
            if(recipe.Kind is "beam" or "stairs" or "railing")
            {var dim=new AlignedDimension(Point3d.Origin,new(l,0,0),new(l/2,-w,0),"",db.Dimstyle);Add(dim);dim.RecomputeDimensionBlock(true);}
        }
    }
    private static void Store(BlockTableRecord definition,Transaction tr,Recipe recipe)
    {
        if(definition.ExtensionDictionary.IsNull)definition.CreateExtensionDictionary();var dictionary=(DBDictionary)tr.GetObject(definition.ExtensionDictionary,OpenMode.ForWrite);Xrecord record;
        if(dictionary.Contains(Key))record=(Xrecord)tr.GetObject(dictionary.GetAt(Key),OpenMode.ForWrite);else{record=new();dictionary.SetAt(Key,record);tr.AddNewlyCreatedDBObject(record,true);}
        string json=JsonSerializer.Serialize(recipe,Wire.Json);using var data=new ResultBuffer(Enumerable.Range(0,(json.Length+999)/1000).Select(n=>new TypedValue((int)DxfCode.Text,json.Substring(n*1000,Math.Min(1000,json.Length-n*1000)))).ToArray());record.Data=data;
    }
    internal static Recipe? Load(BlockTableRecord definition,Transaction tr)
    {
        if(definition.ExtensionDictionary.IsNull)return null;var dictionary=(DBDictionary)tr.GetObject(definition.ExtensionDictionary,OpenMode.ForRead);if(!dictionary.Contains(Key))return null;
        using var data=((Xrecord)tr.GetObject(dictionary.GetAt(Key),OpenMode.ForRead)).Data;return JsonSerializer.Deserialize<Recipe>(string.Concat(data.AsArray().Select(v=>(string)v.Value)),Wire.Json);
    }
    internal static object Inspect(Database db,Transaction tr,BlockReference reference)
    {
        var definition=(BlockTableRecord)tr.GetObject(reference.BlockTableRecord,OpenMode.ForRead);var recipe=Load(definition,tr)??throw new CadFault("NOT_PARAMETRIC_ASSEMBLY","No assembly recipe");
        var solids=definition.Cast<ObjectId>().Where(id=>!id.IsErased).Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Solid3d>().ToArray();
        double meters=db.Insunits switch{UnitsValue.Millimeters=>.001,UnitsValue.Centimeters=>.01,UnitsValue.Meters=>1,UnitsValue.Inches=>.0254,UnitsValue.Feet=>.3048,_=>throw new CadFault("UNITS_REQUIRED","Define drawing units before assembly mass")};
        double volume=solids.Sum(s=>s.MassProperties.Volume)*Math.Abs(reference.ScaleFactors.X*reference.ScaleFactors.Y*reference.ScaleFactors.Z)*Math.Pow(meters,3);
        double? centerline=null;
        if(recipe.Kind=="rebar")
        {
            var points=recipe.Parameters.GetProperty("points").EnumerateArray().Select(EditPlan.Point).ToArray();
            centerline=recipe.GeometryVersion>=2?BendPath.Create(points,EditPlan.Numeric(recipe.Parameters,"bend_radius",P(recipe.Parameters,"diameter")*2)).Length
                :points.Skip(1).Zip(points,(b,a)=>Math.Sqrt(b.Zip(a,(x,y)=>(x-y)*(x-y)).Sum())).Sum();
        }
        return new{handle=reference.Handle.ToString(),recipe,solid_volume_m3=volume,solid_mass_kg=volume*recipe.Density,
            centerline_length=centerline,profile_code=recipe.Parameters.Text("profile"),
            mass_scope=recipe.GeometryVersion>=2?"all native solid components; rounded tangent rebar bends included; helical blade is a 48-segment ruled solid":"legacy recipe: native solids only, mesh blade excluded; straight rebar parts may overlap at corners. assembly_update rebuilds to geometry version 2",
            design="Geometry and schedule only; bearing capacity and joint design require calculation"};
    }
    private const string ScheduleKey="CADMCP_ASSEMBLY_SCHEDULE_V1";
    internal static void RegisterSchedule(Table table,Transaction tr,JsonElement op)
    {
        table.CreateExtensionDictionary();var dictionary=(DBDictionary)tr.GetObject(table.ExtensionDictionary,OpenMode.ForWrite);var record=new Xrecord();dictionary.SetAt(ScheduleKey,record);tr.AddNewlyCreatedDBObject(record,true);
        string json=op.GetProperty("handles").GetRawText();using var data=new ResultBuffer(Enumerable.Range(0,(json.Length+999)/1000).Select(n=>new TypedValue((int)DxfCode.Text,json.Substring(n*1000,Math.Min(1000,json.Length-n*1000)))).ToArray());record.Data=data;
    }
    internal static void RefreshSchedules(Database db,Transaction tr)
    {
        var blocks=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
        foreach(ObjectId bid in blocks)
        {
            var block=(BlockTableRecord)tr.GetObject(bid,OpenMode.ForRead);if(!block.IsLayout)continue;
            foreach(ObjectId id in block)
            {
                if(id.IsErased||tr.GetObject(id,OpenMode.ForRead) is not Table table||table.ExtensionDictionary.IsNull)continue;
                var dictionary=(DBDictionary)tr.GetObject(table.ExtensionDictionary,OpenMode.ForRead);if(!dictionary.Contains(ScheduleKey))continue;
                using var data=((Xrecord)tr.GetObject(dictionary.GetAt(ScheduleKey),OpenMode.ForRead)).Data;
                var handles=JsonSerializer.Deserialize<string[]>(string.Concat(data.AsArray().Select(v=>(string)v.Value)))!;
                if(table.Rows.Count!=handles.Length+2||table.Columns.Count!=6)throw new CadFault("SCHEDULE_STRUCTURE_CHANGED","Generated schedule structure changed; recreate it to avoid overwriting unrelated cells");
                for(int n=0;n<handles.Length;n++)
                {
                    if(!long.TryParse(handles[n],System.Globalization.NumberStyles.HexNumber,null,out var h)||!db.TryGetObjectId(new Handle(h),out var sourceId))throw new CadFault("SCHEDULE_SOURCE_MISSING",handles[n]);
                    var reference=(BlockReference)tr.GetObject(sourceId,OpenMode.ForRead,true);var info=Wire.Element(Inspect(db,tr,reference));var recipe=info.GetProperty("recipe");
                    object[] values=[recipe.Text("mark")!,recipe.Text("kind")!,recipe.Text("material")!,reference.IsErased?0:1,reference.IsErased?0d:info.GetProperty("solid_mass_kg").GetDouble(),reference.IsErased?"Элемент удалён":info.Text("mass_scope")!];
                    for(int c=0;c<values.Length;c++)
                    {var cell=table.Cells[n+2,c];bool equal=Equals(cell.Value,values[c]) || values[c] is int or double && double.TryParse(Convert.ToString(cell.Value,System.Globalization.CultureInfo.InvariantCulture),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var oldValue) && oldValue==Convert.ToDouble(values[c]);if(!equal){if(!table.IsWriteEnabled)table.UpgradeOpen();cell.Value=values[c];}}
                }
                if(table.IsWriteEnabled){table.GenerateLayout();table.RecomputeTableBlock(true);}
            }
        }
    }
    internal static Table Schedule(Database db,Transaction tr,JsonElement op)
    {
        var rows=op.GetProperty("handles").EnumerateArray().Select(h=>Wire.Element(Inspect(db,tr,(BlockReference)tr.GetObject(NativeTables.Resolve(db,h.GetString()!),OpenMode.ForRead)))).ToArray();
        var data=rows.Select(r=>new object[]{r.GetProperty("recipe").Text("mark")!,r.GetProperty("recipe").Text("kind")!,r.GetProperty("recipe").Text("material")!,1,r.GetProperty("solid_mass_kg").GetDouble(),r.GetProperty("mass_scope").GetString()!}).ToArray();
        return NativeTables.Template(db,tr,Wire.Element(new{template="specification",position=op.GetProperty("position"),title=op.Text("title")??"Ведомость параметрических элементов",data}));
    }
}
