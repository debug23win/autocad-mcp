# Происхождение кода и аудит этапа 3А

## Дополнение после обзора проектов AutoCAD MCP (октябрь 2026)

Код из этих проектов не копировался, кроме указанных данных; идеи реализованы заново.

| Источник | Применение |
|---|---|
| [HorizunGroup/horizun-civil3d-mcp](https://github.com/HorizunGroup/horizun-civil3d-mcp/tree/b019448ca45664931529012e1d5ffb2b71ef6f68), Apache-2.0 | Формат дампов сигнатур Civil 3D и первые данные 2024–2026 взяты из `docs/api-probes` (без имён параметров) для офлайн-проверки `CivilApiContract`; теперь `tests/CadMcp.Tests/Fixtures/civil-api-signatures.txt` снимается инструментом `tests/CadMcp.CivilSignatures` с метаданных официальных пакетов Autodesk Civil3D.NET 2024–2027 (`scripts/civil-signatures.ps1`) и для общих типов совпадает с дампами Horizun построчно. Живые наблюдения проекта использованы как сведения: кривые трассы через `AddFreeCurve(…, CurveParamType.Radius, false, CurveType.Compound)`, имя размера детали в поле `PrtSN`. Лицензия: `licenses/horizun-civil3d-mcp-Apache-2.0.txt` |
| [seb21-art/MCP_AutocadMap3D](https://github.com/seb21-art/MCP_AutocadMap3D/tree/18932d47255a421889fd1e255ba78ab350b7afd3), MIT | Подход к назначению системы координат Map 3D: проверка кода по каталогу CS-MAP, перевод EPSG, сверка границ чертежа с областью применения. Переписан на позднее связывание в `MapCoordinateSystems` (VerticalEditing.cs). Лицензия: `licenses/seb21-art-MIT.txt` |
| CHMOSE023/AutoCAD-MCP, ling5477/CAD-MAX (MIT) | Идеи: пробуждение очереди `WM_NULL` после постановки, отказ при модальном диалоге и закрытии AutoCAD (Dispatcher.cs) |
| bimwright/dwg-mcp (Apache-2.0), iwanschelokov-byte/AutoCAD_mcp | Идеи: предупреждающий сканер AutoLISP и список команд, открывающих диалоги (LispPolicy.cs) |
| Moorlack/best-cad-mcp, U-C4N/Autocad-MCP, meococ/765T-Forge (MIT) | Идеи топологических проверок, проверки выпуска и критикующего прохода (Topology.cs, QualityChecks.cs, DrawingQuality.cs) |
| Psalmustrack/lambdacad-mcp (Apache-2.0), Dandarprox | Идеи: отключение объектных привязок на время скрипта, проверка пустого результата булевой операции |
| phamduybill2005-creator/CH-M-I-M-AGENT (MIT), U-C4N | Методика оценки агентов: скрытый результат, вердикт по реальному DWG, жёсткие провалы, «пустой» прогон (AgentEvaluation.cs, benchmarks/) |
| tkcHiunguyen, HorizunGroup, Sacred-G/Civil3D-mcp | Идея «просмотр → токен → применение» (cad_edit_preview, preview_hash) |
| JardiMargalefAgusti | Идея регистрации MCP в Claude Desktop и Claude Code из установщика (ClientRegistration.cs) |

## Дополнение версии 0.2.0

| Источник | Применение |
|---|---|
| [beiming183-cloud/AutoCAD-MCP CadDispatcher.cs](https://github.com/beiming183-cloud/AutoCAD-MCP/blob/11f7c47e5038796a20451b38b23032e625b5aa26/native/AutoCADMcp.Plugin/CadDispatcher.cs), MIT | Адаптирован подход транзакций и создания геометрии в Edits.cs; добавлены 2D-объекты, атрибуты, трансформации, строгий контракт и чтение конечного результата |
| [felixalmesberger/AUTOCAD-MCP](https://github.com/felixalmesberger/AUTOCAD-MCP/tree/ae430dce5125aa4966ebd97d5396b41154597b6f), MIT | Адаптирован подход AutoLISP evaluator из AcadExecutor.cs в LispScript.cs; файловый маркер заменён callbacks и журналом состояний |
| [debug23win/ClaudeRevit](https://github.com/debug23win/ClaudeRevit/tree/25a25ba925dbff48e33e0700233a7fbe53c7863e), MIT | Адаптирован atomic-save подход HistoryStore.cs в ChatStateStore.cs; остальные выводы описаны в clauderevit-analysis.md |
| [U-C4N/Autocad-MCP](https://github.com/U-C4N/Autocad-MCP/tree/cdb10638963898b3ea9b10cdd96a2c9bc495f184), MIT | Изучены COM backend и контракты; использованы принципы проверяемого результата и явного неподдерживаемого свойства. Python-код не включён |
| [puran-water/autocad-mcp](https://github.com/puran-water/autocad-mcp/tree/95476a33a1c246308326eb4709d6379ef2efdbc1), MIT | Изучены File IPC, диспетчер и execute_lisp; использована идентификация отдельных операций. LT/File IPC backend в эту сборку не включён |
| [Slacker-LLC/autocad-mcp](https://github.com/Slacker-LLC/autocad-mcp/tree/2723b4fae13ac7bc91274fcd502528b92da7a6a5), Apache-2.0 | Изучены move/copy/delete, проверки координат и единиц. Реализованы собственные нативные аналоги; Python-код не включён |

Для адаптированных компонентов сохранены полные MIT-лицензии и уведомления в исходниках/NOTICE. Это объединение выбранных механизмов в согласованном объёме, а не импорт всех функций каждого проекта.

Следующие разделы описывают первоначальный этап 3А и являются историческими.

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
