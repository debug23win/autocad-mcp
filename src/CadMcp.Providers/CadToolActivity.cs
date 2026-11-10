using CadMcp.Core;
namespace CadMcp.Providers;

public static class CadToolActivity
{
    public static string Describe(string tool, string phase) => phase == "failed" ? UiText.T("Инструмент сообщил об ошибке; диагностика в чате") : Describe(tool);
    public static bool ProducesVisibleResult(string tool, string phase) => phase == "completed" &&
        new[] { "cad_edit", "cad_edit_preview", "cad_lisp", "cad_csharp", "cad_render" }.Contains(tool.Split("__", StringSplitOptions.None)[^1], StringComparer.OrdinalIgnoreCase);
    public static string Describe(string eventText)
    {
        bool failed = eventText.StartsWith(UiText.T("Ошибка:"), StringComparison.Ordinal);
        if (failed) return UiText.T("Инструмент сообщил об ошибке; диагностика в чате");
        if (eventText.Contains("cad_pdf_read", StringComparison.OrdinalIgnoreCase)) return UiText.T("Читаю текст и страницы PDF");
        if (eventText.Contains("cad_web_download", StringComparison.OrdinalIgnoreCase)) return UiText.T("Скачиваю найденный материал");
        if (eventText.Contains("cad_csharp", StringComparison.OrdinalIgnoreCase)) return UiText.T("Компилирую и выполняю C#; затем проверю результат");
        if (eventText.Contains("cad_verify", StringComparison.OrdinalIgnoreCase)) return UiText.T("Сверяю реальные размеры и сохранение DWG");
        if (eventText.Contains("cad_reference_compare", StringComparison.OrdinalIgnoreCase)) return UiText.T("Сравниваю силуэт с изображением");
        if (eventText.Contains("cad_reference_", StringComparison.OrdinalIgnoreCase)) return UiText.T("Привязываю изображение к координатам");
        if (eventText.Contains("cad_edit_help", StringComparison.OrdinalIgnoreCase)) return UiText.T("Проверяю доступные команды AutoCAD");
        if (eventText.Contains("cad_render", StringComparison.OrdinalIgnoreCase)) return UiText.T("Проверяю вид чертежа");
        if (eventText.Contains("cad_edit", StringComparison.OrdinalIgnoreCase) ||
            eventText.Contains("cad_lisp", StringComparison.OrdinalIgnoreCase)) return UiText.T("Изменяю чертёж");
        if (eventText.Contains("cad_export", StringComparison.OrdinalIgnoreCase) ||
            eventText.Contains("cad_publish", StringComparison.OrdinalIgnoreCase)) return UiText.T("Готовлю файлы");
        if (eventText.Contains("cad_operation_status", StringComparison.OrdinalIgnoreCase) || eventText.Contains("cad_operation_list", StringComparison.OrdinalIgnoreCase)) return UiText.T("Проверяю завершение операции");
        if (eventText.Contains("cad_", StringComparison.OrdinalIgnoreCase)) return UiText.T("Изучаю чертёж");
        return UiText.T("Работаю с AutoCAD");
    }

    public static bool ProducesVisibleResult(string eventText) =>
        eventText.StartsWith(UiText.T("Завершено:"), StringComparison.Ordinal) &&
        (eventText.Contains("cad_edit", StringComparison.OrdinalIgnoreCase) ||
         eventText.Contains("cad_lisp", StringComparison.OrdinalIgnoreCase) ||
         eventText.Contains("cad_csharp", StringComparison.OrdinalIgnoreCase) ||
         eventText.Contains("cad_render", StringComparison.OrdinalIgnoreCase));
}
