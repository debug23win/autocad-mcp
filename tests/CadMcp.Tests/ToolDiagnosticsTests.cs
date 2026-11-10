using CadMcp.Core;
using CadMcp.Providers;
namespace CadMcp.Tests;
public sealed class ToolDiagnosticsTests
{
    private static System.Text.Json.JsonElement Result(object data, bool isError = false) => Wire.Element(new { isError, content = new[] { new { type = "text", text = System.Text.Json.JsonSerializer.Serialize(data) } } });
    [Fact]
    public void Pre_execution_error_explains_stage_code_operation_and_narrow_write_guarantee()
    {
        var result = Result(new { code = "CSHARP_FAILED", message = "PE image does not have metadata", changes_started = false }, true);
        string text = ToolDiagnostics.Describe("cad_csharp", result, false, Wire.Element(new { operation_id = "op-123", code = "PRIVATE_SOURCE" }))!;
        Assert.Contains("cad_csharp", text); Assert.Contains("CSHARP_FAILED", text); Assert.Contains("PE image", text); Assert.Contains("op-123", text);
        Assert.Contains("Этот вызов не начал запись", text); Assert.DoesNotContain("PRIVATE_SOURCE", text); Assert.DoesNotContain("чертёж не изменён", text);
    }
    [Fact]
    public void Failed_native_receipt_is_reported_even_when_the_MCP_wrapper_says_completed()
    {
        var result = Result(new { request_id = "req-1", status = "failed", data = new { operation_id = "op-2", partial_changes_possible = true }, error = new { code = "CAD_ERROR", message = "Native failure" } });
        string text = ToolDiagnostics.Describe("cad_csharp", result, false)!;
        Assert.Contains("CAD_ERROR", text); Assert.Contains("req-1", text); Assert.Contains("op-2", text); Assert.Contains("Исход записи не подтверждён", text); Assert.Contains("автоматически повторять", text);
    }
    [Fact]
    public void Transport_failure_and_plain_error_text_are_available_without_a_tool_receipt()
    {
        string text = ToolDiagnostics.Describe("cad_edit", default, false, transportError: Wire.Element(new { code = -32000, message = "Transport disconnected" }))!;
        Assert.Contains("-32000", text); Assert.Contains("Transport disconnected", text); Assert.Contains("Исход записи не подтверждён", text);
    }
    [Fact]
    public void Claude_string_results_preserve_error_codes_and_read_only_scope()
    {
        string text = ToolDiagnostics.Describe("mcp__cad__cad_search", Wire.Element(new { content = "{\"code\":\"DOCUMENT_CLOSED\",\"message\":\"Drawing closed\"}" }), true)!;
        Assert.Contains("DOCUMENT_CLOSED", text); Assert.Contains("Инструмент чтения", text);
    }
    [Fact]
    public void Successful_and_intentionally_partial_reads_do_not_produce_false_error_cards()
    {
        Assert.Null(ToolDiagnostics.Describe("cad_search", Result(new { status = "completed", data = new { text = "error: test", code = "product code" } }), false));
        Assert.Null(ToolDiagnostics.Describe("cad_snapshot", Result(new { status = "partial", truncated = true }), false));
    }
    [Fact]
    public void Failed_PDF_image_is_visible_even_when_text_extraction_succeeded()
    {
        string text = ToolDiagnostics.Describe("cad_pdf_read", Result(new { text = "readable", render_error = "missing renderer" }), false)!;
        Assert.Contains("PDF_RENDER_FAILED", text); Assert.Contains("missing renderer", text); Assert.Contains("Инструмент чтения", text);
    }
    [Fact]
    public void Echoed_passwords_are_redacted_and_diagnostics_are_bounded()
    {
        string text = ToolDiagnostics.Describe("cad_pdf_read", Result(new { code = "PDF_ERROR", message = "secret-value " + new string('x', 12000) }), false, Wire.Element(new { password = "secret-value", code = "PRIVATE_SOURCE" }))!;
        Assert.DoesNotContain("secret-value", text); Assert.DoesNotContain("PRIVATE_SOURCE", text); Assert.Contains("[скрыто]", text); Assert.True(text.Length < 2500);
    }
    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public async Task Providers_report_the_problem_before_any_final_answer_or_turn_completion(string name)
    {
        IChatProvider provider = name == "Codex" ? new CodexProvider(TestEnvironment.ProviderOptions) : new ClaudeProvider(TestEnvironment.ProviderOptions);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<ChatEvent>();
        await foreach (var item in provider.SendAsync("TOOL_PROBLEM", timeout.Token))
        {
            seen.Add(item);
            if (item.Kind != "problem") continue;
            Assert.Contains("CSHARP_FAILED", item.Text); Assert.Contains("PE image", item.Text); Assert.Contains("op-visible", item.Text);
            Assert.DoesNotContain(seen, e => e.Kind is "text" or "completed");
            return;
        }
        Assert.Fail("Provider ended without reporting the tool problem");
    }
}
