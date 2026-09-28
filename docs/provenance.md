# Происхождение кода и аудит этапа 3А

Новый код: Apache-2.0, согласовано пользователем 28 сентября 2026.

## Фактические заимствования

Источник: [beiming183-cloud/AutoCAD-MCP](https://github.com/beiming183-cloud/AutoCAD-MCP/tree/11f7c47e5038796a20451b38b23032e625b5aa26), commit `11f7c47e5038796a20451b38b23032e625b5aa26`.

| Исходный файл | Наш файл | Изменения |
|---|---|---|
| native/AutoCADMcp.Plugin/PipeServer.cs | src/CadMcp.Core/Frames.cs | Общий транспорт, строгая проверка неполных кадров, лимит исходящих данных, отмена, одна операция на подключение |
| native/AutoCADMcp.Plugin/DocumentRegistry.cs | src/CadMcp.AutoCAD/Documents.cs | Контракт чтения, отписка от событий, удаление подавления событий изменений |

Оба исходных файла не содержат отдельных лицензий. Корневой LICENSE — MIT, Copyright (c) 2024 AutoCAD MCP Server Contributors. Полный текст сохранён, в изменённых файлах стоят уведомления. В исходном дереве на указанном commit отдельного NOTICE не обнаружено. Сторонние ресурсы, логотипы, примеры DWG и upstream-зависимости этих файлов не импортировались.

ClaudeRevit служил архитектурным примером чата и интеграции официальных CLI; код Revit в продукт не перенесён. Наши адаптеры написаны по официальным протоколам, с отдельной реализацией отмены процесса. U-C4N, puran-water, Slacker-LLC и felixalmesberger остаются кандидатами для последующих этапов. Их код не следует считать уже объединённым в этом прототипе.

## Библиотеки

ModelContextProtocol 1.4.1 и его зависимости зафиксированы в `packages.lock.json`. `licenses/dependencies.json` перечисляет фактически разрешённые NuGet-пакеты с хешами и заявленными лицензиями. Microsoft-пакеты декларируют MIT; тексты лицензий и дополнительных уведомлений из пакетов сохранены.

У MCP SDK пакет декларирует Apache-2.0, но upstream LICENSE на теге v1.4.1 дополнительно описывает переход с MIT; сохранён весь текст в `licenses/mcp-sdk-LICENSE.txt`. Это учитывается вместо упрощённого утверждения, что весь сторонний код теперь Apache-2.0.

AutoCAD managed DLL используются как локальные ссылки с `Private=false`; пакет сборки не должен включать их. СПДС, модели и официальные CLI не встраиваются. Лицензионные условия их использования остаются у соответствующих поставщиков.

Аудит относится к перечисленным импортам и текущим зависимостям. Перед новыми заимствованиями и публичным релизом список обновляется. Полный аудит всех исследованных репозиториев не заявляется.

## Источники реализации

- [Codex App Server](https://learn.chatgpt.com/docs/app-server); схемы дополнительно сверены с локальным CLI 0.154.0 через generate-json-schema, без запуска сервера.
- [Claude Code headless](https://code.claude.com/docs/en/headless).
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk/tree/v1.4.1).
- [AutoCAD CapturePreviewImage](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_ApplicationServices_DocumentExtension_CapturePreviewImage_this_Document_uint_modoptIsLong_uint_modoptIsLong.html).
