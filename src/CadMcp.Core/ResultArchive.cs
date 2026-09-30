using System.Text.Json;

namespace CadMcp.Core;

/// <summary>Bounded archive of large read results, always identified as historical.</summary>
public sealed class ResultArchive(string directory)
{
    public sealed record Stored(string Session, string Document, long Revision, JsonElement Value);
    public string Put(string session, string document, long revision, object value)
    {
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Stored(session, document, revision, Wire.Element(value)), Wire.Json);
        if (bytes.Length > 16 * 1024 * 1024) throw new CadFault("RESULT_TOO_LARGE", "Result exceeds 16 MiB; use narrower search or entity pages");
        var files = Directory.GetFiles(directory, "*.json").Select(p => new FileInfo(p)).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long size = files.Sum(f => f.Length); int count = files.Count;
        foreach (var file in files)
        {
            if (count < 64 && size + bytes.Length <= 64 * 1024 * 1024) break;
            size -= file.Length; count--; file.Delete();
        }
        string id = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(directory, id + ".json"), bytes);
        return id;
    }
    public object Read(string id, string session, string document, int offset, int limit)
    {
        if (id.Length != 32 || !id.All(Portable.AsciiHexDigit)) throw new CadFault("INVALID_ARCHIVE_ID", "Expected archive id returned by a CAD read");
        if (offset < 0 || limit is < 1 or > 32000) throw new CadFault("INVALID_PAGE", "offset >= 0; limit 1..32000 characters");
        var path = Path.Combine(directory, id + ".json");
        if (!File.Exists(path)) throw new CadFault("ARCHIVE_NOT_FOUND", "Read the drawing again; archived result expired");
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Wire.Json)!;
        if (stored.Session != session || stored.Document != document) throw new CadFault("DOCUMENT_MISMATCH", "Archived result belongs to another session or drawing");
        var json = stored.Value.GetRawText();
        if (offset > json.Length) throw new CadFault("INVALID_PAGE", "Offset exceeds archived result length");
        return new { archive_id = id, captured_revision = stored.Revision, historical = true, format = "json_text", total_characters = json.Length,
            text = json.Substring(offset, Math.Min(limit, json.Length - offset)), next_offset = offset + limit < json.Length ? (int?)(offset + limit) : null };
    }
}
