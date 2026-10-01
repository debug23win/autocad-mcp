namespace CadMcp.Providers;

public static class CadToolActivity
{
    public static string Describe(string eventText)
    {
        bool failed = eventText.StartsWith("Ошибка:", StringComparison.Ordinal);
        if (failed) return "AutoCAD сообщил об ошибке; проверяю результат";
        if (eventText.Contains("cad_verify", StringComparison.OrdinalIgnoreCase)) return "Сверяю реальные размеры и сохранение DWG";
        if (eventText.Contains("cad_reference_compare", StringComparison.OrdinalIgnoreCase)) return "Сравниваю силуэт с изображением";
        if (eventText.Contains("cad_reference_", StringComparison.OrdinalIgnoreCase)) return "Привязываю изображение к координатам";
        if (eventText.Contains("cad_edit_help", StringComparison.OrdinalIgnoreCase)) return "Проверяю доступные команды AutoCAD";
        if (eventText.Contains("cad_render", StringComparison.OrdinalIgnoreCase)) return "Проверяю вид чертежа";
        if (eventText.Contains("cad_edit", StringComparison.OrdinalIgnoreCase) ||
            eventText.Contains("cad_lisp", StringComparison.OrdinalIgnoreCase)) return "Изменяю чертёж";
        if (eventText.Contains("cad_export", StringComparison.OrdinalIgnoreCase) ||
            eventText.Contains("cad_publish", StringComparison.OrdinalIgnoreCase)) return "Готовлю файлы";
        if (eventText.Contains("cad_operation_status", StringComparison.OrdinalIgnoreCase) || eventText.Contains("cad_operation_list", StringComparison.OrdinalIgnoreCase)) return "Проверяю завершение операции";
        if (eventText.Contains("cad_", StringComparison.OrdinalIgnoreCase)) return "Изучаю чертёж";
        return "Работаю с AutoCAD";
    }

    public static bool ProducesVisibleResult(string eventText) =>
        eventText.StartsWith("Завершено:", StringComparison.Ordinal) &&
        (eventText.Contains("cad_edit", StringComparison.OrdinalIgnoreCase) ||
         eventText.Contains("cad_lisp", StringComparison.OrdinalIgnoreCase) ||
         eventText.Contains("cad_render", StringComparison.OrdinalIgnoreCase));
}
