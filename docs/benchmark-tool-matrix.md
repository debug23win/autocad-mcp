# Независимый проект полного покрытия бенчмарка

Это проект, не выполненный прогон. Inventory: 49 MCP tools по текущим атрибутам CadTools; добавленный cad_csharp учтён.

| Tool | Fixture | Обязательные утверждения | Условия и side effects |
|---|---|---|---|
| cad_sessions | Broker + текущий worker; протокольный fixture для второго worker | Найден текущий session/process; stale descriptor не выдаётся за живой; при pin ожидается текущий context | Без второго реального worker его ветка SKIPPED, не выдумывать две сессии |
| cad_documents | User sentinel DWG + два новых benchmark DWG | Уникальные document_id, правильный active; стабильный project_key при reopen тестового файла | Не закрывать пользовательские документы |
| cad_context | Fixture mm/Model и paper layout | Точный scope/revision/INSUNITS/units; active/inactive editor availability; stale revision разрешён для read | Смена единиц/Model блокирует дальнейшие writes |
| cad_catalog | Известные layer/block/attribute/style/linetype + два layout | Точные имена/handles/параметры, dictionary enumeration; read не меняет revision/DBMOD | Пустой каталог при непустом fixture не PASS |
| cad_runtime_status | Управляемая queued задача или Lisp awaiting approval | Own operation_id, phase, cancel flag и receipt согласованы; статус доступен при занятом UI thread | Не запускать опасный тяжёлый kernel ради busy |
| cad_diagnostics | Рискованная fixture-операция с checkpoint | Phase/version/operation_id/path совпадают; checkpoint реально существует и hash верен | Не менять старые пользовательские recovery files |
| cad_cancel | Две queued задачи разных OwnerId под fixture gate | Отменяется только свой id/owner; свой receipt CANCELLED до старта, чужая задача выполняется | Детерминированный gate обязателен; обычная race create/cancel недостоверна; если gate недоступен — SKIPPED |
| cad_operation_status | Собственные completed/failed/cancelled/queued операции | Exact original request/id/document; state/result согласованы; новый id не используется для recovery | queued/unknown/not_found не PASS отсутствия изменений |
| cad_operation_list | Три собственных операции с разными исходами | Ожидаемые ids, owner/document filter, limit/since/order; receipt не считается свежим geometry read | Исторические/нечитаемые receipts явно помечены |
| cad_edit_help | Реальный Host edit-help resource | Валидный JSON/schema; набор операций/полей совпадает с EditPlan.Fields | Непустая строка сама по себе не PASS |
| cad_spds_help | Реальный Host spds-help resource | Ожидаемые Table/formula/SPDS templates и точные размеры согласованы с кодом | Справочник не доказывает ГОСТ-соответствие результата |
| cad_steel_catalog | Фиксированный профиль 20Б1 | Exact code/dimensions/root radius/mm/density 7850; уникальные коды | Номинальная масса не structural capacity |
| cad_edit | Изолированные 2D/3D/annotation/block/table families | Отдельные readbacks каждого семейства; exact replay не дублирует; conflict id/stale revision отказ до записи | 48/49 MCP tools не равны всем EditPlan operations; нужна отдельная operation coverage карта |
| cad_edit_preview | Контур и existing text_replace | Временный readback верен; после preview объектов/правок нет; точный hash принят commit, изменённый plan/revision отвергнут | Preview может выставить DBMOD; layout/Civil/Map заранее запрещены |
| cad_csharp | Фиксированный C# source в новом fixture; exact runtime references | Исходник реально компилируется; Run получает нужные Database/Transaction/ct; отдельный readback line/circle; checkpoint/receipt; compile/signature/throw/cancel отрицательные ветки | Полное доверие процесса, не sandbox; только фиксированный проверенный код, без файлов/процессов/manual commit; checkpoint не откатывает внешние эффекты |
| cad_verify | Известные line/circle/solid/erased handles + native operation id | Actual length/radius/volume/position и неверный expected->failed; erased/missing не live; aliases receipt верны | Не оценивать только итог success boolean |
| cad_entity_get | Model line + paper viewport + dimension + Xrecord | Geometry/type/layer; paper read без layout switch; formatted dimension; extension dictionary | Nested handle -> ожидаемый отказ; для него нужен search path/root_handle |
| cad_snapshot | N известных объектов, limit=N-1 | Count/coverage/captured_revision/truncated; snapshot неизменен после fixture edit | Пустой snapshot не покрывает truncation |
| cad_query | Snapshot с layer/type/text и несколькими страницами | Фильтры exact-set, страницы без дублей/потерь; после edit historical=true и прежние данные | Snapshot другого document отвергается |
| cad_search | Model/paper/nested block/xref/view/selection/viewport fixtures | Каждый scope/filter/bounds, WCS/path/root_handle, pages exact-set и явный bounds exclusion | Текущий runner проверяет лишь model/text/nested/paging |
| cad_result_get | 200 MText по 2000 символов, пакетами по 20; search >512KiB | Archive_id получен; все text pages восстановлены в JSON; 200 markers/handles exact; captured_revision/historical | Не превышать cap; archive_id сам по себе не доказательство contents |
| cad_focus | Временно активированный fixture DWG с top-level line | Context selection ровно запрошенный handle; geometry/revision/DBMOD неизменны | Нужно активировать fixture, т.к. текущие mutations восстанавливают user active DWG; вернуть активный DWG после кейса |
| cad_render | Известные Model/paper геометрии, явный top view | PNG декодируется/nonblank, размеры/image_id/path; attach=false/true content blocks; камера/revision/DBMOD прежние | Nested handle -> ожидаемый отказ, root_handle -> успех; сложная визуальная верность отдельно |
| cad_image_register | image_id реального render + три неколлинеарные пары | Affine matrix, RMS/max residual и углы совпадают с synthetic mapping | Degenerate/nonplanar/foreign image -> отказ; math-test не доказывает истинную визуальную геопривязку |
| cad_image_point | Та же регистрация и контрольная середина | Оба направления roundtrip <=1e-6; после fixture edit historical=true и прежняя captured_revision | Out-of-image/unknown registration -> точные ошибки |
| cad_reference_calibrate | Собственная PNG 100x100 с known hash | Similarity: (10,90)->(0,0),(90,90)->(80,0), (50,50)->(40,40); projective отдельно; residual 0 | Host-only; degenerate/nonplanar anchors отказывают |
| cad_reference_point | reference_id и отдельная контрольная точка | Roundtrip/WCS z; изменение своей PNG после fit даёт hash mismatch | Менять только fixture file, не пользовательский attachment |
| cad_reference_compare | Две fixture PNG с известными прямоугольными контурами | Same contour IoU=1/deviation=0; сдвиг на полширины IoU=1/3; hashes/dimensions верны | Это сравнение заданных контуров, не автоматическое зрение или доказательство 3D |
| cad_takeoff | Контур, два слоя, block attributes, Model/paper | Lengths/areas/count/by_type/blocks/attrs/CSV quoting; все scopes и layer filters | Current runner проверяет лишь общие длину/площадь; hatch и boundary не складывать как одну площадь без by_type |
| cad_text_units | TEXT paragraph, MTEXT, attribute, table, mleader | Exact full text/keys/frame/group_lines/pagination; source guard при translate | Не выдумывать OCR/перевод; locked layers явно skipped |
| cad_outline | Model + два layout + block/text/style | Exact model count/types/sheets/tab order/layer counts/styles/text sample; повтор детерминирован | В 0.11.3 известен DictionaryEntry failure: FAIL до исправления, без fallback-and-pass |
| cad_file_inspect | Сохранённые fixture DWG/DXF, затем unsaved marker в редакторе | Side database читает saved reference без unsaved marker; coords/text/dims/attrs, scope/pages; source hash неизменен | Нельзя SaveAs рабочий DWG; только свои reference files |
| cad_changes | Снять revision, выполнить add/move/erase только fixture handles | Net added/modified/erased, author/source/revision/since/include_non_entities и complete=true | При неполном журнале отсутствие чужих правок не доказано |
| cad_review | Known duplicate/self-intersect/text-fit/solid collision + clean control | Найдены предусмотренные проблемы по правильным handles/severity; clean не получает errors | Непустой report недостаточен; нужные code/categories сверять с реализацией |
| cad_solid_get | Box 10x20x30 mm | Volume=6000, непустые topology ids/current revision; после topology edit перечитать ids | Нет kernel/solid capability => явный fail/skip, не объём из введённых параметров |
| cad_assembly_get | Beam профиля каталога длиной 1000mm | Recipe/mark/material/profile/length; mass/volume согласованы с catalog и units; update виден отдельно | Не выдавать геометрию/массу за расчёт прочности |
| cad_table_get | Связанные tables + merged/resized/paged cells | Numeric values/formula/field codes; sizes/merged ranges/offset pages | Current runner покрывает четыре cell values, не весь контракт |
| cad_table_dependencies | Те же linked tables со stable cell ids | Exact graph nodes/edges/expressions; update source; cycle reject сохраняет graph; resize сохраняет identity | table_get не заменяет проверку graph tool |
| cad_properties | Line длиной 37.25 и known layer | Реальная непустая OPM palette и семантические Length/layer; учитывать locale | OPM недоступен -> SKIPPED; error/empty palette не PASS |
| cad_proxies | Proxy-free baseline + licensed DWG с known proxy entity/object | Base counts=0; positive original class/app/flags/owner/dictionary key и bounded samples/graphics | Нет настоящей proxy fixture -> SKIPPED позитивной ветки; zero count не покрывает inspection |
| cad_release_check | Два тестовых sheet: valid и known bad ####/plot setup | Valid без errors; bad с ожидаемыми codes/handles; warning отдельно; layouts scope | Нет PC3/fonts -> соответствующий valid variant SKIPPED, expected refusal проверяется отдельно |
| cad_export | Уникальный run output dir + known layout/device | DXF/PDF actual bytes/hash, PDF pagecount=1 и marker; collision отказ без overwrite; readback reference | Нет PDF device -> PDF SKIPPED; DXF всё равно запускать |
| cad_publish | Два paper layouts + уникальная пустая папка | Два PDF в порядке; JSON/CSV actual hashes/bytes; collisions refused; partial manifests точны | Нет PDF device -> SKIPPED; partial output не общий PASS |
| cad_lisp | Свой fixture layer; bounded entmake line/circle | Queued->terminal + отдельная геометрия; deny/decline/cancel/error; unknown останавливает writes; approval time отдельно | User policy не менять, denial не обходить; нет approval UI -> SKIPPED |
| cad_vertical_capabilities | Фактически загруженные Civil/Map assemblies | Версии/подписи совпадают с runtime; no API explicit unavailable | Unavailable contract может пройти, позитивные vertical cases остаются SKIPPED |
| cad_vertical_catalog | Real Civil/Map fixtures: TIN/alignment/profile/network/OD tables | Exact names/handles/API versions/bounded child collections | Нет API/fixture -> SKIPPED позитивного кейса; empty catalog не доказательство |
| cad_vertical_get | Real TIN z=x+y + alignment/profile/network + Map OD entity | Sample (1,2)->3; child/OD scalar fields exact; outside TIN per-point error | Нужны реальные продукты/лицензии/fixtures; нет их -> SKIPPED, без подмены DBObject |
| cad_pdf_read | Deterministic 2-page text PDF + raster page + encrypted PDF | Page/text offsets/text exact; настоящий PNG decode/nonblank/dimensions; attach modes; original hash unchanged | render_error не PASS rendering даже если IsError=false и text доступен; password не обходить картинкой |
| cad_web_download | Own loopback HTTP server с known bytes/hash/redirect; optional pinned HTTPS | Реальный GET, bytes/hash, unique path/read_with; redirect/oversize/cancel cleanup/no overwrite | Loopback не проверяет внешний TLS; network unavailable -> SKIPPED отдельного external variant |

