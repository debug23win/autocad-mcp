using CadMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CadMcp.Host;

internal static class CSharpReferenceCompiler
{
    internal static byte[] Compile(string source, IEnumerable<string> references)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 262144) throw new CadFault("INVALID_CSHARP", "C# source must contain 1..262144 characters");
        // TPA references arrive first; avoid importing another plugin's private copy of the same framework assembly.
        var paths = references.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(p => System.Reflection.AssemblyName.GetAssemblyName(p).Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        if (paths.Length == 0) throw new CadFault("CSHARP_REFERENCES_UNAVAILABLE", "AutoCAD did not provide compilation references");
        var compilation = CSharpCompilation.Create("CadMcpUser_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12))],
            paths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true));
        using var output = new MemoryStream(); var result = compilation.Emit(output);
        if (!result.Success) throw new CadFault("CSHARP_COMPILE_FAILED", string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20)));
        if (output.Length > 4 * 1024 * 1024) throw new CadFault("CSHARP_TOO_LARGE", "Compiled assembly exceeds 4 MiB");
        return output.ToArray();
    }
}
