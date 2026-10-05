using System.Text;
using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

public static class CadResultSummary
{
    public static string[] ChangedHandles(JsonElement operations) => operations.EnumerateArray()
        .Where(o=>o.Text("state")=="completed"&&o.Text("operation")=="cad_edit")
        .SelectMany(o=>o.TryGetProperty("handles",out var handles)&&handles.ValueKind==JsonValueKind.Array?handles.EnumerateArray().Select(h=>h.GetString()!):[])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string Describe(JsonElement data, JsonElement? review = null)
    {
        if (!data.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() == 0) return "";
        var items=operations.EnumerateArray().ToArray(); var text=new StringBuilder("**Проверка плагина:**\n\n");
        int completed=items.Count(o=>o.Text("state")=="completed"), failed=items.Count(o=>o.Text("state")=="failed"), pending=items.Length-completed-failed;
        text.Append("Операций подтверждено: ").Append(completed).Append("; ошибок: ").Append(failed).Append("; ожидают завершения или проверки: ").Append(pending).Append(".\n");
        if(data.TryGetProperty("truncated",out var truncated)&&truncated.ValueKind==JsonValueKind.True)text.Append("История операций ограничена; автоматическая проверка всего ответа может быть неполной. Требуется итоговая проверка всех объектов задания.\n");
        foreach(var item in items.Where(i=>i.TryGetProperty("error",out _)).Take(3))
            text.Append("\n- Ошибка AutoCAD: ").Append(item.GetProperty("error").Text("message")?.Replace('\n',' ')).Append('.');
        if(review is { } result && result.TryGetProperty("verification",out var verification))
        {
            text.Append("\n\nВ текущем DWG перечитано объектов: ").Append(verification.Number("entity_count",0)).Append("; удалённых: ").Append(verification.Number("erased_count",0)).Append('.');
            if(result.TryGetProperty("turn_geometry",out var turn))text.Append(" За весь ответ проверено живых объектов: ").Append(turn.Number("entity_count",0)).Append("; удалённых: ").Append(turn.Number("erased_count",0)).Append(". Размерные критерии ниже относятся к последней группе изменений.");
            if(verification.Text("state")=="not_requested") text.Append(" Размерные критерии для этой проверки не заданы.");
            else {
                text.Append(" Размерных проверок пройдено: ").Append(verification.Number("passed",0)).Append("; нарушено: ").Append(verification.Number("failed",0)).Append("; не проверено: ").Append(verification.Number("unverified",0)).Append('.');
                if(verification.TryGetProperty("checks",out var checks)) foreach(var check in checks.EnumerateArray().Take(12))
                    text.Append("\n- ").Append(check.Text("label")?.Replace('\n',' ')).Append(": ")
                        .Append(check.Text("status") switch {"passed"=>"подтверждено","failed"=>"не соответствует",_=>"не проверено"})
                        .Append(check.TryGetProperty("actual",out var actual)?"; факт "+actual.GetRawText():"")
                        .Append(check.TryGetProperty("expected",out var expected)?"; задано "+expected.GetRawText():"");
            }
            if(result.TryGetProperty("document_state",out var doc)) text.Append("\n\nСохранение DWG: ").Append(doc.Text("disk_save") switch {"saved"=>"подтверждено по текущему состоянию AutoCAD и файлу", "unsaved"=>"есть несохранённые изменения", _=>"не подтверждено"}).Append('.');
            if(verification.TryGetProperty("visual_requirements",out var visual)&&visual.GetArrayLength()>0)
            {
                text.Append("\n\nВизуальные требования нуждаются в проверке по изображениям:");
                foreach(var feature in visual.EnumerateArray())text.Append("\n- ").Append(feature.GetString());
            }
        }
        else text.Append("\n\nТекущее состояние геометрии и сохранение DWG не подтверждены.");
        if(pending>0)text.Append(" Принятые операции могут ещё выполняться; повторять построение нельзя.");
        return text.ToString();
    }
}
