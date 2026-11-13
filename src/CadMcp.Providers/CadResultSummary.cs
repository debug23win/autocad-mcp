using System.Text;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

public static class CadResultSummary
{
    public static bool AppliesToLiveDrawing(JsonElement operation) => operation.Text("execution_mode") != "isolated_acad" ||
        operation.TryGetProperty("applied_to_original", out var applied) && applied.ValueKind == JsonValueKind.True;
    /// <summary>Preview accepts top-level entities in one space; definition geometry is still verified, but never framed directly.</summary>
    public static string[] PreviewHandles(IEnumerable<JsonElement> entities, string? currentSpaceHandle)
    {
        var live = entities.Where(e => !e.TryGetProperty("erased", out var erased) || erased.ValueKind != JsonValueKind.True)
            .Where(e => e.Text("space") is "model" or "paper").ToArray();
        string? space = live.FirstOrDefault(e => e.Text("owner_handle") == currentSpaceHandle).Text("owner_handle")
            ?? live.FirstOrDefault().Text("owner_handle");
        return live.Where(e => e.Text("owner_handle") == space).Select(e => e.Text("handle")).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
    }
    public static string[] ChangedHandles(JsonElement operations) => operations.EnumerateArray()
        .Where(o=>AppliesToLiveDrawing(o)&&o.Text("state")=="completed"&&o.Text("operation") is "cad_edit" or "cad_csharp")
        .SelectMany(o=>o.TryGetProperty("handles",out var handles)&&handles.ValueKind==JsonValueKind.Array?handles.EnumerateArray().Select(h=>h.GetString()!):[])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string Describe(JsonElement data, JsonElement? review = null)
    {
        if (!data.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() == 0) return "";
        var items=operations.EnumerateArray().ToArray(); var text=new StringBuilder(UiText.T("**Проверка плагина:**\n\n"));
        int completed=items.Count(o=>o.Text("state")=="completed"), failed=items.Count(o=>o.Text("state")=="failed"), pending=items.Length-completed-failed;
        text.Append(UiText.T("Операций подтверждено: ")).Append(completed).Append(UiText.T("; ошибок: ")).Append(failed).Append(UiText.T("; ожидают завершения или проверки: ")).Append(pending).Append(".\n");
        foreach (var isolated in items.Where(o => o.Text("execution_mode") == "isolated_acad"))
        {
            text.Append(UiText.T("C# выполнен в отдельном процессе AutoCAD; скрипт в основном процессе повторно не запускается.\n"));
            bool applied = isolated.TryGetProperty("applied_to_original", out var appliedValue) && appliedValue.ValueKind == JsonValueKind.True;
            text.Append(applied ? UiText.T("Рабочий чертёж заменён проверенным DWG; исходная копия сохранена для восстановления.\n") : UiText.T("Замена рабочего чертежа не подтверждена; проверьте квитанцию и сохранённый результат.\n"));
            if (isolated.TryGetProperty("output_file", out var output) && output.ValueKind == JsonValueKind.Object)
                text.Append(UiText.T("Отдельный DWG: ")).Append(output.Text("path")).Append("\nSHA-256: ").Append(output.Text("sha256")).Append('\n');
            if (isolated.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                text.Append(error.Text("code")).Append(": ").Append(error.Text("message")).Append('\n');
            if (isolated.Text("exit_code_hex") is { } exit) text.Append("Exit: ").Append(exit).Append('\n');
            if (isolated.Text("artifacts_directory") is { } artifacts) text.Append(UiText.T("Диагностика дочернего процесса: ")).Append(artifacts).Append('\n');
        }
        if (items.All(o => !AppliesToLiveDrawing(o))) return text.ToString();
        if(data.TryGetProperty("truncated",out var truncated)&&truncated.ValueKind==JsonValueKind.True)text.Append(UiText.T("История операций ограничена; автоматическая проверка всего ответа может быть неполной. Требуется итоговая проверка всех объектов задания.\n"));
        if(items.Any(o=>o.TryGetProperty("handles_truncated",out var limited)&&limited.ValueKind==JsonValueKind.True))
            text.Append(UiText.T("Перечень изменённых объектов неполон; полная проверка операции не подтверждена.\n"));
        foreach(var item in items.Where(i=>i.TryGetProperty("error",out _)).Take(3))
            text.Append(UiText.T("\n- Ошибка AutoCAD: ")).Append(item.GetProperty("error").Text("message")?.Replace('\n',' ')).Append('.');
        if(review is { } result && result.TryGetProperty("verification",out var verification))
        {
            text.Append(UiText.T("\n\nВ текущем DWG перечитано объектов: ")).Append(verification.Number("entity_count",0)).Append(UiText.T("; удалённых: ")).Append(verification.Number("erased_count",0)).Append('.');
            if(result.TryGetProperty("turn_geometry",out var turn))text.Append(UiText.T(" За весь ответ проверено живых объектов: ")).Append(turn.Number("entity_count",0)).Append(UiText.T("; удалённых: ")).Append(turn.Number("erased_count",0)).Append(UiText.T(". Размерные критерии ниже относятся к последней группе изменений."));
            if(verification.Text("state")=="not_requested") text.Append(UiText.T(" Размерные критерии для этой проверки не заданы."));
            else {
                text.Append(UiText.T(" Размерных проверок пройдено: ")).Append(verification.Number("passed",0)).Append(UiText.T("; нарушено: ")).Append(verification.Number("failed",0)).Append(UiText.T("; не проверено: ")).Append(verification.Number("unverified",0)).Append('.');
                if(verification.TryGetProperty("checks",out var checks)) foreach(var check in checks.EnumerateArray().Take(12))
                    text.Append("\n- ").Append(check.Text("label")?.Replace('\n',' ')).Append(": ")
                        .Append(check.Text("status") switch {"passed"=>UiText.T("подтверждено"),"failed"=>UiText.T("не соответствует"),_=>UiText.T("не проверено")})
                        .Append(check.TryGetProperty("actual",out var actual)?UiText.T("; факт ")+actual.GetRawText():"")
                        .Append(check.TryGetProperty("expected",out var expected)?UiText.T("; задано ")+expected.GetRawText():"");
            }
            if(result.TryGetProperty("document_state",out var doc)) text.Append(UiText.T("\n\nСохранение DWG: ")).Append(doc.Text("disk_save") switch {"saved"=>UiText.T("подтверждено по текущему состоянию AutoCAD и файлу"), "unsaved"=>UiText.T("есть несохранённые изменения"), _=>UiText.T("не подтверждено")}).Append('.');
            if(verification.TryGetProperty("visual_requirements",out var visual)&&visual.GetArrayLength()>0)
            {
                text.Append(UiText.T("\n\nВизуальные требования нуждаются в проверке по изображениям:"));
                foreach(var feature in visual.EnumerateArray())text.Append("\n- ").Append(feature.GetString());
            }
        }
        else text.Append(UiText.T("\n\nТекущее состояние геометрии и сохранение DWG не подтверждены."));
        if(pending>0)text.Append(UiText.T(" Принятые операции могут ещё выполняться; повторять построение нельзя."));
        return text.ToString();
    }
}
