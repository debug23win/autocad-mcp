using CadMcp.Core;
using CadMcp.Host;

namespace CadMcp.Tests;

public sealed class CSharpTypeInspectionTests
{
    // API-shaped stubs resolve the same framework symbols without executing any CAD code.
    private const string Prefix = """
        using System;
        using System.Threading;
        using Autodesk.AutoCAD.DatabaseServices;
        namespace Autodesk.AutoCAD.Runtime {
            public class RXClass { public string DxfName => "DATABASE"; }
        }
        namespace Autodesk.AutoCAD.DatabaseServices {
            public class Database { public Autodesk.AutoCAD.Runtime.RXClass GetRXClass() => new(); }
            public class Transaction { }
        }
        public class ShadowType { public new Type GetType() => typeof(Database); }
        public static class CadMcpScript {
            public static object Run(Database database, Transaction transaction, CancellationToken cancellationToken) {
        """;

    private static byte[] Compile(string body) => CSharpReferenceCompiler.Compile(Prefix + body + "} }",
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator), verifyCad: true);

    [Theory]
    [InlineData("return new { type = database.GetType().Name };")]
    [InlineData("return new { type = database.GetType().FullName };")]
    [InlineData("return new { type = ((object)database).GetType().Name };")]
    [InlineData("return new { type = (database.GetType()).Name };")]
    [InlineData("return new { dxf = database.GetRXClass().DxfName };")]
    [InlineData("return database.GetType() == typeof(Database);")]
    public void Immediate_runtime_type_labels_and_native_DXF_labels_compile(string body) => Assert.NotEmpty(Compile(body));

    [Theory]
    [InlineData("var type = database.GetType(); return type.Name;")]
    [InlineData("var type = database.GetType(); return type.FullName;")]
    [InlineData("return typeof(Database).FullName;")]
    [InlineData("return new ShadowType().GetType().Name;")]
    [InlineData("return database.GetType().GetMethod(\"ToString\")!.Name;")]
    [InlineData("var label = database.GetType().Name; return database.GetType().GetMethod(\"ToString\")!.Invoke(database, null);")]
    [InlineData("return System.Reflection.Assembly.GetExecutingAssembly().FullName;")]
    public void Type_label_exception_does_not_authorize_reflection_or_a_method_with_the_same_name(string body)
    {
        var error = Assert.Throws<CadFault>(() => Compile(body));
        Assert.Equal("CSHARP_PREFLIGHT_FAILED", error.Code);
        Assert.Contains("Reflection cannot be verified", error.Message);
        Assert.Contains("GetRXClass().DxfName", error.Message);
    }

    [Theory]
    [InlineData("var label = database.GetType().Name; return System.Threading.Tasks.Task.Run(() => label);")]
    [InlineData("var label = database.GetType().Name; System.Threading.ThreadPool.QueueUserWorkItem(_ => { }); return label;")]
    [InlineData("var label = database.GetType().Name; return System.Runtime.InteropServices.Marshal.ReadInt32(IntPtr.Zero);")]
    public void Type_labels_do_not_relax_background_execution_or_native_interop_checks(string body)
    {
        var error = Assert.Throws<CadFault>(() => Compile(body));
        Assert.Equal("CSHARP_PREFLIGHT_FAILED", error.Code);
    }
}
