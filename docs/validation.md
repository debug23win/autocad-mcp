# Проверка

Как проверяется текущая версия. Что проверялось в каждом выпуске — в [CHANGELOG.md](../CHANGELOG.md).

## Автоматические тесты

`scripts/build.ps1`, затем `scripts/test.ps1` (или `dotnet test tests/CadMcp.Tests`). Тесты xUnit в `tests/CadMcp.Tests` проверяют:

- обмен кадрами через именованные каналы, broker, его запуск, остановку с завершением текущих запросов и замену устаревшей версии;
- журнал операций, восстановление потерянного ответа без повторной правки, архив больших результатов, изоляцию отмены;
- разбор планов правок и критериев приёмки, проверку геометрии, калибровку изображений, обёртку AutoLISP;
- историю и вложения чата, отображение Markdown и экспорт в Word;
- протоколы Codex и Claude Code на подставных процессах из `tests/CadMcp.TestCli`: потоковый текст, дополнения во время хода, отмену, выбор модели, отказ на интерактивные запросы, длину командной строки;
- настоящий MCP SDK через stdio собранного `CadMcp.Host`: список инструментов, их классификацию «только чтение», закрепление за чертежом, ограничения помощников.

AutoCAD, настоящие CLI и модели в этих тестах не запускаются. Тесты выполняются по одному: они запускают процессы и broker и измеряют время.

## CI

`.github/workflows/ci.yml` на Windows для каждого pull request и для `main`:

1. Ставит .NET 8 и .NET 10 и получает справочные сборки AutoCAD 2025 из NuGet (`scripts/autocad-references.ps1`, с проверкой SHA-256). В сборку и установщик они не попадают.
2. Восстанавливает пакеты в строгом режиме по lock-файлам.
3. Собирает все проекты из `.github/ci-projects.txt`, включая адаптеры .NET 10 и проверочные программы для AutoCAD; предупреждения считаются ошибками.
4. Запускает тесты и сохраняет результаты TRX.

## Проверки в AutoCAD

Выполняются вручную на компьютере с AutoCAD; рабочие чертежи не открываются.

- `tests/CadMcp.CoreProbe` и `scripts/test-autocad-core.ps1` — AutoCAD Core Console; `scripts/test-dwg-read.ps1` — чтение DWG.
- `tests/CadMcp.NativeProbe` и `scripts/test-autocad-gui.ps1` — GUI AutoCAD и Map 3D (`-Product MAP`).
- `tests/CadMcp.GuiProbe` — команды для нескольких открытых DWG и чтения листов (загружается через NETLOAD).
- `tests/CadMcp.ChatPanelProbe` — панель чата WPF без AutoCAD.
- [Контрольный список живой проверки](live-checklist.md).

Для регрессий перевода и замены текста 0.11.1 после сборки `tests/CadMcp.CoreProbe`:

```powershell
./scripts/test-autocad-core.ps1 -TextRegressionOnly -DynamicTextFixture 'C:\Program Files\Autodesk\AutoCAD 2025\Sample\ru-RU\Dynamic Blocks\Annotation - Metric.dwg'
```

Это отдельный скрытый Core Console с временным DWG и профилем. Штатный пример AutoCAD читается без записи и клонируется в тестовый чертёж. Для другой локализации укажите путь к локальному примеру с динамическими блоками. Отсутствие такого примера считается невыполненной проверкой, а не успехом. Список проверок и ошибка сохраняются в `result.json` каталога пробы; успешный результат копируется в `artifacts/core-probe-latest.json`.

## Проверки с настоящими CLI

Добровольные и по подписке; используют записывающий CAD-фикстур, чертежи не меняются. После сборки Release из корня репозитория:

- `CadMcp.TestCli.exe --live-subagent-test codex|claude <cli>` — помощник работает только на чтение;
- `CadMcp.TestCli.exe --live-codex-model-test <cli> [--resume-conversation <id>]` — выбор модели и сохранение диалога;
- `CadMcp.TestCli.exe --live-codex-approval-test <cli>` — разрешения CAD-инструментов в Codex;
- `CadMcp.TestCli.exe --cli-config-check <cli>` — Codex принимает конфигурацию MCP и ролей помощников.

## Оценка агентов

Стенд в `benchmarks/` оценивает агента по фактическому состоянию DWG после прогона: задачи со скрытыми проверками, жёсткие провалы (ложный успех, удаление без запроса, правки вне задачи, выдуманный инструмент, запись без данных, повтор неизвестной правки, обход отказа от AutoLISP), три прогона на задачу, проверка грейдера «пустым» прогоном и сравнение с прошлым выпуском. Порядок — в [benchmarks/README.md](../benchmarks/README.md); команды `CadMcp.Host.exe --capture-evidence`, `--grade`, `--grade-selfcheck`.
