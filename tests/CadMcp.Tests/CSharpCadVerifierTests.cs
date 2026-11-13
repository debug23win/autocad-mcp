using CadMcp.Core;
using CadMcp.Host;

namespace CadMcp.Tests;

public sealed class CSharpCadVerifierTests
{
    // API-shaped stubs exercise symbol resolution without loading AutoCAD on the test host.
    private const string Prefix = """
        using System;
        using System.Threading;
        using Db = Autodesk.AutoCAD.DatabaseServices.Database;
        using Autodesk.AutoCAD.DatabaseServices;
        namespace Autodesk.AutoCAD.DatabaseServices {
            public class Database { public void WblockCloneObjects() {} }
            public class Transaction { public void Commit() {} }
        }
        public static class CadMcpScript {
            public static object Run(Db database, Transaction transaction, CancellationToken cancellationToken) {
        """;

    private static byte[] Compile(string body) => CSharpReferenceCompiler.Compile(Prefix + body + "} }",
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator), verifyCad: true);

    [Fact]
    public void Safe_synchronous_dto_compiles() => Assert.NotEmpty(Compile("cancellationToken.ThrowIfCancellationRequested(); return new { ok = true };"));

    [Theory]
    [InlineData("database.WblockCloneObjects(); return null;")]
    [InlineData("Action clone = database.WblockCloneObjects; clone(); return null;")]
    [InlineData("transaction.Commit(); return null;")]
    [InlineData("System.Threading.Tasks.Task.Run(() => database.WblockCloneObjects()); return null;")]
    [InlineData("dynamic db = database; db.WblockCloneObjects(); return null;")]
    [InlineData("unsafe { int n = 0; int* p = &n; } return null;")]
    public void Dangerous_calls_are_rejected_before_execution(string body)
    {
        var error = Assert.Throws<CadFault>(() => Compile(body));
        Assert.Equal("CSHARP_PREFLIGHT_FAILED", error.Code);
    }

    [Fact]
    public void Wrong_entrypoint_is_rejected_before_execution()
    {
        var error = Assert.Throws<CadFault>(() => CSharpReferenceCompiler.Compile("public static class CadMcpScript { public static object Run() => null; }",
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator), verifyCad: true));
        Assert.Equal("CSHARP_ENTRYPOINT", error.Code);
    }
}
