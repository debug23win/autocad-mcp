using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class PreviewRendering
{
#if !CORE_CONSOLE
    internal static Action<string>? DiagnosticTrace { get; set; }
    // A separate graphics view avoids SetCurrentView/Regen and leaves the live DWG view and Undo untouched.
    public static System.Drawing.Bitmap Capture(Document doc,Transaction tr,JsonElement options,int width,int height,CancellationToken ct,out object metadata)
    {
        ObjectId? selectedSpace=null;Extents3d? bounds=null;
        string? layoutName=options.Text("layout_name");
        if(layoutName is not null)selectedSpace=ReadingMetadata.LayoutByName(doc.Database,tr,layoutName).BlockTableRecordId;
        if(options.Text("handles_json") is {} json)
        {
            using var handles=JsonDocument.Parse(json);
            if(handles.RootElement.ValueKind!=JsonValueKind.Array||handles.RootElement.GetArrayLength() is <1 or >500)throw new CadFault("INVALID_HANDLES","Frame 1..500 top-level handles in one model/layout space");
            foreach(var handle in handles.RootElement.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();var id=NativeTables.Resolve(doc.Database,handle.GetString()!);
                if(tr.GetObject(id,OpenMode.ForRead) is not Entity entity||ReadingMetadata.LayoutOf(entity.OwnerId,tr) is null)throw new CadFault("PREVIEW_NESTED_SCOPE","Frame root_handle for a nested instance");
                if(selectedSpace.HasValue&&selectedSpace.Value!=entity.OwnerId)throw new CadFault("PREVIEW_MIXED_SPACES","Frame entities from only one model/layout space; use layout_name explicitly");
                selectedSpace=entity.OwnerId;Add(entity);
            }
        }
        selectedSpace??=doc.Database.CurrentSpaceId;
        var layout=ReadingMetadata.LayoutOf(selectedSpace.Value,tr)!;
        var space=(BlockTableRecord)tr.GetObject(selectedSpace.Value,OpenMode.ForRead);
        if(options.Text("bounds_json") is {} boundsJson)
        {
            using var parsed=JsonDocument.Parse(boundsJson);var b=parsed.RootElement;
            var min=new Point3d(EditPlan.Point(b.GetProperty("min")));var max=new Point3d(EditPlan.Point(b.GetProperty("max")));
            if(min.X>max.X||min.Y>max.Y||min.Z>max.Z)throw new CadFault("INVALID_BOUNDS","Bounds min must not exceed max");bounds=new(min,max);
        }
        if(bounds is null)foreach(ObjectId id in space)
        {
            ct.ThrowIfCancellationRequested();if(tr.GetObject(id,OpenMode.ForRead) is not Entity entity||(entity is Viewport overall&&ReadingMetadata.IsOverallViewport(overall,tr)))continue;
            var layer=(LayerTableRecord)tr.GetObject(entity.LayerId,OpenMode.ForRead);
            if(entity.Visible&&!layer.IsOff&&!layer.IsFrozen)Add(entity);
        }
        if(bounds is null)throw new CadFault("PREVIEW_EMPTY","No frameable geometry in the requested space");
        string name=options.Text("view_name")??"current";
        Vector3d direction=name switch{"current" or "top"=>Vector3d.ZAxis,"front"=>new(0,-1,0),"back"=>new(0,1,0),
            "left"=>new(-1,0,0),"right"=>new(1,0,0),"isometric"=>new(1,-1,1),_=>throw new CadFault("INVALID_VIEW","current/front/back/left/right/top/isometric")};
        double twist=0;
        if(name=="current"&&layout.ModelType&&doc.Database.CurrentSpaceId==selectedSpace.Value)
        {
            if(!ReferenceEquals(doc,Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument))throw new CadFault("EDITOR_VIEW_UNAVAILABLE","Use explicit view_name for an inactive DWG preview");
            using var current=doc.Editor.GetCurrentView();direction=current.ViewDirection;twist=current.ViewTwist;
        }
        if(options.Text("view_direction_json") is {} vector){using var parsed=JsonDocument.Parse(vector);direction=new Vector3d(EditPlan.Point(parsed.RootElement));twist=0;}
        if(direction.Length<1e-10)throw new CadFault("INVALID_VIEW","View direction must be nonzero");direction=direction.GetNormal();
        if(!layout.ModelType&&(Math.Abs(direction.Z)<1-1e-9||twist!=0))throw new CadFault("PAPER_VIEW_UNSUPPORTED","Paper layout preview uses current/top; choose Model for 3D views");
        var box=bounds.Value;var target=new Point3d((box.MinPoint.X+box.MaxPoint.X)/2,(box.MinPoint.Y+box.MaxPoint.Y)/2,(box.MinPoint.Z+box.MaxPoint.Z)/2);
        var rotation=Matrix3d.Rotation(-twist,direction,Point3d.Origin)*Matrix3d.PlaneToWorld(direction);
        var worldToPlane=(Matrix3d.Displacement(target-Point3d.Origin)*rotation).Inverse();
        var corners=(from x in new[]{box.MinPoint.X,box.MaxPoint.X} from y in new[]{box.MinPoint.Y,box.MaxPoint.Y} from z in new[]{box.MinPoint.Z,box.MaxPoint.Z}
            select new Point3d(x,y,z).TransformBy(worldToPlane)).ToArray();
        double frameWidth=Math.Max(1e-6,corners.Max(p=>p.X)-corners.Min(p=>p.X))*1.08,frameHeight=Math.Max(1e-6,corners.Max(p=>p.Y)-corners.Min(p=>p.Y))*1.08;
        frameWidth=Math.Max(frameWidth,frameHeight*width/height);frameHeight=frameWidth*height/width;
        var up=Vector3d.YAxis.TransformBy(rotation);var position=target+direction*(Math.Max(frameWidth,frameHeight)*2+1);
        // GS can regenerate dimension blocks even during a snapshot. Render cloned drawables,
        // so native graphics initialization cannot dirty the live database or its Undo history.
        // Force copy semantics: Wblock must never move entities out of the source database.
        DiagnosticTrace?.Invoke("clone start");
        doc.Database.ForceWblockDatabaseCopy();
        using var drawingCopy=doc.Database.Wblock();
        DiagnosticTrace?.Invoke("clone complete");
        using var copyTransaction=drawingCopy.TransactionManager.StartOpenCloseTransaction();
        var copyLayout=ReadingMetadata.LayoutByName(drawingCopy,copyTransaction,layout.LayoutName);
        var renderedSpace=(BlockTableRecord)copyTransaction.GetObject(copyLayout.BlockTableRecordId,OpenMode.ForRead);
        var renderedModel=(BlockTableRecord)copyTransaction.GetObject(((BlockTable)copyTransaction.GetObject(drawingCopy.BlockTableId,OpenMode.ForRead))[BlockTableRecord.ModelSpace],OpenMode.ForRead);
        var copyLayers=(LayerTable)copyTransaction.GetObject(drawingCopy.LayerTableId,OpenMode.ForRead);
        var previousWorkingDatabase=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=drawingCopy;
        var descriptor=new Autodesk.AutoCAD.GraphicsSystem.KernelDescriptor();
        descriptor.addRequirement(Autodesk.AutoCAD.UniqueString.Intern("3D Drawing"));
        Autodesk.AutoCAD.GraphicsSystem.GraphicsKernel? kernel=null;
        var viewportReports=new List<object>();
        try
        {
            kernel=Autodesk.AutoCAD.GraphicsSystem.Manager.AcquireGraphicsKernel(descriptor);
            var bitmap=Draw(renderedSpace,width,height,position,target,up,frameWidth,frameHeight,null,null);
            if(!layout.ModelType)
            {
                using var paper=bitmap;bitmap=new System.Drawing.Bitmap(width,height);
                try
                {
                    using var graphics=System.Drawing.Graphics.FromImage(bitmap);graphics.Clear(System.Drawing.Color.White);
                    var viewports=space.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Viewport>().Where(v=>!ReadingMetadata.IsOverallViewport(v,tr)&&(v.On||v.Number<0)&&v.ViewHeight>0&&v.Width>0&&v.Height>0).ToArray();
                    if(viewports.Length>32)throw new CadFault("PREVIEW_VIEWPORT_LIMIT","Frame a region containing at most 32 model viewports");
                    foreach(var v in viewports)
                    {
                        ct.ThrowIfCancellationRequested();
                        double left=width/2d+(v.CenterPoint.X-v.Width/2-target.X)*width/frameWidth;
                        double top=height/2d-(v.CenterPoint.Y+v.Height/2-target.Y)*height/frameHeight;
                        double pw=v.Width*width/frameWidth,ph=v.Height*height/frameHeight;
                        if(left+pw<0||top+ph<0||left>width||top>height)continue;
                        if(v.PerspectiveOn){viewportReports.Add(new{handle=v.Handle.ToString(),status="unsupported_perspective"});continue;}
                        var matrix=ReadingMetadata.DcsToWcs(v);var center=new Point3d(v.ViewCenter.X,v.ViewCenter.Y,0).TransformBy(matrix);
                        var dir=v.ViewDirection.GetNormal();var vpUp=Vector3d.YAxis.TransformBy(matrix);
                        using var pixels=Draw(renderedModel,Math.Clamp((int)Math.Ceiling(pw),1,2048),Math.Clamp((int)Math.Ceiling(ph),1,2048),center+dir*(Math.Max(v.ViewHeight,v.ViewHeight*v.Width/v.Height)*2+1),center,vpUp,v.ViewHeight*v.Width/v.Height,v.ViewHeight,v.GetFrozenLayers().Cast<ObjectId>().Select(id=>((LayerTableRecord)tr.GetObject(id,OpenMode.ForRead)).Name).Where(name=>copyLayers.Has(name)).Select(name=>copyLayers[name]).ToArray(),v);
                        var saved=graphics.Save();string clip="rectangular";
                        try
                        {
                            graphics.SetClip(new System.Drawing.RectangleF((float)left,(float)top,(float)pw,(float)ph));
                            if(v.NonRectClipOn&&!v.NonRectClipEntityId.IsNull)
                            {
                                if(tr.GetObject(v.NonRectClipEntityId,OpenMode.ForRead) is Curve curve&&curve.Closed)
                                {
                                    using var outline=new System.Drawing.Drawing2D.GraphicsPath();
                                    var points=Enumerable.Range(0,257).Select(i=>curve.GetPointAtParameter(curve.StartParam+(curve.EndParam-curve.StartParam)*i/256d)).Select(p=>new System.Drawing.PointF((float)(width/2d+(p.X-target.X)*width/frameWidth),(float)(height/2d-(p.Y-target.Y)*height/frameHeight))).ToArray();
                                    outline.AddPolygon(points);graphics.SetClip(outline,System.Drawing.Drawing2D.CombineMode.Intersect);clip="sampled_native_boundary";
                                }
                                else{viewportReports.Add(new{handle=v.Handle.ToString(),status="unsupported_clip"});continue;}
                            }
                            graphics.DrawImage(pixels,new System.Drawing.RectangleF((float)left,(float)top,(float)pw,(float)ph));
                        }
                        finally{graphics.Restore(saved);}
                        viewportReports.Add(new{handle=v.Handle.ToString(),status="rendered",clip,live_on=v.On,runtime_active=v.Number>=0});
                    }
                    // Native paper geometry overlays the viewport images. White masks need plot validation.
                    using var attributes=new System.Drawing.Imaging.ImageAttributes();attributes.SetColorKey(System.Drawing.Color.White,System.Drawing.Color.White);
                    graphics.DrawImage(paper,new System.Drawing.Rectangle(0,0,width,height),0,0,width,height,System.Drawing.GraphicsUnit.Pixel,attributes);
                }
                catch{bitmap.Dispose();throw;}
            }
            metadata=new{layout_name=layout.LayoutName,space=layout.ModelType?"model":"paper",space_handle=space.Handle.ToString(),
                width=frameWidth,height=frameHeight,target=new[]{target.X,target.Y,target.Z},direction=new[]{direction.X,direction.Y,direction.Z},
                twist,perspective=false,live_view_changed=false,source="AutoCAD.GraphicsSystem.offscreen",viewports=viewportReports,
                fidelity=layout.ModelType?"native graphics view":"native model viewport composition; inactive viewport On/Number do not prove saved visibility; sampled nonrectangular clipping; paper white masks, plot styles and overlapping viewports require PDF validation"};return bitmap;
        }
        finally{try{if(kernel is not null)Autodesk.AutoCAD.GraphicsSystem.Manager.ReleaseGraphicsKernel(kernel);}finally{HostApplicationServices.WorkingDatabase=previousWorkingDatabase;}}
        System.Drawing.Bitmap Draw(BlockTableRecord drawable,int pixelWidth,int pixelHeight,Point3d camera,Point3d lookAt,Vector3d vertical,double vw,double vh,ObjectId[]? frozen,Viewport? viewport)
        {
            DiagnosticTrace?.Invoke("draw start "+drawable.Name+" "+(viewport?.Handle.ToString()??"frame"));
            using var device=doc.GraphicsManager.CreateAutoCADOffScreenDevice(kernel!);
            using var model=doc.GraphicsManager.CreateAutoCADModel(kernel!);
            using var view=doc.GraphicsManager.CreateAutoCADView(kernel!,drawable);
            device.OnSize(new System.Drawing.Size(pixelWidth,pixelHeight));device.BackgroundColor=System.Drawing.Color.White;device.Add(view);
            try
            {
                view.SetView(camera,lookAt,vertical,vw,vh,Autodesk.AutoCAD.GraphicsSystem.Projection.Parallel);
                using var visualStyle=new Autodesk.AutoCAD.GraphicsInterface.VisualStyle(Autodesk.AutoCAD.GraphicsInterface.VisualStyleType.Wireframe2D);
                view.VisualStyle=visualStyle;
                if(frozen is not null)foreach(var id in frozen)view.FreezeLayer(id.OldIdPtr);
                if(viewport is not null)
                {
                    view.EnableFrontClip=viewport.FrontClipOn;view.FrontClip=viewport.FrontClipDistance;
                    view.EnableBackClip=viewport.BackClipOn;view.BackClip=viewport.BackClipDistance;
                }
                // Snapshot generation otherwise traverses the whole model for every sheet viewport.
                // Conservatively reject only entities whose complete projected extents miss the frame.
                var normal=(camera-lookAt).GetNormal();var right=vertical.CrossProduct(normal).GetNormal();
                var verticalNormal=normal.CrossProduct(right).GetNormal();int added=0;
                foreach(ObjectId id in drawable)
                {
                    ct.ThrowIfCancellationRequested();
                    if(copyTransaction.GetObject(id,OpenMode.ForRead) is not Entity entity||!entity.Visible)continue;
                    var layer=(LayerTableRecord)copyTransaction.GetObject(entity.LayerId,OpenMode.ForRead);
                    if(layer.IsOff||layer.IsFrozen||(frozen is not null&&frozen.Contains(entity.LayerId)))continue;
                    try
                    {
                        var e=entity.GeometricExtents;
                        var projected=(from x in new[]{e.MinPoint.X,e.MaxPoint.X} from y in new[]{e.MinPoint.Y,e.MaxPoint.Y} from z in new[]{e.MinPoint.Z,e.MaxPoint.Z}
                            let delta=new Point3d(x,y,z)-lookAt select new Point2d(delta.DotProduct(right),delta.DotProduct(verticalNormal))).ToArray();
                        if(projected.Max(p=>p.X)<-vw/2||projected.Min(p=>p.X)>vw/2||projected.Max(p=>p.Y)<-vh/2||projected.Min(p=>p.Y)>vh/2)continue;
                    }
                    catch(Autodesk.AutoCAD.Runtime.Exception){/* Unknown extents stay in the rendering set. */}
                    view.Add(entity,model);added++;
                }
                DiagnosticTrace?.Invoke("update start "+added+" entities");device.Update();DiagnosticTrace?.Invoke("update complete");ct.ThrowIfCancellationRequested();
                var snapshot=device.GetSnapshot(new System.Drawing.Rectangle(0,0,pixelWidth,pixelHeight));DiagnosticTrace?.Invoke("snapshot complete");return snapshot;
            }
            finally{DiagnosticTrace?.Invoke("draw cleanup start");view.EraseAll();device.Erase(view);DiagnosticTrace?.Invoke("draw cleanup complete");}
        }
        void Add(Entity entity)
        {try{var e=entity.GeometricExtents;if(bounds is null)bounds=e;else{var joined=bounds.Value;joined.AddExtents(e);bounds=joined;}}catch(Autodesk.AutoCAD.Runtime.Exception){}}
    }
#endif
}