## Архитектура независимой проверки

1. Канонический inventory брать из реального `tools/list` MCP Host либо reflection `McpServerToolAttribute`, сравнивать с декларативным `tool -> case IDs`. Новый инструмент без сценария должен давать `UNCOVERED`, а не исчезать из знаменателя или получать SKIPPED «не реализован». На момент проверки 49 инструментов, включая новый `cad_csharp`; `benchmark_create` и compile-reference handshake являются внутренними операциями и в этот знаменатель не входят.
2. Разделить worker-native executor и настоящий Host-tool executor. Вызовы `Func<Request,...>` непосредственно в worker не проверяют Host-only инструменты, `IsError`, content blocks/PNG, pin/helper policy и нормализацию MCP JSON. Предпочтителен отдельный MCP Host на время прогона, закреплённый за fixture; `benchmark_create` остаётся внутренним worker setup. Не переиспользовать chat-host, закреплённый за пользовательским DWG.
3. Нужны уровни статуса: выполнен вызов; проверены отдельные утверждения; номинальная ветка PASSED; ожидаемый отказ PASSED; условная номинальная ветка SKIPPED; outcome UNKNOWN. Проверенный отказ Civil API не даёт PASS чтению настоящей поверхности. В итогах отдельно `tests passed/total`, `tools with nominal evidence/49`, `negative contracts checked`, `skipped prerequisites` и `uncovered`.
4. Все реальные правки — только fixture DWG из registry текущего run_id. Для focus/view/selection нужен явный безопасный activate fixture, с восстановлением user active document; остальные фоновые чтения не должны добавлять write lock/Undo marker. Перед каждым write проверять session/document/units/Model, except специально подготовленных sheet cases. Пользовательский sentinel: снимок current name/revision/DBMOD/selection/view до/после, никакого сохранения/закрытия/очистки его данных.
5. Файлы только в уникальном run-root: seed DWG, DXF/PDF/CSV/JSON, изображения, download cache и encrypted fixtures. Не использовать пользовательский attachment как изменяемый тестовый объект. Не менять SECURELOAD/TRUSTEDPATHS, Lisp policy, registry/user templates. PC3/fonts/Vendor API считать prerequisites с явными причинами SKIPPED.
6. Для `cad_cancel` нужен управляемый безопасный gate принятой задачи, иначе тест зависит от гонки между create и cancel. Не вызывать тяжёлое нативное ядро или бесконечный код, чтобы искусственно получить busy. Отмена своего owner/id и изоляция другого owner — два отдельных утверждения. Завершённый чужой job или отсутствие pending задач не проверяет cancellation и не закрывает позитивную ветку.
7. PDF/data/download fixtures должны быть синтетическими с закреплёнными bytes/hash/expected text. Loopback download проверяет реальный HTTP tool без внешних credentials; внешний HTTPS — дополнительный conditional variant. В PDF отдельно оценивать text extraction, page/text pagination и raster rendering: наличие текста при render_error не закрывает рендер.
8. Archive case обязан фактически получить большой ответ, не вызвать `cad_result_get` с выдуманным id. Разумная bounded нагрузка: 200 MText по 2000 символов пакетами по 20, затем details search и чтение всего JSON архива страницами; при другом пороге использовать измеренный размер и ограниченный верхний предел, не бесконечную генерацию.
9. Raw evidence сохранять до нормализации: Request/Response или MCP result/content, scope, source revision, input/output hash, case ID, tool ID. Ожидаемые значения должны быть константами fixture и вычисляться независимо; не принимать `receipt.acceptance=passed` либо заполненный `actual=expected` за проверку.
10. После accepted mutation с потерянным ответом — только receipt original operation_id. UNKNOWN прекращает последующие writes; не выполнять cleanup. Ошибка отдельной read-only conditional проверки не обязана останавливать другие независимые host/read cases. При смене fixture scope/owner/session остановить все writes.

