# План исправлений 0.3.10.3 → 0.3.11.0 (issues #358, #324, #330, #334, #352, #356, #357)

Целевая версия: **0.3.11.0** (минорный инкремент от 0.3.10.3 по схеме 0.3.x.y).

## Обоснование версии

Схема проекта: `0.3.x.y`, где x — функциональный релиз, y — точечные исправления поверх него.
В этот выпуск, помимо исправлений регрессий, входит НОВАЯ функциональность:

- запоминание папки сохранения цепочки обновлений + кнопка «Открыть» (issue #352.2);
- диалог повтора цепочки с таймером ~60 с (issue #352.3);
- выбор «Дистрибутив обновления / Полный дистрибутив» при одиночном скачивании (issue #352.1);
- защита автообновления при скачивании zip-архива (issue #358).

Объём сопоставим с релизом 0.3.10.x целиком, поэтому — **0.3.11.0** (не 0.3.10.4).
Точечные досборки поверх него, если понадобится — 0.3.11.1, 0.3.11.2 и т.д.

## Сводка: корни проблем по результатам исследования кода

| Issue | Корень (файл/место) | Характер |
|---|---|---|
| #358 | Автообновление качает ассет по признаку «win-x64» в имени ([`GitHubReleaseService.IsPlatformAsset`](Configuration%20Management/Services/GitHubReleaseService.cs:294)) — в релизе 0.3.10.3 это ZIP; загрузчик сохраняет его как `*.new.exe` ([`UpdateService.TempFileName`](Configuration%20Management/Services/UpdateService.cs:370)) и PowerShell-помощник делает `Move-Item` ZIP-файла на место exe — «не запускается». В релизе НЕТ ассета `ConfigurationManagement.exe`, на который рассчитаны шаблон ссылки и Atom-фолбэк | Регрессия процесса публикации + отсутствие защиты |
| #324 | [`RacClient.LooksLikeUsageHelp`](Configuration%20Management/Services/RacClient.cs:346) проверяет только ПЕРВУЮ непустую строку; на 8.5.4.1878 вывод начинается с баннера «1C:Enterprise 8.5 Remote Administrative Client Utility …», «Использование:» стоит дальше → справка не распознана → формат кэшируется как «рабочий» → каждый опрос даёт WARN «вывод rac не распознан … 0 записей» | Недоработка фикса 0.3.10.3 |
| #330 | [`PlatformDistributionOption.DisplayName`](Configuration%20Management/Services/PlatformDistributionPicker.cs:67) для Windows-дистрибутивов строится ТОЛЬКО из типа/разрядности/размера («Полный клиент (rar) · x64») — у файлов релиза размер неизвестен (0), имена файлов разные, а подписи одинаковые → «все строки одинаковые». Имя файла подставляется только после выбора | Доработка фикса 0.3.10.3 |
| #334.2 | Ссылки дистрибутивов с новой разметки — эндпоинт `version_file?…` (единственное число): [`OneCPlatformCatalogParser.FileEndpointUrlRegex`](Configuration%20Management/Services/OneCPlatformCatalogParser.cs:75) его распознаёт, но [`OneCUpdatesService.DownloadDistributionAsync`](Configuration%20Management/Services/OneCUpdatesService.cs:791) сначала запускает многопоточную загрузку «как файла» (HTML-страница сохраняется/срывается), а резервный [`DownloadUpdateAsync`](Configuration%20Management/Services/OneCUpdatesService.cs:619) признаёт файловыми эндпоинтами только `transfer_file?`/`additional_file?` ([`IsFileEndpointUrl`](Configuration%20Management/Services/OneCUpdatesService.cs:727)) — `version_file?` не резолвится по цепочке «страница → кнопка "Скачать дистрибутив" → transfer_file» | Недоработка фикса 0.3.9.331/332 для платформы |
| #352.1 | Одиночный «Скачать» идёт на `additional_file?nick=Trade110…` с `.cf` в `path`: страница — настоящий HTML-документ, ссылок на файл не содержит ([`OneCUpdatesService`](Configuration%20Management/Services/OneCUpdatesService.cs:690-699) → WARN «В ответе не найдена ссылка на дистрибутив»). Нужен переход на страницу файлов версии и выбор по подписи «Дистрибутив обновления» / «Полный дистрибутив» | Новая логика |
| #352.2/3 | Папка цепочки не запоминается ([`UpdateCheckWindow.DownloadChainAsync`](Configuration%20Management/Views/UpdateCheckWindow.xaml.cs:477) каждый раз спрашивает диалог), при ошибках — простой `ShowWarning` с количеством ([`UpdateCheckWindow.xaml.cs:555-560`](Configuration%20Management/Views/UpdateCheckWindow.xaml.cs)) | Новая функциональность |
| #356 | Восстановление «через раз»: признак «меню закрыто пунктом» (`overMenuItem`) вычисляется в момент `ContextMenuClosed` через `Mouse.DirectlyOver` ([`MainWindow.Hotkeys.cs:865`](Configuration%20Management/Views/MainWindow.Hotkeys.cs)) — к этому моменту попап уже закрыт, hit-test нестабилен → ветка `menuItem` ([`MainWindow.Hotkeys.cs:982-997`](Configuration%20Management/Views/MainWindow.Hotkeys.cs)) срабатывает не всегда. То же в Avalonia ([`MainWindow.Avalonia.Events.cs`](Configuration%20Management/Views/MainWindow.Avalonia.Events.cs:265)) | Недоработка фикса 0.3.10.3 |
| #357 | Ключи `ConnectionReplace.FieldLabel`/`ScopeLabel`/`ModeLabel` показываются «как есть»: у пользователя внешний устаревший `Languages/ru.json` полностью переопределяет встроенный словарь ([`LocalizationManager.TryAddFromStream`](Configuration%20Management/Localization/LocalizationManager.cs:292)); фолбэк на встроенные словари добавлен в 0.3.10.3, но выпущенная сборка не содержала этих ключей во встроенных JSON — требуется аудит всех ключей окна и защита от рассинхронизации | Регрессия сборки/данных |

---

## Этап 1. #358 «Верните EXE» — релиз Windows single-file EXE + защита автообновления

**Приоритет:** критический (сломано автообновление для всех Windows-пользователей).

### 1.1. Процесс публикации: вернуть ассет `ConfigurationManagement.exe`

- Сборка Windows: `dotnet publish "Configuration Management/Configuration Management.csproj" -c Release -r win-x64`
  (csproj уже содержит `PublishSingleFile`/`SelfContained`/`EnableCompressionInSingleFile` —
  [`Configuration Management.csproj:76-85`](Configuration%20Management/Configuration%20Management.csproj)) →
  артефакт `dist/win-x64/ConfigurationManagement.exe`.
- В релиз GitHub выкладывать **обязательный ассет `ConfigurationManagement.exe`** (именно так, как
  ожидает [`GitHubReleaseService.DownloadUrlTemplate`](Configuration%20Management/Services/GitHubReleaseService.cs:29)
  и Atom-фолбэк [`BuildDownloadUrl`](Configuration%20Management/Services/GitHubReleaseService.cs:154)).
  ZIP-архив с exe внутри — дополнительным ассетом (для ручной загрузки), но НЕ вместо exe.
- Зафиксировать процедуру в новом скрипте `publish/build_deb_win_0.3.11.0.py` (копия
  [`publish/build_deb_win_0.3.10.3.py`](publish/build_deb_win_0.3.10.3.py)) + отдельная
  инструкция/скрипт публикации Windows-exe (например `publish/publish_win_0.3.11.0.md` или `.ps1`):
  publish → проверка запуска → SHA-256 → gh release upload.

### 1.2. Выбор ассета: жёсткий приоритет bare-exe

- [`GitHubReleaseService.IsPlatformAsset`](Configuration%20Management/Services/GitHubReleaseService.cs:294)
  (Windows): точное имя `ConfigurationManagement.exe` — ПЕРВЫЙ приоритет; `win-x64`-признак
  принимать ТОЛЬКО если bare-exe не найден И имя ассета НЕ `.zip` (чтобы zip с exe внутри не
  подсовывался загрузчику автоматически).
- [`FindAsset`](Configuration%20Management/Services/GitHubReleaseService.cs:262): двухпроходный поиск —
  сначала точное совпадение имени, затем остальные признаки.

### 1.3. Защита в загрузчике (если скачан zip — распаковать; не-exe не устанавливать)

- [`UpdateService`](Configuration%20Management/Services/UpdateService.cs): после успешной загрузки
  (`DownloadAsync` возвращает путь) добавить проверку сигнатуры скачанного файла:
  - `MZ` (PE) — штатный путь;
  - `PK\x03\x04` (ZIP) — распаковать `ConfigurationManagement.exe` из архива во временный файл и
    дальше устанавливать его (метод `EnsureExecutablePayload(string downloadedFile, string versionKey)`);
  - иное (HTML и пр.) — удалить файл, вернуть ошибку «не удалось скачать обновление».
  Проверку вставить в оба потребителя: [`DownloadAndInstallAutoAsync`](Configuration%20Management/Services/UpdateService.cs:214)
  и окно [`UpdateAvailableWindow`](Configuration%20Management/Views/UpdateAvailableWindow.xaml.cs)
  (перед `ApplyRestartNow`/`ApplyAfterClose`).
- Логировать фактическую сигнатуру и действие (файл рядом с логами).

### 1.4. Тесты (ConfigurationManagement.Tests)

- Новый `UpdatePayloadValidationTests` (чистая логика вынесена в internal-метод, тестируемый без сети):
  MZ → pass-through; PK+запись с `ConfigurationManagement.exe` → извлечено; PK без записи → null;
  HTML-текст → null.
- `GitHubReleaseServiceTests` (если нет — создать): выбор ассета из JSON-фикстуры assets —
  точное имя побеждает zip с «win-x64».

### 1.5. Критерии проверки

- Релиз v0.3.11.0 содержит ассет `ConfigurationManagement.exe`; автообновление с 0.3.10.3 (и старше)
  скачивает и запускает новую версию.
- Ручной сценарий: подсунуть загрузчику zip — распаковывается и устанавливается exe; HTML — ошибка,
  а не «установка» битого файла.

## Этап 2. #324 «Серверы 1С» — детектор справки rac 8.5.4.1878

### 2.1. Расширить распознавание справки

- [`RacClient.LooksLikeUsageHelp`](Configuration%20Management/Services/RacClient.cs:346):
  проверять не только первую непустую строку, а ПЕРВЫЕ N строк (рекомендуется 15 — баннер
  8.5.4.1878 + пустые строки + «Использование: rac [режим] [команда] …») и/или первые ~500 символов:
  - префикс строки «Использование:» / «Usage:» (как сейчас, но в пределах окна строк);
  - дополнительный маркер: подстрока «rac [» / «rac [режим]» в первых строках (английская/русская
    справка содержит шаблон команды);
  - Optional маркер баннера: строка, содержащая «Remote Administrative Client Utility» /
    «Utility 1С:Предприятия» — при её наличии в первых строках ответ считается справкой, только если
    дальше есть «Использование:»/«Usage:» или парсер не нашёл ни одной записи `job-id`.
- Метод оставить internal — тесты уже вызывают его напрямую.

### 2.2. «exit=0 + справка в stdout = неуспех формата» — гарантировать сквозное поведение

- В [`GetJobsAsync`](Configuration%20Management/Services/RacClient.cs:255) цикл перебора форматов уже
  существует; убедиться, что после расширения детектора справка из ЛЮБОЙ позиции первых строк
  переводит попытку в `failures` и формат НЕ кэшируется ([`RacClient.cs:287-293`](Configuration%20Management/Services/RacClient.cs)).
- Дополнительно (защита от новых форм справки): если [`RacOutputParser.ToJobs`](Configuration%20Management/Services/RacOutputParser.cs)
  вернул 0 записей при непустом stdout, И вывод выглядит как текст справки (содержит
  «Использование:»/«Usage:» в любой из первых строк) — считать попытку неуспешной и удалить
  кэш-ключ (сейчас такой вывод доходит до [`EnsureParsedOrThrow`](Configuration%20Management/Services/RacClient.cs)
  и даёт WARN, а формат остаётся закэшированным — повторяется при каждом опросе).

### 2.3. Тесты

- [`RacClientTests`](ConfigurationManagement.Tests/RacClientTests.cs) /
  [`RacOutputParserTests`](ConfigurationManagement.Tests/RacOutputParserTests.cs):
  - фиксatura вывода 8.5.4.1878 (баннер «1C:Enterprise 8.5 Remote Administrative Client Utility …»,
    затем «Использование: rac …», 1833 симв.) — `LooksLikeUsageHelp` = true;
  - англоязычная справка (баннер + «Usage:») — true;
  - реальный data-вывод `job list` (блоки «job-id : …») — false;
  - пустой вывод (кластер без заданий) — false (не должен попадать в retry);
  - сценарий: справка → перебор форматов → успех на `--cluster=<uuid>` → формат сохранён в
    `RacJobListFormatStore` (fake store/временный каталог).

### 2.4. Критерии

- На платформе 8.5.4.1878 вкладка «Задания» заполняется (или честно пуста при отсутствии заданий),
  WARN «вывод rac не распознан» больше не повторяется на каждом опросе.

## Этап 3. #330 — уникальные имена файлов в списках выбора дистрибутива

### 3.1. Имя файла в подписи варианта

- [`PlatformDistributionOption.DisplayName`](Configuration%20Management/Services/PlatformDistributionPicker.cs:67):
  включить фактическое имя файла всегда, когда вариантов с одинаковым типом может быть несколько:
  формат `«Полный клиент (rar) · x64 · setuptc64_8_3_27_2325.rar · 1,2 ГБ»`. Имя файла —
  обязательная часть для Windows-вариантов (`WindowsSetupZip`) и для прочих, если в списке больше
  одного файла одного типа; для единственного Linux-пакета допустимо без имени.
- [`ToString()`](Configuration%20Management/Services/PlatformDistributionPicker.cs:88) уже
  возвращает `DisplayName` — ComboBox/списки «Скачивание версии платформы 1С»
  ([`PlatformDownloadWindow.xaml`](Configuration%20Management/Views/PlatformDownloadWindow.xaml),
  [`PlatformDownloadViewModel.DistributionOptions`](Configuration%20Management/ViewModels/PlatformDownloadViewModel.cs:142))
  и диалог выбора в окне обновления ([`PlatformUpdateViewModel.ResolvePickedFileAsync`](Configuration%20Management/ViewModels/PlatformUpdateViewModel.cs:877),
  [`PlatformDistributionPickerWindow.xaml:50`](Configuration%20Management/Views/PlatformDistributionPickerWindow.xaml))
  получат уникальные подписи без изменения привязок.
- Avalonia-версия окна выбора ([`PlatformDistributionPickerWindow.Avalonia.cs`](Configuration%20Management/Views/PlatformDistributionPickerWindow.Avalonia.cs))
  — проверить, что использует тот же `DisplayName` (без собственных подписей).

### 3.2. Тесты

- [`PlatformDistributionPickerTests`](ConfigurationManagement.Tests/PlatformDistributionPickerTests.cs):
  - два полных клиента разных версий → разные `DisplayName`, каждое содержит имя своего файла;
  - тонкий/полный/updsetup — подписи различимы и содержат имена файлов;
  - сортировка `BuildOptions` не изменилась (регресс).

### 3.3. Критерии

- В обоих окнах выбора каждая строка сразу показывает уникальное имя файла; выбранная строка
  совпадает с фактически скачиваемым файлом. Скачивание не регрессирует (механизм выбора по `Url`
  не меняется).

## Этап 4. #334.2 — скачивание `.rar` по ссылке `version_file?…`

### 4.1. Резолвинг `version_file?` перед загрузкой

- [`OneCUpdatesService.IsFileEndpointUrl`](Configuration%20Management/Services/OneCUpdatesService.cs:727):
  НЕ добавлять `version_file?` в «файловые» эндпоинты (это страница-посредник), а наоборот —
  гарантировать, что ответ `version_file?…` всегда проходит резолвинг:
  - в [`DownloadUpdateAsync`](Configuration%20Management/Services/OneCUpdatesService.cs:619) цикл
    уже следует по HTML-страницам через [`SelectNextDistributionUrl`](Configuration%20Management/Services/OneCUpdatesService.cs:1040)
    (приоритет: якорь «Скачать дистрибутив»/«Скачать файл» → setup-архивы → transfer_file…);
    для 8.3.27.2325 разметка страницы скачивания `.rar` может отличаться — получить/сохранить
    образец страницы (как в 0.3.10.3 для version_files: диагностический HTML рядом с журналом) и
    при необходимости расширить приоритеты выбора (например, якорь с текстом, содержащим имя файла
    `setuptc64…rar`).
- [`DownloadDistributionAsync`](Configuration%20Management/Services/OneCUpdatesService.cs:791):
  **многопоточную загрузку запускать только после резолвинга**, если URL — промежуточная страница
  (`version_file?…` или ответ будет HTML):
  - добавить предварительный шаг `ResolveDistributionUrlAsync(url)` (внутренний, переиспользует
    логику `DownloadUpdateAsync` до шага сохранения): возвращает конечный URL (transfer_file/CDN)
    или исходный, если это сразу бинарник;
  - уже скачанный HTML «в никуда» не сохранять: перед принятием результата многопоточной загрузки
    проверять первые байты файла — если это HTML-документ (`<!doctype`/`<html`), файл удалить,
    вернуться к однопоточному резолвингу (защита дублирует [`LooksLikeHtmlDocument`](Configuration%20Management/Services/OneCUpdatesService.cs:736)).
- [`PlatformUpdateService`](Configuration%20Management/Services/PlatformUpdateService.cs) /
  [`PlatformUpdateViewModel`](Configuration%20Management/ViewModels/PlatformUpdateViewModel.cs):
  изменений не требуется (download идёт через `IOneCUpdatesService.DownloadDistributionAsync`),
  только журнал: при неудаче писать признак «страница-посредник не разрешилась в файл» и URL.

### 4.2. Тесты

- [`OneCUpdatesDistributionResolutionTests`](ConfigurationManagement.Tests/OneCUpdatesDistributionResolutionTests.cs):
  - цепочка `version_file?…` → HTML со «Скачать дистрибутив» → `transfer_file?…` → бинарник — файл
    сохранён под именем `.rar`;
  - `version_file?…` → HTML без ссылки → null + диагностический файл;
  - защита: многопоточный «файл», оказавшийся HTML, не принимается (fake handler отдаёт HTML
    с content-type application/octet-stream).

### 4.3. Критерии

- «Обновление платформы 8.3.27.2325» скачивает `setuptc64_8_3_27_2325.rar`; в окне и журнале —
  фактический URL после резолвинга и размер файла.

## Этап 5. #352 «Цепочки обновлений» — три пункта

### 5.1. Кнопка «Скачать» (одиночный релиз): additional_file с `.cf` → выбор дистрибутива обновления

- Новый сервисный метод в [`IOneCUpdatesService`](Configuration%20Management/Services/IOneCUpdatesService.cs) /
  [`OneCUpdatesService`](Configuration%20Management/Services/OneCUpdatesService.cs):
  `Task<IReadOnlyList<UpdateFileChoice>> GetReleaseFileChoicesAsync(string url, CancellationToken ct)`:
  - вход — ссылка релиза/`additional_file?nick=…&ver=…` (`.cf` в query `path`);
  - строит адрес страницы файлов версии (`version_files?nick=…&ver=…` — nick/ver извлекаются из
    query исходной ссылки; хелпер рядом с [`ToAbsoluteVersionFilesUrl`](Configuration%20Management/Services/OneCUpdatesService.cs:901));
  - со страницы файлов версии выбирает файловые ссылки с подписями: «Дистрибутив обновления»
    (приоритет — это `.cf`-обновление), «Полный дистрибутив» (может отсутствовать);
  - для каждой кандидаты резолвит конечный URL (как в 0.3.9.331/332: страница файла → якорь
    «Скачать дистрибутив» → transfer_file).
  - модель `UpdateFileChoice` (new, `Models/` или внутри сервиса): `Caption`, `Url`, `FileName`.
- Окна: [`UpdateCheckWindow.OnDownloadRow`](Configuration%20Management/Views/UpdateCheckWindow.xaml.cs:336)
  и зеркальный обработчик [`UpdateCheckWindow.Avalonia.cs`](Configuration%20Management/Views/UpdateCheckWindow.Avalonia.cs):
  - если `GetReleaseFileChoicesAsync` вернула 2+ варианта — диалог выбора (существующий
    `IDialogService`/`_dialogs` выбор из списка или минимальное новое окно-список; для Avalonia —
    эквивалент);
  - 1 вариант — качать сразу; 0 вариантов — прежний путь через [`DownloadUpdateAsync`](Configuration%20Management/Services/OneCUpdatesService.cs:619)
    (текущее поведение как fallback).
- Имя сохранения: расширение из выбранного файла (`.cf`), а не фиксированный `.zip`
  ([`BuildDownloadFileName`](Configuration%20Management/Views/UpdateCheckWindow.xaml.cs:587)) —
  в диалоге сохранения предлагать имя с расширением выбранного файла.

### 5.2. Запоминание папки сохранения цепочки + «Открыть»

- Настройка в [`AppSettings`](Configuration%20Management/Models/AppSettings.cs):
  `UpdateChainFolder` (string, по умолчанию пусто). Сохранение через существующий механизм
  сохранения настроек (как папки платформы — уточнить место, где платформа хранит свою папку,
  и сделать единообразно).
- [`UpdateCheckWindow.DownloadChainAsync`](Configuration%20Management/Views/UpdateCheckWindow.xaml.cs:477)
  (и Avalonia-версия): начальный каталог диалога — сохранённая папка; после успешного выбора —
  записать в настройки.
- UI: над таблицей цепочки строка «Папка: <путь>» + кнопка «Открыть»
  ([`UpdateCheckWindow.xaml`](Configuration%20Management/Views/UpdateCheckWindow.xaml),
  [`UpdateCheckWindow.Avalonia.cs`](Configuration%20Management/Views/UpdateCheckWindow.Avalonia.cs)):
  - путь обновляется после выбора; кнопка открывает папку в проводнике
    (`Process.Start explorer` / `Process.Start("xdg-open")` — обёртка уже есть в
    [`ExplorerIntegrationService`](Configuration%20Management/Services/ExplorerIntegrationService.cs)
    или открыть через `OpenFolderAndSelect`-подобный хелпер);
  - при пустой папке строка показывает подсказку «выберите папку при скачивании».
- Локализация: ключи `Updates.Chain.Folder`, `Updates.Chain.OpenFolder`, ru/en
  ([`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json), `en.json`).

### 5.3. Диалог «Попробовать ещё раз?» с таймером ~60 с

- Новое окно `ChainRetryWindow` (WPF: `Views/ChainRetryWindow.xaml(.cs)`; Avalonia: code-built
  `Views/ChainRetryWindow.Avalonia.cs`): сообщение «Цепочка скачалась с ошибками: N из M» +
  вопрос «Попробовать ещё раз?»; кнопка «Да» с обратным отсчётом 60 с («Да (57)»), по нулю —
  автоматический «Нет»; кнопка «Нет». `DispatcherTimer`/`DispatcherTimer Avalonia`.
- [`UpdateCheckWindow.xaml.cs:555-560`](Configuration%20Management/Views/UpdateCheckWindow.xaml.cs):
  вместо `ShowWarning(LoadedFailed)` — открыть диалог; «Да» → повторный вызов загрузки цепочки
  ([`UpdateChainDownloadPlanner.SelectPendingSteps`](Configuration%20Management/Services/UpdateChainDownloadPlanner.cs:31)
  сам пропустит скачанное и покажет корректный «Скачивается X из Y»); ограничить число
  автоматических вопросов разумно (спрашивать после каждой попытки с ошибками — по ТЗ).
- Avalonia-версия зеркально ([`UpdateCheckWindow.Avalonia.cs`](Configuration%20Management/Views/UpdateCheckWindow.Avalonia.cs)).
- Локализация: `Updates.Chain.Retry.Title`, `Updates.Chain.Retry.Message`, `Updates.Chain.Retry.Yes`,
  `Updates.Chain.Retry.No`, ru/en.

### 5.4. Не регрессировать

- Докачка: пропуск существующих непустых файлов ([`UpdateChainDownloadPlanner`](Configuration%20Management/Services/UpdateChainDownloadPlanner.cs)),
  счётчик «Скачивается N из M», суффикс `.download` ([`OneCUpdatesService.PartialSuffix`](Configuration%20Management/Services/OneCUpdatesService.cs:616)),
  «Все файлы цепочки уже скачаны».
- Тесты: [`UpdateChainDownloadPlannerTests`](ConfigurationManagement.Tests/UpdateChainDownloadPlannerTests.cs)
  (без изменений — регресс), новые:
  - парсер nick/ver из `additional_file?…`-ссылки (продлить [`OneCUpdatesUrlTests`](ConfigurationManagement.Tests/OneCUpdatesUrlTests.cs));
  - `GetReleaseFileChoicesAsync` на фикстурах HTML: обе подписи, только «Дистрибутив обновления», ничего;
  - чистая логика диалога повтора (интервал/текст кнопки) — вынести в helper (например
    `ChainRetryCountdown`), тест без UI.

### 5.5. Критерии

- «Скачать» для релиза с `.cf`-дополнительным файлом скачивает дистрибутив обновления (или предлагает
  выбор), WARN «не найдена ссылка на дистрибутив» исчезает.
- Папка цепочки запоминается, путь виден над таблицей, «Открыть» работает на Windows и Linux.
- При ошибках цепочки — диалог с 60-секундным таймером; «Да» докачивает только недостающее.

## Этап 6. #356 — детерминированное восстановление текущей строки

**Хоткеи не трогать.**

### 6.1. Детерминированный признак «закрыто пунктом меню»

- WPF [`MainWindow.Hotkeys.cs`](Configuration%20Management/Views/MainWindow.Hotkeys.cs):
  - в [`OnTreeMenuPopupMouseLeftButtonDown`](Configuration%20Management/Views/MainWindow.Hotkeys.cs:820)
    (подписан на попап ВСЕХ tree-like меню, включая меню закладок) фиксировать, что клик пришёл по
    пункту: `FindAncestor<MenuItem>(e.OriginalSource as DependencyObject) is not null` → новое поле
    `_menuClosedByItemClick = true`;
  - сбрасывать флаг в [`OnContextMenuOpened`](Configuration%20Management/Views/MainWindow.Hotkeys.cs:789);
  - в [`OnContextMenuClosed`](Configuration%20Management/Views/MainWindow.Hotkeys.cs:828) значение
    `overMenuItem` вычислять как `_menuClosedByItemClick || Mouse.DirectlyOver-эвристика`
    (hit-test оставить как запасной путь для закрытия мышью мимо попапа);
  - ветка `menuItem` ([`MainWindow.Hotkeys.cs:982-997`](Configuration%20Management/Views/MainWindow.Hotkeys.cs))
    остаётся, но получает детерминированный вход.
- Avalonia [`MainWindow.Avalonia.Events.cs`](Configuration%20Management/Views/MainWindow.Avalonia.Events.cs):
  аналогично — в обработчике клика по попапу (запись снимка ~строка 561) фиксировать пункт меню,
  использовать в `overMenuItem` (~строка 265).
- Дополнительно (устойчивость «через раз»): после восстановления по пункту запланировать второй
  контрольный проход стабилизации на приоритете `Loaded`/`Background` (контейнеры после Recycling
  могут переработаться ПОСЛЕ первого прохода) — переиспользовать `EnsureSelectionStable`
  (идемпотентен) / `ShouldRetryRestoreForUnrealizedContainer`
  ([`BatchSelectionHelper.cs:606`](Configuration%20Management/Services/BatchSelectionHelper.cs)).

### 6.2. Чистая логика + тесты

- [`BatchSelectionHelper`](Configuration%20Management/Services/BatchSelectionHelper.cs):
  предикат [`ShouldRestoreCurrentSelectionAfterMenuItemClick`](Configuration%20Management/Services/BatchSelectionHelper.cs:629)
  дополнить параметром `closedByItemClick` (детерминированный сигнал приоритетнее эвристики) —
  либо добавить отдельный предикат `ShouldRestoreCurrentSelectionAfterMenuCloseEx`; сигнатура
  фиксируется тестами.
- [`BatchSelectionHelperTests`](ConfigurationManagement.Tests/BatchSelectionHelperTests.cs): новые
  кейсы (closedByItemClick=true / false + над строкой / ESC).
- [`MenuCloseTraceFormatTests`](ConfigurationManagement.Tests/MenuCloseTraceFormatTests.cs): формат
  строки решения дополнить полем `closedByItemClick` (обратная совместимость парсера — поле
  необязательное).
- Трассировка: в обеих платформах писать `closedByItemClick` в MenuClosed-запись — по логам
  пользователя сразу видно, почему восстановление не сработало.

### 6.3. Критерии

- 20/20 повторов: Ctrl+B → клик по пункту меню закладок → подсветка/выделение текущей строки
  восстановлено; то же для контекстного меню дерева; ESC и клик мимо — выбор не переносят;
  мультивыделение и хоткеи не задеты.

## Этап 7. #357 — «Поле/Область/Режим»: ключи вместо значений

### 7.1. Аудит и добавление ключей во встроенные словари

- Пройтись по [`ConnectionReplaceWindow.xaml`](Configuration%20Management/Views/ConnectionReplaceWindow.xaml)
  (все `{loc:Loc ConnectionReplace.*}`), [`ConnectionReplaceWindow.Avalonia.cs`](Configuration%20Management/Views/ConnectionReplaceWindow.Avalonia.cs)
  и [`ConnectionReplaceViewModel`](Configuration%20Management/ViewModels/ConnectionReplaceViewModel.cs:426)
  (`ConnectionReplace.Fields.*`, `.Scopes.*`, `.Modes.*`, `Columns.*`, `Summary.*`, `Result.*`) —
  составить полный список используемых ключей и сверить с встроенными
  [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) /
  [`en.json`](Configuration%20Management/Localization/Languages/en.json).
- Добавить отсутствующие ключи (как минимум `ConnectionReplace.FieldLabel`, `ScopeLabel`,
  `ModeLabel` — в выпущенной сборке 0.3.10.3 их не было) в оба файла; форматы `{0}` — сверить
  с кодом (`Scopes.Selected`, `Summary.Format` и т.д.).
- Проверить, что все ключи, использующиеся ТОЛЬКО в Avalonia-версии окна (окно строится кодом
  через `LocalizationManager.T`), тоже присутствуют.

### 7.2. Защита от рассинхронизации (регресс-тест)

- Новый тест `ConnectionReplaceLocalizationTests` (по образцу
  [`SettingsWindowXamlResourcesTests`](ConfigurationManagement.Tests/SettingsWindowXamlResourcesTests.cs)):
  - распарсить XAML/код (regex по `loc:Loc ConnectionReplace\…` и `"ConnectionReplace.…"` в .cs
    соответствующих файлов) и ассертить, что КАЖДЫЙ найденный ключ есть и в ru.json, и в en.json;
  - встроенность: тест читает файлы словарей из репозитория (те же, что встраиваются
    `EmbeddedResource Include="Localization\Languages\*.json"` —
    [`Configuration Management.csproj:137`](Configuration%20Management/Configuration%20Management.csproj)).
- Продлить [`LocalizationFallbackTests`](ConfigurationManagement.Tests/LocalizationFallbackTests.cs):
  случай «внешний устаревший ru.json без `ConnectionReplace.FieldLabel`» → `T` возвращает
  значение встроенного словаря (а не ключ) — фиксирует смысл issue #357.
- Разметку [`ConnectionReplaceWindow.xaml`](Configuration%20Management/Views/ConnectionReplaceWindow.xaml)
  править только если аудит найдёт опечатки в ключах; Avalonia-версию — аналогично.

### 7.3. Критерии

- Свежий exe + устаревший внешний `Languages/ru.json`: все поля окна «Замена строк подключения»
  показывают значения («Поле», «Область», «Режим», пункты списков, заголовки колонок).

## Этап 8. Версия, CHANGELOG, README

- [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj:62-65):
  `Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion` → **0.3.11.0**.
- [`CHANGELOG.md`](CHANGELOG.md): новая запись `[0.3.11.0] — дата` по образцу 0.3.10.3
  (разделы «Исправлено» / «Добавлено» / «Тесты»: `dotnet test` зелёный, кросс-сборка
  `dotnet build -p:BuildLinux=true` без ошибок).
- [`README.md`](README.md): обновить раздел версии/загрузки (если содержит номер версии или
  описание формата релизных файлов — вернуть «single-file EXE для Windows»).
- Issues: комментарии с релизом v0.3.11.0 (#358 — приоритетно, с объяснением «вернули EXE»);
  issues не закрывать до подтверждения репортёров.

## Этап 9. Сборка артефактов (на следующем этапе, план НЕ запускает сборку)

- **Windows**: `dotnet publish -c Release -r win-x64` → `dist/win-x64/ConfigurationManagement.exe`
  (single-file, self-contained, сжатие — свойства в csproj). В релиз — ассет
  `ConfigurationManagement.exe` (обязательный, для автообновления) + опционально
  `ConfigurationManagement-0.3.11.0-win-x64.zip`; SHA256SUMS.txt.
- **Linux**: кросс-сборка `dotnet publish -c Release -r linux-x64 -p:BuildLinux=true` →
  `dist/linux-x64/ConfigurationManagement`; пакет `.deb` — скрипт
  `publish/build_deb_win_0.3.11.0.py` (копия `build_deb_win_0.3.10.3.py`, версия читается из
  csproj); AppImage — `package/linux/appimage.sh` (при необходимости включить в релиз).
- Проверки перед выкладкой: запуск exe на чистой машине, автообновление с 0.3.10.3 на 0.3.11.0
  (главный сценарий #358), `dpkg -i` на чистой системе, контрольные суммы.

## Порядок выполнения

1. **Этап 1 (#358)** — критический: возврат EXE в релиз + защита загрузчика (+ тесты).
2. **Этап 3 (#330)** и **Этап 4 (#334.2)** — скачивание платформы: подписи вариантов, резолвинг
   `version_file?` (+ тесты, фикстуры).
3. **Этап 5 (#352)** — три пункта цепочек (+ тесты, новые ключи локализации).
4. **Этап 2 (#324)** — детектор справки rac (+ тесты).
5. **Этап 6 (#356)** — детерминированное восстановление строки (+ трассировка, тесты).
6. **Этап 7 (#357)** — аудит/добавление ключей локализации (+ регресс-тест).
7. Полный `dotnet test` + кросс-сборка Linux (`dotnet build -p:BuildLinux=true`).
8. **Этап 8** — версия/CHANGELOG/README; **Этап 9** — сборка и публикация артефактов (отдельный этап).

Код не изменяется данным планом; коммиты не создаются.
