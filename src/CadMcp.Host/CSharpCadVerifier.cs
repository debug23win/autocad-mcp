using CadMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CadMcp.Host;

// A conservative preflight for ordinary scripts, not a sandbox for hostile full-trust code.
internal static class CSharpCadVerifier
{
    internal static void Verify(CSharpCompilation compilation)
    {
        var script = compilation.GetTypeByMetadataName("CadMcpScript");
        string[] parameters = ["Autodesk.AutoCAD.DatabaseServices.Database", "Autodesk.AutoCAD.DatabaseServices.Transaction", "System.Threading.CancellationToken"];
        if (script is null || script.DeclaredAccessibility != Accessibility.Public || !script.IsStatic ||
            !script.GetMembers("Run").OfType<IMethodSymbol>().Any(m => m.IsStatic && !m.IsGenericMethod && !m.IsAsync &&
                m.DeclaredAccessibility == Accessibility.Public && m.ReturnType.SpecialType == SpecialType.System_Object &&
                m.Parameters.Length == 3 && m.Parameters.Select(p => p.Type.ToDisplayString()).SequenceEqual(parameters) &&
                m.Parameters.All(p => p.RefKind == RefKind.None)))
            throw new CadFault("CSHARP_ENTRYPOINT", "Expected public static class CadMcpScript with synchronous public static object Run(Database, Transaction, CancellationToken)");

        var errors = new HashSet<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                string? reason = null;
                if (node is MemberDeclarationSyntax member && member.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                    reason = "Unsafe code is not allowed in CAD scripts";
                if (node is UnsafeStatementSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax)
                    reason = "Unsafe code is not allowed in CAD scripts";
                if (node is SimpleNameSyntax identifier)
                {
                    if (model.GetTypeInfo(identifier).Type?.TypeKind == TypeKind.Dynamic)
                        reason = "Dynamic dispatch cannot be verified before CAD execution";
                    var symbol = model.GetSymbolInfo(identifier).Symbol;
                    string type = (symbol as INamedTypeSymbol ?? symbol?.ContainingType)?.ToDisplayString() ?? "";
                    string name = symbol?.Name ?? "";
                    if (type.StartsWith("Autodesk.AutoCAD.", StringComparison.Ordinal))
                    {
                        if (name is "WblockCloneObjects" or "DeepCloneObjects" or "Wblock" or "DeepClone" or "Clone")
                            reason = "Direct AutoCAD cloning requires a reviewed native adapter with database/ObjectId ownership checks";
                        if (name is "Commit" or "Abort" or "StartTransaction" or "StartOpenCloseTransaction" or "Dispose")
                            reason = "Use the supplied transaction; the plugin owns its lifetime and commit. Explicit CAD Dispose is also blocked";
                        if (name is "SaveAs" or "DxfOut" or "CloseAndSave" or "CloseAndDiscard" or "SendStringToExecute" or "ExecuteInCommandContextAsync")
                            reason = "Scripts must not save/close documents or schedule CAD commands outside the plugin transaction";
                    }
                    if (type.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal) ||
                        type is "System.Threading.Thread" or "System.Threading.ThreadPool" or "System.Threading.Timer" ||
                        type.StartsWith("System.Runtime.InteropServices.", StringComparison.Ordinal))
                        reason = "Background execution, reflection and native interop cannot be verified for CAD execution";
                    bool runtimeTypeLabel = type == "System.Type" && symbol is IPropertySymbol { Name: "Name" or "FullName" };
                    if ((type.StartsWith("System.Reflection.", StringComparison.Ordinal) || runtimeTypeLabel) && !IsRuntimeTypeLabel(identifier, symbol, model))
                        reason = "Reflection cannot be verified for CAD execution. For a type label read value.GetType().Name or value.GetType().FullName directly; for AutoCAD objects use value.GetRXClass().DxfName";
                }
                if (reason is not null)
                {
                    var location = node.GetLocation().GetLineSpan().StartLinePosition;
                    errors.Add($"({location.Line + 1},{location.Character + 1}): {reason}");
                }
            }
        }
        if (errors.Count > 0) throw new CadFault("CSHARP_PREFLIGHT_FAILED", string.Join("\n", errors.Take(20)));
    }

    private static bool IsRuntimeTypeLabel(SimpleNameSyntax identifier, ISymbol? symbol, SemanticModel model)
    {
        // Type.Name is declared on System.Reflection.MemberInfo. Reading its string directly
        // does not expose a reflection handle or authorize invoking any other member.
        if (symbol is not IPropertySymbol { Name: "Name" or "FullName", Type.SpecialType: SpecialType.System_String } ||
            identifier.Parent is not MemberAccessExpressionSyntax access || access.Name != identifier)
            return false;
        ExpressionSyntax receiver = access.Expression;
        while (receiver is ParenthesizedExpressionSyntax parentheses) receiver = parentheses.Expression;
        return receiver is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } invocation &&
            model.GetSymbolInfo(invocation).Symbol is IMethodSymbol
            {
                Name: "GetType", IsStatic: false, IsGenericMethod: false,
                ContainingType.SpecialType: SpecialType.System_Object, Parameters.Length: 0
            };
    }
}
