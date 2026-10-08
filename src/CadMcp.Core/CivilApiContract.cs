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
        "Autodesk.Civil.DatabaseServices.Styles.PartFamily|P String Description {get;}",
        "Autodesk.Civil.DatabaseServices.Alignment|M static ObjectId Create(CivilDocument,String,ObjectId,ObjectId,ObjectId,ObjectId)",
        "Autodesk.Civil.DatabaseServices.Alignment|M ObjectIdCollection GetProfileIds()",
        "Autodesk.Civil.DatabaseServices.Alignment|M ObjectIdCollection GetSampleLineGroupIds()",
        "Autodesk.Civil.DatabaseServices.Alignment|P Double StartingStation {get;}",
        "Autodesk.Civil.DatabaseServices.Alignment|P Double EndingStation {get;}",
        "Autodesk.Civil.DatabaseServices.Alignment|P String StyleName {get;set;}",
        "Autodesk.Civil.DatabaseServices.Network|M ObjectIdCollection GetPipeIds()",
        "Autodesk.Civil.DatabaseServices.Network|M ObjectIdCollection GetStructureIds()",
        "Autodesk.Civil.DatabaseServices.TinSurface|M TerrainSurfaceProperties GetTerrainProperties()",
        "Autodesk.Civil.DatabaseServices.Styles.StyleBase|P String Name {get;set;}",
        "Autodesk.Civil.DatabaseServices.Styles.PartFamily|P DomainType Domain {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P AlignmentStyleCollection AlignmentStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P CorridorStyleCollection CorridorStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P FeatureLineStyleCollection FeatureLineStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P PipeRuleSetStyleCollection PipeRuleSetStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P PipeStyleCollection PipeStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P PointStyleCollection PointStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P ProfileViewStyleCollection ProfileViewStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P SampleLineStyleCollection SampleLineStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P SectionStyleCollection SectionStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P StructureRuleSetStyleCollection StructureRuleSetStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P StructureStyleCollection StructureStyles {get;}",
        "Autodesk.Civil.DatabaseServices.Styles.StylesRoot|P SurfaceStyleCollection SurfaceStyles {get;}",
        // Read fail-closed before an edit: a data shortcut reference is refused.
        "Autodesk.Civil.DatabaseServices.Entity|P Boolean IsReferenceObject {get;}",
        // The active drawing's Civil document, with a fallback by database.
        "Autodesk.Civil.ApplicationServices.CivilApplication|P static CivilDocument ActiveDocument {get;}",
        // Part size names from the size data record.
        "Autodesk.Civil.DatabaseServices.Styles.PartSize|P PartDataRecord SizeDataRecord {get;set;}",
        "Autodesk.Civil.DatabaseServices.PartDataRecord|M PartDataField GetDataFieldBy(String)",
        "Autodesk.Civil.DatabaseServices.PartDataRecord|M PartDataField[] GetAllDataFields()",
        "Autodesk.Civil.DatabaseServices.PartDataField|P Object Value {get;set;}",
        // Pipe and structure properties civil_set accepts or the reader shows.
        "Autodesk.Civil.DatabaseServices.Pipe|P Point3d StartPoint {get;set;}",
        "Autodesk.Civil.DatabaseServices.Pipe|P Point3d EndPoint {get;set;}",
        "Autodesk.Civil.DatabaseServices.Pipe|P Double InnerDiameterOrWidth {get;}",
        "Autodesk.Civil.DatabaseServices.Pipe|P Double InnerHeight {get;}",
        "Autodesk.Civil.DatabaseServices.Structure|P Double RimElevation {get;set;}",
        "Autodesk.Civil.DatabaseServices.Structure|P Double SumpElevation {get;set;}",
        // Surface statistics.
        "Autodesk.Civil.DatabaseServices.GeneralSurfaceProperties|P Double MaximumElevation {get;}",
        "Autodesk.Civil.DatabaseServices.GeneralSurfaceProperties|P Double MinimumElevation {get;}",
        "Autodesk.Civil.DatabaseServices.GeneralSurfaceProperties|P Int32 NumberOfPoints {get;}",
        "Autodesk.Civil.DatabaseServices.TinSurfaceProperties|P Int32 NumberOfTriangles {get;}"
    ];

    /// <summary>
    /// Names the Civil code uses on any Civil object, so no one type's dump confirms them, with the reason each is safe. A test requires
    /// every member name in the Civil code paths to be in <see cref="Members"/> or here, so a typo cannot pass silently.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Unchecked = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["StyleId"] = "style id accepted by civil_set on any Civil object; checked by the vendor setter",
        ["AlignmentName"] = "display property read when present and skipped otherwise",
        ["ProfileName"] = "display property read when present and skipped otherwise",
        ["SurfaceName"] = "display property read when present and skipped otherwise",
        ["StartStation"] = "display property read when present and skipped otherwise",
        ["EndStation"] = "display property read when present and skipped otherwise",
        ["Elevation"] = "display property read when present and skipped otherwise",
        ["Area"] = "display property read when present and skipped otherwise"
    };
}