## Время и C#/AutoLISP

- Общий и case wall-clock — Stopwatch. Отдельно approval_wait_ms, network_wait, compile_ms (для реального `cad_csharp`), execution/verification, если их можно измерить достоверно; не вычитать неизвестные интервалы и не угадывать CPU time.
- Сравнение генерации нужно в трёх явных вариантах: native API через `cad_edit`, настоящий source через `cad_csharp`, AutoLISP через `cad_lisp`. Во всех — одна и та же геометрия и точные независимые проверки. Старый case «C#» через cad_edit не покрывает компиляцию/Run API нового cad_csharp.
- Отказ/отсутствие approval UI оставляет Lisp SKIPPED. compile error/неправильная сигнатура C# — отдельные ожидаемые negative cases; они не заменяют успешную реальную компиляцию и выполнение.
- Один быстро прошедший вызов не доказывает производительность. Для повторов сохранять отдельные run IDs и summaries, не смешивать предыдущий неуспешный запуск с успешным продолжением как один ровный прогон.

## Независимые негативные проверки расширенного runner

- Отсутствующий tool в coverage map, дубли case/tool IDs, пустые checks, попытка считать invocation coverage за assertion coverage.
- Ложный positive result при пустом palette/proxy catalog/vertical collection; unsupported API, missing PC3/font/PDF renderer/network endpoint -> явный SKIPPED prerequisite, не общий success.
- Incorrect bytes/hash, пустой/не декодирующийся PNG, render_error с успешным PDF text, перепутанные страницы/text offsets, truncated/partial/archived evidence без полной реконструкции.
- Сдвинутые geometry/неверные nested root handles, несогласованная ревизия, duplicate pages/next_offset, duplicated expected entity вместо отсутствующего.
- Path escape/overwrite collision/частичный файл после отмены, file inspect неожиданно читает unsaved editor data.
- Попытка вызова mutation/focus/cancel read-only helper; pin violation; чужой OwnerId cancellation; новая запись после unknown.
- C# compile failure/invalid Run signature/serialization live DBObject; Lisp denied/declined/never started/terminal mismatch; ни один случай не даёт PASS geometry generation.

