using System.Text.Json;
using CadMcp.Core;
namespace CadMcp.Providers;

/// <summary>Selected error fields only; never dump tool arguments, source code or image content.</summary>
public static class ToolDiagnostics
{
    private static JsonElement Child(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    private static string? Text(JsonElement value, string name) => Child(value, name).ValueKind == JsonValueKind.String ? Child(value, name).GetString() : null;
    private static bool True(JsonElement value, string name) => Child(value, name).ValueKind == JsonValueKind.True;
    public static string? Describe(string tool, JsonElement result, bool failed, JsonElement arguments = default, JsonElement transportError = default)
    {
        failed |= True(result, "isError") || transportError.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        JsonElement payload = default, error = transportError;
        string? plain = null;
        var candidates = new List<JsonElement>();
        if (Child(result, "structuredContent") is { ValueKind: JsonValueKind.Object } structured) candidates.Add(structured);
        var content = Child(result, "content");
        if (content.ValueKind == JsonValueKind.String)
        {
            plain = content.GetString();
            if (plain is { Length: <= 2097152 })
                try { using var json = JsonDocument.Parse(plain); candidates.Add(json.RootElement.Clone()); } catch (JsonException) { }
        }
        if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray().Take(8))
            {
                string? text = Text(block, "text"); if (text is null) continue;
                plain ??= text;
                if (text.Length > 2 * 1024 * 1024) continue;
                try { using var json = JsonDocument.Parse(text); candidates.Add(json.RootElement.Clone()); } catch (JsonException) { }
            }
        foreach (var candidate in candidates)
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            if (Text(candidate, "status") == "failed" && Child(candidate, "error").ValueKind == JsonValueKind.Object)
            { failed = true; payload = candidate; error = Child(candidate, "error"); break; }
            if (Text(candidate, "code") is not null && Text(candidate, "message") is not null)
            { failed = true; payload = error = candidate; break; }
            if (Text(candidate, "render_error") is { Length: > 0 } renderError)
            { failed = true; payload = candidate; error = Wire.Element(new { code = "PDF_RENDER_FAILED", message = renderError }); break; }
        }
        if (!failed) return null;
        string message = error.ValueKind == JsonValueKind.String ? error.GetString()! : Text(error, "message") ?? plain ?? "Инструмент не передал подробности ошибки.";
        // Only an explicit false confirms no write began. A failed full-trust operation may have side effects.
        var data = Child(payload, "data");
        var started = Child(payload, "changes_started"); if (started.ValueKind == JsonValueKind.Undefined) started = Child(data, "changes_started");
        string? operation = Text(payload, "operation_id") ?? Text(data, "operation_id") ?? Text(arguments, "operation_id");
        string? request = Text(payload, "request_id");
        string code = Text(error, "code") ?? (Child(error, "code").ValueKind == JsonValueKind.Number ? Child(error, "code").GetRawText() : "TOOL_ERROR");
        string normalized = tool.Contains("mcp__", StringComparison.Ordinal) ? tool[(tool.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : tool;
        string state = !CadOperations.IsMutation(normalized) && (CadOperations.ReadOnly.Contains(normalized) || CadOperations.HostTools.Contains(normalized))
            ? "Инструмент чтения; состояние DWG этим сообщением не подтверждается."
            : started.ValueKind == JsonValueKind.False ? "Этот вызов не начал запись в DWG."
            : "Исход записи не подтверждён. Проверить квитанцию и DWG; автоматически повторять правку нельзя.";
        // Password-like arguments are never shown, even if an error echoed their value.
        if (arguments.ValueKind == JsonValueKind.Object)
            foreach (var field in arguments.EnumerateObject().Where(p => p.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("token", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)))
                if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() is { Length: > 0 } secret) message = message.Replace(secret, "[скрыто]", StringComparison.Ordinal);
        static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "…";
        return "Инструмент/этап: " + Limit(tool, 120) + "\nКод: " + Limit(code, 120) + "\nОшибка: " + Limit(message, 1600)
            + (operation is null ? "" : "\nОперация: " + Limit(operation, 160)) + (request is null ? "" : "\nЗапрос: " + Limit(request, 160)) + "\n" + state;
    }
}
