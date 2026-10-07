using System.Collections;
using System.Reflection;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
namespace CadMcp.AutoCAD;
internal static class VerticalEditing
{
    // Vendor members are late-bound: hidden properties, uint/ref parameters and overloads are resolved in VendorReflection.
    private static object Get(object instance,string property)=>VendorReflection.TryGet(instance,property)??throw new CadFault("VERTICAL_API_UNAVAILABLE",property);
    internal static object? Invoke(object instance,string method,params object?[] args)=>VendorReflection.Invoke(instance,method,args);
    private static object Index(object instance,object index)=>VendorReflection.Index(instance,index);
    private static void Set(object instance,string name,object? value)=>VendorReflection.Set(instance,name,value);
    private static ObjectId Id(Database db,JsonElement op,string key)=>NativeTables.Resolve(db,EditPlan.RequiredText(op,key));
    private static Point3d P(JsonElement value){var p=EditPlan.Point(value);return new(p[0],p[1],p[2]);}
    private static Point2d XY(JsonElement value){var p=EditPlan.Point(value);return new(p[0],p[1]);}
    internal static object Civil(Database db,Transaction tr,JsonElement op)
    {
        var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="AeccDbMgd")??throw new CadFault("CIVIL3D_REQUIRED","Run this operation inside Civil 3D");
        var civil=Verticals.CivilDocument(db)??throw new CadFault("CIVIL3D_REQUIRED","No Civil document");string kind=op.Text("op")!;ObjectId id;
        Type Type(string name)=>assembly.GetType("Autodesk.Civil.DatabaseServices."+name)??throw new CadFault("VERTICAL_API_UNAVAILABLE",name);
        void Points(DBObject obj,string method)
        {
            var points=op.GetProperty("points").EnumerateArray().ToArray();if(points.Length is <2 or >500)throw new CadFault("INVALID_POINTS","2..500 station/elevation or XY points required");
            for(int n=1;n<points.Length;n++)
            {
                // AlignmentEntityCollection.AddFixedLine takes Point3d only; profile tangents are station/elevation Point2d.
                if(method=="AddFixedTangent"){var a=XY(points[n-1]);var b=XY(points[n]);if(b.X<=a.X)throw new CadFault("INVALID_STATIONS","Profile stations must increase");Invoke(Get(obj,"Entities"),method,a,b);}
                else Invoke(Get(obj,"Entities"),method,P(points[n-1]),P(points[n]));
            }
        }
        object? detail=null;
        if(kind=="civil_alignment_create")
        {
            id=(ObjectId)Invoke(Type("Alignment"),"Create",civil,op.Text("name"),op.Text("site"),op.Text("layer"),op.Text("style"),op.Text("label_set"))!;
            var alignment=tr.GetObject(id,OpenMode.ForWrite);
            if(op.TryGetProperty("radii",out _))detail=CurvedAlignment(alignment,op);
            else Points(alignment,"AddFixedLine");
        }
        else if(kind is "civil_profile_create" or "civil_profile_from_surface")
        {
            ObjectId layer=op.Text("layer") is {} layerName?LayerId(db,tr,layerName):Id(db,op,"layer_handle");
            ObjectId style=op.Text("style") is {} styleName?CivilStyle(civil,tr,"ProfileStyles",styleName):Id(db,op,"style_handle");
            ObjectId labels=op.Text("label_set") is {} labelName?CivilLabelSet(civil,tr,"Profile",labelName):Id(db,op,"label_set_handle");
            object?[] args=kind=="civil_profile_create"?[op.Text("name"),Id(db,op,"alignment_handle"),layer,style,labels]:[op.Text("name"),Id(db,op,"alignment_handle"),Id(db,op,"surface_handle"),layer,style,labels];
            id=(ObjectId)Invoke(Type("Profile"),kind=="civil_profile_create"?"CreateByLayout":"CreateFromSurface",args)!;if(kind=="civil_profile_create")Points(tr.GetObject(id,OpenMode.ForWrite),"AddFixedTangent");
        }
        else if(kind=="civil_network_create")
        {
            var partsList=op.Text("parts_list") is {} partsListName?PartsListId(civil,tr,partsListName):Id(db,op,"parts_list_handle");
            object?[] args=[civil,op.Text("name")];id=(ObjectId)Invoke(Type("Network"),"Create",args)!;var network=tr.GetObject(id,OpenMode.ForWrite);Set(network,"PartsListId",partsList);
            if(op.Text("surface_handle") is not null)Set(network,"ReferenceSurfaceId",Id(db,op,"surface_handle"));if(op.Text("alignment_handle") is not null)Set(network,"ReferenceAlignmentId",Id(db,op,"alignment_handle"));
        }
        else
        {
            id=Id(db,op,"handle");var obj=tr.GetObject(id,OpenMode.ForWrite);
            if(obj.GetType().Assembly!=assembly||GetOptional(obj,"IsReferenceObject") is true)throw new CadFault("INVALID_CIVIL_TARGET","Editable native Civil object required; data shortcut references cannot be edited");
            if(kind=="civil_alignment_add_line")Invoke(Get(obj,"Entities"),"AddFixedLine",P(op.GetProperty("start")),P(op.GetProperty("end")));
            else if(kind=="civil_profile_add_tangent")Invoke(Get(obj,"Entities"),"AddFixedTangent",XY(op.GetProperty("start")),XY(op.GetProperty("end")));
            else if(kind=="civil_network_add_pipe")
            {
                var (family,size)=Part(db,tr,obj,op);
                using var line=new LineSegment3d(P(op.GetProperty("start")),P(op.GetProperty("end")));object?[] args=[family,size,line,ObjectId.Null,op.TryGetProperty("apply_rules",out var rules)&&rules.GetBoolean()];Invoke(obj,"AddLinePipe",args);id=(ObjectId)args[3]!;
            }
            else if(kind=="civil_network_add_structure")
            {var (family,size)=Part(db,tr,obj,op);object?[] args=[family,size,P(op.GetProperty("position")),EditPlan.Numeric(op,"rotation_deg",0)*Math.PI/180,ObjectId.Null,op.TryGetProperty("apply_rules",out var rules)&&rules.GetBoolean()];Invoke(obj,"AddStructure",args);id=(ObjectId)args[4]!;}
            else
            {
                var allowed=new HashSet<string>{"Name","Description","StyleId","StartPoint","EndPoint","RimElevation","SumpElevation","PartsListId","ReferenceSurfaceId","ReferenceAlignmentId"};
                foreach(var p in op.GetProperty("properties").EnumerateObject())
                {if(!allowed.Contains(p.Name))throw new CadFault("CIVIL_PROPERTY_NOT_ALLOWED",p.Name);object value=p.Name.EndsWith("Id",StringComparison.Ordinal)?NativeTables.Resolve(db,p.Value.GetString()!):p.Name.EndsWith("Point",StringComparison.Ordinal)?P(p.Value):p.Value.ValueKind==JsonValueKind.Number?p.Value.GetDouble():p.Value.GetString()!;Set(obj,p.Name,value);}
            }
        }
        var result=tr.GetObject(id,OpenMode.ForRead);return new{handle=id.Handle.ToString(),class_name=result.GetType().FullName,name=GetOptional(result,"Name"),api_version=assembly.GetName().Version?.ToString(),detail,verification=detail is null?"native_API_returned_object_id; inspect cad_vertical_get":"geometry compared with the plan before commit",live_product_validation="required"};
    }

    /// <summary>
    /// Tangents between the PI points with circular free curves of the requested radii, then a comparison with the
    /// analytic plan: line and curve counts, radii and total length. A mismatch fails the edit before commit.
    /// </summary>
    private static object CurvedAlignment(DBObject alignment,JsonElement op)
    {
        var points=op.GetProperty("points").EnumerateArray().Select(P).ToArray();
        var radii=op.GetProperty("radii").EnumerateArray().Select(r=>r.GetDouble()).ToArray();
        var plan=AlignmentGeometry.Compute(points.Select(p=>(p.X,p.Y)).ToArray(),radii);
        var entities=Get(alignment,"Entities");
        var lines=new List<int>();
        for(int i=0;i+1<points.Length;i++)lines.Add(Convert.ToInt32(Get(Invoke(entities,"AddFixedLine",points[i],points[i+1])!,"EntityId")));
        // The curve enums are taken from the method itself, whatever namespace a release puts them in.
        var addCurve=entities.GetType().GetMethods().FirstOrDefault(m=>m.Name=="AddFreeCurve"&&m.GetParameters().Length==6&&m.GetParameters()[3].ParameterType.IsEnum)
            ??throw new CadFault("VERTICAL_API_UNAVAILABLE","AlignmentEntityCollection.AddFreeCurve(previous, next, value, CurveParamType, greaterThan180, CurveType)");
        var parameters=addCurve.GetParameters();
        object radiusType=Enum.Parse(parameters[3].ParameterType,"Radius"),compound=Enum.Parse(parameters[5].ParameterType,"Compound");
        foreach(var curve in plan.Curves)
            try{addCurve.Invoke(entities,[lines[curve.Pi-1],lines[curve.Pi],curve.Radius,radiusType,false,compound]);}
            catch(TargetInvocationException error){throw new CadFault("CURVE_FAILED","Curve at PI "+curve.Pi+": "+VendorReflection.Describe(error));}
        int count=Convert.ToInt32(Get(entities,"Count"));
        var arcs=new List<double>();int straight=0;
        for(int i=0;i<count;i++)
        {
            var entity=Invoke(entities,"GetEntityByOrder",i)!;
            if(entity.GetType().Name=="AlignmentArc")arcs.Add(Convert.ToDouble(Get(entity,"Radius")));else if(entity.GetType().Name=="AlignmentLine")straight++;
        }
        double length=Convert.ToDouble(Get(alignment,"Length"));
        var expected=plan.Curves.Select(c=>c.Radius).ToArray();
        if(straight!=points.Length-1||arcs.Count!=expected.Length||arcs.Zip(expected).Any(p=>Math.Abs(p.First-p.Second)>1e-6*Math.Max(1,p.Second))||Math.Abs(length-plan.ExpectedLength)>1e-6*Math.Max(1,plan.ExpectedLength))
            throw new CadFault("ALIGNMENT_GEOMETRY_MISMATCH","Built "+straight+" tangents and "+arcs.Count+" curves of length "+length.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+
                "; planned "+(points.Length-1)+" tangents and "+expected.Length+" curves of length "+plan.ExpectedLength.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture));
        return new{tangents=straight,curves=plan.Curves.Select(c=>new{pi=c.Pi,radius=c.Radius,deflection_deg=Math.Round(c.DeflectionDeg,6),tangent_length=Math.Round(c.TangentLength,6),arc_length=Math.Round(c.ArcLength,6)}).ToArray(),
            length=Math.Round(length,6),expected_length=Math.Round(plan.ExpectedLength,6)};
    }

    private static ObjectId LayerId(Database db,Transaction tr,string name)
    {
        var layers=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);
        return layers.Has(name)?layers[name]:throw new CadFault("LAYER_NOT_FOUND",name);
    }

    /// <summary>Names and ids of a Civil style collection; collections enumerate the ids of their styles.</summary>
    internal static IEnumerable<(string Name,ObjectId Id)> Named(object? collection,Transaction tr)
    {
        if(collection is not IEnumerable items)yield break;
        foreach(var item in items)
            if(item is ObjectId id&&!id.IsNull&&!id.IsErased&&VendorReflection.TryGet(tr.GetObject(id,OpenMode.ForRead),"Name") is string name)yield return (name,id);
    }

    private static ObjectId Find(IEnumerable<(string Name,ObjectId Id)> named,string name,string what)
    {
        var all=named.ToArray();
        var match=all.Where(n=>string.Equals(n.Name,name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(match.Length==1)return match[0].Id;
        throw new CadFault(match.Length==0?"CIVIL_NAME_NOT_FOUND":"CIVIL_NAME_AMBIGUOUS",what+" \""+name+"\""+(match.Length==0?" not found; available: "+string.Join(", ",all.Select(n=>n.Name).Take(30)):" matches several objects"));
    }

    private static ObjectId CivilStyle(object civil,Transaction tr,string collection,string name)=>
        Find(Named(VendorReflection.TryGet(Get(civil,"Styles"),collection),tr),name,collection);

    /// <summary>Label set by name among the label-set collections of the object kind (Profile, Alignment ...).</summary>
    private static ObjectId CivilLabelSet(object civil,Transaction tr,string kind,string name)
    {
        var root=Get(Get(civil,"Styles"),"LabelSetStyles");
        var collections=root.GetType().GetProperties(BindingFlags.Public|BindingFlags.Instance).Where(p=>p.GetIndexParameters().Length==0&&p.Name.StartsWith(kind,StringComparison.Ordinal)).ToArray();
        if(collections.Length==0)throw new CadFault("VERTICAL_API_UNAVAILABLE",kind+" label sets");
        return Find(collections.SelectMany(p=>Named(VendorReflection.TryGet(root,p.Name),tr)),name,kind+" label set");
    }

    private static ObjectId PartsListId(object civil,Transaction tr,string name)=>Find(Named(Get(Get(civil,"Styles"),"PartsListSet"),tr),name,"Parts list");

    /// <summary>Family and size by handle, or by description within the network's parts list.</summary>
    private static (ObjectId Family,ObjectId Size) Part(Database db,Transaction tr,DBObject network,JsonElement op)
    {
        ObjectId family=op.Text("family") is {} familyName?PartChild(tr,tr.GetObject((ObjectId)Get(network,"PartsListId"),OpenMode.ForRead),"PartFamilyCount",familyName,"Part family"):Id(db,op,"family_handle");
        ObjectId size=op.Text("size") is {} sizeName?PartChild(tr,tr.GetObject(family,OpenMode.ForRead),"PartSizeCount",sizeName,"Part size"):Id(db,op,"size_handle");
        return (family,size);
    }

    private static ObjectId PartChild(Transaction tr,object parent,string countProperty,string name,string what)
    {
        var children=Children(tr,parent,countProperty).ToArray();
        if(countProperty!="PartSizeCount")return Find(children.Select(c=>(Name:Convert.ToString(VendorReflection.TryGet(c.Object,"Description"))??"",c.Id)),name,what);
        // Sizes are listed by their catalog size name; another text value of the size record also identifies one.
        var listed=children.Select(c=>(Name:SizeNames(c.Object).FirstOrDefault()??c.Id.Handle.ToString(),c.Id)).ToArray();
        if(listed.Any(n=>string.Equals(n.Name,name,StringComparison.OrdinalIgnoreCase)))return Find(listed,name,what);
        var other=children.Where(c=>SizeNames(c.Object).Skip(1).Contains(name,StringComparer.OrdinalIgnoreCase)).Select(c=>c.Id).ToArray();
        if(other.Length>1)throw new CadFault("CIVIL_NAME_AMBIGUOUS",what+" \""+name+"\" matches several sizes; use the listed size name");
        return other.Length==1?other[0]:Find(listed,name,what);
    }

    internal static IEnumerable<(ObjectId Id,DBObject Object)> Children(Transaction tr,object parent,string countProperty)
    {
        int count=Convert.ToInt32(Get(parent,countProperty));
        for(int i=0;i<Math.Min(count,1000);i++)
            if(Index(parent,i) is ObjectId id&&!id.IsNull&&!id.IsErased)yield return (id,tr.GetObject(id,OpenMode.ForRead));
    }

    /// <summary>
    /// Names of a part size: the catalog size name (PrtSN, as Civil lists it) first, then the record's other text values.
    /// The size object itself has no name property.
    /// </summary>
    internal static IEnumerable<string> SizeNames(object size)
    {
        if(VendorReflection.TryGet(size,"SizeDataRecord") is not {} record)yield break;
        object? field=null;
        try{field=VendorReflection.Invoke(record,"GetDataFieldBy","PrtSN");}catch(CadFault){}
        if(field is not null&&Convert.ToString(VendorReflection.TryGet(field,"Value"),System.Globalization.CultureInfo.InvariantCulture) is {Length:>0} sizeName)yield return sizeName;
        object? all=null;
        try{all=VendorReflection.Invoke(record,"GetAllDataFields");}catch(CadFault){}
        if(all is IEnumerable fields)
            foreach(var item in fields)
                if(VendorReflection.TryGet(item,"Value") is string {Length:>0} text)yield return text;
    }
    private static object? GetOptional(object obj,string name)=>VendorReflection.TryGet(obj,name);
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
        {
            string requested=EditPlan.RequiredText(op,"code").Trim(),previous=Convert.ToString(VendorReflection.TryGet(project,"Projection"))??"";
            var check=MapCoordinateSystems.Check(db,requested,op.TryGetProperty("force",out var force)&&force.GetBoolean());
            Set(project,"Projection",check.Code);
            return new{coordinate_system=Get(project,"Projection"),previous=previous.Length==0?null:previous,requested,check.Validated,check.Description,check.Epsg,check.Units,check.Compatible,check.UsefulRange,check.CenterLonLat,check.Note,geometry_reprojected=false};
        }
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

/// <summary>
/// The CS-MAP catalog of Map 3D's geospatial platform, late-bound: a code (or an EPSG number) is checked against the
/// catalog and the drawing extents against the system's useful range before it is assigned, since assigning a
/// system never moves geometry. Approach from seb21-art/MCP_AutocadMap3D (MIT), rewritten for reflection.
/// </summary>
internal static class MapCoordinateSystems
{
    internal sealed record Result(string Code, bool Validated, string? Description, int? Epsg, string? Units, bool? Compatible, string? UsefulRange, double[]? CenterLonLat, string? Note);

    private static Type? FactoryType()
    {
        const string name="OSGeo.MapGuide.MgCoordinateSystemFactory";
        foreach(var loaded in AppDomain.CurrentDomain.GetAssemblies())
            if(loaded.GetName().Name=="OSGeo.MapGuide.Geometry"&&loaded.GetType(name) is {} type)return type;
        try
        {
            var acad=System.IO.Path.GetDirectoryName(typeof(Database).Assembly.Location)!;
            var path=System.IO.Path.Combine(acad,"Map","bin","GisPlatform","OSGeo.MapGuide.Geometry.dll");
            return System.IO.File.Exists(path)?Assembly.LoadFrom(path).GetType(name):null;
        }
        catch(System.Exception error) when(error is System.IO.IOException or BadImageFormatException or System.Security.SecurityException){return null;}
    }

    private static object? Call(object target,string method,params object?[] args)
    {
        try{return VendorReflection.Invoke(target,method,args);}
        catch(CadFault){return null;}
    }

    public static Result Check(Database db,string requested,bool force)
    {
        if(FactoryType() is not {} factoryType)return new(requested,false,null,null,null,null,null,null,"The CS-MAP catalog is unavailable; the code was assigned without validation");
        var factory=Activator.CreateInstance(factoryType)!;
        string code=requested;
        var epsgText=requested.StartsWith("EPSG:",StringComparison.OrdinalIgnoreCase)?requested[5..]:requested;
        if(int.TryParse(epsgText,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out int epsg))
        {
            // A bare number is an EPSG code; the catalog names it with its own code.
            var wkt=Call(factory,"ConvertEpsgCodeToWkt",epsg) as string;
            code=(wkt is null?null:Call(factory,"ConvertWktToCoordinateSystemCode",wkt) as string)??throw new CadFault("UNKNOWN_COORDINATE_SYSTEM","EPSG "+epsg+" is not in the CS-MAP catalog");
        }
        var system=Call(factory,"CreateFromCode",code)??throw new CadFault("UNKNOWN_COORDINATE_SYSTEM","\""+code+"\" is not in the CS-MAP catalog; use a catalog code such as LL84 or an EPSG number");
        double Number(string method)=>Convert.ToDouble(Call(system,method)??double.NaN,System.Globalization.CultureInfo.InvariantCulture);
        double lonMin=Number("GetLonMin"),lonMax=Number("GetLonMax"),latMin=Number("GetLatMin"),latMax=Number("GetLatMax");
        bool hasRange=lonMax>lonMin&&latMax>latMin;
        string? range=hasRange?FormattableString.Invariant($"longitude {lonMin:0.###}..{lonMax:0.###}, latitude {latMin:0.###}..{latMax:0.###}"):null;
        (double Lon,double Lat)? LonLat(double x,double y)
        {
            if(Call(system,"ConvertToLonLat",x,y) is not {} coordinate)return null;
            double lon=Convert.ToDouble(Call(coordinate,"GetX")??double.NaN),lat=Convert.ToDouble(Call(coordinate,"GetY")??double.NaN);
            return double.IsNaN(lon)||double.IsNaN(lat)?null:(lon,lat);
        }
        Point3d min=db.Extmin,max=db.Extmax;
        bool extents=min.X<=max.X&&min.Y<=max.Y&&Math.Abs(min.X)<1e19&&Math.Abs(max.X)<1e19;
        bool? compatible=null;double[]? center=null;
        if(extents)
        {
            compatible=true;
            foreach(var (x,y) in new[]{(min.X,min.Y),(max.X,min.Y),(min.X,max.Y),(max.X,max.Y)})
            {
                if(Call(system,"IsValidXY",x,y) is false||LonLat(x,y) is not {} corner||hasRange&&(corner.Lon<lonMin||corner.Lon>lonMax||corner.Lat<latMin||corner.Lat>latMax)){compatible=false;break;}
            }
            if(LonLat((min.X+max.X)/2,(min.Y+max.Y)/2) is {} middle)center=[Math.Round(middle.Lon,6),Math.Round(middle.Lat,6)];
        }
        if(compatible==false&&!force)
            throw new CadFault("CS_EXTENTS_MISMATCH","The drawing extents (X "+min.X.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+".."+max.X.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+
                ", Y "+min.Y.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+".."+max.Y.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+") lie outside the useful range of "+code+(range is null?"":" ("+range+")")+
                "; the coordinates are probably in another system. Set force:true only if this is intended");
        int? epsgCode=Call(system,"GetEpsgCode") is int value&&value>0?value:null;
        return new(Convert.ToString(Call(system,"GetCsCode"))??code,true,Call(system,"GetDescription") as string,epsgCode,Call(system,"GetUnits") as string,compatible,range,center,
            extents?null:"The drawing has no valid extents; compatibility was not checked");
    }
}
