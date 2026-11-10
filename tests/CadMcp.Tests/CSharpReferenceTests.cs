using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Host;
namespace CadMcp.Tests;

public sealed class CSharpReferenceTests
{
    private static string[] Framework => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
    internal static string NativeLibrary(TempFolder folder)
    {
        // A PE32+ native DLL header with no CLR metadata directory; no executable code.
        var bytes = new byte[512];
        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, 0x8664); U16(0x94, 240); U16(0x96, 0x2022); U16(0x98, 0x20b);
        U32(0xb8, 4096); U32(0xbc, 512); U32(0xd0, 4096); U32(0xd4, 512); U32(0x104, 16);
        string path = folder.File("native.dll"); File.WriteAllBytes(path, bytes);
        using var pe = new PEReader(File.OpenRead(path)); Assert.NotNull(pe.PEHeaders.PEHeader); Assert.False(pe.HasMetadata);
        return path;
    }
    [Fact]
    public void Native_PE_in_AutoCAD_reference_inventory_does_not_prevent_real_compilation()
    {
        using var folder = new TempFolder(); string native = NativeLibrary(folder);
        byte[] assembly = CSharpReferenceCompiler.Compile("public static class CadMcpScript { public static int Value => 7; }", new[] { native }.Concat(Framework));
        using var pe = new PEReader(new MemoryStream(assembly)); Assert.True(pe.HasMetadata); Assert.True(pe.GetMetadataReader().IsAssembly);
    }
    [Fact]
    public void Only_native_or_damaged_references_report_unavailable_without_starting_execution()
    {
        using var folder = new TempFolder(); string native = NativeLibrary(folder), damaged = folder.File("damaged.dll");
        File.WriteAllText(damaged, "not a PE file");
        var error = Assert.Throws<CadFault>(() => CSharpReferenceCompiler.Compile("public class Script {}", [native, damaged, folder.File("missing.dll")]));
        Assert.Equal("CSHARP_REFERENCES_UNAVAILABLE", error.Code);
    }
    [Fact]
    public void Invalid_source_still_reports_real_compiler_diagnostics_with_native_candidates_present()
    {
        using var folder = new TempFolder(); string native = NativeLibrary(folder);
        var error = Assert.Throws<CadFault>(() => CSharpReferenceCompiler.Compile("public class {", new[] { native }.Concat(Framework)));
        Assert.Equal("CSHARP_COMPILE_FAILED", error.Code); Assert.Contains("CS", error.Message);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Actual_MCP_compiles_mixed_inventory_or_stops_before_a_CAD_mutation(bool managedReferences)
    {
        using var folder = new TempFolder(); string native = NativeLibrary(folder), pipe = "csharp-reference-" + Guid.NewGuid().ToString("N");
        var calls = new List<string>();
        using var broker = new PipeServer(pipe, (r, ct) =>
        {
            calls.Add(r.Operation);
            if (r.Operation == "cad_operation_status") return Task.FromResult(new Response(r.RequestId, "completed", new { state = "not_found" }, "s", "d"));
            if (r.Operation == "csharp_references") return Task.FromResult(new Response(r.RequestId, "completed", new { references = managedReferences ? new[] { native }.Concat(Framework).ToArray() : new[] { native } }, "s", "d"));
            Assert.Equal("cad_csharp", r.Operation);
            using var pe = new PEReader(new MemoryStream(Convert.FromBase64String(r.Data.Text("assembly_base64")!))); Assert.True(pe.HasMetadata);
            return Task.FromResult(new Response(r.RequestId, "completed", new { operation_id = "reference-test", submitted = true }, "s", "d"));
        });
        broker.Start(); await using var host = await McpHostProcess.StartAsync(pipe);
        var response = await host.CallAsync("cad_csharp", new { session_id = "s", document_id = "d", expected_revision = 1, operation_id = "reference-test", code = "public static class CadMcpScript { public static int Value => 7; }" });
        using var json = JsonDocument.Parse(response.GetProperty("content")[0].GetProperty("text").GetString()!);
        if (managedReferences) { Assert.False(response.TryGetProperty("isError", out var failed) && failed.GetBoolean()); Assert.Equal(new[] { "cad_operation_status", "csharp_references", "cad_csharp" }, calls); }
        else { Assert.Equal(new[] { "cad_operation_status", "csharp_references" }, calls); Assert.Equal("CSHARP_REFERENCES_UNAVAILABLE", json.RootElement.Text("code")); Assert.False(json.RootElement.GetProperty("changes_started").GetBoolean()); Assert.False(json.RootElement.GetProperty("requires_reconciliation").GetBoolean()); }
    }
}