Production файлы во время этой проверки не изменялись. Это проект покрытия; реальные CAD/Host/side-effect/conditional сценарии пока не исполнены.

## Конкретные payload-шаблоны для реализации

Во всех документных вызовах добавляются фактические `session_id`, `document_id` из fixture registry; у операций записи — свежая `expected_revision` и новый `operation_id`. В примерах `"2A"`, `"2B"` и `"2C"` — типизированные placeholders для настоящих handles из предыдущего результата, а не готовые идентификаторы. Поля `*_json` в реальном MCP передаются **JSON-строками**; ниже массивы показаны декодированными для читаемости. Каждый блок `operations_json` перед исполнением должен пройти `EditPlan.Parse`.

### SOLID — cad_edit / cad_solid_get / cad_verify

```json
{
  "operations_json": [
    {"op":"layer","name":"B_SOLID"},
    {"op":"box","id":"solid","center":[10000,0,0],"length":10,"width":20,"height":30,"layer":"B_SOLID"}
  ],
  "expectations_json":{"entity_count":1,"enforce":true}
}
```

Из результата получить `solid` handle. `cad_solid_get(handle=<solid>)` обязан вернуть volume=6000, ненулевые faces/edges и текущую ревизию. `cad_verify(handles_json=[<solid>], expectations_json={"entity_count":1,"checks":[{"handle":<solid>,"property":"volume","expected":6000,"tolerance":0.000001}]})` даёт независимое чтение. Для проверки нестабильности topology ids отдельно выбрать реальное edge id из `cad_solid_get`, выполнить `solid_fillet` с radius=1, затем перечитать ids. Старые id в новом запросе не переиспользовать. Если edge не поддерживает fillet, это FAIL конкретного topology-кейса, а не SKIP базового box-volume.

