namespace CadMcp.Core;

/// <summary>
/// Civil 3D members that CAD MCP calls by reflection, in the normalized form of the published signature dumps
/// (type|kind return name(parameter types)). A unit test checks every entry against the dumps of each supported
/// release, so a wrong overload (for example Point2d where the API takes Point3d) fails offline.
/// </summary>
public static class CivilApiContract
{
    public static readonly IReadOnlyList<string> Members =
    [
        "Autodesk.Civil.ApplicationServices.CivilDocument|M static CivilDocument GetCivilDocument(Database)",
        "Autodesk.Civil.ApplicationServices.CivilDocument|M ObjectIdCollection GetSurfaceIds()",
        "Autodesk.Civil.ApplicationServices.CivilDocument|M ObjectIdCollection GetAlignmentIds()",
        "Autodesk.Civil.ApplicationServices.CivilDocument|M ObjectIdCollection GetPipeNetworkIds()",
        "Autodesk.Civil.ApplicationServices.CivilDocument|M ObjectIdCollection GetSiteIds()",
        "Autodesk.Civil.ApplicationServices.CivilDocument|P StylesRoot Styles {get;}",
        "Autodesk.Civil.DatabaseServices.Alignment|M static ObjectId Create(CivilDocument,String,String,String,String,String)",
        "Autodesk.Civil.DatabaseServices.Alignment|P AlignmentEntityCollection Entities {get;}",
        "Autodesk.Civil.DatabaseServices.Alignment|P Double Length {get;}",
        "Autodesk.Civil.DatabaseServices.AlignmentEntityCollection|M AlignmentLine AddFixedLine(Point3d,Point3d)",
        "Autodesk.Civil.DatabaseServices.AlignmentEntityCollection|M AlignmentArc AddFreeCurve(Int32,Int32,Double,CurveParamType,Boolean,CurveType)",
        "Autodesk.Civil.DatabaseServices.AlignmentEntityCollection|M AlignmentEntity GetEntityByOrder(Int32)",
        "Autodesk.Civil.DatabaseServices.AlignmentEntityCollection|P Int32 Count {get;}",
        "Autodesk.Civil.DatabaseServices.AlignmentEntity|P Int32 EntityId {get;}",
        "Autodesk.Civil.DatabaseServices.AlignmentArc|P Double Radius {get;set;}",
        "Autodesk.Civil.DatabaseServices.Profile|M static ObjectId CreateByLayout(String,ObjectId,ObjectId,ObjectId,ObjectId)",
        "Autodesk.Civil.DatabaseServices.Profile|M static ObjectId CreateFromSurface(String,ObjectId,ObjectId,ObjectId,ObjectId,ObjectId)",
        "Autodesk.Civil.DatabaseServices.Profile|P ProfileEntityCollection Entities {get;}",
        "Autodesk.Civil.DatabaseServices.ProfileEntityCollection|M ProfileTangent AddFixedTangent(Point2d,Point2d)",
        "Autodesk.Civil.DatabaseServices.Network|M static ObjectId Create(CivilDocument,String&)",
        "Autodesk.Civil.DatabaseServices.Network|M Void AddLinePipe(ObjectId,ObjectId,LineSegment3d,ObjectId&,Boolean)",
        "Autodesk.Civil.DatabaseServices.Network|M Void AddStructure(ObjectId,ObjectId,Point3d,Double,ObjectId&,Boolean)",
        "Autodesk.Civil.DatabaseServices.Network|P ObjectId PartsListId {get;set;}",
        "Autodesk.Civil.DatabaseServices.Network|P ObjectId ReferenceSurfaceId {get;set;}",
        "Autodesk.Civil.DatabaseServices.Network|P ObjectId ReferenceAlignmentId {get;set;}",
        "Autodesk.Civil.DatabaseServices.TinSurface|M static ObjectId Create(Database,String)",
        "Autodesk.Civil.DatabaseServices.TinSurface|M SurfaceOperationAddTinMultipleVertices AddVertices(Point3dCollection)",
        "Autodesk.Civil.DatabaseServices.TinSurface|M TinSurfaceProperties GetTinProperties()",
        "Autodesk.Civil.DatabaseServices.Surface|M Double FindElevationAtXY(Double,Double)",
        "Autodesk.Civil.DatabaseServices.Surface|M GeneralSurfaceProperties GetGeneralProperties()",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P ProfileStyleCollection ProfileStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P LabelSetStylesRoot LabelSetStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P PartsListCollection PartsListSet {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StyleCollectionBase|P ObjectId Item[String styleName] {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.PartsList|P ObjectId Item[String description] {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.PartsList|P Int32 PartFamilyCount {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.PartsList|P ObjectId Item[Int32 index] {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.PartFamily|P ObjectId Item[String description] {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.PartFamily|P ObjectId Item[Int32 index] {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.PartFamily|P Int32 PartSizeCount {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.PartFamily|P String Description {get;}"
    ];
}
