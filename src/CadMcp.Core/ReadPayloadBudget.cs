using System.Text;
using System.Text.Json;

namespace CadMcp.Core;

/// <summary>Budgets apply while materializing entities, not after collecting an entire drawing.</summary>
public static class ReadPayloadBudget
{
    public const int EntityBytes = 256 * 1024;
    public const int SnapshotBytes = 8 * 1024 * 1024;
    public const int MeshVertices = 2000;
    public const int MeshFaces = 2000;
    public const int MeshFaceIndices = 16000;

    public static (int Offset, int Count, int? Next) VertexPage(int total, int offset, int limit)
    {
        if (offset < 0 || offset > total || limit is < 1 or > MeshVertices)
            throw new CadFault("INVALID_VERTEX_PAGE", "vertex_offset must be within the mesh; vertex_limit must be 1..2000");
        int count = Math.Min(limit, total - offset);
        return (offset, count, offset + count < total ? offset + count : null);
    }

    public static int Bytes(JsonElement entity) => Encoding.UTF8.GetByteCount(entity.GetRawText());
    public static JsonElement BoundEntity(JsonElement entity)
    {
        if (Bytes(entity) <= EntityBytes) return entity;
        // Retain scope and measurable scalar facts; never label clipped geometry as complete.
        var fields = entity.EnumerateObject().Where(p => p.Name is "handle" or "type" or "layer" or "class_name" or "dxf_name"
            or "owner_handle" or "space" or "space_handle" or "layout_name" or "bounds" or "vertex_count" or "face_count" or "length" or "area" or "volume" or "radius")
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        fields["access"] = "partial"; fields["geometry_complete"] = false;
        fields["limitation"] = "entity_payload_limit; request bounded geometry or text pages";
        return Wire.Element(fields);
    }
}