### ASSEMBLY — cad_edit / cad_assembly_get / cad_steel_catalog

Базовый геометрический оракул без зависимости от внешнего каталога:

```json
{
  "operations_json":[
    {"op":"layer","name":"B_ASSEMBLY"},
    {"op":"assembly_create","id":"beam","kind":"beam","position":[12000,0,0],"parameters":{"length":1000,"width":100,"height":200,"web_thickness":5,"flange_thickness":8,"root_radius":0},"mark":"B-1","material":"S235","density":7850,"show_annotations":false,"layer":"B_ASSEMBLY"}
  ]
}
```

Площадь сечения по независимой формуле `2*100*8 + (200-2*8)*5 = 2520 mm²`. Ожидаемые объём 2520000 mm³ и масса 19.782 kg для 1000 mm; сверять именно поля, описывающие solid volume/mass, не unqualified approximate annotation. `cad_assembly_get(handle=<beam>)`: kind=beam, mark=B-1, material=S235, recipe.length=1000, scope массы явный. После `assembly_update(handle=<beam>,parameters={...same dimensions...,"length":2000})` новое чтение: объём/масса удвоены. Отдельный catalog variant: `parameters={"length":1000,"profile":"20Б1"}` и dimensions/root radius из `cad_steel_catalog`; не смешивать profile и explicit dimensions в одном payload.

