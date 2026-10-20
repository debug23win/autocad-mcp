using System.Text;

namespace CadMcp.Core;

public static class ProjectIdentity
{
    public static string ForDrawing(string name, string unsavedDocumentId) => Path.IsPathFullyQualified(name)
        ? Portable.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(name).ToUpperInvariant())) : unsavedDocumentId;
}
