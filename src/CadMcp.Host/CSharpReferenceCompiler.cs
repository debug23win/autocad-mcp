using CadMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CadMcp.Host;

internal static class CSharpReferenceCompiler
{
    internal static byte[] Compile(string source, IEnumerable<string> references, bool verifyCad = false)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 262144) throw new CadFault("INVALID_CSHARP", "C# source must contain 1..262144 characters");
        // TPA references arrive first; avoid importing another plugin's private copy of the same framework assembly.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new List<PortableExecutableReference>();
        foreach (string path in references.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var file = File.OpenRead(path);
                using var pe = new PEReader(file);
                // AutoCAD's runtime inventory can include native PE companions. They are not
                // compiler references; querying AssemblyName on them aborts all compilation.
                if (!pe.HasMetadata) continue;
                var reader = pe.GetMetadataReader();
                if (!reader.IsAssembly) continue;
                string name = reader.GetString(reader.GetAssemblyDefinition().Name);
                if (names.Contains(name)) continue;
                metadata.Add(MetadataReference.CreateFromFile(path)); names.Add(name);
            }
            catch (Exception error) when (error is BadImageFormatException or IOException or UnauthorizedAccessException) { }
        }
        if (metadata.Count == 0) throw new CadFault("CSHARP_REFERENCES_UNAVAILABLE", "AutoCAD did not provide readable managed compilation references");
        var compilation = CSharpCompilation.Create("CadMcpUser_" + Portable.Hash(System.Text.Encoding.UTF8.GetBytes(source)),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12))],
            metadata.OrderBy(m => m.Display, StringComparer.OrdinalIgnoreCase),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true, deterministic: true));
        using var output = new MemoryStream(); var result = compilation.Emit(output);
        if (!result.Success) throw new CadFault("CSHARP_COMPILE_FAILED", string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20)));
        if (verifyCad) CSharpCadVerifier.Verify(compilation);
        if (output.Length > 4 * 1024 * 1024) throw new CadFault("CSHARP_TOO_LARGE", "Compiled assembly exceeds 4 MiB");
        return output.ToArray();
    }
}