### RENDER / IMAGE — cad_render, cad_image_register, cad_image_point

```json
{
  "width":512,"height":384,"view_name":"top","handles_json":["2A"],"attach":false
}
```

`2A` должен быть top-level handle реального box/polyline в **одном** model space. PNG-файл декодировать и проверить размеры/nonblank; нет файла или `preview_file_warning` — не PASS сохранённой копии. `attach=false` с существующим файлом должен дать только text block; `attach=true` дополнительно ImageContentBlock. Камера и DBMOD fixture до/после неизменны.

Использовать фактические width/height и image_id ответа. Для чисто математического теста регистрационного API:

```json
{
  "image_id":"<returned image_id>",
  "control_points_json":[
    {"pixel":[0,0],"world":[0,0,0]},
    {"pixel":[512,0],"world":[100,0,0]},
    {"pixel":[0,384],"world":[0,100,0]}
  ]
}
```

После регистрации `cad_image_point(points_json=[[256,192]],direction="pixel_to_world")` => [50,50,0], reverse [[50,50]] => [256,192]. Обязательная оговорка в evidence: эти заданные anchors проверяют математику, а не доказанную реальную геопривязку нарисованного объекта на PNG. После fixture edit ожидается historical=true при неизменном captured_revision.

### HOST REFERENCE — calibrate / point / compare

Создать собственную PNG 100x100 с известным hash в run-root. Payload `cad_reference_calibrate`:

```json
{
  "image_path":"<run-root>/reference.png",
  "model":"similarity",
  "invert_pixel_y":true,
  "control_points_json":[{"pixel":[10,90],"world":[0,0,0]},{"pixel":[90,90],"world":[80,0,0]}]
}
```

`cad_reference_point(reference_id=<returned>,points_json=[[50,50]],direction="pixel_to_world")` => [40,40,0]; reverse => [50,50]. Изменить только эту свою PNG на другую валидную PNG того же размера и проверить отказ по source hash; затем использовать новый файл/id для последующих кейсов.

