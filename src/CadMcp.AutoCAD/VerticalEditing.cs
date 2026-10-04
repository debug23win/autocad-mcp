using System.Collections;
using System.Reflection;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
namespace CadMcp.AutoCAD;
internal static class VerticalEditing
{
    private static object Get(object instance,string property)=>instance.GetType().GetProperty(property)?.GetValue(instance)??throw new CadFault("VERTICAL_API_UNAVAILABLE",property);
    internal static object? Invoke(object instance,string method,params object?[] args)
    {
        Type type=instance as Type??instance.GetType();var matches=type.GetMethods(BindingFlags.Public|(instance is Type?BindingFlags.Static:BindingFlags.Instance)).Where(m=>m.Name==method&&m.GetParameters().Length==args.Length).Where(m=>m.GetParameters().Select((p,n)=>{var t=p.ParameterType.IsByRef?p.ParameterType.GetElementType()!:p.ParameterType;return args[n] is null?!t.IsValueType:t.IsInstanceOfType(args[n]);}).All(ok=>ok)).ToArray();
        if(matches.Length!=1)throw new CadFault("VERTICAL_API_UNAVAILABLE",type.FullName+"."+method+" has no unambiguous supported signature");
        try{return matches[0].Invoke(instance is Type?null:instance,args);}catch(TargetInvocationException e){throw new CadFault("VERTICAL_API_ERROR",e.InnerException?.Message??e.Message);}
    }
    private static object Index(object instance,object index)=>instance.GetType().GetProperties().FirstOrDefault(p=>p.GetIndexParameters().Length==1&&p.GetIndexParameters()[0].ParameterType.IsInstanceOfType(index))?.GetValue(instance,[index])??throw new CadFault("VERTICAL_INDEX_UNAVAILABLE",instance.GetType().Name);
    private static void Set(object instance,string name,object? value)
    {var p=instance.GetType().GetProperty(name);if(p?.SetMethod is null||(value is not null&&!p.PropertyType.IsInstanceOfType(value)))throw new CadFault("VERTICAL_PROPERTY_UNAVAILABLE",name);p.SetValue(instance,value);}
    private static ObjectId Id(Database db,JsonElement op,string key)=>NativeTables.Resolve(db,EditPlan.RequiredText(op,key));
    private static Point3d P(JsonElement value){var p=EditPlan.Point(value);return new(p[0],p[1],p[2]);}
    private static Point2d XY(JsonElement value){var p=EditPlan.Point(value);return new(p[0],p[1]);}
    internal static object Civil(Database db,Transaction tr,JsonElement op)
    {
        var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="AeccDbMgd")??throw new CadFault("CIVIL3D_REQUIRED","Run this operation inside Civil 3D");
        var civil=Verticals.CivilDocument()??throw new CadFault("CIVIL3D_REQUIRED","No Civil document");string kind=op.Text("op")!;ObjectId id;
        Type Type(string name)=>assembly.GetType("Autodesk.Civil.DatabaseServices."+name)??throw new CadFault("VERTICAL_API_UNAVAILABLE",name);
        void Points(DBObject obj,string method)
        {
            var points=op.GetProperty("points").EnumerateArray().Select(XY).ToArray();if(points.Length is <2 or >500)throw new CadFault("INVALID_POINTS","2..500 station/elevation or XY points required");
            for(int n=1;n<points.Length;n++){if(method=="AddFixedTangent"&&points[n].X<=points[n-1].X)throw new CadFault("INVALID_STATIONS","Profile stations must increase");Invoke(Get(obj,"Entities"),method,points[n-1],points[n]);}
        }
        if(kind=="civil_alignment_create")
        {id=(ObjectId)Invoke(Type("Alignment"),"Create",civil,op.Text("name"),op.Text("site"),op.Text("layer"),op.Text("style"),op.Text("label_set"))!;Points(tr.GetObject(id,OpenMode.ForWrite),"AddFixedLine");}
        else if(kind is "civil_profile_create" or "civil_profile_from_surface")
        {
            object?[] args=kind=="civil_profile_create"?[op.Text("name"),Id(db,op,"alignment_handle"),Id(db,op,"layer_handle"),Id(db,op,"style_handle"),Id(db,op,"label_set_handle")]:[op.Text("name"),Id(db,op,"alignment_handle"),Id(db,op,"surface_handle"),Id(db,op,"layer_handle"),Id(db,op,"style_handle"),Id(db,op,"label_set_handle")];
            id=(ObjectId)Invoke(Type("Profile"),kind=="civil_profile_create"?"CreateByLayout":"CreateFromSurface",args)!;if(kind=="civil_profile_create")Points(tr.GetObject(id,OpenMode.ForWrite),"AddFixedTangent");
        }
        else if(kind=="civil_network_create")
        {
            object?[] args=[civil,op.Text("name")];id=(ObjectId)Invoke(Type("Network"),"Create",args)!;var network=tr.GetObject(id,OpenMode.ForWrite);Set(network,"PartsListId",Id(db,op,"parts_list_handle"));
            if(op.Text("surface_handle") is not null)Set(network,"ReferenceSurfaceId",Id(db,op,"surface_handle"));if(op.Text("alignment_handle") is not null)Set(network,"ReferenceAlignmentId",Id(db,op,"alignment_handle"));
        }
        else
        {
            id=Id(db,op,"handle");var obj=tr.GetObject(id,OpenMode.ForWrite);
            if(obj.GetType().Assembly!=assembly||GetOptional(obj,"IsReferenceObject") is true)throw new CadFault("INVALID_CIVIL_TARGET","Editable native Civil object required; data shortcut references cannot be edited");
            if(kind is "civil_alignment_add_line" or "civil_profile_add_tangent")Invoke(Get(obj,"Entities"),kind=="civil_alignment_add_line"?"AddFixedLine":"AddFixedTangent",XY(op.GetProperty("start")),XY(op.GetProperty("end")));
            else if(kind=="civil_network_add_pipe")
            {
                using var line=new LineSegment3d(P(op.GetProperty("start")),P(op.GetProperty("end")));object?[] args=[Id(db,op,"family_handle"),Id(db,op,"size_handle"),line,ObjectId.Null,op.TryGetProperty("apply_rules",out var rules)&&rules.GetBoolean()];Invoke(obj,"AddLinePipe",args);id=(ObjectId)args[3]!;
            }
            else if(kind=="civil_network_add_structure")
            {object?[] args=[Id(db,op,"family_handle"),Id(db,op,"size_handle"),P(op.GetProperty("position")),EditPlan.Numeric(op,"rotation_deg",0)*Math.PI/180,ObjectId.Null,op.TryGetProperty("apply_rules",out var rules)&&rules.GetBoolean()];Invoke(obj,"AddStructure",args);id=(ObjectId)args[4]!;}
            else
            {
                var allowed=new HashSet<string>{"Name","Description","StyleId","StartPoint","EndPoint","RimElevation","SumpElevation","PartsListId","ReferenceSurfaceId","ReferenceAlignmentId"};
                foreach(var p in op.GetProperty("properties").EnumerateObject())
                {if(!allowed.Contains(p.Name))throw new CadFault("CIVIL_PROPERTY_NOT_ALLOWED",p.Name);object value=p.Name.EndsWith("Id",StringComparison.Ordinal)?NativeTables.Resolve(db,p.Value.GetString()!):p.Name.EndsWith("Point",StringComparison.Ordinal)?P(p.Value):p.Value.ValueKind==JsonValueKind.Number?p.Value.GetDouble():p.Value.GetString()!;Set(obj,p.Name,value);}
            }
        }
        var result=tr.GetObject(id,OpenMode.ForRead);return new{handle=id.Handle.ToString(),class_name=result.GetType().FullName,name=GetOptional(result,"Name"),api_version=assembly.GetName().Version?.ToString(),verification="native_API_returned_object_id; inspect cad_vertical_get",live_product_validation="required"};
    }
    private static object? GetOptional(object obj,string name)=>obj.GetType().GetProperty(name)?.GetValue(obj);
    internal static object Capabilities(Database db)
    {
        var civil=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="AeccDbMgd");var map=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="ManagedMapApi");
        object[] Describe(Assembly? a,string[] types)=>types.Select(name=>new{type=name,methods=a?.GetType(name)?.GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance).Where(m=>m.DeclaringType==a.GetType(name)).Select(m=>m.ToString()).Take(150).ToArray()??[]}).Cast<object>().ToArray();
        return new{civil_loaded=civil is not null,map_loaded=map is not null,civil_api=Describe(civil,["Autodesk.Civil.DatabaseServices.Alignment","Autodesk.Civil.DatabaseServices.Profile","Autodesk.Civil.DatabaseServices.Network"]),map_api=Describe(map,["Autodesk.Gis.Map.ObjectData.Tables","Autodesk.Gis.Map.ObjectData.Table","Autodesk.Gis.Map.ObjectData.Records","Autodesk.Gis.Map.Project.ProjectModel"]),contract=ExtendedPlan.Fields.Where(p=>p.Key.StartsWith("civil_")||p.Key.StartsWith("map_")),map_atomic="Map operations run singly with checkpoint; Map API has separate transaction semantics"};
    }
    internal static object Map(Database db,JsonElement op)
    {
        var project=Verticals.MapProject(db)??throw new CadFault("MAP3D_REQUIRED","Map 3D project API is unavailable");var assembly=project.GetType().Assembly;
        string kind=op.Text("op")!;var tables=Get(project,"ODTables");
        if(kind=="map_coordinate_system")
        {string code=EditPlan.RequiredText(op,"code");Set(project,"Projection",code);return new{coordinate_system=Get(project,"Projection"),geometry_reprojected=false};}
        Type Type(string name)=>assembly.GetType("Autodesk.Gis.Map.ObjectData."+name)??throw new CadFault("VERTICAL_API_UNAVAILABLE",name);
        if(kind=="map_od_table")
        {
            if((bool)Invoke(tables,"IsTableDefined",op.Text("name"))!)throw new CadFault("OD_TABLE_EXISTS",op.Text("name")!);
            var definitions=Invoke(Type("FieldDefinitions"),"Create")!;
            try
            {
                int n=0;var dataType=assembly.GetType("Autodesk.Gis.Map.Constants.DataType")??throw new CadFault("VERTICAL_API_UNAVAILABLE","Map DataType");
                foreach(var field in op.GetProperty("fields").EnumerateArray())
                {string type=field.Text("type")??"Character";if(type is not("Character" or "Integer" or "Real" or "Point"))throw new CadFault("INVALID_OD_TYPE",type);Invoke(definitions,"Add",EditPlan.RequiredText(field,"name"),field.Text("description")??"",Enum.Parse(dataType,type),n++);}
                if(n is <1 or >100)throw new CadFault("INVALID_OD_FIELDS","1..100 fields required");Invoke(tables,"Add",op.Text("name"),definitions,op.Text("description")??"",false);
                return new{table=op.Text("name"),fields=n};
            }
            finally{(definitions as IDisposable)?.Dispose();}
        }
        var table=Index(tables,EditPlan.RequiredText(op,"table"));var entityId=Id(db,op,"handle");
        try
        {
            void Values(object record)
            {
                var definitions=Get(table,"FieldDefinitions");int count=Convert.ToInt32(Get(definitions,"Count"));var supplied=op.GetProperty("values").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value);var assigned=new HashSet<string>();
                for(int n=0;n<count;n++)
                {
                    var field=Index(definitions,n);string name=Convert.ToString(Get(field,"Name"))!;if(!supplied.TryGetValue(name,out var value))continue;var target=Index(record,n);
                    object typed=value.ValueKind==JsonValueKind.String?value.GetString()!:value.ValueKind==JsonValueKind.Array?P(value):Convert.ToString(Get(target,"Type"))=="Integer"?(object)value.GetInt32():value.GetDouble();Invoke(target,"Assign",typed);assigned.Add(name);
                }
                if(assigned.Count!=supplied.Count)throw new CadFault("OD_FIELD_NOT_FOUND","One or more supplied fields are absent");
            }
            if(kind=="map_od_add")
            {
                var record=Invoke(Type("Record"),"Create")!;try{Invoke(table,"InitRecord",record);Values(record);Invoke(table,"AddRecord",record,entityId);}finally{(record as IDisposable)?.Dispose();}
            }
            else
            {
                var mode=assembly.GetType("Autodesk.Gis.Map.Constants.OpenMode")??throw new CadFault("VERTICAL_API_UNAVAILABLE","Map OpenMode");var records=Invoke(table,"GetObjectTableRecords",0,entityId,Enum.Parse(mode,"OpenForWrite"),false)!;
                try{int n=DraftingPlan.Integer(op,"record_index",0,999,0);var record=Index(records,n);Values(record);Invoke(records,"UpdateRecord",record);}finally{(records as IDisposable)?.Dispose();}
            }
            return new{handle=entityId.Handle.ToString(),table=op.Text("table"),operation=kind,verification="Map API accepted mutation; inspect cad_vertical_get"};
        }
        finally{(table as IDisposable)?.Dispose();}
    }
}
