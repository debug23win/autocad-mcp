using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class ReadingMetadata
{
    private static double[] P(Point3d p)=>[p.X,p.Y,p.Z];
    public static Layout? LayoutOf(ObjectId space,Transaction tr)
    {
        if(space.IsNull||tr.GetObject(space,OpenMode.ForRead) is not BlockTableRecord b||!b.IsLayout)return null;
        return (Layout)tr.GetObject(b.LayoutId,OpenMode.ForRead);
    }
    public static object Space(ObjectId owner,Transaction tr)
    {
        var layout=LayoutOf(owner,tr);
        return new{owner_handle=owner.IsNull?null:owner.Handle.ToString(),layout_name=layout?.LayoutName,
            space=layout is null?"block_definition":layout.ModelType?"model":"paper",layout_handle=layout?.Handle.ToString()};
    }
    public static Layout LayoutByName(Database db,Transaction tr,string name)
    {
        var dictionary=(DBDictionary)tr.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
        foreach(System.Collections.DictionaryEntry entry in dictionary)
        {var layout=(Layout)tr.GetObject((ObjectId)entry.Value!,OpenMode.ForRead);if(string.Equals(layout.LayoutName,name,StringComparison.OrdinalIgnoreCase)||(layout.ModelType&&string.Equals(name,"Model",StringComparison.OrdinalIgnoreCase)))return layout;}
        throw new CadFault("LAYOUT_NOT_FOUND",name);
    }
    public static Matrix3d DcsToWcs(Viewport v)=>Matrix3d.Rotation(-v.TwistAngle,v.ViewDirection,v.ViewTarget)*
        Matrix3d.Displacement(v.ViewTarget-Point3d.Origin)*Matrix3d.PlaneToWorld(v.ViewDirection);
    public static bool IsOverallViewport(Viewport v,Transaction tr)
    {
        if(v.Number==1)return true;
        var layout=LayoutOf(v.OwnerId,tr);if(layout is null)return false;
        var ids=layout.GetViewports();if(ids.Count>0)return ids[0]==v.ObjectId;
        // An uninitialized layout has no runtime viewport list; the first paper viewport is its overall view.
        return ((BlockTableRecord)tr.GetObject(v.OwnerId,OpenMode.ForRead)).Cast<ObjectId>().FirstOrDefault(id=>id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Viewport))))==v.ObjectId;
    }
    public static Point3d[] ViewportCorners(Viewport v)
    {
        double height=v.ViewHeight,width=height*v.Width/v.Height;var c=v.ViewCenter;var m=DcsToWcs(v);
        return new[]{new Point3d(c.X-width/2,c.Y-height/2,0),new Point3d(c.X+width/2,c.Y-height/2,0),
            new Point3d(c.X+width/2,c.Y+height/2,0),new Point3d(c.X-width/2,c.Y+height/2,0)}.Select(p=>p.TransformBy(m)).ToArray();
    }
    public static object? SearchBounds(Viewport v,Transaction tr)
    {
        if(v.PerspectiveOn||IsOverallViewport(v,tr)||v.Height<=0||v.ViewHeight<=0||v.ViewDirection.Length<1e-10||Math.Abs(v.ViewDirection.GetNormal().Z)<1-1e-9)return null;
        var corners=ViewportCorners(v);
        return new{min=new[]{corners.Min(p=>p.X),corners.Min(p=>p.Y),-1e100},max=new[]{corners.Max(p=>p.X),corners.Max(p=>p.Y),1e100}};
    }
    public static object Viewport(Viewport v,Transaction tr)
    {
        bool valid=!v.PerspectiveOn&&v.Height>0&&v.ViewHeight>0&&v.ViewDirection.Length>1e-10;
        return new{handle=v.Handle.ToString(),owner_handle=v.OwnerId.Handle.ToString(),layout_name=LayoutOf(v.OwnerId,tr)?.LayoutName,
            number=v.Number,is_paper_overall=IsOverallViewport(v,tr),runtime_state=v.Number<0?"inactive_layout_number_and_on_are_runtime_only":"active_layout",paper_center=P(v.CenterPoint),paper_width=v.Width,paper_height=v.Height,
            model_target=P(v.ViewTarget),model_view_center_dcs=new[]{v.ViewCenter.X,v.ViewCenter.Y},
            model_view_center_wcs=valid?P(new Point3d(v.ViewCenter.X,v.ViewCenter.Y,0).TransformBy(DcsToWcs(v))):null,
            model_view_height=v.ViewHeight,model_view_width=v.Height>0?v.ViewHeight*v.Width/v.Height:0,
            direction=new[]{v.ViewDirection.X,v.ViewDirection.Y,v.ViewDirection.Z},twist=v.TwistAngle,angle_units="radians",
            perspective=v.PerspectiveOn,scale=v.CustomScale,locked=v.Locked,on=v.On,
            dcs_to_wcs=valid?DcsToWcs(v).ToArray():null,target_plane_corners_wcs=valid?ViewportCorners(v).Select(P).ToArray():null,
            model_search_bounds=SearchBounds(v,tr),nonrectangular_clip=v.NonRectClipOn,
            clip_handle=v.NonRectClipEntityId.IsNull?null:v.NonRectClipEntityId.Handle.ToString(),
            frozen_layer_handles=v.GetFrozenLayers().Cast<ObjectId>().Take(256).Select(id=>id.Handle.ToString()).ToArray(),
            coverage="target-plane projection; search bounds are conservative XY envelope, not exact visibility or clipping"};
    }
    public static object DimensionStyle(DimStyleTableRecord s)=>new{name=s.Name,measurement_factor=s.Dimlfac,drawing_scale=s.Dimscale,
        linear_units=s.Dimlunit,linear_precision=s.Dimdec,rounding=s.Dimrnd,decimal_separator=s.Dimdsep.ToString(),
        primary_postfix=s.Dimpost,alternate_enabled=s.Dimalt,alternate_factor=s.Dimaltf,alternate_precision=s.Dimaltd,
        alternate_postfix=s.Dimapost,angular_units=s.Dimaunit,angular_precision=s.Dimadec,
        tolerance=s.Dimtol,limits=s.Dimlim,tolerance_upper=s.Dimtp,tolerance_lower=s.Dimtm,zero_suppression=s.Dimzin};
    public static void Dimension(Dimension dim,Transaction tr,Dictionary<string,object?> fields)
    {
        using var effective=dim.GetDimstyleData();
        fields["effective_dimension_style"]=DimensionStyle(effective);
        bool angular=dim is LineAngularDimension2 or Point3AngularDimension;
        var layout=LayoutOf(dim.OwnerId,tr);
        double factor=angular?1:effective.Dimlfac<0?(layout?.ModelType==false?Math.Abs(effective.Dimlfac):1):effective.Dimlfac;
        fields["measurement_factor_applied"]=factor;
        fields["scaled_measurement"]=dim.Measurement;
        fields["measurement_source"]="native Dimension.Measurement; already includes its linear measurement factor";
        if(dim is RotatedDimension rotated)
        {
            var delta=(rotated.XLine2Point-rotated.XLine1Point).TransformBy(Matrix3d.WorldToPlane(rotated.Normal));
            fields["geometric_measurement"]=Math.Abs(delta.X*Math.Cos(rotated.Rotation)+delta.Y*Math.Sin(rotated.Rotation));
        }
        else if(dim is AlignedDimension aligned)fields["geometric_measurement"]=aligned.XLine1Point.DistanceTo(aligned.XLine2Point);
        else if(dim is RadialDimension radial)fields["geometric_measurement"]=radial.Center.DistanceTo(radial.ChordPoint);
        else if(dim is DiametricDimension diameter)fields["geometric_measurement"]=diameter.ChordPoint.DistanceTo(diameter.FarChordPoint);
        else if(angular)fields["geometric_measurement"]=dim.Measurement;
        fields["has_text_override"]=!string.IsNullOrEmpty(dim.DimensionText);
        fields["dimension_evidence"]="geometric_measurement is computed from definition points when supported; measurement is native scaled value; displayed text is stored dimension-block evidence, not regenerated or verified design data; physical units need project evidence";
        try
        {
            var raw=new List<string>();var plain=new List<string>();
            if(!dim.DimBlockId.IsNull&&tr.GetObject(dim.DimBlockId,OpenMode.ForRead) is BlockTableRecord block)
                foreach(ObjectId id in block)
                {
                    if(raw.Count>=16)break;
                    var text=tr.GetObject(id,OpenMode.ForRead);
                    if(text is MText m){raw.Add(m.Contents);plain.Add(m.Text);}
                    else if(text is DBText t){raw.Add(t.TextString);plain.Add(t.TextString);}
                }
            if(raw.Count>0)
            {
                fields["formatted_text"]=string.Join("\\P",raw);fields["displayed_text"]=string.Join("\n",plain);
                fields["formatted_text_source"]="stored_dimension_block";fields["text"]=string.Join("\n",plain);
            }
            else fields["formatting_unavailable"]="No stored dimension-block text; read effective style, override and geometry. Reading never regenerates or writes the dimension.";
        }
        catch(Autodesk.AutoCAD.Runtime.Exception error){fields["formatting_error"]=error.ErrorStatus.ToString();}
    }
}