Для `cad_reference_compare` нормализованный прямоугольник A=[[0,0],[0.5,0],[0.5,1],[0,1]]; тот же контур => IoU=1/deviation=0. Контур B=[[0.25,0],[0.75,0],[0.75,1],[0.25,1]] => IoU=1/3 (допуск с учётом дискретного sampling implementation). Это проверка заданных контуров; положительный threshold-кейс и отрицательный threshold-кейс считаются отдельно.

### ARCHIVE — cad_result_get

На новом слое `B_ARCHIVE` 10 пакетами по 20 создать MText, `height=1,width=200,text="ARCHIVE-<i>:" + "x"*2000`, позиции [20000+100*(i%20),100*(i/20),0], i=0..199. Пакет остаётся меньше лимита 65536 chars плана; реальный размер сериализации проверять до отправки. Тестовые строки и coords генерируются ограниченным C# циклом runner, не моделью и не AutoLISP.

```json
{"options_json":{"scope":"model","layer":"B_ARCHIVE","details":true,"limit":500,"offset":0}}
```

Если ответ содержит archive_id, не пропускать кейс как «partial»: читать `cad_result_get(archive_id=<id>,offset=0,limit=32000)`, затем использовать реальный `next_offset` до конца. Склеить **text**, распарсить полный JSON и проверить 200 уникальных handles и все markers. Для archive-aware helper это единственный разрешённый partial transport: после полного восстановления содержимого оно должно быть полным. Ограничить максимум 200 страниц/8 MiB восстановленного текста; нарушение — FAIL. После изменения fixture архив обязан оставаться historical с прежней captured_revision. Если 200 MText неожиданно не достигают порога, один ограниченный дополнительный пакет и повторное чтение допускаются как setup; верхний предел fixture заранее фиксирован. Не создавать до бесконечности.

### EXPORT / PUBLISH / RELEASE_CHECK

Создать только в benchmark DWG два листа B_SHEET_1 и B_SHEET_2, каждый **отдельной** операцией `layout_create`. Следующими отдельными операциями `layout_configure` установить `device="DWG To PDF.pc3"`, `media_name="ISO_full_bleed_A3_(420.00_x_297.00_MM)"`, `paper_units="millimeters"`, `paper_rotation=0`. Если device/media реально недоступны — сохранить точную prerequisite error и SKIP PDF/export/publish-positive, но не DXF/side inspection. Нет обхода через другую произвольную печать.

На листах разместить `spds_sheet` с format=A3, designation=BENCH-1/BENCH-2, drawing_title="Benchmark", sheet="1"/"2", sheets="2" и отличимую известную геометрию. Совместимость paper layout с рамкой/plot area проверять `cad_release_check` и раздельным render; сам факт создания layout не PASS выпуска.

```json
{"format":"dxf","path":"<run-root>/export/fixture.dxf"}
```

```json
{"format":"pdf","layout":"B_SHEET_1","path":"<run-root>/export/sheet1.pdf"}
```

```json
{"output_folder":"<run-root>/publish","layouts_json":["B_SHEET_1","B_SHEET_2"]}
```

Папки создаются только helper-ом runner внутри уникального run-root. Проверить независимым FileInfo/SHA256 каждый PDF/DXF и hashes/bytes из результата, читать PDF через `cad_pdf_read` (page_count=1, ожидаемый sheet marker или реальная PNG), DXF через `cad_file_inspect`. Повторный export на уже существующий **свой** path обязан отказать и оставить hash/bytes неизменными. Для publish сравнить JSON/CSV manifest с реальными файлами и порядком входных layouts; частичный выпуск не общий PASS.

Чтобы проверить saved-vs-unsaved для `cad_file_inspect`, после DXF export создать в benchmark DWG отдельный MText "UNSAVED_AFTER_EXPORT". `cad_file_inspect(path=<fixture.dxf>,scope=model,limit=5)` с полной пагинацией **не** должен видеть этот marker; live `cad_search` обязан его видеть. Для DWG variant нужен отдельный fixture reference, сохранённый внутренним helper из своей side database или безопасным fixture-save; не использовать seed-пустой DWG как положительный тест чтения сложной геометрии.

