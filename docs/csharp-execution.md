# Произвольный C#

`cad_csharp` компилирует C# 12 и выполняет его в закреплённом документе. Готовые операции `cad_edit` предпочтительны для обычной геометрии. Компилятор поставляется с MCP host; SDK .NET и Visual Studio не требуются. References берутся из framework/AutoCAD/загруженных managed-сборок текущего CAD-процесса, компиляция проходит вне AutoCAD.

С версии 0.11.6 кандидаты references проверяются по CLR-метаданным: native DLL без .NET metadata, повреждённые и недоступные файлы пропускаются. Они больше не вызывают общий сбой «PE image does not have metadata». Если управляемых ссылок нет, инструмент останавливается до отправки CAD-операции.

Параметры: `session_id`, `document_id`, свежая `expected_revision`, уникальный `operation_id`, полный `code`:

```csharp
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
public static class CadMcpScript
{
    public static object Run(Database database, Transaction transaction,
                             CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var model = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        using var line = new Line(new Point3d(0,0,0), new Point3d(100,0,0));
        model.AppendEntity(line);
        transaction.AddNewlyCreatedDBObject(line, true);
        return new { handle = line.Handle.ToString() };
    }
}
```

Работайте в переданных database/transaction, возвращайте DTO/handles, не живые DBObjects. Не вызывайте CAD API на фоновых потоках, не выдавайте запланированную `SendStringToExecute` команду за завершённую. Вызов синхронный; отмена кооперативная. Проверяйте токен в длинных циклах: нативный kernel нельзя безопасно прервать принудительно.

До загрузки сборки обязательна DWG-копия; при ошибке checkpoint код не запускается. Ответ/квитанция содержат checkpoint, SHA-256 исходника и изменённые handles. Ошибка компиляции не запускает код. Неопределённый исход проверяется по исходному operation_id, без повтора с новым id. Результат перечитывается через cad_entity_get/search/verify.

Код работает с полным доверием процесса. Ошибка откатывает предоставленную host-транзакцию, но не гарантирует откат собственных commit, файлов, внешних API и обработчиков событий. Checkpoint сохраняет базу DWG, а не все внешние файлы. Не закрывайте/сохраняйте посторонние документы. Readonly-помощники не могут вызывать cad_csharp.

Основная сессия Codex получает workspace-write в выбранной рабочей папке и сетевой доступ; readonly-профили помощников сохранены. Права папки позволяют хранить исходники, но выполнение внутри CAD обеспечивает именно cad_csharp.
