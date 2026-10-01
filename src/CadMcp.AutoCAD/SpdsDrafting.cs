using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class SpdsDrafting
{
    private static Point3d P(JsonElement op,string key){var p=EditPlan.Point(op.GetProperty(key));return new(p[0],p[1],p[2]);}
    public static ObjectId TextStyle(Database db,Transaction tr,string? name)
    {if(name is null)return db.Textstyle;var styles=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);return styles.Has(name)?styles[name]:throw new CadFault("STYLE_NOT_FOUND",name);}
    public static ObjectId DimensionStyle(Database db,Transaction tr,string? name)
    {if(name is null)return db.Dimstyle;var styles=(DimStyleTable)tr.GetObject(db.DimStyleTableId,OpenMode.ForRead);return styles.Has(name)?styles[name]:throw new CadFault("STYLE_NOT_FOUND",name);}
    public static object ConfigureDimensions(Database db,Transaction tr,JsonElement op)
    {
        string name=op.Text("name")!;var styles=(DimStyleTable)tr.GetObject(db.DimStyleTableId,OpenMode.ForRead);
        if(styles.Has(name))throw new CadFault("STYLE_EXISTS","Choose a new name; existing dimension styles are not overwritten");
        var record=new DimStyleTableRecord();using (var defaults = db.GetDimstyleData()) record.CopyFrom(defaults);record.Name=name;
        double factor=op.TryGetProperty("measurement_factor",out _)?DraftingPlan.Positive(op,"measurement_factor",1):db.Insunits switch{UnitsValue.Meters=>1000,UnitsValue.Centimeters=>10,UnitsValue.Millimeters=>1,_=>throw new CadFault("UNITS_REQUIRED","Specify measurement_factor for this drawing unit")};
        record.Dimscale=DraftingPlan.Positive(op,"drawing_scale",1)/factor;
        record.Dimlfac=factor;record.Dimtxt=DraftingPlan.Positive(op,"text_height",2.5);
        record.Dimtxsty=TextStyle(db,tr,op.Text("text_style"));record.Dimasz=3;
        record.Dimtsz=op.TryGetProperty("ticks",out var ticks)&&!ticks.GetBoolean()?0:1.5;
        record.Dimdle=2;record.Dimexe=2;record.Dimexo=0;record.Dimgap=1;record.Dimtad=1;
        record.Dimtih=false;record.Dimtoh=false;record.Dimdec=0;record.Dimzin=8;record.Dimlunit=2;
        record.Dimalt=false;record.Dimtol=false;record.Dimlim=false;record.Dimpost="";record.Dimapost="";
        record.Dimsah=false;record.Dimblk=ObjectId.Null;record.Dimblk1=ObjectId.Null;record.Dimblk2=ObjectId.Null;
        record.Dimclrd=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByLayer,256);
        record.Dimclre=record.Dimclrd;record.Dimclrt=record.Dimclrd;
        record.Dimlwd=LineWeight.LineWeight018;record.Dimlwe=LineWeight.LineWeight018;
        styles.UpgradeOpen();styles.Add(record);tr.AddNewlyCreatedDBObject(record,true);
        return new {name,handle=record.Handle.ToString(),linear_units="millimeters",measurement_factor=factor,drawing_scale=record.Dimscale*factor,
            paper_text_height=record.Dimtxt,tick_length=record.Dimtsz*2,standard="ГОСТ Р 21.101-2026 / 5.4.1–5.4.2",
            limitations=new[]{"text_font_must_be_reviewed_for_GOST_2_304","radial_diametric_dimensions_use_arrow_overrides"}};
    }
    public static Dimension Dimension(Database db,Transaction tr,JsonElement op)
    {
        var style=DimensionStyle(db,tr,op.Text("style"));string text=op.Text("text")??"";
        var position=P(op,"position");
        switch(op.Text("op"))
        {
            case "dimension_rotated":return new RotatedDimension(EditPlan.Numeric(op,"angle_deg",0)*Math.PI/180,P(op,"first"),P(op,"second"),position,text,style);
            case "dimension_radius":
                var radius=new RadialDimension(P(op,"center"),P(op,"chord"),P(op,"chord").DistanceTo(position),text,style);radius.Dimtsz=0;radius.TextPosition=position;return radius;
            default:
                var diameter=new DiametricDimension(P(op,"first"),P(op,"second"),P(op,"first").DistanceTo(position),text,style);diameter.Dimtsz=0;diameter.TextPosition=position;return diameter;
        }
    }
    public static BlockReference Axis(Database db,Transaction tr,JsonElement op)
    {
        var first=P(op,"first");var second=P(op,"second");if(first.DistanceTo(second)<1e-8||Math.Abs(first.Z-second.Z)>1e-8)throw new CadFault("INVALID_AXIS","Axis needs distinct coplanar XY endpoints");
        double scale=DraftingPlan.Positive(op,"scale",1),diameter=DraftingPlan.Positive(op,"bubble_diameter",8)*scale;
        var types=(LinetypeTable)tr.GetObject(db.LinetypeTableId,OpenMode.ForRead);
        if(!types.Has("CENTER2"))db.LoadLineTypeFile("CENTER2","acadiso.lin");
        var block=NewBlock(db,tr,"AXIS");
        Add(block,tr,new Line(first,second){LineWeight=LineWeight.LineWeight018,Linetype="CENTER2",LinetypeScale=scale});
        foreach(var point in new[]{first,second})
        { Add(block,tr,new Circle(point,Vector3d.ZAxis,diameter/2));Add(block,tr,new DBText{Position=point,HorizontalMode=TextHorizontalMode.TextCenter,VerticalMode=TextVerticalMode.TextVerticalMid,AlignmentPoint=point,Height=2.5*scale,TextString=op.Text("label")!,TextStyleId=db.Textstyle}); }
        return new BlockReference(Point3d.Origin,block.ObjectId);
    }
    public static BlockReference Level(Database db,Transaction tr,JsonElement op)
    {
        var point=P(op,"position");double scale=DraftingPlan.Positive(op,"scale",1),sign=op.Text("direction")=="left"?-1:1;
        var block=NewBlock(db,tr,"LEVEL");Point3d Q(double x,double y)=>new(point.X+x*scale*sign,point.Y+y*scale,point.Z);
        Add(block,tr,new Line(Q(-2,2),Q(0,0)));Add(block,tr,new Line(Q(0,0),Q(2,2)));
        Add(block,tr,new Line(Q(0,0),Q(0,6)));Add(block,tr,new Line(Q(0,6),Q(18,6)));
        double elevation=EditPlan.Numeric(op,"elevation");string text=Math.Abs(elevation)<1e-12?"±0.000":(elevation>0?"+":"")+elevation.ToString("0.000",CultureInfo.InvariantCulture);
        Add(block,tr,new DBText{Position=Q(1,7),Height=2.5*scale,TextString=text,TextStyleId=TextStyle(db,tr,op.Text("text_style"))});
        return new BlockReference(Point3d.Origin,block.ObjectId);
    }
    private static BlockTableRecord NewBlock(Database db,Transaction tr,string kind)
    {var table=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);var block=new BlockTableRecord{Name="CADMCP_SPDS_"+kind+"_"+Guid.NewGuid().ToString("N")};table.Add(block);tr.AddNewlyCreatedDBObject(block,true);return block;}
    private static void Add(BlockTableRecord block,Transaction tr,Entity entity){block.AppendEntity(entity);tr.AddNewlyCreatedDBObject(entity,true);}
    public static (ObjectId Id,object Detail) Sheet(Database db,Transaction tr,JsonElement op)
    {
        string layout=op.Text("layout")!;var space=Sheets.Space(db,tr,layout);
        foreach(ObjectId id in space)if(tr.GetObject(id,OpenMode.ForRead) is BlockReference reference && ((BlockTableRecord)tr.GetObject(reference.BlockTableRecord,OpenMode.ForRead)).Name.StartsWith("CADMCP_SPDS_FORM3_",StringComparison.Ordinal))
            throw new CadFault("SHEET_FRAME_EXISTS","This layout already has a generated SPDS frame; edit its block attributes");
        var sizes=new Dictionary<string,(double W,double H)>{{"A0",(841,1189)},{"A1",(594,841)},{"A2",(420,594)},{"A3",(297,420)},{"A4",(210,297)}};
        var (w,h)=sizes[op.Text("format")!];if(op.Text("orientation")=="landscape"||(op.Text("orientation") is null&&op.Text("format")!="A4"))(w,h)=(h,w);
        var block=NewBlock(db,tr,"FORM3");var style=TextStyle(db,tr,op.Text("text_style"));
        void Line(double x1,double y1,double x2,double y2,bool thick=false)=>Add(block,tr,new Line(new(x1,y1,0),new(x2,y2,0)){LineWeight=thick?LineWeight.LineWeight050:LineWeight.LineWeight025});
        // Drawing frame in paper millimetres: binding edge 20, remaining edges 5.
        Line(20,5,w-5,5,true);Line(w-5,5,w-5,h-5,true);Line(w-5,h-5,20,h-5,true);Line(20,h-5,20,5,true);
        double ox=w-190,oy=5;
        void TLine(double x1,double y1,double x2,double y2,bool thick=false)=>Line(ox+x1,oy+y1,ox+x2,oy+y2,thick);
        void Text(double x,double y,string text,double height=2.5)=>Add(block,tr,new DBText{Position=new(ox+x,oy+y,0),TextString=text,Height=height,TextStyleId=style});
        void Attribute(string tag,double x,double y,double width,double height,string value)
        {
            var definition=new AttributeDefinition{Tag=tag,Prompt=tag,TextString=value,Position=new(ox+x,oy+y,0),Height=2.5,TextStyleId=style,IsMTextAttributeDefinition=true};
            var position=definition.Position;definition.Justify=AttachmentPoint.TopLeft;definition.AlignmentPoint=position;
            definition.MTextAttributeDefinition=new MText{Location=position,Width=width,TextHeight=2.5,Contents=value,TextStyleId=style,Attachment=AttachmentPoint.TopLeft};
            Add(block,tr,definition);
        }
        TLine(0,0,185,0,true);TLine(185,0,185,55,true);TLine(185,55,0,55,true);TLine(0,55,0,0,true);
        for(int y=5;y<55;y+=5)TLine(0,y,65,y);
        foreach(int x in new[]{10,20,30,40,55,65})TLine(x,30,x,55,x==65);
        foreach(int x in new[]{20,40,55,65})TLine(x,0,x,30,x==65);
        TLine(65,45,185,45);TLine(65,30,185,30);TLine(65,15,185,15);TLine(135,0,135,30);TLine(135,25,185,25);
        TLine(150,15,150,30);TLine(165,15,165,30);
        foreach(var (x,text) in new (double,string)[]{(.5,"Изм."),(10.5,"Кол.уч."),(21,"Лист"),(30.5,"№ док."),(41,"Подп."),(55.5,"Дата")})Text(x,31.5,text,2);
        Text(136,26.5,"Стадия",2);Text(151,26.5,"Лист",2);Text(166,26.5,"Листов",2);
        Attribute("DESIGNATION",67,53,116,8,op.Text("designation")??"");
        Attribute("PROJECT",67,43,116,13,op.Text("project")??"");
        Attribute("BUILDING",67,28,66,13,op.Text("building")??"");
        Attribute("DRAWING_TITLE",67,13,66,13,op.Text("drawing_title")??"");
        Attribute("ORGANIZATION",137,13,46,13,op.Text("organization")??"");
        Attribute("STAGE",137,23,11,8,op.Text("stage")??"Р");
        Attribute("SHEET",152,23,11,8,op.Text("sheet")??"");Attribute("SHEETS",167,23,16,8,op.Text("sheets")??"");
        var signatures=op.TryGetProperty("signatures",out var sig)?sig:Wire.Element(new{});
        for(int i=0;i<6;i++)
        {
            double top=29-i*5;string role=signatures.Text("role"+i)??(i==0?"Разраб.":i==1?"Проверил":i==4?"Н. контр.":"");
            Attribute("ROLE"+i,1,top,18,4,role);Attribute("NAME"+i,21,top,18,4,signatures.Text("name"+i)??"");
            Attribute("SIGN"+i,41,top,13,4,signatures.Text("sign"+i)??"");Attribute("DATE"+i,56,top,8,4,signatures.Text("date"+i)??"");
        }
        // Additional archive columns on the binding margin; agreement rows stay editable.
        Line(8,5,8,90);Line(20,5,20,90);foreach(int y in new[]{5,30,65,90})Line(8,y,20,y);
        Add(block,tr,new DBText{Position=new(10,6,0),Rotation=Math.PI/2,Height=2,TextString="Инв. № подл.",TextStyleId=style});
        Add(block,tr,new DBText{Position=new(10,31,0),Rotation=Math.PI/2,Height=2,TextString="Подп. и дата",TextStyleId=style});
        Add(block,tr,new DBText{Position=new(10,66,0),Rotation=Math.PI/2,Height=2,TextString="Взам. инв. №",TextStyleId=style});
        TLine(65,55,65,65);TLine(65,65,185,65);TLine(185,65,185,55);
        Attribute("FORMAT",152,-.5,31,4,"Формат "+op.Text("format"));
        var insert=new BlockReference(Point3d.Origin,block.ObjectId);space.AppendEntity(insert);tr.AddNewlyCreatedDBObject(insert,true);
        foreach(ObjectId id in block)if(tr.GetObject(id,OpenMode.ForRead) is AttributeDefinition definition)
        {var attribute=new AttributeReference();attribute.SetAttributeFromBlock(definition,insert.BlockTransform);insert.AttributeCollection.AppendAttribute(attribute);tr.AddNewlyCreatedDBObject(attribute,true);}
        return(insert.ObjectId,new {layout,format=op.Text("format"),paper_width=w,paper_height=h,standard="ГОСТ Р 21.101-2026",form="Е / форма 3",stamp_width=185,stamp_height=55,
            native_objects="BlockReference / Line / AttributeReference",limitations=new[]{"review_text_fit_font_and_optional_additional_fields","configure_plot_media_and_viewports_separately","not_automatic_full_project_norm_control"}});
    }
}