### CANCEL / RUNTIME / RECEIPTS

Недостоверный вариант: отправить очень короткий `cad_edit`, тут же cancel и ожидать обязательного CANCELLED — native edit может честно успеть завершиться. Нельзя считать такой исход дефектом.

Достоверная последовательность должна использовать внутренний **fixture-only deterministic gate**, не блокирующий пользовательские чертежи:

1. Создать две зарегистрированные queued job с owners `benchmark-<run>-A` и `benchmark-<run>-B`, по одному известному operation_id; gate удерживает их до исполнения/проверки cancellation. При текущем worker guard owner format нужно расширить проверяемым opaque sub-owner token или иной явной схемой; нельзя просто отправить неподдерживаемый owner и получить scope error.
2. `cad_runtime_status(document_id=<fixture>)` показывает ожидаемые собственные job/phase, пока gate удерживает их.
3. `cad_cancel(document_id=<fixture>,operation_id=<A>)` от owner A. `cad_operation_status` для A становится cancelled с Error.Code=CANCELLED и no geometry; B не отменён.
4. Разрешить gate B, дождаться terminal completed, затем отдельно прочитать его geometry. `cad_operation_list` содержит оба результата; `since` и `limit` проверяются отдельно.
5. Отдельная ожидаемая отрицательная проверка: cancel от другого owner не возвращает/не отменяет job A.

Если детерминированный gate не реализован, позитивный кейс отмены обязан остаться SKIPPED с причиной, а no-op `cad_cancel` на пустой очереди нельзя засчитать как полноценный PASS. Альтернатива — queued AutoLISP awaiting approval и отмена до исполнения, но она conditional: при policy=allow мгновенный Lisp может успеть исполниться, а policy пользователя менять нельзя.

### CIVIL / MAP — условное покрытие

`cad_vertical_capabilities` и `cad_vertical_catalog` вызываются всегда. Отсутствующие API должны быть явно unavailable; это проверенный отрицательный контракт. Для реальных positive `cad_vertical_get` нужны поверхности/сети Civil и OD Map, а не обычный AutoCAD 2027.

Минимальный Civil positive fixture: отдельной операцией `civil_tin_create(name="B_TIN",vertices=[[0,0,0],[10,0,10],[0,10,10]])`; `cad_vertical_get(handle=<returned surface>,sample_points_json=[[1,2],[2,1],[20,20]])` => две отметки 3 и по-точечная ошибка за границей. Реальные layer/style/label/parts names для alignment/profile/network брать из доступного API-каталога или заранее лицензированного fixture, не угадывать "Standard"/"Basic". Нет API/стиля/fixture — SKIPPED конкретного positive variant, не PASS и не остановка всех остальных инструментов.

Минимальный Map positive fixture: зарегистрированная таблица OD B_OD с известными строковым/числовым полями; данные присвоены только объекту benchmark DWG; `cad_vertical_get` обязан вернуть exact table/values. Код coordinate system брать из реально проверенного runtime списка; setter меняет только свой fixture и проходит checkpoint. Отсутствие продукта/OD API => SKIPPED. Подмена результата `supported=true` без loaded API запрещена.

## Проверка этого проекта матрицы

Независимый проверяющий сверил 49 строк с текущими атрибутами `McpServerTool` без пропусков и дублей. Все 9 fenced JSON-шаблонов синтаксически разобраны; оба нативных `operations_json` (SOLID и ASSEMBLY) прошли реальные `EditPlan.Parse`, а критерий SOLID — `DrawingVerification.Parse`. Это проверка контракта примеров, не выполнение геометрии в AutoCAD и не закрытие перечисленных позитивных/условных кейсов.
