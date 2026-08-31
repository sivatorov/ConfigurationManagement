# История изменений

Все заметные изменения проекта «Управление конфигурациями 1С» фиксируются в этом файле.

Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/),
версионирование — на [Semantic Versioning](https://semver.org/lang/ru/).

> Примечание: история сведена из детальных промежуточных сборок (микро-версий вида
> `0.3.x.y`) к сводным выпускам по основным версиям, чтобы отделить значимые
> возможности от точечных исправлений и регрессий предыдущих сборок.

## [0.3.5.88] — 2026-08-31

При переключении видимости колонки «Действия» главное окно теперь корректно пересчитывает выравнивание заголовка с данными: `nameof(MainViewModel.ShowActionsColumn)` добавлен в обработчик изменения свойств в [`MainWindow.xaml.cs`](Configuration%20Management/Views/MainWindow.xaml.cs), поэтому колонки не разъезжаются при включении/выключении «Действий» в окне настроек — так же, как для остальных `Show*Column`.

### Изменено

- **Пересчёт выравнивания при переключении видимости колонки «Действия»** в [`MainWindow.xaml.cs`](Configuration%20Management/Views/MainWindow.xaml.cs): в условие `e.PropertyName is ...` блока пересчёта выравнивания заголовка с данными (через `Dispatcher.BeginInvoke` → `AlignHeaderToData`) добавлена ветка `or nameof(MainViewModel.ShowActionsColumn)` (размещена после `ShowSizeColumn`). Теперь изменение `ShowActionsColumn` обрабатывается так же, как у остальных колонок.

### Версия

- **Версия поднята до `0.3.5.87` → `0.3.5.88`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.87] — 2026-08-31

В окне настроек (Настройки → Отображение → Колонки) появился переключатель видимости колонки «Действия»: в [`SettingsWindow.Display.cs`](Configuration%20Management/Views/SettingsWindow.Display.cs) добавлена ветка `"Actions"` в метод `ColumnVisible`, возвращающая реальную настройку `ShowActionsColumn`, а при сохранении настроек в [`SettingsWindow.xaml.cs`](Configuration%20Management/Views/SettingsWindow.xaml.cs) значение `VisibleOf("Actions")` передаётся последним аргументом в `ApplyDisplaySettings`. Теперь пользователь может включать и выключать колонку «Действия» из окна настроек наравне с остальными колонками.

### Изменено

- **Переключатель видимости колонки «Действия» в окне настроек**: в [`SettingsWindow.Display.cs`](Configuration%20Management/Views/SettingsWindow.Display.cs) в метод `ColumnVisible` добавлена ветка `"Actions" => _viewModel.ShowActionsColumn` (размещена после `"Size"`, ветка `_ => true` по умолчанию сохранена в конце). В [`SettingsWindow.xaml.cs`](Configuration%20Management/Views/SettingsWindow.xaml.cs) при сохранении настроек отображения в вызов `ApplyDisplaySettings(...)` добавлен последний аргумент `VisibleOf("Actions")` (после порядка колонок), использующий уже существующую локальную функцию `VisibleOf`.

### Версия

- **Версия поднята до `0.3.5.86` → `0.3.5.87`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.86] — 2026-08-31

Колонка «Действия» в списке баз теперь действительно может скрываться через переключатель видимости `ShowActionsColumn`. Ширина колонки во всех трёх сетках (заголовок, строка группы, строка базы) привязана через конвертер `ColumnVis` к `ShowActionsColumn`, убран жёсткий `MinWidth=120`, чтобы колонка могла схлопнуться в 0 при выключенной настройке.

### Изменено

- **Колонка «Действия» скрывается через `ShowActionsColumn`** в [`MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml): во всех трёх определениях колонки (заголовок `x:Name="ActionsColumn"`, строка группы, строка базы) привязка ширины `DoubleToGridLength` к `ActionsColumnWidth` заменена на `MultiBinding` конвертера `ColumnVis` с двумя значениями — первым `DataContext.ShowActionsColumn` (показывать колонку) и вторым `DataContext.ActionsColumnWidth` (сохранённая ширина). Атрибут `MinWidth="120"` убран, чтобы колонка могла схлопнуться в 0 при скрытии; минимальная ширина в 120 пикселей по-прежнему гарантируется обработчиком перетаскивания разделителя, пока колонка видима.

### Версия

- **Версия поднята до `0.3.5.85` → `0.3.5.86`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.85] — 2026-08-31

Настройка `ShowActionsColumn` подключена к модели представления (Windows/WPF): добавлено поле, загрузка из настроек при запуске, публичное свойство, применение через `ApplyDisplaySettings` и сохранение. Настройка полностью задействована в логике приложения и готова к использованию разметкой и окном настроек в следующих сборках.

### Изменено

- **Настройка `ShowActionsColumn` подключена к Windows-версии `MainViewModel`**: в [`MainViewModel.cs`](Configuration%20Management/ViewModels/MainViewModel.cs) объявлено поле `_showActionsColumn` (по умолчанию `true`) и в конструкторе добавлена загрузка `_showActionsColumn = settings.ShowActionsColumn;`. В [`MainViewModel.Display.cs`](Configuration%20Management/ViewModels/MainViewModel.Display.cs) добавлено публичное свойство `ShowActionsColumn => _showActionsColumn`, а метод `ApplyDisplaySettings(...)` получил параметр `bool showActionsColumn = true` (в конце, с дефолтом), присваивает `_showActionsColumn` и уведомляет `OnPropertyChanged(nameof(ShowActionsColumn))`. В [`MainViewModel.Launch.cs`](Configuration%20Management/ViewModels/MainViewModel.Launch.cs) при сохранении настроек добавляется `ShowActionsColumn = _showActionsColumn`.

### Версия

- **Версия поднята до `0.3.5.84` → `0.3.5.85`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.84] — 2026-08-31

Добавлена новая настройка `ShowActionsColumn` (по умолчанию включена), которая позволит скрывать колонку «Действия» (кнопки запуска/конфигуратора/очистки кеша) в списке баз. Реализация видимости появится в последующих сборках.

### Изменено

- **Новая настройка `ShowActionsColumn`** в [`AppSettings.cs`](Configuration%20Management/Models/AppSettings.cs): добавлено булево свойство (значение по умолчанию `true`), управляющее отображением колонки «Действия» в списке баз. На данном этапе настройка уже доступна в модели настроек, а применение видимости колонки в ViewModel/разметке будет реализовано в следующих сборках.

### Версия

- **Версия поднята до `0.3.5.83` → `0.3.5.84`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.83] — 2026-08-31

Тестовая сборка для проверки автоматического обновления (Windows): выпущена следующая версия, чтобы убедиться, что программа обнаруживает и устанавливает новое обновление при включённой проверке обновлений.

### Версия

- **Версия поднята до `0.3.5.82` → `0.3.5.83`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.82] — 2026-08-31

Программа больше не открывает окно/страницу GitHub при обновлении: теперь она всегда сама скачивает Windows-версию (single-file exe) по прямой ссылке. Удалён fallback-метод `OpenInBrowser`, который открывал страницу релиза в браузере при отсутствии прямой ссылки. Если прямой ссылки на exe нет — показывается только локализованная ошибка.

### Исправлено

- **Больше никакого окна GitHub при обновлении** (Windows/WPF). В [`UpdateService.DownloadAndInstallAsync`](Configuration%20Management/Services/UpdateService.cs) убран блок, который при пустом `DownloadUrl` открывал страницу релиза в браузере через [`OpenInBrowser`](Configuration%20Management/Services/UpdateService.cs). Теперь при отсутствии прямой ссылки на exe программа лишь показывает локализованную ошибку `Update.NoDownloadUrl` («скачивание недоступно») и завершает операцию, не открывая GitHub. Вместе с fallback на прошлом этапе (Atom-лента теперь отдаёт прямую ссылку) это гарантирует, что в нормальном сценарии обновление всегда скачивается самим приложением.

### Версия

- **Версия поднята до `0.3.5.81` → `0.3.5.82`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.81] — 2026-08-31

Исправление резервного источника проверки обновлений (Atom-лента GitHub): теперь fallback отдаёт прямую ссылку на Windows-сборку, благодаря чему программа сможет сама скачать новый exe без открытия браузера GitHub, даже когда основной GitHub Releases API недоступен.

### Исправлено

- **Прямая ссылка на exe в Atom-fallback** (Windows/WPF). В [`GitHubReleaseService.GetLatestFromAtomAsync`](Configuration%20Management/Services/GitHubReleaseService.cs) после извлечения тега релиза из первого `<entry>` ленты теперь собирается прямая ссылка на Windows-сборку `ConfigurationManagement.exe` по шаблону `https://github.com/sivatorov/ConfigurationManagement/releases/download/{ТЕГ}/ConfigurationManagement.exe` и присваивается свойству `DownloadUrl` возвращаемого `ReleaseInfo`. Тег подставляется без нормализации (как в `<title>` ленты), небезопасные символы пути экранируются, итоговый URL проверяется через `Uri.TryCreate` (см. `BuildWindowsDownloadUrl`). Раньше `DownloadUrl` в fallback оставался `null`, и при недоступности GitHub Releases API программа открывала окно GitHub вместо самостоятельной загрузки. `HtmlUrl` (страница релиза) по-прежнему заполняется как запасной путь для ручной загрузки.

### Версия

- **Версия поднята до `0.3.5.80` → `0.3.5.81`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.80] — 2026-08-31

Тестовая сборка для проверки полностью автоматического обновления (Windows): выпущена следующая версия, чтобы убедиться, что программа обнаруживает и молча устанавливает новое обновление при включённой настройке «Автоматически обновлять приложение».

### Версия

- **Версия поднята до `0.3.5.79` → `0.3.5.80`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.79] — 2026-08-31

Итоговая сборка подсистемы полностью автоматического обновления (Windows): подтверждена компиляция Release-конфигурации, документация обновлена.

### Исправлено / Прочее

- Выполнена контрольная Release-сборка Windows-версии; подсистема автообновления компилируется без ошибок и предупреждений. `dotnet build -c Release` прошёл успешно (`bin\Release\net10.0-windows\win-x64\ConfigurationManagement.dll`, 0 ошибок / 0 предупреждений). Также выполнен self-contained single-file publish через [`build-windows-single-file.ps1`](Configuration%20Management/build-windows-single-file.ps1): собран одиночный исполняемый файл `ConfigurationManagement.exe` (~78.9 МБ) в `dist\win-x64`.

### Версия

- **Версия поднята до `0.3.5.78` → `0.3.5.79`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.78] — 2026-08-31

Реализован полностью автоматический self-update: при включённой настройке «Автоматически обновлять приложение» программа сама скачивает, устанавливает и перезапускается при обнаружении новой версии, без диалога подтверждения и участия пользователя.

### Добавлено

- **Автоматическая установка обновлений без подтверждения** (Windows/WPF). В [`UpdateService`](Configuration%20Management/Services/UpdateService.cs) добавлено свойство `AutoUpdateEnabled`; фоновая проверка [`CheckForUpdatesAsync`](Configuration%20Management/Services/UpdateService.cs) при наличии новой версии и включённом флаге сразу вызывает [`DownloadAndInstallAsync`](Configuration%20Management/Services/UpdateService.cs) — скачивает self-contained exe, заменяет текущий исполняемый файл через временный PowerShell-помощник и перезапускает приложение, минуя диалог «Скачать/Отмена». Флаг устанавливается из настроек в [`App.OnStartup`](Configuration%20Management/App.xaml.cs) (`settings.AutoUpdateEnabled`). При выключенном флаге сохраняется прежнее поведение с диалогом подтверждения. Ручная проверка «Проверить обновления» по-прежнему показывает диалог/результат, не устанавливая молча: кнопка является явным действием пользователя, поэтому даже при включённом автообновлении она не запускает установку без запроса.

### Версия

- **Версия поднята до `0.3.5.77` → `0.3.5.78`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.77] — 2026-08-31

Добавлена настройка «автоматически обновлять приложение» (флаг `AutoUpdateEnabled`) и её UI-переключатель в окне настроек — подготовка к полностью автоматическому self-update без подтверждения пользователя.

### Добавлено

- **Настройка «Автоматически обновлять приложение»** (Windows/WPF). Новый флаг [`AppSettings.AutoUpdateEnabled`](Configuration%20Management/Models/AppSettings.cs) (по умолчанию `true`) управляет автоматической установкой новых версий приложения без запроса подтверждения (используется в следующих пунктах). В окне настроек вкладки «Настройки» → «Поведение приложения» под переключателем `CheckForUpdatesOnStartupCheck` добавлен переключатель `AutoUpdateEnabledCheck` в [`SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml) с иконкой обновления и локализованной подписью. Загрузка текущего значения при открытии окна выполняется в [`SettingsWindow.Display.cs`](Configuration%20Management/Views/SettingsWindow.Display.cs), сохранение при «ОК» — через [`MainViewModel.ApplyAppBehaviorSettings`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs); значение персистится в `settings.json` через [`MainViewModel.BuildSettings`](Configuration%20Management/ViewModels/MainViewModel.Launch.cs). Ключи локализации `Settings.General.AutoUpdate` и `Settings.General.AutoUpdateTooltip` согласованы в `ru.json` и `en.json`.

### Версия

- **Версия поднята до `0.3.5.76` → `0.3.5.77`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.76] — 2026-08-31

Тестовая сборка для проверки автоматического обновления.

### Версия

- **Версия поднята до `0.3.5.75` → `0.3.5.76`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.75] — 2026-08-31

Исправлена ошибка «Не удалось проверить наличие обновлений» при проверке обновлений на Windows: добавлен резервный источник данных о релизах (Atom-лента GitHub), исправлено распознавание версий из тегов с префиксом `new-`, а при отсутствии прямой ссылки на установочный файл теперь открывается страница релиза в браузере.

### Исправлено

- **Fallback на Atom-ленту релизов** в [`GitHubReleaseService.GetLatestReleaseAsync`](Configuration%20Management/Services/GitHubReleaseService.cs). Раньше проверка обновлений использовала только GitHub Releases API (`api.github.com`); при его недоступности или таймауте (`ConnectTimeoutError`) показывалась ошибка `Update.CheckFailed`, хотя сам `github.com` работал. Теперь сначала пробуется API `releases/latest`, а если он не ответил/не распознан — берётся резервный источник — Atom-лента `https://github.com/sivatorov/ConfigurationManagement/releases.atom` (работает через обычный `github.com`). Из первого `<entry>` ленты заполняются `TagName`, `Name`, `Body` (текст `<content>` очищается от разметки и переносов), `PublishedAt` и `HtmlUrl` (атрибут `href` у `<link rel="alternate">`).
- **Корректный парсинг версий из тегов `new-*`** в [`NormalizeTag`](Configuration%20Management/Services/GitHubReleaseService.cs). Раньше обрезался только ведущий `v`/`V`, поэтому теги вида `new-0.3.5.75` не распознавались (`Version.TryParse` возвращал `false`) и новая версия не находилась даже при успешном ответе. Теперь из тега извлекается подстрока с первой цифры до первого пробела/двоеточия/`+` (например `new-0.3.5.75` → `0.3.5.75`, `v0.3.5.74` → `0.3.5.74`, `new-0.3.5.16: Merge …` → `0.3.5.16`). Код устойчив и к 3-, и к 4-частным версиям; поведение для обычных тегов не изменилось.
- **Открытие страницы релиза при отсутствии прямой ссылки на exe** в [`UpdateService.DownloadAndInstallAsync`](Configuration%20Management/Services/UpdateService.cs). В `ReleaseInfo` добавлено свойство `HtmlUrl` (страница релиза, `html_url` из API либо `href` из ленты). Если `DownloadUrl` пуст (например, при получении выпуска из Atom-ленты, где прямой ссылки на asset нет), при подтверждении «Скачать» теперь открывается страница релиза в браузере по умолчанию (`Process.Start` с `UseShellExecute = true`) вместо ошибки `Update.NoDownloadUrl`; ошибка показывается только если недоступен и `HtmlUrl`.

### Версия

- **Версия поднята до `0.3.5.74` → `0.3.5.75`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.74] — 2026-08-31

Реализована реальная загрузка и установка Windows-версии приложения (self-update) вместо открытия браузера, а также кнопка «Проверить обновления» во вкладке «О программе» — завершающая часть подсистемы автоматического обновления из GitHub Releases.

### Добавлено

- **Кнопка «Проверить обновления»** (Windows/WPF). Во вкладке «О программе» окна настроек [`SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml) рядом с кнопкой «Скопировать техническую информацию» добавлена кнопка с иконкой `PackIcon Kind="Update"` и локализованной подписью (`Settings.About.CheckForUpdates`). Обработчик [`OnCheckForUpdates_Click`](Configuration%20Management/Views/SettingsWindow.Platforms.cs) получает [`UpdateService`](Configuration%20Management/Services/UpdateService.cs) через `AppServices.GetRequiredService<UpdateService>()` и вызывает ручную проверку.
- **Ручная проверка обновлений**. В [`UpdateService`](Configuration%20Management/Services/UpdateService.cs) добавлен метод `CheckForUpdatesManualAsync()`, который в отличие от фоновой проверки явно сообщает результат: ошибку проверки (`Update.CheckFailed`), «вы используете актуальную версию» (`Update.UpToDate`) или показывает диалог о доступной новой версии. Фоновая `CheckForUpdatesAsync()` при запуске не изменена.
- **Реальная загрузка и установка (self-update)**. Метод [`DownloadAndInstall`](Configuration%20Management/Services/UpdateService.cs) заменён на `DownloadAndInstallAsync(ReleaseInfo)`: скачивает self-contained single-file `ConfigurationManagement.exe` из `ReleaseInfo.DownloadUrl` через `HttpClient` во временный каталог `%TEMP%\ConfigurationManagement\update`, проверяет размер файла, затем создаёт и запускает временный PowerShell-помощник, который дожидается завершения основного процесса (по PID), заменяет текущий исполняемый файл скачанным (`Move-Item -Force`), перезапускает приложение и удаляет сам скрипт. После запуска помощника показывается сообщение о перезапуске (`Update.RestartPrompt`) и вызывается `Application.Current.Shutdown()`. При отсутствии прямой ссылки на asset (`Update.NoDownloadUrl`), сетевых ошибках (`Update.DownloadFailed`) или сбое установки (`Update.InstallFailed`) показывается локализованная ошибка через `IDialogService.ShowError`.
- **Локализация**: новые ключи `Update.CheckFailed`, `Update.UpToDate`, `Update.Downloading`, `Update.RestartPrompt`, `Update.NoDownloadUrl`, `Update.DownloadFailed`, `Update.InstallFailed` и `Settings.About.CheckForUpdates` согласованы в `ru.json` и `en.json`.
- **Версия поднята до `0.3.5.73` → `0.3.5.74`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.73] — 2026-08-31

Реализована фоновая проверка обновлений при запуске и диалог «Доступна новая версия» с кнопками «Скачать»/«Отмена» — логическая завершающая часть подсистемы автоматического обновления из GitHub Releases (загрузка и установка exe будет добавлена следующей задачей).

### Добавлено

- **Фоновая проверка обновлений при запуске** (Windows/WPF). В [`App.OnStartup`](Configuration%20Management/App.xaml.cs) сразу после показа главного окна, если включён флаг `CheckForUpdatesOnStartup`, запускается асинхронная проверка через новый [`UpdateService`](Configuration%20Management/Services/UpdateService.cs). Проверка не блокирует UI: метод `CheckForUpdatesAsync` выполняется в фоне, использует [`GitHubReleaseService.GetLatestReleaseAsync`](Configuration%20Management/Services/GitHubReleaseService.cs) и сравнивает доступную версию с текущей (`VersionInfo.Display()`) через `GitHubReleaseService.IsNewerThan`. При сбоях сети/парсинга проверка молча пропускается и не влияет на работу приложения.
- **Диалог «Доступна новая версия»** (Windows/WPF). Новый [`UpdateAvailableWindow`](Configuration%20Management/Services/UpdateAvailableWindow.xaml) показывает текущую и доступную версии и краткое описание выпуска (`ReleaseInfo.Body`), с кнопками **«Скачать»** (зелёная, по умолчанию) и **«Отмена»**. Кнопки «Скачать» и заголовки/подписи локализованы через ключи `Update.*`.
- **Сервис обновления [`UpdateService`](Configuration%20Management/Services/UpdateService.cs)**: оркестрирует проверку, показ диалога и обработку выбора пользователя. Предусмотрена точка расширения `DownloadAndInstall(ReleaseInfo)` — в текущей задаче она открывает ссылку скачивания Windows-инсталлятора (или страницу релиза, если asset не найден) в браузере по умолчанию через `Process.Start` с `UseShellExecute = true`; в следующей задаче метод будет заменён на скачивание и установку exe. Сервис зарегистрирован `AddSingleton` в [`AppServices.Configure()`](Configuration%20Management/AppServices.cs) в блоке `#if WINDOWS`; новые Windows-only файлы исключены из сборки Linux в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- **Локализация** диалога обновления: ключи `Update.NewVersionAvailable`, `Update.CurrentVersion`, `Update.NewVersion`, `Update.WhatsNew`, `Update.NoDescription`, `Update.Download`, `Update.Cancel`, `Update.Failed` согласованы в `ru.json` и `en.json`.
- **Версия поднята до `0.3.5.72` → `0.3.5.73`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.72] — 2026-08-31

Добавлена пользовательская настройка «проверять обновления при запуске» и UI-переключатель в окне настроек — следующий шаг подсистемы автоматического обновления из GitHub Releases.

### Добавлено

- **Настройка «Проверять обновления при запуске»** (Windows/WPF). Новый флаг [`AppSettings.CheckForUpdatesOnStartup`](Configuration%20Management/Models/AppSettings.cs) (по умолчанию `true`) управляет проверкой новых версий приложения через GitHub Releases при каждом запуске. В окне настроек вкладки «Настройки» → «Поведение приложения» добавлен переключатель `CheckForUpdatesOnStartupCheck` в [`SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml) со значком обновления и локализованной подписью. Загрузка текущего значения при открытии окна выполняется в [`SettingsWindow.Display.cs`](Configuration%20Management/Views/SettingsWindow.Display.cs), сохранение при «ОК» — через [`MainViewModel.ApplyAppBehaviorSettings`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs); значение персистится в `settings.json` через [`MainViewModel.BuildSettings`](Configuration%20Management/ViewModels/MainViewModel.Launch.cs). Ключи локализации `Settings.General.CheckForUpdatesOnStartup` и `Settings.General.CheckForUpdatesOnStartupTooltip` согласованы в `ru.json` и `en.json`.
- **Версия поднята до `0.3.5.71` → `0.3.5.72`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.71] — 2026-08-31

Реализован сервис проверки новых версий приложения через GitHub Releases (первый шаг подсистемы автоматического обновления).

### Добавлено

- **Сервис проверки обновлений из GitHub Releases** (Windows/WPF). Новый [`GitHubReleaseService`](Configuration%20Management/Services/GitHubReleaseService.cs) запрашивает последний выпуск через GitHub API (`/repos/sivatorov/ConfigurationManagement/releases/latest`), разбирает JSON и возвращает модель [`ReleaseInfo`](Configuration%20Management/Models/ReleaseInfo.cs): тег, название, описание, признак pre-release, дату публикации и прямую ссылку на Windows-инсталлятор (из `assets` выбирается `.exe` или asset с `win-x64` / `ConfigurationManagement.exe`). Ошибки сети/HTTP/парсинга обрабатываются внутри — метод возвращает `null`, не бросая исключений наружу. Статический помощник [`IsNewerThan`](Configuration%20Management/Services/GitHubReleaseService.cs) сравнивает тег выпуска (нормализуя ведущий `v`) с текущей версией приложения. Сервис зарегистрирован как `AddSingleton` в [`AppServices.Configure()`](Configuration%20Management/AppServices.cs) в блоке `#if WINDOWS`; на Linux он не компилируется и не нужен (автообновление Windows-only), модель `ReleaseInfo` остаётся общей.
- **Версия поднята до `0.3.5.70` → `0.3.5.71`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.70] — 2026-08-31

Версия программы теперь отображается и в видимой шапке главного окна (иконка + название программы), а не только в скрытом системном заголовке.

### Исправлено

- **Версия выводится в шапку окна (иконка + название программы)** (Windows/WPF). Окно использует `WindowChrome` с `CaptionHeight="0"` и скрытыми системными кнопками, поэтому системная строка заголовка не показывается — пользователь видел в шапке только название программы без версии. Теперь `UpdateWindowTitle()` в [`MainWindow.Language.cs`](Configuration Management/Views/MainWindow.Language.cs) обновляет и `Title` окна, и видимый `TextBlock` шапки (`AppTitleBlock` в [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml)) — с тем же суффиксом версии и защитой от дублирования.
- **Версия поднята до `0.3.5.69` → `0.3.5.70`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.69] — 2026-08-31

Исправлено сворачивание родительской группы, внутри которой есть вложенная группа «домашнее»: свёрнутая группа больше не разворачивается обратно при пересборке дерева.

### Исправлено

- **Свёрнутая группа с вложенной «домашнее» больше не раскрывается обратно** (Windows/WPF). При восстановлении выделения после пересборки дерева [`RevealAndSelectAfterRebuild()`](Configuration Management/Views/MainWindow.Tree.cs) принудительно раскрывал группы-предков выбранной цели. Опора только на `group.IsExpanded` была недостаточной: к моменту пересборки `IsExpanded` у только что свёрнутой группы мог быть `true` (например, при авторазворачивании поиска/фильтра), и если внутри такой группы (во вложенной «домашнее») была выбрана база или группа, она попадала в цепочку раскрытия и родитель раскрывался обратно — «не сворачивался». Теперь раскрытие предков дополнительно проверяет ключ группы в `_collapsedGroups` через `IsGroupCollapsed(node.NodeKey)`: явно свёрнутые пользователем группы принудительно не раскрываются, сохраняя состояние сворачивания/разворачивания остальных групп и поиск.
- **Версия поднята до `0.3.5.68` → `0.3.5.69`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.68] — 2026-08-31

Версия программы теперь гарантированно отображается в заголовке главного окна и не теряется и не дублируется при смене языка интерфейса.

### Добавлено

- **Отображение версии в заголовке главного окна защищено от дублирования** (Windows/WPF). Версия читается через [`VersionInfo.Display()`](Configuration Management/VersionInfo.cs) (информационная версия без суффикса `+<sha>`) и выводится в заголовок как «v0.3.5.68». Сборка заголовка вынесена в общий метод `UpdateWindowTitle()` в [`MainWindow.Language.cs`](Configuration Management/Views/MainWindow.Language.cs), который вызывается и из конструктора [`MainWindow.xaml.cs`](Configuration Management/Views/MainWindow.xaml.cs), и при пересборке интерфейса после смены языка (`RebuildAfterLanguageChange`). Метод начинается с базового локализованного имени `App.Title` и добавляет суффикс версии только если его ещё нет — поэтому суффикс не дублируется (не появляется «v0.3.5.68 v0.3.5.68») и не теряется (не остаётся просто «App.Title») даже при повторном применении XAML-привязки `Title="{loc:Loc App.Title}"`.
- **Версия поднята до `0.3.5.67` → `0.3.5.68`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.67] — 2026-08-31

Навигация по списку баз клавишей «стрелка вниз» (↓) при достижении нижней границы видимой области снова сдвигает список ровно на одну строку, как и «стрелка вверх» (↑).

### Исправлено

- **Прокрутка вниз снова сдвигает список ровно на одну строку** (Windows/WPF). В [`ScrollSelectedIntoView`](Configuration Management/Views/MainWindow.Tree.cs) перед замером позиции целевого контейнера (`TransformToAncestor(scrollViewer)` + `item.ActualHeight`) вызывается принудительная раскладка `item.UpdateLayout()`. Раньше при прокрутке вниз ниже края вьюпорта из-за Recycling-виртуализации создавались новые контейнеры строк, которые к моменту замера ещё не были разложены, из-за чего позиция и высота оказывались неактуальными и величина прокрутки получалась больше одной строки («прыжок»). Прокрутка вверх работала корректно, поскольку строки над вьюпортом уже реализованы и разложены; теперь поведение ↓ симметрично ↑. Выравнивание заголовка колонок (синхронизация через [`MainWindow.Scroll.cs`](Configuration Management/Views/MainWindow.Scroll.cs)) не затронуто.
- **Версия поднята до `0.3.5.66` → `0.3.5.67`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.66] — 2026-08-31

Отладочный вывод `[l10n-debug]` скрыт за `#if DEBUG` — больше не пишется в stderr в Release-сборке.

### Изменено

- **Отладочный вывод `[l10n-debug]` скрыт за `#if DEBUG`** (Windows/WPF). Строки `Console.Error.WriteLine("[l10n-debug] ...")`, помеченные как `[DEBUG]`, но ранее выполнявшиеся всегда, теперь компилируются только в Debug-конфигурации и не пишутся в stderr в Release-сборке. Затронутые места: блок диагностики локализации в `Initialize` и запись в `SetLanguage` в [`LocalizationManager.cs`](Configuration Management/Localization/LocalizationManager.cs), а также записи `LoadSettings` (отсутствие файла и `Language=`) и `SaveSettings` (`Language=`) в [`InfobaseRepository.cs`](Configuration Management/Services/InfobaseRepository.cs) — каждая обёрнута в `#if DEBUG ... #endif`, логика методов и фигурные скобки не нарушены. Легитимное логирование ошибок в `catch`-блоках (например `MainWindow.Language.cs`, `LocalizationSource.cs`) не затрагивалось.
- **Версия поднята до `0.3.5.65` → `0.3.5.66`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.65] — 2026-08-31

Убрана незащищённая debug-запись `cm_theme_debug.log`, которая создавалась в каталоге `%TEMP%` при каждом запуске/переключении темы даже в Release-сборке.

### Изменено

- **Debug-запись `cm_theme_debug.log` скрыта за `#if DEBUG`** (Windows/WPF). Несанкционированная запись во временный файл `%TEMP%\cm_theme_debug.log`, остававшаяся от отладки темы и выполнявшаяся даже в Release-сборке, теперь компилируется только в Debug-конфигурации и не попадает в релизные сборки. Затронутые места: блок `try { System.IO.File.AppendAllText(...) } catch` при старте в [`App.xaml.cs`](Configuration Management/App.xaml.cs), запись `[theme-debug]` в [`MainViewModel.Theme.cs`](Configuration Management/ViewModels/MainViewModel.Theme.cs) и запись `[settings]` в [`SettingsWindow.Schemes.cs`](Configuration Management/Views/SettingsWindow.Schemes.cs) — каждая обёрнута в `#if DEBUG ... #endif`, логика сохранена, в Release не выполняется.
- **Версия поднята до `0.3.5.64` → `0.3.5.65`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.64] — 2026-08-31

Иконка и надпись в заголовке колонки «Название» выровнены по левому краю (Windows/WPF).

### Исправлено

- **Иконка и надпись в заголовке колонки «Название» выровнены по левому краю** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) заголовок колонки «Название» (`StackPanel Grid.Column="4"` внутри `HeaderGrid`) уже прижат к левому краю звёздной колонки через `HorizontalAlignment="Left"`; для надёжности у дочерних элементов (`materialDesign:PackIcon Kind="FormatTitle"` и `TextBlock` с подписью «Название») явно заданы `HorizontalAlignment="Left"` и `VerticalAlignment="Center"`, чтобы никакой неявный стиль/триггер не мог отцентрировать содержимое колонки. Начало иконки и текста теперь точно совпадает с началом данных в строках. Соседние заголовки (Версия, Режим запуска, Сервер и т.д.) не изменялись.
- **Версия поднята до `0.3.5.64`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.63] — 2026-08-31

Иконка главного окна теперь берётся из файла app.ico (Windows/WPF).

### Изменено

- **Иконка главного окна теперь берётся из файла `app.ico`** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) атрибут `Window.Icon` заменён с `{StaticResource AppIcon}` на pack URI `pack://application:,,,/app.ico`, поэтому иконка окна в панели задач и в заголовке теперь извлекается из `app.ico`. Иконка в верхней строке-заголовке слева тоже переключена на `app.ico`: источник `<Image>` заменён с `{StaticResource AppIcon}` на `BitmapImage` с `UriSource="pack://application:,,,/app.ico"` и `DecodePixelWidth="18"` (размер ~18×18), чтобы `.ico` отображался аккуратно, без слишком крупного кадра. Ресурс `AppIcon` из `App.xaml` не удалялся — он продолжает использоваться в других местах (трей, окно «О программе», Linux/Avalonia).
- **Версия поднята до `0.3.5.63`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.62] — 2026-08-31

Колонка «Действия» больше не может быть сжата до размера, при котором кнопки-иконки недоступны (добавлена минимальная ширина и ограничение при перетаскивании) (Windows/WPF).

### Изменено

- **Колонка «Действия» больше не сжимается ниже минимальной ширины** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) всем трём `ColumnDefinition`, привязанным к `ActionsColumnWidth` (в заголовке — `ActionsColumn`, в шаблоне группы и в шаблоне строки базы), задан `MinWidth="120"`, чтобы три кнопки-иконки («Запуск», «Конфигуратор», «Очистить кеш») вместе с отступами оставались полностью доступными. В [`MainWindow.Columns.cs`](Configuration Management/Views/MainWindow.Columns.cs) в `OnColumnResize_MouseMove` для колонки «Действия» добавлен отдельный нижний предел (`ActionsColumnMinWidth = 120`), совпадающий с `MinWidth` из XAML; общий кламп `40` для остальных колонок сохранён. `UpdateTreeMinWidth` уже учитывает `d.MinWidth` для не-absolute колонок, а `SaveColumnWidths`/`UpdateColumnWidths` сохраняют итоговую ширину через `ActualWidth`/`newWidth` (уже ограниченную минимумом), поэтому синхронизация заголовка с данными и сохранение ширины корректны.
- **Версия поднята до `0.3.5.62`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.61] — 2026-08-31

Убран лишний пустой отступ слева в заголовке колонки «Название» (Windows/WPF).

### Изменено

- **Убран лишний пустой отступ слева в заголовке колонки «Название»** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) уменьшены ведущие колонки `ColumnDefinitions` 0–3 заголовка (`HeaderGrid`, внутри `DbHeaderScroll`), которые резервировали место под уже перенесённые в панель команд кнопки управления группами, переключатель тегов и индикатор закрепления: ширина первой колонки (`ShowExpandCollapseButtons`) уменьшена с `144` до `24` px, а у колонки резерва под переключатель тегов убран `MinWidth="30"`. Тот же набор изменений синхронно применён к ведущим колонкам шаблонов строк — заголовка группы (`GroupRowGrid`) и строки базы (`InfobaseRowGrid`), чтобы колонки данных по-прежнему точно совпадали по горизонтали с заголовками. Иерархический отступ вложенности групп и место под звёздочку/статус в строках сохраняются: они обеспечиваются сдвигом названия по уровню (`LevelToThickness`/`GroupOffset`), а не шириной этих колонок. Компенсатор `HeaderOffsetColumn` (заполняется в `AlignHeaderToData`) и синхронизация ширины заголовка с данными (`SyncHeaderWidthWithList`/`DbHeaderScroll`) не изменялись — заголовок «Название» теперь начинается существенно левее, ближе к данным строк верхнего уровня.
- **Версия поднята до `0.3.5.61`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.60] — 2026-08-31

Заголовок колонки «Название» выровнен по левому краю (Windows/WPF).

### Изменено

- **Заголовок колонки «Название» выровнен по левому краю** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) у StackPanel-заголовка колонки «Название» (`Grid.Column="4"`, внутри `DbHeaderScroll`; содержит `materialDesign:PackIcon Kind="FormatTitle"` и `TextBlock` с текстом `Column.Name`) значение `HorizontalAlignment` изменено с `Stretch` на `Left`. При `Stretch` содержимое StackPanel (иконка + текст) могло сдвигаться к центру/вправо относительно данных строк при нестандартной ширине колонки; `Left` прижимает контент к левому краю колонки, чтобы заголовок совпадал с началом данных в строках. Разделители колонок, `ColumnDefinitions` и остальные заголовки (колонки 5, 6, 8–11 остались на `Stretch`, колонка 7 «Действия» — на `Left`) не изменялись.
- **Версия поднята до `0.3.5.60`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.59] — 2026-08-31

Убран индикатор «закрепить» из области заголовка колонок таблицы списка (Windows/WPF).

### Изменено

- **Убран индикатор «закрепить» из области заголовка колонок таблицы** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) из левого `StackPanel` заголовка (`Grid.Column="0" Grid.ColumnSpan="4"`, внутри `DbHeaderScroll`) удалён значок-пометка закрепления базы `materialDesign:PackIcon Kind="Pin"`. Сам `StackPanel` к этому моменту уже не содержал других элементов (кнопки управления группами и переключатель тегов были перенесены в панель команд), поэтому он полностью удалён вместе со ставшими неактуальными комментариями. Ведущие колонки заголовка (`ColumnDefinitions` 0–3: кнопки групп, компенсатор отступа дерева, колонки избранного и закрепления) оставлены без изменений — они нужны для выравнивания заголовков с данными: колонка «Название» по-прежнему начинается на `Grid.Column="4"` там же, где строки данных, а горизонтальная синхронная прокрутка заголовка (`DbHeaderScroll`) не затрагивается. Свойство `ShowPinnedButton` сохранено: оно по-прежнему используется в `ColumnDefinitions` заголовка и строк данных для выравнивания колонок.
- **Версия поднята до `0.3.5.59`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.58] — 2026-08-31

Кнопки запуска «1С Предприятие»/«Конфигуратор» правой панели выровнены по уровню панели команд над списком баз (Windows/WPF).

### Изменено

- **Кнопки запуска «1С Предприятие»/«Конфигуратор» правой панели выровнены по уровню панели команд над списком баз** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) верхняя составляющая `Padding` у `ScrollViewer` правой панели (`x:Name="RightPanelBorder"`) увеличена, чтобы верхний край блока действий запуска совпадал с верхним краем панели команд `CommandPanelBorder` левой колонки, а не уходил в самый верх основной области: обычный режим `"12,44"` → `"12,56"`, компактный (`ShowRightPanelDetails=False`) `"2,40,4,6"` → `"2,56,4,6"`. Значение 56 примерно равно высоте строки `TopBarBorder` (поиск/вкладки/переключатели групп) вместе с верхним отступом левой колонки; поскольку окно теперь имеет отдельную строку-заголовок, кнопки окна больше не накладываются на основную область, и прежний увеличенный зазор (44/40) был избыточен. Расположение кнопок окна в строке-заголовке не изменялось.
- **Версия поднята до `0.3.5.58`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.57] — 2026-08-31

Восстановлена высота заголовков колонок таблицы списка (Windows/WPF).

### Изменено

- **Восстановлена высота заголовков колонок таблицы списка** (Windows/WPF). После переноса кнопок управления группами (развернуть/свернуть все, сортировка, теги) из левой части заголовка таблицы в панель команд строка заголовка стала ниже — равной высоте только текста заголовков колонок. В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) у `Border`-контейнера строки заголовка (`DbHeaderScroll`, область списка) задан `MinHeight="36"`, чтобы вернуть прежнюю комфортную высоту (~34–38 px). В `HeaderGrid` добавлена явная star-строка (`RowDefinition Height="*"`), которая растягивается на высоту контейнера, благодаря чему StackPanel-заголовки всех колонок (у них уже стоит `VerticalAlignment="Center"`) остаются отцентрированы по вертикали строки. Горизонтальное выравнивание, разделители колонок и синхронная прокрутка заголовка с данными (`DbHeaderScroll`) не изменялись; данные списка не смещаются.
- **Версия поднята до `0.3.5.57`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.56] — 2026-08-31

Иконка и заголовок программы вынесены в отдельную верхнюю строку окна (заголовок-титул); кнопки управления окном встроены в неё (Windows/WPF).

### Изменено

- **Иконка и заголовок программы вынесены в отдельную верхнюю строку окна** (Windows/WPF). В корневой `Grid` окна [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) добавлена новая первая строка (`RowDefinition Height="Auto"`) — полоса-«заголовок-титул» приложения по образцу заголовка обычного окна. Слева размещены иконка (`Image` с `{StaticResource AppIcon}`, 18×18, `Margin` справа) и текст заголовка (`{loc:Loc App.Title}`, `FontWeight="SemiBold"`, размер 13, цвет `{DynamicResource TextPrimaryBrush}`), выровненные по вертикали по центру; справа — кнопки управления окном, вертикально отцентрированные в полосе. Фон полосы — `{DynamicResource CardBackgroundBrush}`, нижняя граница — `{DynamicResource BorderBrushColor}` (`BorderThickness="0,0,0,1"`), `Height="Auto"`, горизонтальный `Padding` 12, вертикальный 6. Перетаскивание окна за полосу реализовано через существующий обработчик `OnTopBar_MouseLeftButtonDown` (`MouseLeftButtonDown`), который вызывает `DragMove` и двойной клик для разворота.
- **Кнопки управления окном встроены в строку-заголовок, старый оверлей удалён** (Windows/WPF). Кнопки «свернуть/развернуть/закрыть» (`MinimizeButton`, `MaximizeButton`, `CloseButton`) перенесены из прежнего оверлейного `StackPanel` (прямой дочерний элемент корневого `Grid`, правый верхний угол поверх контента, `Panel.ZIndex=10`) в правую часть новой строки-заголовка. Сохранены `x:Name`, обработчики `OnMinimizeButton_Click`/`OnMaximizeButton_Click`/`OnCloseButton_Click`, стили `WindowControlButton`/`WindowControlCloseButton`, `ToolTip` (`Window.Minimize`/`Window.Maximize`/`Common.Close`) и глифы `MaximizeGlyphPath`/`RestoreGlyphPath`. Старый оверлейный `StackPanel` полностью удалён — дублирования кнопок нет. Прежняя основная область окна смещена на `Grid.Row="1"`, строка состояния — на `Grid.Row="2"`, поэтому левая колонка и правая панель `RightPanelBorder` не залезают под новую полосу.
- **Версия поднята до `0.3.5.56`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.55] — 2026-08-31

Исправлено наложение правых команд запуска («1С Предприятие»/«Конфигуратор») на кнопки управления окном — контент правой панели опущен ниже (Windows/WPF).

### Исправлено

- **Наложение правых команд запуска на кнопки управления окном устранено** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) верхняя составляющая `Padding` у `ScrollViewer` правой панели (`x:Name="RightPanelBorder"`) увеличена так, чтобы верхний ряд кнопок правой панели (split-кнопки «1С Предприятие» `LaunchEnterpriseCommand` / «Конфигуратор» `LaunchConfiguratorCommand` и детали базы) гарантированно начинались ниже полосы кнопок окна («свернуть/развернуть/закрыть»): обычный режим `"12,10"` → `"12,44"`, компактный (`ShowRightPanelDetails=False`) `"2,6,4,6"` → `"2,40,4,6"`. Кнопки окна не перемещались и остались в правом верхнем углу с высоким `Panel.ZIndex`.
- **Версия поднята до `0.3.5.55`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.54] — 2026-08-31

Кнопки «Избранное» и «Закрепление» возвращены в строку информационной базы (колонку названия) (Windows/WPF).

### Изменено

- **Кнопки «Избранное» и «Закрепление» возвращены в колонку названия строки информационной базы** (Windows/WPF). Из панели команд `StackPanel x:Name="CommandPanelStack"` ([`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml)) удалены глобальные кнопки «Избранное» (`ToggleFavoriteCommand`, `PackIcon Star`) и «Закрепление» (`TogglePinCommand`, `PackIcon Pin`), действовавшие на выбранную базу; разделители панели сохранены и она осталась аккуратной. В шаблон строки (`DataTemplate DataType=Infobase`) в `StackPanel` области названия перед иконкой статуса возвращены две per-row кнопки, действующие на конкретную строку: «Избранное» (звезда, `Command="{Binding DataContext.ToggleFavoriteForCommand, RelativeSource={RelativeSource AncestorType=Window}}"`, `CommandParameter="{Binding}"`, `PackIcon Star` 14×14, прозрачная, `Cursor="Hand"`, цвет по умолчанию `TextSecondaryBrush`, при `IsFavorite=True` — `FavoriteBrush`) с круглым бейджем номера горячей клавиши (`Border` на `FavoriteHotkeyDisplay`, цвет `FavoriteBrush`, видим при `IsFavorite=True` и `FavoriteHotkeyNumber != 0`) и «Закрепление» (пин, `TogglePinForCommand`, `PackIcon Pin` 14×14, цвет по умолчанию `TextSecondaryBrush`, при `IsPinned=True` — `AccentBrush`). Сохранены локализованные `ToolTip` (`Main.ToggleFavoriteTooltip`, `Main.TogglePinTooltip`); структура `Grid.ColumnDefinitions` строки не менялась.
- **Версия поднята до `0.3.5.54`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.53] — 2026-08-31

Команды управления группами (развернуть/свернуть все, сортировка, отображение тегов) перенесены в панель команд над заголовками колонок (Windows/WPF).

### Изменено

- **Команды управления группами перенесены из заголовка таблицы в панель команд** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) в начало `StackPanel x:Name="CommandPanelStack"` панели `CommandPanelBorder` (`Grid.Row="2"`) добавлена отдельная группа «Управление группами» из пяти элементов: «Развернуть все группы» (`ExpandAllGroupsCommand`, `PackIcon ExpandAll`), «Свернуть все группы» (`CollapseAllGroupsCommand`, `PackIcon CollapseAll`), «Сортировка по возрастанию» (`SortGroupsAscendingCommand`, `PackIcon SortAscending`), «Сортировка по убыванию» (`SortGroupsDescendingCommand`, `PackIcon SortDescending`) и переключатель «Отображение тегов» (`ToggleButton`, `IsChecked="{Binding ShowTags, Mode=TwoWay}"`, `Path IconTag`). Сохранены команды, стили (`IconButton`/`ToolbarToggleButton`), локализованные `ToolTip`, `Visibility`-привязки (`ShowExpandCollapseButtons` для кнопок групп), размеры иконок приведены к 18×18 под остальные кнопки панели. После группы добавлен вертикальный разделитель (`Border Width=1`, `Background={DynamicResource BorderBrushColor}`, `Opacity=0.55`, `Margin="4,5"`), отделяющий её от кнопок «Избранное»/«Закрепление». Из `StackPanel Grid.Column="0" Grid.ColumnSpan="4"` заголовка (`HeaderGrid` внутри `DbHeaderScroll`) эти пять элементов удалены; там оставлен пустой контейнер со значком-индикатором «закрепить» (`PackIcon Pin`, `ShowPinnedButton`). `ColumnDefinitions` заголовка не менялись — выравнивание заголовков и строк, а также прокрутка `DbHeaderScroll` сохранены.
- **Версия поднята до `0.3.5.53`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.52] — 2026-08-31

Все кнопки-команды (добавить, правка, удалить, очистить кеш, синхронизация, настройки и др.) перенесены в отдельную панель команд над заголовками колонок (Windows/WPF).

### Изменено

- **Все кнопки-команды перенесены из верхней панели поиска/вкладок в отдельную панель команд** (Windows/WPF). Из `TopBarBorder` (`Grid.Row="0"` левой колонки) [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) удалён блок «Действия» (`Border Grid.Column="3"`) вместе с кнопками «Добавить базу» (`AddInfobaseCommand`, `PackIcon Plus`), «Правка» (`EditInfobaseCommand`, `PackIcon Pencil`), «Удаление» (`DeleteInfobaseCommand`, `PackIcon Delete`), «Очистить кеш» (`ClearCacheCommand`, `PackIcon Broom`), индикатором выгрузки (`ExportIndicatorButton` с анимацией `ExportIndicatorBounce`, `PackIcon Upload`), «Синхронизация» (`SynchronizeWithIbasesCommand`, `PackIcon Sync`), «Проверка доступности» (`CheckAvailabilityCommand`, `Path IconSonar`), «Тема» (`ThemeToggleButton`/`ThemeToggleIcon`, `Click="OnToggleTheme_Click"`), «Компактный режим» (`CompactModeButton`, `Click="OnCompactMode_Toggled"`), «Настройки» (`OpenSettingsCommand`, `PackIcon Cog`) и справкой (`controls:HelpLink`). Также удалена ставшая неиспользуемой четвёртая колонка внутреннего `Grid` панели поиска/вкладок. В `TopBarBorder` остаются только переключатели групп/тегов (колонка 0), поиск (колонка 1) и вкладки (колонка 2); перетаскивание окна через `OnTopBar_MouseLeftButtonDown` работает на оставшейся области. Все перечисленные элементы перенесены в `StackPanel x:Name="CommandPanelStack"` панели `CommandPanelBorder` (`Grid.Row="2"`) после кнопок «Избранное» и «Закрепление» с сохранением порядка, `x:Name`, команд, `Click`-обработчиков, стилей, `ToolTip`, `Visibility`-привязок и обоих вертикальных разделителей. Группировка сохранена: «Правка» (добавить/правка/удалить), разделитель, «Управление списком» (очистить кеш/выгрузка/синхронизация/проверка), разделитель, «Настройки» (тема/компактный/настройки/справка).
- **Версия поднята до `0.3.5.52`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.51] — 2026-08-31

Кнопки «Избранное» и «Закрепление» перенесены из колонки названия строк в новую панель команд над заголовками колонок (Windows/WPF).

### Изменено

- **Кнопки «Избранное» и «Закрепление» перенесены из колонки названия строк в новую панель команд** (Windows/WPF). В шаблоне строки информационной базы (`DataTemplate DataType=Infobase`) из области названия [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) удалены кнопка «Избранное» (звезда, вместе с бейджем `FavoriteHotkeyDisplay`) и кнопка «Закрепление» (пин). В колонке названия теперь остаются только иконка статуса (`Path` по ключу `StatusIconKey`) и текст названия (`TextBlock Name`); структура `Grid.ColumnDefinitions` шаблона строки не менялась. Вместо этого в контейнер `StackPanel x:Name="CommandPanelStack"` панели команд `CommandPanelBorder` (над заголовками колонок) добавлены две кнопки, действующие на выбранную базу: «Избранное» (`Command="{Binding ToggleFavoriteCommand}"`) и «Закрепление» (`Command="{Binding TogglePinCommand}"`), обе со стилем `{DynamicResource IconButton}` и единым `Margin="0,0,2,0"`. Иконка звезды (`PackIcon Kind="Star"`, 18×18) окрашена по умолчанию в `TextSecondaryBrush`, а при `SelectedInfobase.IsFavorite=True` — в `FavoriteBrush`; иконка пина (`PackIcon Kind="Pin"`, 18×18) по умолчанию `TextSecondaryBrush`, при `SelectedInfobase.IsPinned=True` — `AccentBrush` (через `DataTrigger`). Кнопки автоматически недоступны при отсутствии выбранной базы благодаря `CanExecute` (`SelectedInfobase != null`) команд `ToggleFavoriteCommand`/`TogglePinCommand`. Сохранены локализованные `ToolTip`: `Main.ToggleFavoriteTooltip` и `Main.TogglePinTooltip`.
- **Версия поднята до `0.3.5.51`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.50] — 2026-08-31

Добавлена отдельная панель команд над заголовками колонок списка (Windows/WPF).

### Добавлено

- **Добавлен каркас отдельной панели команд над заголовками колонок списка** (Windows/WPF). Во внутреннем `Grid` левой колонки [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) перед строкой списка добавлена новая строка `RowDefinition Height="Auto"` (Grid.Row=`2`), а область списка смещена на `Grid.Row="3"`. В новой строке размещён контейнер `Border x:Name="CommandPanelBorder"` (занимает всю ширину левой колонки, `Margin="4,0,4,0"`, `Padding="8,6"`, фон `CardBackgroundBrush`, нижняя граница `BorderBrushColor` `BorderThickness="0,0,0,1"`) с пустым горизонтальным `StackPanel x:Name="CommandPanelStack"` (`VerticalAlignment="Center"`). Панель расположена непосредственно над заголовками колонок (между панелью быстрого отбора по тегам и `HeaderGrid`/`DbHeaderScroll`). Строка списка осталась единственной со значением `*`, поэтому прокрутка `TreeView` не ломается. Существующие кнопки, привязки и обработчики не переносились — в каркас позже будут добавлены команды (звезда/пин из колонки названия, добавить/правка/удалить и др.).
- **Версия поднята до `0.3.5.50`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.49] — 2026-08-31

Кнопки управления окном «свернуть/развернуть/закрыть» перенесены в правый верхний угол окна (Windows/WPF).

### Изменено

- **Кнопки управления окном перенесены в правый верхний угол** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) из панели команд (`TopBarBorder`) удалён блок кнопок «свернуть/развернуть/закрыть» (элементы `MinimizeButton`, `MaximizeButton`, `CloseButton`), а вместе с ним — пятая колонка внутреннего `Grid` панели команд (остались колонки 0–3: группы/теги, поиск, вкладки, действия). Эти же три кнопки размещены в правом верхнем углу окна поверх содержимого (над правой панелью, как у обычного заголовка): горизонтальный `StackPanel` с выравниванием `HorizontalAlignment="Right"` / `VerticalAlignment="Top"`, добавленный последним дочерним элементом корневого `Grid` (`Grid.Row="0"`, `Panel.ZIndex="10"`). Сохранены `x:Name`, обработчики `OnMinimizeButton_Click`/`OnMaximizeButton_Click`/`OnCloseButton_Click`, стили `WindowControlButton`/`WindowControlCloseButton`, локализованные `ToolTip` (`Window.Minimize`/`Window.Maximize`/`Common.Close`) и глифы `MaximizeGlyphPath`/`RestoreGlyphPath`, которые по-прежнему переключаются через `OnWindowStateChanged`. Кнопки больше не входят в область перетаскивания окна за панель (`OnTopBar_MouseLeftButtonDown`), так как лежат вне `TopBarBorder`.
- **Версия поднята до `0.3.5.49`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.48] — 2026-08-31

На панели команд добавлены разделители между группами «Правка», «Управление списком» и «Настройки» (Windows/WPF).

### Изменено

- **Добавлены вертикальные разделители на панель команд** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) в блок «Действия» (`Border Grid.Column="3"`) горизонтального `StackPanel` панели команд добавлены два тонких вертикальных разделителя (элементы `Border Width="1"` на основе `BorderBrushColor`). Первый размещён сразу после кнопки «Удаление» и отделяет группу «Правка» (Добавить, Правка, Удаление) от группы «Управление списком» (Очистить кеш, индикатор выгрузки, Синхронизация, Проверка доступности); второй — сразу после кнопки «Проверка доступности» и отделяет «Управление списком» от группы «Настройки» (Тема, Компактный режим, Настройки, Справка).
- **Версия поднята до `0.3.5.48`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.47] — 2026-08-31

Кнопки «Правка» и «Удаление» перенесены из колонки «Действия» строк списка на панель команд, в группу с добавлением (Windows/WPF). В колонке «Действия» строки информационной базы теперь остаются только «Запуск», «Конфигуратор» и «Очистить кеш», а команды правки и удаления выполняются по выбранной базе через `ResolveActionTarget` и автоматически становятся недоступными, когда ничего не выбрано.

### Изменено

- **Кнопки «Правка» и «Удаление» перенесены из колонки «Действия» строк на панель команд** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) из шаблона строки информационной базы (`StackPanel Grid.Column="7"`) удалены кнопки «Правка» (`Kind="Pencil"`) и «Удаление» (`Kind="Delete"`); в колонке «Действия» остались кнопки «Запуск» (`Play`), «Конфигуратор» (`Wrench`) и «Очистить кеш» (`Broom`). В блок «Действия» панели команд сразу после кнопки «Добавить базу» добавлены кнопки «Правка» (`EditInfobaseCommand`, `Kind="Pencil"`, цвет `TextSecondaryBrush`) и «Удаление» (`DeleteInfobaseCommand`, `Kind="Delete"`, красный `#DC2626`) без `CommandParameter` — команды работают по выбранной базе через `ResolveActionTarget` и автоматически отключаются, когда ничего не выбрано (обеспечивает `CanExecute` у `RelayCommand`).
- **Версия поднята до `0.3.5.47`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.46] — 2026-08-31

Панель команд перенесена внутрь левой колонки над заголовком списка (Windows/WPF): верхняя панель команд больше не занимает отдельную строку во всю ширину окна, а размещена непосредственно над заголовком таблицы списка внутри левой колонки. Панель занимает всю ширину левой колонки, не накладываясь на правую панель сведений; перетаскивание окна за панель (DragMove), все кнопки и переключатели сохранили работу. Изменение внесено только для Windows/WPF-разметки `MainWindow.xaml`.

### Изменено

- **Панель команд перенесена внутрь левой колонки** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) верхняя панель команд перенесена из отдельной строки внешнего грида в левую панель, размещена над заголовком таблицы списка и занимает всю ширину левой колонки, не пересекая правую панель `RightPanelBorder`. Сохранены все элементы и обработчики: переключатели групп/тегов, поиск, вкладки «Все/Избранное/Недавние», кнопки действий (добавить, очистить кеш, индикатор выгрузки, синхронизация, проверка доступности, тема, компактный режим, настройки, справка), кнопки управления окном (свернуть/развернуть/закрыть) и обработчик `OnTopBar_MouseLeftButtonDown` для перетаскивания окна.
- **Версия поднята до `0.3.5.46`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.45] — 2026-08-31

Устранены «призрачные» (неактивные) системные кнопки заголовка «свернуть / развернуть / закрыть», которые DWM рисовал поверх фона диалоговых окон (в том числе окна настроек) из-за расширенной стеклянной рамки (`GlassFrameThickness=-1`). Теперь флаги стиля окна `WS_SYSMENU` / `WS_MINIMIZEBOX` / `WS_MAXIMIZEBOX` снимаются на уровне Win32, поэтому на фоне остаётся только собственная кнопка «закрыть» в заголовке. Изменение внесено только для Windows/WPF.

### Исправлено

- **Системные кнопки не рисуются «призрачными»** (Windows/WPF). В [`WindowChromeHelper.cs`](Configuration Management/Views/WindowChromeHelper.cs) добавлен метод `RemoveSystemCaptionButtons`: через `GetWindowLong`/`SetWindowLong` снимаются флаги `WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX`, а `SetWindowPos(SWP_FRAMECHANGED)` принудительно перерисовывает рамку. Вызывается в `Apply` для каждого диалога после установки `WindowChrome`; `WindowChrome.UseAeroCaptionButtons=false` уже скрывал кнопки, но теперь их исчезновение гарантировано и на уровне Win32.
- **Версия поднята до `0.3.5.45`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.44] — 2026-08-31

В заголовке диалоговых окон (в том числе окна настроек) оставлена **только кнопка «закрыть»**: кнопки «свернуть» и «развернуть/восстановить», добавленные в `0.3.5.42`, убраны, а код хелпера упрощён до единственной кнопки закрытия. Это стандартная схема диалогов: полный набор кнопок «свернуть / развернуть / закрыть» остаётся только у главного окна. Изменение внесено только для Windows/WPF.

### Изменено

- **Только кнопка «закрыть» в заголовке диалогов** (Windows/WPF). В [`WindowChromeHelper.BuildTitleBar`](Configuration Management/Views/WindowChromeHelper.cs) в полосу заголовка добавляется только кнопка закрытия (стиль `WindowControlCloseButton`, красное выделение); `BuildButton` и значок креста построены напрямую, а неиспользуемые ветки «свернуть»/«развернуть» удалены.
- **Версия поднята до `0.3.5.44`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.43] — 2026-08-31

Исправлена первопричина отсутствия кнопок управления окном в диалоговых окнах (в том числе в окне настроек): [`WindowChromeHelper.BuildChrome`](Configuration Management/Views/WindowChromeHelper.cs) переносил текущее содержимое окна в новую сетку заголовка, **не отсоединив** его, из-за чего WPF бросал `InvalidOperationException` («элемент уже является логическим дочерним для другого элемента»). Глобальный обработчик на `Loaded` молча перехватывал это исключение — системные кнопки уже были скрыты (`WindowChrome`), а собственный заголовок с кнопками так и не добавлялся, поэтому окно оставалось без кнопки закрытия. Теперь содержимое сначала отсоединяется (`window.Content = null`), затем оборачивается в сетку с полосой заголовка — собственные кнопки «свернуть / развернуть/восстановить / закрыть» корректно появляются у всех диалогов. Изменение внесено только для Windows/WPF.

### Исправлено

- **Собственные кнопки управления окном у диалогов** (Windows/WPF). В [`WindowChromeHelper.Apply`](Configuration Management/Views/WindowChromeHelper.cs) перед вызовом `BuildChrome` текущий `Content` окна отсоединяется (`window.Content = null`), чтобы добавить его в новую сетку с полосой заголовка без `InvalidOperationException`. Исправление восстанавливает появление кнопок «свернуть», «развернуть/восстановить» и «закрыть» у всех диалоговых окон, включая окно настроек (оформление применяется глобальным обработчиком на `Loaded` после создания HWND, поэтому системный фон acrylic/mica и скругление углов тоже работают).
- **Версия поднята до `0.3.5.43`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.42] — 2026-08-30

В собственном заголовке диалоговых окон (который оформляется централизованно через [`WindowChromeHelper.cs`](Configuration Management/Views/WindowChromeHelper.cs)) появилась недостающая кнопка **«развернуть/восстановить»** — теперь у изменяемых окон (например, у окна настроек) в полосе заголовка есть полный набор кнопок управления, как у главного окна: **«свернуть», «развернуть/восстановить», «закрыть»** (с красным выделением). Изменение внесено только для Windows/WPF.

### Добавлено

- **Кнопка «развернуть/восстановить» в заголовке диалогов** (Windows/WPF). В [`WindowChromeHelper.cs`](Configuration Management/Views/WindowChromeHelper.cs) добавлен тип кнопки `MaximizeRestore`: значок переключается между контуром одного прямоугольника («развернуть») и двумя наложенными прямоугольниками («восстановить») по состоянию окна (`StateChanged`), клик разворачивает/восстанавливает окно. Кнопка добавляется только у изменяемых окон (`ResizeMode=CanResize` / `CanResizeWithGrip`), поэтому окно настроек получило полный набор кнопок «свернуть / развернуть / закрыть». Подсказки используют ключи `Window.Maximize` / новый `Window.Restore` ([`ru.json`](Configuration Management/Localization/Languages/ru.json) — «Восстановить», [`en.json`](Configuration Management/Localization/Languages/en.json) — «Restore»).

### Изменено

- **Версия поднята до `0.3.5.42`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.41] — 2026-08-30

Управление учётными записями (профилями) в Windows-версии перенесено из отдельного окна во вкладку **«Учётные записи»** окна настроек: весь редактор профилей теперь живёт прямо в настройках, а прежняя кнопка «Управление учётными записями…» и отдельное окно в Windows-версии упразднены. Изменение внесено только для Windows/WPF; Linux/Avalonia-версия не затрагивалась.

### Добавлено

- **Вкладка «Учётные записи» в окне настроек** (Windows/WPF). В окне настроек [`SettingsWindow`](Configuration Management/Views/SettingsWindow.xaml) появилась отдельная вкладка, встраивающая панель управления профилями — новый `UserControl` [`ProfilesPanel.xaml`](Configuration Management/Views/ProfilesPanel.xaml), размещённый рядом с прежним окном. Панель полностью повторяет интерфейс отдельного окна: выпадающее меню активной учётной записи, список «список + редактор» (имя, пароль, флажок «Защитить паролем»), сообщение об ошибке и кнопки «Создать / Сохранить / Удалить / Сделать активной». Бизнес-логика (CRUD, валидация, подтверждение удаления) переиспользует прежнюю [`ProfilesViewModel`](Configuration Management/ViewModels/ProfilesViewModel.cs); пароль по-прежнему передаётся из `PasswordBox` в code-behind ([`ProfilesPanel.xaml.cs`](Configuration Management/Views/ProfilesPanel.xaml.cs)). Вкладка строится в [`SettingsWindow.Accounts.cs`](Configuration Management/Views/SettingsWindow.Accounts.cs) и вставляется перед вкладкой «О программе», как и «Резервное копирование». Заголовок вкладки локализован ключом `Settings.TabAccounts` в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json).

### Изменено

- **Отказано от отдельного окна управления учётными записями** (Windows/WPF). Кнопка «Управление учётными записями…» во вкладке «Настройки» удалена, обработчик `OnManageProfiles_Click` убран; отдельное окно [`ProfilesWindow`](Configuration Management/Views/ProfilesWindow.xaml) в Windows-версии больше не открывается — управление доступно только во вкладке «Учётные записи».
- **Версия поднята до `0.3.5.41`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.40] — 2026-08-30

Все диалоговые окна приложения переведены на новый «стеклянный» стиль главного окна на обеих платформах: собственные кнопки управления окном (свернуть/закрыть, у закрытия красное выделение), полупрозрачный стеклянный фон и скруглённые углы (при максимизации обнуляются). Логика диалогов (DialogResult, кнопки ОК/Отмена, ShowDialogSync) не менялась.

### Изменено

- **Все диалоговые окна** (Windows/WPF). Добавлен общий класс [`WindowChromeHelper.cs`](Configuration Management/Views/WindowChromeHelper.cs): он применяет `WindowChrome` без системных кнопок, полупрозрачную подложку цвета темы (~0xE8), системный acrylic/mica (Windows 11, при недоступности — blur-behind) и скруглённые углы DWM, а также добавляет полосу заголовка с собственными кнопками «свернуть»/«закрыть» (у закрытия — красная подложка `#E81123`, при нажатии `#C50F1F`). Стили `WindowControlButton` / `WindowControlCloseButton` вынесены из [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) в общие ресурсы приложения [`App.xaml`](Configuration Management/App.xaml); глобальное оформление регистрируется в [`App.xaml.cs`](Configuration Management/App.xaml.cs). Оформляются все диалоги: `AddEditWindow`, `CacheCleanWindow`, `ColorPickerWindow`, `ConnectionSettingsWindow`, `ConnectionStringInputWindow`, `CreateInfobaseWindow`, `DeleteInfobaseWindow`, `GroupEditWindow`, `GroupPickerWindow`, `GroupSettingsWindow`, `LaunchParametersWindow`, `LinkInputWindow`, `LoginWindow`, `NameInputWindow`, `PlatformVersionPickerWindow`, `ProfilesWindow`, `SettingsWindow`, `TagInputWindow`, а также окно сообщений `MaterialMessageWindow`.
- **Все диалоговые окна** (Linux/Avalonia). Базовый класс [`ModalWindowBase.cs`](Configuration Management/Views/ModalWindowBase.cs) задаёт `SystemDecorations=None`, `ExtendClientAreaToDecorationsHint=true`, `TransparencyLevelHint={AcrylicBlur, Blur, Transparent}`, прозрачный фон и автоматически оборачивает содержимое каждого диалога в «стеклянный» контейнер (скруглённые углы + полупрозрачная подложка цвета темы через `ThemeBrushes.WithAlpha`) с полосой заголовка, перетаскиванием (`BeginMoveDrag`) и невидимыми зонами ресайза для изменяемых окон. Собственные кнопки «свернуть»/«закрыть» с красным закрытием добавляются единообразно без правки восемнадцати окон. XAML-диалоги `NameInputWindow`, `LinkInputWindow`, `DeleteInfobaseWindow`, `TagInputWindow` переведены на `SystemDecorations="None"`.
- **Версия поднята до `0.3.5.40`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.39] — 2026-08-30

Кнопка «закрыть» в собственных кнопках управления окном получила классическое красное выделение при наведении/нажатии на обеих платформах: красная подложка (алый `#E81123` при наведении, темнее `#C50F1F` при нажатии) и белый значок креста поверх неё. У обычных кнопок «свернуть»/«развернуть» поведение и цвета темы не изменились.

### Добавлено

- **Красное выделение кнопки «закрыть»** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) добавлен отдельный стиль `WindowControlCloseButton` (`BasedOn="WindowControlButton"`): при наведении фон становится алым `#E81123`, при нажатии — `#C50F1F`, значок перекрашивается в белый. Стиль применён к `CloseButton`, кнопки «свернуть»/«развернуть» продолжают использовать прежний `WindowControlButton`.
- **Красное выделение кнопки «закрыть»** (Linux/Avalonia). В [`MainWindow.Avalonia.cs`](Configuration Management/Views/MainWindow.Avalonia.cs) класс `WindowControlButton` для типа `WindowControlKind.Close` при наведении/нажатии красит фон в алый (`CloseHoverBrush` `#E81123` / `ClosePressedBrush` `#C50F1F`) и перекрашивает значок в белый; при выходе курсора значок возвращается к цвету темы (`TextPrimaryColorBrush`) через `ApplyState`. Кнопки «свернуть»/«развернуть» используют прежние hover/pressed-кисти темы.

### Изменено

- **Версия поднята до `0.3.5.39`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.38] — 2026-08-30

В WPF-версии (Windows) главное окно оформлено в стиле «прозрачного стекла»: расширенная системная стеклянная рамка DWM + полупрозрачная подложка из цвета темы (~0xE8) вместо сплошного фона рабочей области, системный acrylic/mica backdrop (Windows 11, при недоступности — классический blur-behind) и скруглённые углы окна, которые при максимизации обнуляются. Собственные кнопки управления окном и верхняя панель из `0.3.5.37` остались нетронутыми и теперь выглядят согласованно с полупрозрачным фоном. Если системный акрил недоступен (старый Windows / аппаратное ограничение), окно остаётся рабочим и красивым за счёт полупрозрачного фона без размытия. Изменение внесено только для Windows/WPF.

### Добавлено

- **Стеклянный/полупрозрачный фон окна** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) `WindowChrome.GlassFrameThickness` расширен до `-1`, чтобы системная стеклянная рамка DWM покрывала всю клиентскую область; в [`MainWindow.xaml.cs`](Configuration Management/Views/MainWindow.xaml.cs) добавлен P/Invoke-помощник (`DwmSetWindowAttribute`/`DwmEnableBlurBehindWindow`): на Windows 11 включается системный acrylic backdrop (`DWMWA_SYSTEMBACKDROP_TYPE`, значение `DWMSBT_TRANSIENTWINDOW`), при недоступности — mica (`DWMSBT_MAINWINDOW`), на старых Windows — классический blur-behind. Если эффект недоступен, применяется откат на полупрозрачный фон без размытия, окно остаётся рабочим.
- **Полупрозрачная подложка из цвета темы** (Windows/WPF). Вместо сплошного фона рабочей области фон окна задаётся пересчитанным из текущего `ContentBackgroundBrush` с альфой `0xE8` (~91% непрозрачности) — адаптивно для обеих тем (светлая/тёмная) и всех цветовых схем. Подложка пересчитывается при смене темы/схемы: слушатель коллекции `Application.Current.Resources.MergedDictionaries` (тема меняется через `ThemeManager.ApplyScheme`) вызывает `ApplyGlassBackground()`.
- **Скруглённые углы окна в стиле glass** (Windows/WPF). На Windows 11 углы окна скругляются на уровне DWM через `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`; при развёрнутом состоянии углы обнуляются (`DWMWCP_DONOTROUND`), а толщина стеклянной рамки возвращается к `0`, чтобы окно корректно прилегало к краям экрана и панели задач.

### Изменено

- **Версия поднята до `0.3.5.38`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.37] — 2026-08-30

В WPF-версии (Windows) главное окно отказалось от системных кнопок управления окном (закрыть/свернуть/развернуть) и системной рамки в пользу собственных кнопок, нарисованных средствами WPF. Окно оформлено через `WindowChrome` (без стеклянной рамки и системных кнопок), перетаскивание за верхнюю панель реализовано вручную через `DragMove`, а изменение размера — невидимой рамкой ресайза `WindowChrome`. Изменение внесено только для Windows/WPF.

### Добавлено

- **Собственные кнопки управления окном** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) добавлен стиль `WindowControlButton` и правый блок из трёх кнопок: «свернуть» (минус), «развернуть/восстановить» (квадрат / два квадрата — переключается по состоянию окна) и «закрыть» (крест); значки построены геометрией `Path`. Цвет значка и hover-подложка берутся из активной темы через `DynamicResource` (`TextSecondaryBrush`, `ItemHoverBrush`, `AccentPressedBrush`), поэтому корректно работают в светлой и тёмной темах. Закрытие идёт через штатный `Close()` и потому уважает настройку «свернуть в трей» (`CloseToTray`) в `OnClosing`. Подсказки используют существующие ключи локализации `Window.Minimize`, `Window.Maximize`, `Common.Close`.
- **Отключение системной рамки и системных кнопок** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml) задан `WindowChrome` с `GlassFrameThickness=0`, `UseAeroCaptionButtons=False`, `CaptionHeight=0`, `CornerRadius=0` и `ResizeBorderThickness=6`.
- **Перетаскивание без системной рамки** (Windows/WPF). В [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml.cs) добавлен обработчик `OnTopBar_MouseLeftButtonDown`: перетаскивание окна за фон верхней панели через `DragMove()` и разворот/восстановление по двойному клику (`ToggleMaximize`); интерактивные элементы (кнопки/поля) перехватывают нажатия сами, поэтому случайного перетаскивания при кликах нет.
- **Изменение размера без системной рамки** (Windows/WPF). Рамка ресайза задана свойством `WindowChrome.ResizeBorderThickness`, поэтому окно растягивается за любую границу и углы.

### Изменено

- **Версия поднята до `0.3.5.37`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.36] — 2026-08-30

В Avalonia-версии (Linux) главное окно оформлено в стиле «прозрачного стекла»: прозрачное окно с запрошенным уровнем прозрачности AcrylicBlur (с откатом на Blur, затем Transparent), полупрозрачная подложка цвета темы вместо сплошного фона рабочей области и скруглённые углы корня окна в стиле glass. Собственные кнопки управления окном и верхняя панель из `0.3.5.35` остались нетронутыми и теперь выглядят согласованно с полупрозрачным фоном. Если оконный менеджер не поддерживает размытие (вернулся Transparent), окно остаётся рабочим и красивым за счёт полупрозрачного фона без размытия. Изменение внесено только для Linux/Avalonia.

### Добавлено

- **Прозрачность окна** (Linux/Avalonia). В конструкторе [`MainWindow.Avalonia.cs`](Configuration Management/Views/MainWindow.Avalonia.cs) заданы `TransparencyLevelHint = { AcrylicBlur, Blur, Transparent }` (запасные варианты по убыванию желаемого) и `Background = Brushes.Transparent`, без чего эффект acrylic/размытия не активируется.
- **Полупрозрачный «стеклянный» фон рабочей области** (Linux/Avalonia). Сплошная привязка `ContentBackgroundColorBrush` в корне окна (`BuildRoot`) заменена на полупрозрачную подложку: новая `ThemeBrushes.WithAlpha(brush, alpha)` берёт текущий цвет темы и пересчитывает его с альфой `0xE8` (~91% непрозрачности) — адаптивно для обеих тем (светлая/тёмная) и всех цветовых схем. Такой же полупрозрачный фон задан области списка баз, чтобы размытие проступало равномерно, а не пятнами.
- **Скруглённые углы окна в стиле glass** (Linux/Avalonia). Корень окна обёрнут в `Border` с `CornerRadius = UiMetrics.RadiusLg` и `ClipToBounds`, при развёрнутом состоянии углы обнуляются, чтобы в углах окна не просвечивал рабочий стол.

### Изменено

- **Версия поднята до `0.3.5.36`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.35] — 2026-08-30

В Avalonia-версии (Linux) главное окно отказалось от системной рамки и системных кнопок управления окном в пользу собственных кнопок «свернуть / развернуть / закрыть», нарисованных в коде. Перетаскивание окна за верхнюю панель и изменение размера за края/углы реализованы вручную, поэтому окно полноценно работает без системных декораций. Изменение внесено только для Linux/Avalonia.

### Добавлено

- **Собственные кнопки управления окном** (Linux/Avalonia). В [`MainWindow.Avalonia.cs`](Configuration Management/Views/MainWindow.Avalonia.cs) добавлен класс `WindowControlButton`: значки «свернуть» (минус), «развернуть/восстановить» (квадрат / два квадрата — переключается по состоянию окна) и «закрыть» (крест) построены из `StreamGeometry`; цвет значка и hover-подложка берутся из темы через `ThemeBrushes.Bind`/`Observe` (`TextPrimaryColorBrush`, `ItemHoverBrush`, `AccentPressedBrush`). Кнопки размещены справа в верхней панели (`BuildTopBar`); закрытие идёт через штатный `Close()` и потому уважает настройку «сворачивать в трей» (`CloseToTray`) в `OnClosing`. Строки подсказок добавлены в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json) (`Window.Minimize`, `Window.Maximize`).
- **Перетаскивание без системной рамки** (Linux/Avalonia). В конструкторе [`MainWindow.Avalonia.cs`](Configuration Management/Views/MainWindow.Avalonia.cs) установлены `SystemDecorations = SystemDecorations.None` и `ExtendClientAreaToDecorationsHint = true`. Перемещение окна реализовано за фон верхней панели (`BeginMoveDrag` по `PointerPressed`, обработчик `OnTopBarPointerPressed`), интерактивные элементы исключаются проверкой источника.
- **Изменение размера без системной рамки** (Linux/Avalonia). В [`MainWindow.Avalonia.cs`](Configuration Management/Views/MainWindow.Avalonia.cs) добавлены невидимые зоны ресайза по краям и углам окна (`AddResizeZones`/`AddResizeZone` с `BeginResizeDrag`), чтобы окно можно было растягивать за любую границу и углы.

### Изменено

- **Версия поднята до `0.3.5.35`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.34] — 2026-08-30

Исправлены регрессии правок `0.3.5.31`–`0.3.5.33`: у всех комбобоксов вновь надёжно открывается выпадающий список по клику (в том числе по стрелке у редактируемых), а контент окон настройки и создания инфобазы больше не обрезается при увеличенных полях. Изменения внесены для обеих платформ (Windows/WPF и Linux/Avalonia).

### Исправлено

- **Починено открытие выпадающего списка и стрелка у всех комбобоксов** (Windows/WPF). В шаблоне `ModernComboBox` тем [`LightTheme.xaml`](Configuration Management/Themes/LightTheme.xaml) и [`DarkTheme.xaml`](Configuration Management/Themes/DarkTheme.xaml) стрелка возвращена внутрь шаблона кнопки-переключателя `DropDownToggle` (а не вынесена отдельным элементом `ArrowGlyph`, как в `0.3.5.33`): клик по стрелке теперь гарантированно попадает в кнопку и переключает `IsDropDownOpen`. `ClickMode` переведён на `Press` для мгновенного срабатывания. Кнопка по-прежнему растянута на всю площадь (`Grid.ColumnSpan="2"`), поэтому нередактируемые комбобоксы открывают список кликом в любом месте, а у редактируемых («Сервер», «Порт» в [`ConnectionSettingsWindow.xaml`](Configuration Management/Views/ConnectionSettingsWindow.xaml), `DbmsBox` в [`CreateInfobaseWindow.xaml`](Configuration Management/Views/CreateInfobaseWindow.xaml)) клик по полю ставит курсор для ввода, а по стрелке/области вне поля — открывает список. События `SelectionChanged`, ввод и привязки не изменены.
- **Avalonia подтверждено**: штатная тема Fluent (см. комментарий в [`Controls.axaml`](Configuration Management/Themes/Controls.axaml)) уже открывает список кликом в любом месте, а клик по `PART_EditableTextBox` редактируемых комбобоксов ставит курсор для ввода — отдельного переопределения шаблона не требуется.
- **Устранено обрезание контента в окне настройки подключения ИБ** (Windows/WPF). Контент каждой вкладки в [`ConnectionSettingsWindow.xaml`](Configuration Management/Views/ConnectionSettingsWindow.xaml) обёрнут в `ScrollViewer VerticalScrollBarVisibility="Auto"` (как в [`SettingsWindow.xaml`](Configuration Management/Views/SettingsWindow.xaml)), чтобы после увеличения высоты полей (`MinHeight=36`, `FontSize=13`) содержимое прокручивалось, а не обрезалось. Колонки полей оставлены растягиваемыми (`Width="*"`), длинные значения переносятся/обрезком не теряются.
- **Avalonia подтверждено**: контент вкладок окна подключения уже обёрнут в `ScrollViewer` (метод `Tab(...)` в [`ConnectionSettingsWindow.Avalonia.cs`](Configuration Management/Views/ConnectionSettingsWindow.Avalonia.cs)), а содержимое окна создания инфобазы — в `ScrollViewer fieldsHost` ([`CreateInfobaseWindow.Avalonia.cs`](Configuration Management/Views/CreateInfobaseWindow.Avalonia.cs)). Окно создания инфобазы на WPF (`CreateInfobaseWindow.xaml`) уже имело `ScrollViewer` вокруг содержимого.
- **Версия поднята до `0.3.5.34`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.33] — 2026-08-30

Исправлена регрессия правок `0.3.5.31`–`0.3.5.32` в окне настройки информационной базы: текстовые поля и комбобоксы снова выглядят единообразно, а стрелка и открытие выпадающего списка редактируемых комбобоксов «Сервер»/«Порт» починены. Изменения внесены для обеих платформ (Windows/WPF и Linux/Avalonia).

### Исправлено

- **Выровнены размеры и отступы `TextBox` и `ComboBox` в окне настройки ИБ** (Windows/WPF). Локальный неявный стиль `TextBox` в [`ConnectionSettingsWindow.xaml`](Configuration Management/Views/ConnectionSettingsWindow.xaml) больше не переопределяет тему (`Padding="6,4"`, `MinHeight="28"`, `FontSize="12"`) — высота, внутренний отступ и кегль берутся из `ModernTextBox` (`10,6 / 36 / 13`), как у соседних комбобоксов; у полей «Сервер БД», «Путь к файлу» и «URL» убраны локальные `Padding`.
- **Выровнены текстовые поля и комбобокс в окне создания ИБ** (Windows/WPF). В [`CreateInfobaseWindow.xaml`](Configuration Management/Views/CreateInfobaseWindow.xaml) у `NameBox`, `FilePathBox`, `ServerBox`, `RefBox`, `DbServerBox`, `DbNameBox`, `DbUserBox`, `TemplateBox`, `PlatformBox`, `GroupPathBox` и у редактируемого `DbmsBox` убраны локальные переопределения `Padding`, чтобы текст начинался на той же позиции, что и у нередактируемых комбобоксов.
- **То же самое на Avalonia** (Linux). В [`ConnectionSettingsWindow.Avalonia.cs`](Configuration Management/Views/ConnectionSettingsWindow.Avalonia.cs) вспомогательный построитель полей `Tb(...)` больше не задаёт локально `Padding`/`MinHeight`/`FontSize` — параметры берутся из темы `ModernTextBox`. В [`CreateInfobaseWindow.Avalonia.cs`](Configuration Management/Views/CreateInfobaseWindow.Avalonia.cs) убраны локальные `Padding` у `_platformBox`, `_filePathBox`, `_templateBox` и у редактируемого `_dbmsBox`.
- **Починена стрелка и открытие списка редактируемых комбобоксов** (Windows/WPF). В шаблоне `ModernComboBox` тем [`LightTheme.xaml`](Configuration Management/Themes/LightTheme.xaml) и [`DarkTheme.xaml`](Configuration Management/Themes/DarkTheme.xaml) стрелка вынесена из кнопки-переключателя в отдельный элемент `ArrowGlyph`, закреплённый в правой колонке (ширина `32`, `IsHitTestVisible=False`, чтобы клик по ней открывал список). У редактируемых комбобоксов отступ текста теперь задаётся через `Margin="{TemplateBinding Padding}"` вместо двойного `Margin + Padding`, что выравнивает текст с обычными полями и нередактируемыми списками. Нередактируемые комбобоксы открывают список кликом в любом месте; у редактируемых клик по полю позволяет ввод, а по остальной области/стрелке — открывает список. Логика выбора и события `SelectionChanged` не изменены.
- **Avalonia подтверждено**: штатная тема Fluent (см. `ModernComboBox` в [`Controls.axaml`](Configuration Management/Themes/Controls.axaml)) открывает список кликом по любой области, а клик по `PART_EditableTextBox` редактируемых комбобоксов ставит курсор для ввода — отдельного переопределения шаблона не требуется.
- **Версия поднята до `0.3.5.33`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.32] — 2026-08-30

Выпадающее меню `ComboBox` теперь открывается кликом по любому месту комбобокса, а не только по стрелке справа. Изменение внесено для обеих платформ (Windows/WPF и Linux/Avalonia).

### Исправлено

- **Открытие выпадающего списка по клику в любой области комбобокса** (Windows/WPF). В шаблоне стиля `ModernComboBox` в [`LightTheme.xaml`](Configuration Management/Themes/LightTheme.xaml) и [`DarkTheme.xaml`](Configuration Management/Themes/DarkTheme.xaml) кнопка-переключатель `DropDownToggle` больше не ограничена колонкой стрелки (`Grid.Column="1"`), а растянута на всю площадь комбобокса (`Grid.ColumnSpan="2"`) и вынесена нижним слоем шаблона; стрелка закреплена у правого края. Основная часть комбобокса была некликабельной из-за `IsHitTestVisible="False"` у контента — теперь клик по ней переключает `IsDropDownOpen`. У редактируемых комбобоксов (`IsEditable="True"`, например `FontSizeComboBox`) поверх кнопки лежит `PART_EditableTextBox`, который перехватывает клик для установки курсора и ввода текста, а список открывается по стрелке — логика выбора и события `SelectionChanged` не изменены.
- **Поведение Avalonia подтверждено и задокументировано** (Linux/Avalonia). В [`Controls.axaml`](Configuration Management/Themes/Controls.axaml) шаблон не переопределяется: штатная тема Fluent уже открывает список кликом в любом месте (`ComboBox.OnPointerReleased` переключает `IsDropDownOpen`), а клик по `PART_EditableTextBox` редактируемых комбобоксов ставит курсор и не открывает список. Добавлен поясняющий комментарий, чтобы исключить опасное переопределение шаблона в будущем.
- **Версия поднята до `0.3.5.32`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.31] — 2026-08-30

Текстовые поля (`TextBox`) приведены к единому визуальному стилю комбобоксов (`ComboBox`) в окне настроек и связанных окнах: выровнены высота, шрифт, внутренние отступы (padding), скругление углов и толщина рамки. Изменение внесено для обеих платформ (Windows/WPF и Linux/Avalonia).

### Изменено

- **Стиль `ModernTextBox` приведён к `ModernComboBox`** (обе платформы). В [`LightTheme.xaml`](Configuration Management/Themes/LightTheme.xaml) и [`DarkTheme.xaml`](Configuration Management/Themes/DarkTheme.xaml) (Windows/WPF) у `ModernTextBox` теперь те же параметры, что у `ModernComboBox`: толщина рамки `1.5`, внутренний отступ `10,6`, скругление углов `8`, минимальная высота `36`. Аналогично обновлён `ModernTextBox` в [`Controls.axaml`](Configuration Management/Themes/Controls.axaml) (Linux/Avalonia); комментарий у `ModernPasswordBox` приведён в соответствие — внешние параметры совпадают с полем ввода.
- **Индивидуальное текстовое поле `SyncFilePathTextBox` выровнено по соседним комбобоксам** (Windows/WPF). В [`SettingsWindow.xaml`](Configuration Management/Views/SettingsWindow.xaml) полю заданы `Height="40"`, `FontSize="14"` и явно применён стиль `ModernTextBox` — как у соседних `SyncModeComboBox`/`SyncTriggerComboBox` в блоке ibases.v8i. Avalonia-реализация [`SettingsWindow.Avalonia.cs`](Configuration Management/Views/SettingsWindow.Avalonia.cs) согласуется через обновлённую тему (высота из `MinHeight`).
- **Версия поднята до `0.3.5.31`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration Management/Configuration%20Management.csproj).

## [0.3.5.30] — 2026-08-30

Кнопки «Вверх»/«Вниз» для изменения порядка колонок перенесены из-под списка «Порядок колонок» в правую колонку справа от списка — так же, как расположены кнопки порядка избранного во вкладке «Клавиши». Изменение внесено для обеих платформ (Windows/WPF и Linux/Avalonia).

### Изменено

- **Расположение кнопок порядка колонок** (обе платформы). В подвкладке «Колонки» вкладки «Отображение» кнопки «Вверх»/«Вниз» (`ColumnOrderUpButton`/`ColumnOrderDownButton`) перенесены из горизонтальной панели под списком в вертикальный столбец **справа от списка** (`ColumnOrderList`). Использована та же сетка, что во вкладке «Клавиши» для избранного: список по ширине `*`, справа колонка `Auto` с вертикально расположенными кнопками. Обработчики `OnColumnOrderUp_Click`/`OnColumnOrderDown_Click`, имена кнопок и всплывающие подсказки `Settings.Columns.MoveUpTooltip`/`MoveDownTooltip` сохранены. Изменены [`SettingsWindow.xaml`](Configuration Management/Views/SettingsWindow.xaml) (Windows/WPF) и [`SettingsWindow.Avalonia.cs`](Configuration Management/Views/SettingsWindow.Avalonia.cs) (Linux/Avalonia).

## [0.3.5.29] — 2026-08-30

Во вкладке «Отображение» окна настроек порядок колонок списка баз теперь перемещается стрелками — так же, как порядок избранного во вкладке «Клавиши» (Windows/WPF).

### Изменено

- **Кнопки порядка колонок во вкладке «Отображение» заменены на стрелки** (Windows/WPF). Вместо текстовых кнопок «Вверх»/«Вниз» (`Settings.Columns.OrderUp`/`OrderDown`) под списком «Порядок колонок» теперь находятся кнопки со стрелками ↑/↓ в стиле `SecondaryButton`, аналогичные кнопкам порядка избранного во вкладке «Клавиши». Добавлены всплывающие подсказки «Переместить колонку выше/ниже» ([`SettingsWindow.xaml`](Configuration Management/Views/SettingsWindow.xaml)). Добавлены ключи локализации `Settings.Columns.MoveUpTooltip` и `Settings.Columns.MoveDownTooltip` в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json).

## [0.3.5.28] — 2026-08-30

Для Windows теперь собирается **один автономный (self-contained) single-file исполняемый файл**: скрипт [`build-windows-single-file.ps1`](Configuration Management/build-windows-single-file.ps1) публикует WPF-приложение (`net10.0-windows`, RID `win-x64`) и очищает выходную папку, оставляя в ней только `ConfigurationManagement.exe` — без `.dll`, `.pdb` и сопутствующих папок.

### Добавлено

- **Выделенный скрипт сборки одного Windows-файла** — [`build-windows-single-file.ps1`](Configuration Management/build-windows-single-file.ps1). В отличие от [`build.ps1`](Configuration Management/build.ps1), он запускается только на Windows, использует параметры `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract` и `EnableCompressionInSingleFile`, а затем удаляет из выходного каталога `dist\win-x64\` все лишние файлы и папки, оставляя единственный `ConfigurationManagement.exe`. Поддерживаются аргументы `-Configuration` (по умолчанию `Release`) и `-RID` (по умолчанию `win-x64`), а также `SKIP_PUBLISH=1` для быстрой проверки синтаксиса.
- **Документация способа сборки** в [`README.md`](README.md): раздел «Публикация автономного приложения» дополнен командой `.\build-windows-single-file.ps1` с указанием результата — один исполняемый файл `dist\win-x64\ConfigurationManagement.exe`, не требующий установки .NET Runtime.

## [0.3.5.27] — 2026-08-29

Удалены окна `GroupSettingsWindow` и `TagInputWindow`, которые собирались в сборку, но были недостижимы из интерфейса. **Авторство правок — [ksv47](https://github.com/ksv47)** (PR #110, ветка `ksv47/fix-issue-79`).

### Удалено

- **Окно `GroupSettingsWindow`** (обе платформы): не имело ни одной ссылки в коде — управление группами уже доступно через контекстные меню и окно настроек. Удалены `Configuration Management/Views/GroupSettingsWindow.xaml/.xaml.cs` и `Configuration Management/Views/GroupSettingsWindow.Avalonia.cs`.
- **Окно `TagInputWindow` и его реализации** (обе платформы): осталось от прежнего способа добавления тега и было недостижимо. Удалены `Configuration Management/Views/TagInputWindow.xaml/.xaml.cs`, `TagInputWindow.axaml` и `TagInputWindow.Avalonia.cs`.

### Изменено

- **Пример использования в doc-комментарии [`LocExtension.Avalonia.cs`](Configuration Management/Localization/LocExtension.Avalonia.cs)** — ссылка `{loc:Loc TagInput.Title}` заменена на актуальную `{loc:Loc Settings.Title}`.

## [0.3.5.26] — 2026-08-29

Поле «Версия» в окне «Создание информационной базы» теперь корректно обновляется при переключении типа базы между файловой и клиент-серверной (issue #91): для двух типов хранятся разные последние успешно использованные версии платформы. **Авторство правок — [ksv47](https://github.com/ksv47)** (PR #109, ветка `ksv47/fix-issue-91`).

### Исправлено

- **При смене типа базы read-only поле «Версия» пересчитывается** (обе платформы). Окно всегда открывается с файловым типом, поэтому без пересчёта при переключении на клиент-серверную базу в поле оставалась версия, сохранённая для файловой. Теперь `RefreshPlatformList(replaceSelection: true)` вызывается при переключении типа и подставляет последнюю успешно использованную версию для выбранного типа базы ([`CreateInfobaseWindow.xaml.cs`](Configuration Management/Views/CreateInfobaseWindow.xaml.cs), [`CreateInfobaseWindow.Avalonia.cs`](Configuration Management/Views/CreateInfobaseWindow.Avalonia.cs)).

## [0.3.5.25] — 2026-08-29

Команда `CREATEINFOBASE`: пароль СУБД (`DBPwd`) больше не попадает в диагностические сообщения при ошибке создания базы, а галочка «Блокировка фоновых заданий» снова использует документированный параметр `SchJobDn="Y"` (issues #90/#94). **Авторство правок — [ksv47](https://github.com/ksv47)** (PR #108, ветка `ksv47/fix-issues-90-94`).

### Добавлено

- **Маскирование пароля СУБД в сообщениях об ошибке `CREATEINFOBASE`** (обе платформы). Новый [`SensitiveDataMasker.cs`](Configuration Management/Services/SensitiveDataMasker.cs) скрывает значение `DBPwd` (заменяется на `********`) и в показанной команде создания, и в диагностике платформы, если она повторила строку подключения. Применяется в [`OneCLauncher.Arguments.cs`](Configuration Management/Services/OneCLauncher.Arguments.cs) (Windows/WPF) и [`OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs) (Linux/Avalonia).

### Исправлено

- **Восстановлен документированный параметр `SchJobDn="Y"`** (обе платформы). Если включена галочка «Блокировка фоновых заданий», в строку подключения `CREATEINFOBASE` добавляется `SchJobDn="Y"` рядом с `CrSQLDB="Y"`. Параметр действует только при создании базы и не попадает в обычную строку подключения ([`OneCLauncher.Arguments.cs`](Configuration Management/Services/OneCLauncher.Arguments.cs), [`OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs)).

## [0.3.5.24] — 2026-08-29

Три WPF-точки вызова окна выбора группы `GroupPickerWindow` в Windows/WPF теперь передают вид выбираемого объекта, чтобы заголовок/подзаголовок/справка называли именно тот объект, для которого выбирается группа (issue #83, часть 3).

### Добавлено

- **Передача вида объекта из WPF-точек вызова** (Windows/WPF, issue #83, часть 3). При создании информационной базы ([`CreateInfobaseWindow.xaml.cs`](Configuration Management/Views/CreateInfobaseWindow.xaml.cs)) и при настройке подключения базы ([`ConnectionSettingsWindow.xaml.cs`](Configuration Management/Views/ConnectionSettingsWindow.xaml.cs)) в конструктор `GroupPickerWindow` передаётся `GroupPickerObjectKind.Infobase` — используются формулировки про **базу**. При выборе родительской группы в окне настройки группы ([`GroupEditWindow.xaml.cs`](Configuration Management/Views/GroupEditWindow.xaml.cs)) передаётся `GroupPickerObjectKind.Group` — используются формулировки про **группу**.

## [0.3.5.23] — 2026-08-29

Окно выбора группы `GroupPickerWindow` в Windows/WPF теперь принимает вид выбираемого объекта и подставляет конкретные формулировки заголовка/подзаголовка/справки вместо нейтральных (issue #83, часть 2).

### Добавлено

- **Выбор формулировок окна по виду объекта** (Windows/WPF, issue #83, часть 2). В конструктор [`GroupPickerWindow`](Configuration Management/Views/GroupPickerWindow.xaml.cs) добавлен параметр `kind` (по умолчанию `Group`) типа `GroupPickerObjectKind` (`Group`/`Infobase`). Новый метод `ApplyObjectKind` выбирает ключи локализации в зависимости от вида: для **группы** — `GroupPicker.TitleGroup`/`SubtitleGroup`/`HelpGroup`, для **базы** — `GroupPicker.TitleBase`/`SubtitleBase`/`HelpBase`. Заголовку, подзаголовку и справке в [`GroupPickerWindow.xaml`](Configuration Management/Views/GroupPickerWindow.xaml) присвоены имена `TitleText`/`SubtitleText`/`HelpLink`, а нейтральные привязки `{loc:Loc GroupPicker.Title/Subtitle/Help}` заменены пустыми строками — текст подставляется кодом. Нейтральные `Hint`/`SearchPlaceholder` сохранены как есть.

## [0.3.5.22] — 2026-08-29

Введены отдельные формулировки окна выбора группы для **группы** и для **информационной базы** в Windows/WPF (issue #83, часть 1): текст теперь зависит от вида выбираемого объекта.

### Добавлено

- **Конкретные ключи локализации окна выбора группы** (issue #83, часть 1). Раньше окно выбора группы использовало нейтральные формулировки о «выбранном элементе» (`GroupPicker.Title/Subtitle/Help`), которые подходят для Linux/Avalonia. Теперь добавлены специализированные ключи для Windows/WPF, чтобы заголовок, подзаголовок и справка называли именно тот объект, для которого выбирается группа: для **группы** — `GroupPicker.TitleGroup` («Выбор родительской группы»), `GroupPicker.SubtitleGroup` («Группа будет размещена внутри выбранной группы»), `GroupPicker.HelpGroup`; для **базы** — `GroupPicker.TitleBase` («Выбор группы для базы»), `GroupPicker.SubtitleBase` («База будет размещена внутри выбранной группы»), `GroupPicker.HelpBase`. Нейтральные ключи `GroupPicker.Title/Subtitle/Help/Hint/SearchPlaceholder` сохранены как есть и продолжают использоваться Avalonia/Linux. Ключи добавлены в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json).

## [0.3.5.21] — 2026-08-29

Исправлена ошибка issue #103 на Windows/WPF: изменение положения колонки «Действия» (и любой другой колонки) в списке баз теперь реально применяется.

### Исправлено

- **Настраиваемый порядок колонок в списке баз снова работает** (Windows/WPF, issue #103). Раньше перемещение колонки «Действия» (и любой другой) не давало никакого эффекта: метод [`BuildColumnLayout()`](Configuration Management/Views/MainWindow.Columns.cs) перебирал жёстко заданный массив `known = { "Version", "LaunchMode", "Actions", "ServerBase", "LastLaunch", "Size", "Configuration" }`, лишь проверяя наличие ключа в `ColumnOrderKeys`. Из-за этого итоговый порядок всегда совпадал с порядком по умолчанию из массива `known`, а выбранный пользователем порядок игнорировался. Метод переписан по образцу корректной Avalonia-версии: первая итерация идёт по пользовательскому порядку `_viewModel.ColumnOrderKeys` (незнакомые ключи отбрасываются), а второй проход лишь дополняет недостающие известные колонки в порядке по умолчанию. Колонка «Действия» теперь участвует в настраиваемом порядке наравне с остальными и может быть перемещена пользователем.

## [0.3.5.20] — 2026-08-29

В колонке «Версия платформы» списка баз теперь показывается разрядность рядом с версией, если она выбрана явно (issue #101).

### Добавлено

- **Отображение разрядности в списке баз** (Windows/WPF, issue #101). Если у информационной базы разрядность выбрана конкретно (`32`/`x86` или `64`/`x64`), рядом с версией платформы в колонке «Версия платформы» выводится суффикс `[x86]`/`[x64]`: например `8.3.19 [x86]`, `8.3 [x64]`. При автоопределении или приоритетном режиме (`32-priority`/`64-priority`) показывается только чистая версия, как раньше (`8.3.27.2325`). Реализовано новым вычисляемым свойством [`Infobase.PlatformVersionDisplay`](Configuration Management/Models/Infobase.cs) (суффикс добавляется по значению `Architecture`), а привязка колонки переведена на него ([`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml)). Свойство `Architecture` переведено на уведомляющий сеттер, чтобы колонка обновлялась при изменении версии или разрядности базы.

## [0.3.5.19] — 2026-08-29

Восстановлена подсветка наведения у значковых команд верхней панели и вторичных кнопок правой панели в Linux/Avalonia-версии (issue #98).

### Исправлено

- **Подсветка при наведении у команд верхней панели** (Linux/Avalonia). Кнопки «Добавить базу», «Очистить кеш», «Синхронизация», «Проверить доступность», «Тема», «Настройки» и переключатель плотности не подсвечивались при наведении, хотя сегментные переключатели («Группы/Теги», «Все/Избранное/Недавние») и компактный режим уже подсвечивались. Причина: hover-кисть значковых кнопок бралась по ключу `ItemHoverColorBrush` через `ThemeBrushes.Observe` ([`ThemeBrushes.Avalonia.cs`](Configuration Management/Themes/ThemeBrushes.Avalonia.cs)), который статическую кисть, установленную один раз при применении схемы, доставлял ненадёжно, и фон оставался прозрачным. Замена на проверенный ключ `ItemHoverBrush` (XAML-кисть с `DynamicResource` на `ItemHoverColor`, как у сегментов) делает подсветку единой со всей панелью. Места замены — [`MainWindow.Avalonia.cs`](Configuration Management/Views/MainWindow.Avalonia.cs): конструктор `TopBarIconButton`, кнопка «Проверить доступность», кнопки-иконки строк списка и вторичные кнопки запуска правой панели.

## [0.3.5.18] — 2026-08-28

Завершение устранения замечаний issue #77 к команде `CREATEINFOBASE` и экранированию строк/аргументов: чтение пути файловой базы стало симметричным записи, а значения, подставляемые в аргументы командной строки платформы, больше не могут внедрить дополнительный ключ `1cv8`.

### Исправлено

- **Разбор ссылки на файловую базу (`File=`) теперь симметричен записи** (обе платформы). Регекс `File="…"` в [`OneCLauncher.Arguments.cs`](Configuration Management/Services/OneCLauncher.Arguments.cs) (`ParseLink`) и [`OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs) (`ParseLinkArguments`) допускает удвоенную кавычку `""` внутри пути и разворачивает её обратно (`UnescapeConnectValue`). Раньше разбор останавливался на первой же кавычке, и путь вида `C:\my""dir` обрезался до `C:\my`.
- **Защита аргументов запуска 1С от внедрения дополнительного ключа** (обе платформы). Значения, подставляемые внутрь кавычек ключей командной строки (`/F`, `/S`, `/WS`, `/N`, `/P`, `/ConfigurationRepositoryF/N/P`, `/DumpIB`, `/DumpCfg`, `/UseTemplate`), проверяются помощником `IsSafeCliValue`: если значение содержит двойную кавычку или управляющий символ (CR/LF и т.п.), аргумент не подставляется, а создание базы из шаблона или выгрузка `.dt`/`.cf` отменяется с сообщением — вместо превращения значения в лишний ключ `1cv8`. Пробелы внутри значения безопасны и не отклоняются (обычные пути вида `C:\Program Files\…` продолжают работать). Для грамматики ключа командной строки удвоение кавычки НЕ применяется (в отличие от строки подключения) — это отдельное правило; см. комментарии в [`OneCLauncher.Arguments.cs`](Configuration Management/Services/OneCLauncher.Arguments.cs) и [`OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs).
- **Новое сообщение об ошибке** `Launcher.CreateTemplateInvalidPathFormat` для недопустимого пути шаблона при создании базы (добавлено в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json)).

## [0.3.5.17] — 2026-08-28

Устранено зависание при запуске обновлённой версии поверх старых конфигурационных файлов (issue #64): раньше процесс стартовал, но главное окно не появлялось, и восстановить работу можно было только удалением `*.json` и повторным импортом баз.

### Исправлено

- **Null-безопасная загрузка легаси-`settings.json`** (обе платформы). Если в старом файле настроек поля-коллекции (`CollapsedGroups`, `InstalledPlatformVersions`, `AdditionalPlatformSearchPaths`, `ColumnOrder`, `FavoriteHotkeyIds`, `TemplateCatalogPaths`, `ElementFonts`, `FileSizeCache` и др.) отсутствовали или содержали `null`, десериализация перезаписывала значения по умолчанию на `null`, и конструктор главной ViewModel падал с `NullReferenceException` при старте — окно не открывалось. Теперь после чтения файла вызывается `AppSettings.NormalizeForLoad()` ([`AppSettings.cs`](Configuration Management/Models/AppSettings.cs)), который восстанавливает непустые коллекции и безопасные значения строк, поэтому приложение гарантированно стартует на данных любой прежней версии.
- **Разрыв циклических ссылок групп при загрузке `groups.json`** (обе платформы). Циклическая цепочка родительских ссылок (A→B→A) в плоском списке приводила к бесконечной вложенности при построении дерева и могла «вешать» приложение или вызывать переполнение стека на повреждённых/легаси-файлах. `NormalizeGroups()` ([`InfobaseRepository.cs`](Configuration Management/Services/InfobaseRepository.cs)) теперь разрывает именно ту ссылку, которая замыкает цикл (группа становится корневой), а результат сразу сохраняется на диск — иерархия не ломает последующие запуски.

## [0.3.5.16] — 2026-08-28

Исправлена галочка **«Блокировка фоновых заданий»** в окне создания информационной базы (issue #94): раньше она не действовала ни при создании, ни при подключении к базе — в коде использовалось несуществующее имя `SCHEDJOBS`, которого нет в документации платформы.

### Исправлено

- **Галочка «Блокировка фоновых заданий» теперь реально блокирует фоновые задания при создании клиент-серверной базы** (обе платформы, issue #94). Раньше значение сохранялось только в `ConnectionSettings.BlockScheduledJobs`, а сборщики команды `CREATEINFOBASE` про блокировку не знали — параметр в строку подключения не попадал вовсе. Теперь галочка передаётся в метод `CreateInfoBase`, и при её включении в строку подключения `CREATEINFOBASE` добавляется документированный параметр `SchJobDn="Y"` рядом с `CrSQLDB`. Реализовано в [`OneCLauncher.Arguments.cs`](Configuration Management/Services/OneCLauncher.Arguments.cs) (Windows/WPF) и [`OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs) (Linux/Avalonia); вызовы из [`CreateInfobaseWindow.xaml.cs`](Configuration Management/Views/CreateInfobaseWindow.xaml.cs) и [`CreateInfobaseWindow.Avalonia.cs`](Configuration Management/Views/CreateInfobaseWindow.Avalonia.cs) передают значение чекбокса.
- **Из строки подключения и списка баз убрано несуществующее имя `SCHEDJOBS=NO`.** Параметр `SchJobDn` действует только при создании базы и не влияет на уже созданную ИБ при подключении (проверено на PostgreSQL и MS SQL Server), поэтому писать его в строку соединения и в `ibases.v8i` бессмысленно. Убрано из `ConnectionSettings.ToConnectionString()` ([`ConnectionSettings.cs`](Configuration Management/Models/ConnectionSettings.cs)) и из экспортёра ([`IbasesV8iExporter.cs`](Configuration Management/Services/IbasesV8iExporter.cs)); блокировка задаётся только при создании через `SchJobDn="Y"`.
- **Обратный разбор строки подключения распознаёт документированный параметр `SchJobDn`** (значения `Y`/`1`/`True`/`Yes`/`On`) и для совместимости по-прежнему читает устаревший `SCHEDJOBS=NO` из строк прежних версий ([`ConnectionSettings.cs`](Configuration Management/Models/ConnectionSettings.cs)).

## [0.3.5.15] — 2026-08-28

### Изменено

- **Поле «Версия» в окне «Создание информационной базы» по умолчанию подставляется последней успешно использованной версией платформы** (обе платформы, issue #91), а не самой новой из установленных. Раньше поле заполнялось первой строкой списка установленных версий (сортировка по убыванию), что для клиент-серверной базы часто давало несовместимую с сервером версию. Теперь версия запоминается отдельно для файловых и клиент-серверных баз и только после успешного создания ИБ; если сохранённой версии больше нет среди установленных — берётся самая новая. Реализовано в [`CreateInfobaseWindow.xaml.cs`](Configuration Management/Views/CreateInfobaseWindow.xaml.cs) (WPF) и [`CreateInfobaseWindow.Avalonia.cs`](Configuration Management/Views/CreateInfobaseWindow.Avalonia.cs) (Linux); хранение — новые поля `LastFileCreatePlatformVersion`/`LastClientServerCreatePlatformVersion` в [`AppSettings.cs`](Configuration Management/Models/AppSettings.cs).

### Добавлено

- **Предупреждение о несоответствии версии платформы при создании клиент-серверной базы** (обе платформы, issue #91). Если выбранная версия отличается по первым двум числам (major.minor) от версий, которыми уже работают клиент-серверные базы на этом же сервере, перед созданием показывается предупреждение с возможностью продолжить. Раньше о несовместимости пользователь узнавал только по отказу платформы, причём текст отказа ничего о версии не сообщал. Добавлены ключи локализации `CreateInfobase.VersionMismatchTitle`/`CreateInfobase.VersionMismatchMsg` в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json).

## [0.3.5.14] — 2026-08-28

### Исправлено

- **Переключатель «Показывать/скрывать теги» в шапке списка баз больше не обрезается границей колонки «Название»** (WPF, issue #84). При включённой группировке всегда видимый переключатель тегов жил в горизонтальном `StackPanel`, охватывающем ведущие колонки заголовка; когда колонка «избранное» схлопывалась до нуля (`ShowFavoritesButton=false`), суммарная ширина ведущих колонок становилась меньше блока, и переключатель молча срезался правым краем колонки «Название» — был виден лишь «огрызок» кнопки. Колонке переключателя тегов теперь задана гарантированная минимальная ширина `MinWidth="30"` в шапке и в обеих сетках строк (группа и информационная база), поэтому место под переключатель всегда участвует в раскладке, а выравнивание заголовков со строками сохраняется ([`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml)). В Avalonia-версии (Linux) обрезки нет: компенсатор заголовка там динамически резервирует ширину блока кнопок, включая переключатель тегов.

## [0.3.5.13] — 2026-08-28

### Исправлено

- **Горячая клавиша правки открывает окно «Изменить группу» для закреплённой группы** (обе платформы). Раньше при выборе служебного узла «Закреплённые» (без модели `Group`) команда правки ничего не делала, хотя для обычной группы и узла «Без группы» по той же клавише открывался редактор. Теперь при выделенном узле «Закреплённые» нажатие горячей клавиши правки открывает окно редактирования оформления узла (цвет и иконка), как для «Без группы». Правка конкретной базы внутри узла (кнопка «Действия» строки базы) осталась прежней. Реализовано в [`MainViewModel.Commands.cs`](Configuration Management/ViewModels/MainViewModel.Commands.cs) (WPF) и [`MainViewModel.Avalonia.cs`](Configuration Management/ViewModels/MainViewModel.Avalonia.cs) (Linux).
- **Надпись чекбокса «Очистить кеш удалённых групп» в окне очистки кеша больше не обрезается** (обе платформы). Чекбоксу отдаётся вся доступная ширина (колонка `*`), надпись переносится на несколько строк (`TextWrapping=Wrap`), поэтому текст читается целиком даже при минимальной ширине окна. Справа остаются размер и кнопки «Очистить»/«Отмена». WPF: [`CacheCleanWindow.xaml`](Configuration Management/Views/CacheCleanWindow.xaml); Avalonia: [`CacheCleanWindow.Avalonia.cs`](Configuration Management/Views/CacheCleanWindow.Avalonia.cs).
- **Кнопки «Да» и «Нет» в окне подтверждения больше не перепутаны** (обе платформы). В окне предупреждения/подтверждения [`MaterialMessageWindow.xaml`](Configuration Management/Services/MaterialMessageWindow.xaml) (WPF) и [`MaterialMessageWindow.Avalonia.cs`](Configuration Management/Services/MaterialMessageWindow.Avalonia.cs) (Linux) кнопки теперь расположены в порядке «Да» (подтверждение) слева, «Нет» (отмена) справа, как принято в диалогах Windows. Раньше они были переставлены местами.
- **Текст кнопки подтверждения в окне сообщений стал контрастным и читаемым** (WPF). Надпись «Да»/«ОК» на зелёном фоне кнопки `OkButton` теперь белая и жирная (`Foreground="White"`, `FontWeight="Bold"`), добавлена явная настройка сглаживания текста; полупрозрачная подсветка стандартного стиля Material Design при `IsDefault="True"` больше не перекрывает фон кнопки — остались только явные состояния наведения/нажатия, меняющие оттенок зелёного ([`MaterialMessageWindow.xaml`](Configuration Management/Services/MaterialMessageWindow.xaml)).
- **Устранено падение `System.InvalidOperationException: DialogResult можно задать только после создания Window...` в окне сообщений** (WPF). Обработчики `OnOkClick`/`OnCancelClick` ([`MaterialMessageWindow.xaml.cs`](Configuration Management/Services/MaterialMessageWindow.xaml.cs)) теперь устанавливают результат через единый безопасный метод `CloseWithResult`, который корректно выставляет `Confirmed`, пытается присвоить `DialogResult` в блоке `try/catch` (валидно только для модального окна через `ShowDialog`) и всегда закрывает окно через `Close()`. Повторное подключение обработчиков кликов в конструкторе убрано, чтобы исключить двойную регистрацию.
- **Окно выбора группы говорит нейтрально о «выбранном элементе», а не только о группе** (обе платформы). Раньше заголовок «Выбор родительской группы», подзаголовок «Группа будет размещена внутри выбранной группы» и справка описывали только размещение группы внутри группы, хотя окно `GroupPickerWindow` открывается и при создании информационной базы, и в настройках её подключения, где выбирается группа для базы. Теперь заголовок, подзаголовок, подсказка и справка говорят нейтрально про «выбранный элемент» (issue #83). Изменены ключи `GroupPicker.Title`/`Subtitle`/`Help` в [`Localization/Languages/ru.json`](Configuration Management/Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Configuration Management/Localization/Languages/en.json); правка покрывает все шесть точек вызова (Windows/WPF и Linux/Avalonia).

## [0.3.5.12] — 2026-08-28

Интерфейс приведён к единому стилю Material Design: чекбоксы/радиокнопки, окно подключения, очистка кеша, правка закреплённой группы и все всплывающие сообщения (предупреждения, подтверждения, ошибки).

### Добавлено

- **Кнопка «Изменить группу» появилась у служебного узла «Закреплённые»** (обе платформы) — теперь через то же окно можно изменять цвет и иконку узла закрепления, как и у «Без группы». Раньше узел не редактировался вовсе.
- **Собственное окно сообщений в стиле Material Design** — [`MaterialMessageWindow.xaml`](Configuration Management/Services/MaterialMessageWindow.xaml) (WPF) и [`MaterialMessageWindow.Avalonia.cs`](Configuration Management/Services/MaterialMessageWindow.Avalonia.cs) (Linux): все предупреждения, подтверждения и ошибки показываются через единое модальное окно с иконкой типа, акцентными кнопками и карточкой вместо стандартного MessageBox.

### Изменено

- **Шаблон `ArchRadio` окна выбора версии платформы приведён к Material Design** ([`PlatformVersionPickerWindow.xaml`](Configuration Management/Views/PlatformVersionPickerWindow.xaml)): внешний круг теперь при выборе заливается акцентным цветом `AccentBrush` (обводка убирается), внутри появляется белая точка; при наведении добавляется мягкая подложка (hover-ring) и акцентная обводка. Индикатор стал гладким и гармонирует с остальным UI (фон `CardBackgroundBrush`, акцент `AccentBrush`). Кнопки «Все / x32 / x64» используют этот же стиль.
- **Радиокнопки в окне подключения (вкладка «Авторизация» и другие) приведены к Material Design** — округлый индикатор с акцентной заливкой, ховер-круг; поля «Пользователь»/«Пароль» сближены, убран лишний вертикальный отступ.
- **Надпись «Остатки от удалённых баз» в окне очистки кеша больше не обрезается** — блок переведён на сетку с переносом текста, размер рядом с кнопками отображается отдельно.
- **Кнопка «Изменить группу» в панели группы больше не подсвечивается при наведении и фокусе** (WPF) — ей задан стиль `IconButton` ([`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml)), как у соседней кнопки «Удалить», поэтому выделение/подсветка стандартной кнопки исчезли. В Avalonia-версии кнопка «Изменить» строится тем же методом `GroupRowActionButton` с прозрачным фоном и нулевой рамкой, так что поведение идентично.

## [0.3.5.11] — 2026-08-28

Служебные узлы дерева «Закреплённые» и «Без группы» выделены отдельным оформлением по умолчанию, их настройки больше нельзя изменять, а кнопка удаления у них убрана.

### Добавлено

- **Собственные цвета по умолчанию для служебных узлов** «Закреплённые» и «Без группы», чтобы они визуально отличались от обычных групп (по умолчанию синие `#2D6CDF`): закреплённые — фиолетовый `#8B5CF6`, «Без группы» — серый `#6B7280`. Для узла «Закреплённые» добавлены настройки отображения `PinnedColor`/`PinnedIconColor`/`PinnedIcon` в [`AppSettings.cs`](Configuration Management/Models/AppSettings.cs) (для «Без группы» аналогичные `NoGroup*` уже существовали, их значение по умолчанию стало серым).

### Изменено

- **Настройки закреплённой группы больше нельзя менять** — редактирование узла «Закреплённые» запрещено в [`MainViewModel.Commands.cs`](Configuration Management/ViewModels/MainViewModel.Commands.cs), как и для «Без группы» имя и содержимое таких служебных узлов зафиксированы.
- **Кнопка «Удалить группу» скрыта у служебных узлов** «Закреплённые» и «Без группы» (WPF — [`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml); Avalonia — [`MainWindow.Avalonia.cs`](Configuration Management/Views/MainWindow.Avalonia.cs)). Кнопка «Изменить группу» остаётся у «Без группы» (для правки цвета/иконки), но скрыта у закреплённых.

## [0.3.5.10] — 2026-08-27

Улучшено окно «Создание информационной базы из шаблона» (Windows/WPF): дерево шаблонов больше не блокирует показ окна на крупных каталогах `tmplts`, подсказка каталога отражает фактически используемый каталог, а стартовый выбор шаблона снят.

### Исправлено

- **Дерево шаблонов строится в фоне** — окно открывается сразу, без видимой «задержки/зависания» 10–12 секунд при больших каталогах `tmplts` (например, ~1100 манифестов `1cv8.mft`). Сканирование каталогов и построение дерева выполняются в фоновом потоке (`Task.Run`), а результат дособирается по готовности; во время загрузки в окне показывается индикатор прогресса и текст «Загрузка шаблонов…». Повторное нажатие «Обновить» отменяет предыдущий запущенный скан. Реализовано в [`CreateInfobaseWindow.xaml.cs`](Configuration Management/Views/CreateInfobaseWindow.xaml.cs) + [`CreateInfobaseWindow.xaml`](Configuration Management/Views/CreateInfobaseWindow.xaml).
- **Подсказка каталога шаблонов больше не врёт при настроенном своём каталоге.** Раньше первичным всегда показывался дефолтный `%PUBLIC%\Documents\1C\1cv8\tmplts` с пометкой «папка ещё не создана», даже когда дерево строилось из собственного каталога, заданного в настройках программы. Теперь основным берётся первый фактически существующий корень (а пользовательские каталоги в списке идут первыми), а дефолтный путь используется как fallback только когда ничего не найдено ([`CreateInfobaseWindow.xaml.cs`](Configuration Management/Views/CreateInfobaseWindow.xaml.cs)).
- **Отменён автоматический выбор первого шаблона при открытии окна.** Раньше сразу подставлялись наименование и путь к файлу произвольного первого шаблона, и «Создать» мог создать базу без явного выбора. Теперь выбор начинается пустым: имя и поле шаблона заполняются только после того, как пользователь сам выделит шаблон в дереве (или укажет файл вручную).
- **Добавлены ключи локализации** `CreateInfobase.Loading` и `CreateInfobase.LoadingFailed` в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json).

## [0.3.5.9] — 2026-08-27

В режиме «Конфигуратор» при запуске автоматически передаются параметры подключения к хранилищу конфигурации (`/ConfigurationRepositoryF`, `/ConfigurationRepositoryN`, `/ConfigurationRepositoryP`), заданные в настройках подключения базы.

### Добавлено

- **Подключение к хранилищу конфигурации в «Конфигураторе»** (обе платформы). Если в настройках базы заполнен адрес сервера хранилища (вкладка настроек подключения, блок «Хранилище конфигурации»), при запуске в режиме «Конфигуратор» добавляются ключи: `/ConfigurationRepositoryF "<путь>"` (для серверного хранилища путь вида `tcp://сервер:порт/имяХранилища`, собирается из `Repository.Server` и `Repository.RepositoryName`), а при заданном пользователе — `/ConfigurationRepositoryN "<пользователь>"` и `/ConfigurationRepositoryP "<пароль>"`. Путь строится без задвоения слэшей, пароль передаётся только вместе с пользователем. Реализовано в [`Configuration Management/Services/OneCLauncher.cs`](Configuration Management/Services/OneCLauncher.cs) (Windows/WPF) и [`Configuration Management/Services/OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs) (Linux/Avalonia).

## [0.3.5.8] — 2026-08-27

В окно «Создание информационной базы» для клиент-серверного варианта добавлена галочка **«Блокировка фоновых заданий»**, которая сохраняется как параметр строки подключения `SCHEDJOBS=NO`.

### Добавлено

- **Галочка «Блокировка фоновых заданий»** в окне создания ИБ (обе платформы): для клиент-серверной базы включает параметр строки подключения `SCHEDJOBS=NO`, поэтому регламентные (фоновые) задания такой базы блокируются. UI — [`CreateInfobaseWindow.xaml`](Configuration Management/Views/CreateInfobaseWindow.xaml) + [`CreateInfobaseWindow.xaml.cs`](Configuration Management/Views/CreateInfobaseWindow.xaml.cs) (Windows/WPF) и [`CreateInfobaseWindow.Avalonia.cs`](Configuration Management/Views/CreateInfobaseWindow.Avalonia.cs) (Linux/Avalonia).
- **Свойство `BlockScheduledJobs`** в [`ConnectionSettings.cs`](Configuration Management/Models/ConnectionSettings.cs): отражается в `ToConnectionString()` (`;SCHEDJOBS=NO`), разбирается обратно в `ParseConnectionString()` и сохраняется в `ibases.v8i` при экспорте ([`IbasesV8iExporter.cs`](Configuration Management/Services/IbasesV8iExporter.cs)).
- **Ключ локализации** `CreateInfobase.BlockScheduledJobs` в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json).

## [0.3.5.7] — 2026-08-27

Возвращено создание **клиент-серверных** информационных баз через `CREATEINFOBASE`: команда снова собирается с параметрами СУБД (`DBMS`, `DBSrvr`, `DB`, `DBUID`/`DBPwd`) и флагом `/CreateDatabase`, а в окне «Создание информационной базы» появляется выбор типа базы и поля параметров СУБД (issue #77).

### Добавлено

- **Выбор типа создаваемой базы** в окне «Создание информационной базы» (обе платформы): сегмент «Файловая база» / «Клиент-серверная». Для клиент-серверного варианта доступны поля: сервер 1С (`Srvr`), имя базы на сервере (`Ref`), СУБД (`DBMS`), сервер СУБД (`DBSrvr`), имя базы данных (`DB`), пользователь и пароль СУБД (`DBUID`/`DBPwd`) и флажок создания базы данных на сервере СУБД (`/CreateDatabase`). Реализовано в [`CreateInfobaseWindow.xaml`](Configuration Management/Views/CreateInfobaseWindow.xaml) + [`CreateInfobaseWindow.xaml.cs`](Configuration Management/Views/CreateInfobaseWindow.xaml.cs) (Windows/WPF) и [`CreateInfobaseWindow.Avalonia.cs`](Configuration Management/Views/CreateInfobaseWindow.Avalonia.cs) (Linux/Avalonia).
- **Параметры СУБД в `CreateInfoBase`** (обе платформы): метод принимает `dbms`, `dbServer`, `dbName`, `dbUser`, `dbPassword` и `createSqlDatabase`; значения добавляются в строку подключения только когда заданы, а флаг `createSqlDatabase` добавляет `/CreateDatabase` для клиент-серверного варианта ([`OneCLauncher.Arguments.cs`](Configuration Management/Services/OneCLauncher.Arguments.cs), [`OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs)).
- **Строки локализации** для типа базы и параметров СУБД в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json).

## [0.3.5.6] — 2026-08-27

Исправлена сборка команды `CREATEINFOBASE` и связанное экранирование строк подключения (issue #77): клиент-серверное создание ИБ временно отключено, так как без параметров СУБД команда собиралась неполной и база на сервере не создавалась.

### Исправлено

- **Убран недоступный «Клиент-серверный» тип в окне «Создание информационной базы»** (обе платформы). Команда `CREATEINFOBASE` для клиент-серверного варианта собиралась неполной — только `Srvr=` и `Ref=`, без `DBMS`, `DBSrvr`, `DB`, `DBUID`/`DBPwd` и `CrSQLDB`, при этом окно запрашивало лишь «Сервер 1С» и «Имя базы», которых платформе недостаточно. Пока полноценная поддержка параметров СУБД не реализована, в окне создания доступен только файловый вариант ([`CreateInfobaseWindow.xaml`](Configuration Management/Views/CreateInfobaseWindow.xaml), [`CreateInfobaseWindow.Avalonia.cs`](Configuration Management/Views/CreateInfobaseWindow.Avalonia.cs)).
- **Каталог файловой базы больше не остаётся на диске после неудачного `CREATEINFOBASE`** (обе платформы). Раньше каталог создавался до запуска команды и при ошибке, таймауте или отказе пустой каталог оставался. Теперь запоминается каталог, созданный в этой попытке, и при неудаче он удаляется, если остался пустым ([`OneCLauncher.Arguments.cs`](Configuration Management/Services/OneCLauncher.Arguments.cs), [`OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs)).
- **Экранирование кавычек (удвоение, как в `AppendParameter`) добавлено ещё в три места записи строки подключения**: экспорт `ibases.v8i` включая `Usr`/`Pwd` ([`IbasesV8iExporter.cs`](Configuration Management/Services/IbasesV8iExporter.cs)), `ConnectionSettings.ToConnectionString()` ([`ConnectionSettings.cs`](Configuration Management/Models/ConnectionSettings.cs)) и сборщик строки подключения Linux-реализации COM-коннектора ([`OneCComConnector.Linux.cs`](Configuration Management/Services/OneCComConnector.Linux.cs)).
- **Обратный разбор строки подключения теперь разворачивает удвоение кавычки** — запись и чтение стали симметричными. Исправлены `ExtractQuoted` в [`ConnectionSettings.cs`](Configuration Management/Models/ConnectionSettings.cs) и в импортёре [`IbasesV8iImporter.cs`](Configuration Management/Services/IbasesV8iImporter.cs), а также regex разбора `Srvr=`/`Ref=` в [`OneCLauncher.Arguments.cs`](Configuration Management/Services/OneCLauncher.Arguments.cs) и [`OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs). Значения с кавычкой больше не портятся при импорте `.v8i` и в окне ввода строки подключения.

## [0.3.5.5] — 2026-08-27

Исправлена потеря клавиатурного фокуса на строке базы после редактирования её настроек (обе платформы).

### Исправлено

- **После сохранения настроек базы или группы выделение сохраняется на той же строке (и база, и группа).** При редактировании (окно «Настройки подключения» / окно группы) дерево списка пересобирается, контейнер прежней строки уничтожается, и подсветка с клавиатурным фокусом пропадали вместе с ним. Теперь после пересборки выделение восстанавливается, а фокус — только если курсор не в текстовом поле (поиск/теги), чтобы не мешать набору. **Windows/WPF**: событие `TreeRebuilt` (поднято в [`ReplaceGroupNodes()`](Configuration Management/ViewModels/MainViewModel.Tools.cs)) обрабатывает новый [`RestoreTreeKeyboardFocus()`](Configuration Management/Views/MainWindow.Tree.cs) — с учётом виртуализации раскрывает цепочку групп-предков, материализует и выбирает контейнер строки, а фокус возвращает отдельным отложенным вызовом на `ApplicationIdle`; [`EditInfobase()`](Configuration Management/ViewModels/MainViewModel.Commands.cs) фиксирует отредактированную базу как выбранную, а для групп пересборка ремапит `SelectedGroupNode` по `Group.Id` (`RebuildGroupTree` + `EditGroup`). **Linux/Avalonia** — [`RestoreTreeSelection()`](Configuration Management/Views/MainWindow.Avalonia.cs) возвращает выделение и фокус через новый метод [`ContainerForItem()`](Configuration Management/Controls/LeveledTreeView.Avalonia.cs), а `EditGroup` так же восстанавливает выбранную группу по `Group.Id`.

## [0.3.5.4] — 2026-08-27

Удалены два окна, которые собирались в сборку, но были недостижимы из интерфейса, — `GroupSettingsWindow` и `TagInputWindow`. Добавление тега унифицировано на обеих платформах: кнопка «+ тег» раскрывает поле ввода прямо в строке базы, отдельного диалога больше нет.

### Удалено

- **Окно `GroupSettingsWindow`** (обе платформы): не имело ни одной ссылки в коде — управление группами уже доступно через контекстные меню и окно настроек. Удалены `Configuration Management/Views/GroupSettingsWindow.xaml/.xaml.cs` и `Configuration Management/Views/GroupSettingsWindow.Avalonia.cs`, осиротевшие ключи локализации `GroupSettings.*` убраны из [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json).
- **Окно `TagInputWindow` и команда `AddTagCommand`** (обе платформы): остались от прежнего способа добавления тега и были недостижимы (на Windows не было ни одной привязки). Удалены `Configuration Management/Views/TagInputWindow.xaml/.xaml.cs`, `Configuration Management/Views/TagInputWindow.Avalonia.cs`, метод `AddTag` и команда `AddTagCommand`; ключи локализации `TagInput.*` убраны.

### Изменено

- **Linux/Avalonia: «+ тег» переведён на inline-ввод** ([`Configuration Management/Views/MainWindow.Avalonia.cs`](Configuration Management/Views/MainWindow.Avalonia.cs)): раньше открывался диалог `TagInputWindow`, теперь в строке базы раскрывается поле ввода (Enter — добавить, Esc — отмена, потеря фокуса — сохранить), как уже было на Windows. В Avalonia-ViewModel добавлена команда `AddTagInlineCommand`.

## [0.4.4] — 2026-08-27

Окно «Выбор родительской группы» переработано в стиле Material Design на обеих платформах (Windows/WPF и Linux/Avalonia): шапка с иконкой и подзаголовком, поле поиска с кнопкой очистки, сегментный переключатель сортировки A→Z / Z→A, карточка-дерево с цветными «чипами» иконок групп и панель действий со сводкой выбора.

### Добавлено

- **Поле поиска по имени группы** в окне выбора группы (обе платформы): фильтрует дерево, сохраняя иерархию — остаются узлы, где совпал сам узел или любой из потомков. При отсутствии результатов показывается «Ничего не найдено», доступна кнопка очистки поиска.
- **Сводка выбора внизу окна**: слева отображается полный путь выбранной группы (или «Корневая группа»).

### Изменено

- **Макет окна переработан в стиле Material Design** (обе платформы): заголовок с иконкой и подзаголовком, «outlined»-поле поиска, сегментный переключатель сортировки, скруглённая карточка-дерево с цветными «чипами» иконок и Material-кнопки «Отмена»/«Выбрать» с состояниями наведения/нажатия из темы. Windows/WPF — [`Views/GroupPickerWindow.xaml`](Views/GroupPickerWindow.xaml); Linux/Avalonia — [`Views/GroupPickerWindow.Avalonia.cs`](Views/GroupPickerWindow.Avalonia.cs).
- **Выделение строки дерева стало исключительным**: подсвечивается только ровно выбранная группа — родители больше не «засвечиваются» при выборе вложенного узла. Windows/WPF — собственный шаблон `TreeViewItem` в окне (подсветка по `IsSelected` для любой строки); Linux/Avalonia — выделение через `TreeView.SelectedItem` в одиночном режиме.
- **Иконки групп стали крупнее и выразительнее**: цветной «чип» 36×28 с иконкой 18 px (было 28×22 и 13 px), размер шрифта имени группы увеличен до 14.
- **Кнопки действий поменяли порядок**: главная кнопка «Выбрать» размещена слева, «Отмена» — справа (единообразно на обеих платформах).
- **Новые ключи локализации** `GroupPicker.Subtitle`, `GroupPicker.SearchPlaceholder`; обновлён текст `GroupPicker.Help` (поиск и сегментная сортировка) в [`Localization/Languages/ru.json`](Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Localization/Languages/en.json).

## [0.4.3] — 2026-08-27

В окне «Выбор родительской группы» (Windows/WPF) кнопка справки «?» уезжала за правый край окна,
а текст пояснения обрывался на полуслове — при размере окна по умолчанию справка была недоступна.

### Исправлено

- **Кнопка справки «?» за краем окна выбора группы (Windows/WPF)**: верхняя строка окна строилась
  горизонтальным `StackPanel`, который выдаёт детям бесконечную ширину, поэтому `TextWrapping="Wrap"`
  у текста пояснения не срабатывал — `TextBlock` разворачивался на полную естественную длину, а `HelpLink`
  следом уезжал за пределы окна. `StackPanel` заменён на `Grid` из двух колонок: текст занимает оставшуюся
  ширину (`*`) и корректно переносится, кружок «?» прижат к правому краю колонки (`Auto`)
  ([`Views/GroupPickerWindow.xaml`](Configuration Management/Views/GroupPickerWindow.xaml)).

## [0.4.2] — 2026-08-27

Исправлено экранирование значений в строке подключения команды `CREATEINFOBASE`: кавычки внутри значений теперь корректно удваиваются, и значение больше не «закрывает само себя», подмешивая в строку подключения произвольный параметр (например, имя базы вида `base";Usr="admin` уходило в `CREATEINFOBASE` как два параметра вместо одного). Исправление применено на обеих платформах.

### Исправлено
- **Экранирование значений в строке подключения `CREATEINFOBASE`** (обе платформы): добавлен помощник `EscapeConnectValue`, удваивающий кавычку внутри значения (`"` → `""`) — то же правило, что уже применяется в `OneCComConnector.AppendParameter`. Windows/WPF — [`OneCLauncher.Arguments.cs`](Configuration Management/Services/OneCLauncher.Arguments.cs); Linux/Avalonia — [`OneCLauncher.Linux.cs`](Configuration Management/Services/OneCLauncher.Linux.cs) (метод продублирован, так как csproj исключает части Windows-класса из компиляции под Linux). Экранируются значения `File=`, `Srvr=` и `Ref=`.

Авторство исправления — **[ksv47](https://github.com/ksv47)** (PR #76, ветка `ksv47/fix-createinfobase-escaping`).

## [0.4.1] — 2026-08-26

Точечные исправления окна управления учётными записями (Windows/WPF): удаление записи снова работает, добавлена кнопка «Отмена».

### Исправлено
- **Учётная запись не удалялась (Windows/WPF)**: список строится через элементы `ProfileListItem`, а свойство `SelectedProfile` оставалось типа `UserProfile` — типы не совпадали, `SelectedItem` не привязывался и выбор записи не фиксировался. `SelectedProfile` переведён на `ProfileListItem`, все операции (удаление, сохранение, «Сделать активной») теперь корректно получают выбранную запись ([`ProfilesViewModel.cs`](Configuration Management/ViewModels/ProfilesViewModel.cs)).

### Добавлено
- **Кнопка «Отмена»** в нижней части окна (слева), закрывающая окно без изменений: Windows/WPF — [`ProfilesWindow.xaml`](Configuration Management/Views/ProfilesWindow.xaml) + обработчик `OnCancel_Click` в [`ProfilesWindow.xaml.cs`](Configuration Management/Views/ProfilesWindow.xaml.cs); Linux/Avalonia — кнопка в [`ProfilesWindow.Avalonia.cs`](Configuration Management/Views/ProfilesWindow.Avalonia.cs) (текст через существующий ключ `Common.Cancel`).

## [0.4.0] — 2026-08-26

Дружелюбный интерфейс управления учётными записями (профилями): выпадающее меню активной записи, макет «список + редактор», явная индикация того, какая запись редактируется и какая активна (обе платформы).

### Добавлено
- **Выпадающее меню «Активная учётная запись»** вверху окна учётных записей: выбор профиля в списке сразу делает его активным (`SetCurrentProfile`) — активную запись видно и переключать её стало просто ([`ProfilesWindow.Avalonia.cs`](Configuration Management/Views/ProfilesWindow.Avalonia.cs), [`ProfilesWindow.xaml`](Configuration Management/Views/ProfilesWindow.xaml)).
- **Кнопка «Сделать активной»**: помечает выбранную для редактирования учётную запись активной без необходимости менять выпадающий список.
- **Бейдж «активная»** у активной учётной записи в списке — активная запись отличается от выбранной для редактирования.

### Изменено
- **Макет окна переработан на «список + редактор»**: слева список учётных записей, справа панель редактирования выбранной записи. Заголовок панели явно показывает, какая запись правится — «Редактирование записи: <имя>», а при отсутствии выбора — приглашение выбрать запись. Раньше было неочевидно, какой профиль редактируется ([`ProfilesWindow.Avalonia.cs`](Configuration Management/Views/ProfilesWindow.Avalonia.cs), [`ProfilesWindow.xaml`](Configuration Management/Views/ProfilesWindow.xaml)).
- **Окно стало масштабируемым** (`CanResize`/`ResizeMode="CanResize"`) и шире по умолчанию под новый макет.
- **WPF-ViewModel** ([`ProfilesViewModel.cs`](Configuration Management/ViewModels/ProfilesViewModel.cs)) дополнена активной записью `CurrentProfile`, заголовком редактирования `EditingTitle`, командой `ActivateCommand` и коллекцией `Accounts` для выпадающего меню. Список строится через общий элемент [`ProfileListItem.cs`](Configuration Management/ViewModels/ProfileListItem.cs) (общий для Windows/Linux).
- **Новые ключи локализации** `Profiles.ActiveAccount`, `Profiles.Active`, `Profiles.Editing`, `Profiles.EditingNone`, `Profiles.SelectToEdit`, `Profiles.Activate`, `Profiles.NoSelectionToActivate` в [`ru.json`](Configuration Management/Localization/Languages/ru.json) и [`en.json`](Configuration Management/Localization/Languages/en.json).

## [0.3.5.1] — 2026-08-26

Точечное исправление после 0.3.5.0.

### Исправлено
- **Колонка «Название» в списке баз могла схлопываться до нулевой ширины** при запуске: использование `ColumnVis` с `Source=True` и литералом `True` приводилось к `bool` ненадёжно и обнуляло ширину колонки. Исправлено явным заданием ширины по умолчанию (170) для колонки «Название» ([`MainWindow.xaml`](Configuration Management/MainWindow.xaml)) — список баз больше не «ломается» при первом показе окна.

## [0.3.5.0] — 2026-08-26

Ускорен запуск при большом числе информационных баз (Windows): главное окно появляется сразу, а тяжёлая инициализация выполняется в фоне с индикатором прогресса. Попутно — иконки в заголовках колонок списка баз, объединённая настройка колонок и значок приложения в заголовке окна (обе платформы).

### Добавлено
- **Индикатор фоновой загрузки при старте**: если список баз большой, окно показывается мгновенно, а построение дерева групп, назначение избранного и восстановление последнего выделения выполняются в фоне с полосой прогресса и подписью текущего этапа (`Main.LoadingInfobases` / `Main.LoadingFavorites` / `Main.LoadingTree`). Свойства `IsLoading` / `LoadingMessage` добавлены в [`MainViewModel.cs`](Configuration Management/ViewModels/MainViewModel.cs), оверлей — в [`MainWindow.xaml`](Configuration Management/MainWindow.xaml). По завершении автоматически восстанавливается последнее выделение и пересчитывается раскладка (`StartupInitializationCompleted`, `RestoreLastSelection` / `AlignHeaderToData` в [`MainWindow.xaml.cs`](Configuration Management/MainWindow.xaml.cs)).
- **Значок приложения в заголовке главного окна**: в качестве иконки окна используется тот же `app.ico`, что и у исполняемого файла (загрузка через `IconBitmapDecoder`, [`App.xaml.cs`](Configuration Management/App.xaml.cs)); `app.ico` получил приоритет над `tray.ico` в загрузке значка (`LoadApplicationIcon`, [`MainWindow.xaml.cs`](Configuration Management/MainWindow.xaml.cs)).

### Изменено
- **Тяжёлая инициализация вынесена после показа окна** ([`MainViewModel.cs`](Configuration Management/ViewModels/MainViewModel.cs)): назначение слотов избранного `Alt+1…9`, раскрытие ветки последнего выделения, построение дерева групп и расчёт размеров выполняются асинхронно с отдачей управления диспетчеру между этапами (`CompleteStartupInitializationAsync`) — отрисовка интерфейса больше не блокируется при большом количестве баз.
- **Кеширование размеров файловых ИБ**: вычисленный размер сохраняется в `settings.json` вместе со временем последней записи файла базы (`1Cv8.1CD`); при повторном запуске размер берётся из кеша без сканирования диска, а пересчёт выполняется только для изменившихся баз ([`FileSizeCacheEntry.cs`](Configuration Management/Models/FileSizeCacheEntry.cs), `AppSettings.FileSizeCache`, `CalculateFileBaseSizeCached`).
- **Иконки в заголовках колонок списка баз и в настройках колонок** (обе платформы): заголовки получают векторные иконки по содержимому, совпадающие с иконками в списке колонок на вкладке «Отображение» (единый источник `IconHelper.ColumnIconKey`, WPF — `PackIcon` в [`MainWindow.xaml`](Configuration Management/MainWindow.xaml), Avalonia — `ColumnHeader` в [`MainWindow.Avalonia.cs`](Configuration Management/MainWindow.Avalonia.cs)).
- **Настройки колонок объединены в единый список** (видимость + порядок) на вкладке «Отображение → Колонки» ([`SettingsWindow.xaml`](Configuration Management/SettingsWindow.xaml), [`SettingsWindow.xaml.cs`](Configuration Management/SettingsWindow.xaml.cs), [`SettingsWindow.Avalonia.cs`](Configuration Management/SettingsWindow.Avalonia.cs)): у каждой строки — флажок видимости и иконка колонки, порядок меняется кнопками «Вверх»/«Вниз»; добавлена подсказка `Settings.Columns.RowSelectHint`.
- **Компактный режим плотнее (Avalonia)**: уменьшены вертикальные отступы заголовков групп ([`UiMetrics.Avalonia.cs`](Configuration Management/Controls/UiMetrics.Avalonia.cs)).

## [0.3.5] — 2026-08-26

Ускорен запуск при большом числе информационных баз (Windows): главное окно появляется сразу, список строится в фоне с индикатором прогресса, а размеры файловых ИБ кешируются между запусками.

### Добавлено
- **Индикатор фоновой загрузки при старте**: если список баз большой, окно показывается мгновенно, а построение дерева групп и восстановление выделения выполняются в фоне с полосой прогресса и подписью текущего этапа. Свойства `IsLoading` / `LoadingMessage` добавлены в [`MainViewModel.cs`](Configuration Management/ViewModels/MainViewModel.cs), оверлей — в [`MainWindow.xaml`](Configuration Management/MainWindow.xaml). По завершении автоматически восстанавливается последнее выделение и пересчитывается раскладка (`StartupInitializationCompleted`).

### Изменено
- **Тяжёлая инициализация вынесена после показа окна** ([`MainViewModel.cs`](Configuration Management/ViewModels/MainViewModel.cs)): назначение слотов избранного `Alt+1…9`, раскрытие ветки последнего выделения, построение дерева групп и расчёт размеров выполняются асинхронно с отдачей управления диспетчеру между этапами (`CompleteStartupInitializationAsync`) — отрисовка интерфейса больше не блокируется при большом количестве баз.
- **Кеширование размеров файловых ИБ**: вычисленный размер сохраняется в `settings.json` вместе со временем последней записи файла базы (`1Cv8.1CD`); при повторном запуске размер берётся из кеша без сканирования диска, а пересчёт выполняется только для изменившихся баз ([`FileSizeCacheEntry.cs`](Configuration Management/Models/FileSizeCacheEntry.cs), `AppSettings.FileSizeCache`).

## [0.3.4] — 2026-08-26

Учётные записи (профили), окно входа, настройка колонок списка баз, переработанный выбор цвета, иконки в заголовках колонок.

### Добавлено
- **Несколько учётных записей (профилей)** с собственными настройками, списком баз и групп: данные (`settings.json`, `infobases.json`, `groups.json`) хранятся в подкаталоге профиля `profiles/<Id>/` внутри каталога данных приложения; при первом запуске старые данные мигрируют в профиль по умолчанию «Пользователь». Пароль профиля хэшируется **PBKDF2-SHA256** со случайной солью и никогда не сохраняется открытым текстом ([`ProfileService.cs`](Configuration Management/Services/ProfileService.cs), [`UserProfile.cs`](Configuration Management/Models/UserProfile.cs)).
- **Окно входа как в 1С**: при нескольких учётных записях при запуске выбирается профиль (для защищённого паролем запрашивается пароль, для незащищённого — вход сразу); при одной записи окно не показывается; при отмене выбора приложение завершается ([`LoginWindow`](Configuration Management/LoginWindow.Avalonia.cs)).
- **Управление учётными записями** в **Настройки → Настройки → «Управление учётными записями…»**: создание, переименование, удаление, установка/снятие пароля ([`ProfilesWindow`](Configuration Management/ProfilesWindow.Avalonia.cs)).
- **Размер кеша выбранной базы** отображается в правой панели главного окна (строка «Размер кеша», вычисляется асинхронно).
- **Кнопка «Скопировать техническую информацию»** во вкладке «О программе» — обезличенный отчёт о системе (версия, интерфейс, ОС, .NET, память и др.) через [`TechnicalInfoService.cs`](Configuration Management/Services/TechnicalInfoService.cs).

### Изменено
- **Иконки в заголовках колонок списка баз и в настройках колонок** (обе платформы): заголовки получают векторные иконки по содержимому, совпадающие с иконками в списке колонок на вкладке «Отображение → Колонки».
- **Настройки колонок объединены в единый список** (видимость + порядок) в **Настройки → Отображение → Колонки**; колонки «Название» и «Действия» закреплены, «Действия» стоит сразу после «Режима запуска», «Конфигурация» — в конце.
- **Окно выбора цвета полностью переработано**: градиентная область «полной палитры» с перетаскиваемым маркером (оттенок × насыщенность), бегунок яркости, расширенная палитра предустановленных цветов (до 73), работа в HSV-модели ([`ColorPickerWindow`](Configuration Management/ColorPickerWindow.Avalonia.cs)).
- **Компактный режим стал плотнее**: уменьшены шрифты имён баз и групп, высота оформления групп, расстояние между группами и ширина правой панели.
- **Отдельная команда «Доступность»** вместо фоновой автопроверки всех баз при запуске: проверка выполняется только по явной команде, недоступные базы помечаются красным крестиком, в строке состояния показывается сводка.
- **Колонка «Действия»** с кнопками «Запуск 1С:Предприятие», «Конфигуратор», «Изменить настройки», «Очистить кеш» и «Удалить»; кнопки «Добавить» и «Очистить кеш» перенесены в верхнюю панель команд, «Избранное»/«Закрепить» убраны из правой панели.
- **Кнопка-подсказка «?» перенесена в конец верхней панели**; кнопки «Текущая сессия» и «правая панель» поменялись местами в строке состояния.

### Исправлено
- Сняты устаревшие (deprecated) API Avalonia 11 — предупреждений компилятора CS0618 стало с 32 до 15 (автор — [ksv47](https://github.com/ksv47), PR #73).
- Устранён молчаливый обрыв процесса на старте Windows (код **0xC0000409**) из-за прямого COM-вызова `comcntr.dll`; доступность клиент-серверных баз проверяется безопасным путём через процесс-агент.
- Исправлена сборка решения [`Configuration Management.slnx`](Configuration Management.slnx) — удалена битая ссылка на несуществующий тестовый проект.
- Зависание Windows-версии при запуске с пустым списком баз (диалог «Импорт»/«Выход») — запрос отложен до отрисовки первого кадра.
- Зависание при запуске из-за отложенной инициализации главного окна (Avalonia) — данные снова загружаются синхронно.

## [0.3.3] — 2026-08-24

Полная локализация интерфейса (русский/английский с динамической сменой языка), доведение Linux/Avalonia-версии до полноценной работы, вынос COM-коннектора в отдельный процесс-агент, компактный режим, резервное копирование профиля.

### Добавлено
- **Многоязычность**: встроенные `ru`/`en`, загрузка внешних `*.json` без пересборки, выбор языка в настройках с мгновенным применением, автоопределение по языку ОС, перевод с откатом (текущий → английский → русский → ключ). Локализация доведена до конца: окна, контролы, сервисы, ViewModel, модели данных, тултипы, названия колонок, специальные узлы дерева групп.
- **Резервное копирование профиля** в произвольный каталог и восстановление после переустановки системы (вкладка «Резервное копирование» в настройках, [`ProfileBackupService.cs`](Configuration Management/Services/ProfileBackupService.cs), восстановление при запуске).
- **Глобальная настройка «После запуска базы или конфигуратора»** — что делать с окном после успешного запуска: ничего / свернуть в трей / закрыть программу.
- **Публичная команда «Проверить доступность всех баз»** с выводом сводки; колонка «Действия»; кнопка-помощь «?» в конце верхней панели.
- **Юнит-тесты** на протокол COM-агента и сборку строки подключения (проект `Configuration Management.Tests`, xUnit).
- Защита от зависания при повреждённых/старых конфигурационных файлах (версия схемы в `settings.json`, резервные копии `*.bak`).

### Изменено (Linux/Avalonia — автор [ksv47](https://github.com/ksv47))
- Доведение Linux-версии до сборки, запуска и работы с платформой 1С (PR #54): запуск и работа с платформой, табличный список баз с колонками, теги и дерево, сессия/контекстное меню/действия, настройки.
- Перетаскивание баз и групп мышью, действие после запуска, сохранение выбора при пересборке дерева (PR #55).
- Освобождение подписок диалогов, оформление окон сообщений темой, кисти темы через динамический ресурс, прокрутка после пересборки дерева (PR #56).
- Выпадающие меню кнопок запуска, вертикальная полоса прокрутки списка, клавиши `Home`/`End`/`PageUp`/`PageDown`, разовый запуск с параметрами и с запросом имени/пароля (PR #58).
- Каталоги шаблонов и операции со списком баз на вкладке «Базы», безопасный импорт из `ibases.v8i` (PR #59).
- Обслуживание и опасные операции на вкладке «Базы», рабочие настройки трея, упаковка в deb и AppImage (PR #60).
- Читаемый справочник ключей в параметрах запуска, кнопка тегов, меню трея, показывающее текущее состояние списка, исправление перегрузки `SetProperty` (PR #61).
- Чистка репозитория от мёртвого кода и устаревших Linux-заглушек; сборка без предупреждений.

### Изменено (Windows/WPF)
- **COM-коннектор 1С вынесен в отдельный процесс-агент** ([`ComReadHost.cs`](Configuration Management/Services/ComReadHost.cs)) — устраняет молчаливое завершение на старте с кодом 0xC0000409; пароль передаётся только по `stdin`, строка подключения экранируется, таймаут честный, добавлена поддержка платформы 8.5.
- **Настройка порядка колонок списка баз** (видимость + порядок), колонка «Действия» после «Режима запуска», колонка «Конфигурация» в конец; команды групп («Изменить группу»/«Удалить группу») размещены на уровне колонки «Действия».
- **Кнопка «Очистить кеш» оформлена как split-кнопка** и доступна даже при выбранной группе.
- Сброс пользовательской цветовой схемы при переключении светлой/тёмной темы устранён — схемы светлой и тёмной тем хранятся раздельно.
- Исправлен устаревший текст версии на вкладке «О программе»; проведён аудит непереведённых строк и подсказок.

## [0.3.2] — 2026-08-21

Завершение локализации интерфейса и моделей данных, расширение окна очистки кеша, компактный режим.

### Добавлено
- **Вынесены в локализацию оставшиеся русские строки**: диалоговые сервисы и точки входа, ViewModels (строка состояния, сообщения, тултипы), модели данных (статусы, типы подключения, режимы запуска, разрядность, группы, цвета), Avalonia-контролы и код-бихайнды окон настроек, WPF-XAML окна (Создание ИБ, Удаление ИБ, `HelpLink`, главное окно).
- **Очистка кэша 1С**: показ размера программного и пользовательского кеша, две колонки размера на базу, закреплённая шапка, изменение ширины колонок, запоминание ширин, «остатки» от удалённых баз.
- **Компактный режим интерфейса** (обе платформы); настройка языка перенесена во вкладку «Настройки».

### Исправлено
- Строка подключения `Connect` в `ibases.v8i` всегда завершается знаком «;» (важно для EDT).
- Изменение шрифта теперь применяется к группам и списку баз (Linux/Avalonia).

## [0.3.1] — 2026-08-20

Linux-порт и цветные иконки статуса баз.

### Добавлено
- **Полный порт на Linux (Avalonia 11.3)**, этапы 0–8: инфраструктура csproj, окна/контролы/конвертеры/темы, пути и хранилище ([`PlatformPaths.cs`](Configuration Management/Services/PlatformPaths.cs)), сервисы платформы 1С (`*.Linux.cs`), ярлыки/файловый менеджер/трей, сборка и упаковка (AppImage, `.deb`). Подробности — в [`LINUX_PORT.md`](Configuration Management/LINUX_PORT.md) и [`PLAN_LINUX.md`](PLAN_LINUX.md).
- **Цветные иконки статуса баз** в списке: файловая (янтарная папка), веб (синий глобус), клиент-серверная (фиолетовая сеть), недоступная (красный крест) — с подсказкой при наведении.

## [0.2.7] — 2026-08-19/20

Настройка шрифта, цветовое оформление, хранилище и раздельная авторизация, конфигуратор параметров запуска, физическое удаление ИБ.

### Добавлено
- **Настройка шрифта интерфейса** (семейство/размер/начертание) и **по элементам** (по умолчанию, список, заголовки, правая панель, статус, вкладки, кнопки, поля ввода) с мгновенным применением ко всем окнам.
- **Вкладка «Цветовое оформление»**: выбор темы, изменение отдельных цветов, создание/переименование/удаление своих тем, выгрузка и загрузка схем в JSON ([`ColorScheme.cs`](Configuration Management/Models/ColorScheme.cs)).
- **Вкладка «Хранилище»** и раздельные **авторизация в Предприятии / Конфигураторе** ([`RepositorySettings.cs`](Configuration Management/Models/RepositorySettings.cs), [`InfobaseAuthSettings.cs`](Configuration Management/Models/InfobaseAuthSettings.cs)).
- **Конфигуратор параметров запуска** переработан в «поле ввода + справочник ключей 1С».
- Разделение **«Толстый клиент» по режиму форм** (управляемые/обычные) в настройках базы и в блоке «Текущая сессия».
- Запоминание расположения/размера/монитора окна и последней выделенной строки списка.
- Анимированный индикатор выгрузки `.dt`/`.cf` со сводкой при наведении; проверка блокировки конфигуратора перед выгрузкой/тестом.

### Исправлено
- Ошибка «Платформа не найдена» при выгрузке `.dt`/`.cf` — поиск по противоположной разрядности.
- Читаемость причины ошибки при неуспешной выгрузке; использование отдельной авторизации конфигуратора при пакетной выгрузке.
- Запуск платформ из дополнительных папок; численное сравнение версий (`8.3.10` > `8.3.9`); приоритет суффикса разрядности в версии базы.
- Идентификатор базы теперь всегда назначается при добавлении (для точечной очистки кеша и экспорта).

## [0.2.6] — 2026-08-17/18

Расширенная очистка кеша, гиперссылки-подсказки, свободные горячие клавиши, группы (цвет/иконка, вложенность).

### Добавлено
- **Очистка кэша 1С разделена** на программный/пользовательский; окно «Очистка кэша 1С» с выбором типа и набора баз, «Выбрать все», поиском и чекбоксами в стиле Material Design; удаление «остатков» от удалённых баз.
- **Гиперссылки-подсказки «?»** в ключевых местах интерфейса ([`HelpLink`](Configuration Management/Controls/HelpLink.xaml)).
- **Свободное назначение горячих клавиш** (Ctrl/Shift/Alt/Win, F2–F12, Delete/Insert); горячие клавиши вкладок «Все базы/Избранное/Недавние».
- **Сортировка групп по имени** (А→Я / Я→А) с учётом вложенности; отображение цвета и иконки группы.
- Команды групп («Изменить»/«Удалить») на уровне строки группы; окно группы с вкладками «Основные/Цвет/Иконка».
- Редактируемые выпадающие списки «Сервер» и «Порт» в настройках подключения.

### Исправлено
- Выравнивание колонок данных при вложенных группах; корректный расчёт уровня вложенности.
- Перенос групп/баз перетаскиванием без «уезжания» баз в «Без группы».
- Иконки в контекстном меню трея; читаемость подписей и кнопок в обеих темах.
- Сброс фильтра тегов, очистка поиска, учёт пути/строки подключения в поиске.

## [0.2.5] — 2026-08-13/14/16

Вкладки списка, мультифильтр по тегам, «Текущая сессия», обслуживание баз, создание ИБ и шаблоны.

### Добавлено
- **Вкладки списка «Все базы / Избранное / Недавние»**; **мультифильтр по тегам** с чипами и панелью быстрого отбора.
- **Блок «Текущая сессия»** — режим клиента и разрядность для запуска без изменения настроек базы.
- **Создание ИБ** через `CREATEINFOBASE`: пустая или из шаблона (.cf/.dt), файловая/клиент-серверная; **шаблоны конфигураций** из каталогов `tmplts` (разбор `1cv8.mft`).
- **Обслуживание баз**: история запусков, выгрузка `.dt`/`.cf`, тестирование, открытие каталога, ярлык на рабочем столе, удаление отсутствующих баз, завершение процессов 1С.
- Колонка **«Размер»** для файловых ИБ; разрядность запуска (4 режима) и настройка разрядности по умолчанию; веб-клиент доступен только при веб-подключении.
- Дата-время в имени файла выгрузки и настраиваемый шаблон; дополнительные пути поиска платформ.
- Меню трея с недавними базами; поле поиска в окне очистки кеша.

### Исправлено
- Производительность: виртуализация списка, debounce поиска, без полной пересборки дерева при добавлении/удалении тегов.
- Корректное поведение переключателей тегов (теги в списке vs панель быстрого отбора).
- Читаемость тёмной темы, полосы прокрутки.

## [0.2.4] — 2026-08-13

Системный трей, сообщение при повторном запуске, авторазворот групп при поиске/фильтре, настраиваемые горячие клавиши запуска.

## [0.2.3] — 2026-08-13

Избранное `Alt+1…9` с настраиваемым порядком слотов, закрытие в системный трей, сортировка по заголовкам колонок, глобальная обработка ошибок при запуске.

## [0.2.2] — 2026-08-13

Полировка UX и производительности: надёжный drag-and-drop групп и баз, живые счётчики, виртуализация списка, полноценное окно выбора группы ([`GroupPickerWindow`](Configuration Management/GroupPickerWindow.xaml)).

## [0.2.1] — 2026-08-13

Один экземпляр приложения, панель быстрого отбора по тегам, drag-and-drop, резервные копии `ibases.v8i`, подключение к веб-серверу, режимы аутентификации, выбор значка и цвета группы, надёжный поиск платформы 1С по разрядности.

## [0.2.0] — 2026-08-12

Переход на MVVM с Dependency Injection ([`AppServices.cs`](Configuration Management/AppServices.cs), `IDialogService`), файловое логирование, асинхронная атомарная запись JSON, модульные тесты, полный редизайн интерфейса на Material Design.

## [0.1.10] — 2026-08-12

Векторные иконки и Material Design Icons (`PackIcon`), DI и сервисный слой, виртуализация дерева групп.

## [0.1.9] — 2026-08-12

Атомарное сохранение JSON, исправление прокрутки списка баз колесом мыши.

## [0.1.8] — 2026-08-11

Мастер добавления («Что добавить?»), управление группами, варианты платформы с разрядностью, запуск конкретной установленной версии платформы.

## [0.1.7] — 2026-08-11

Гибкие триггеры автоматической синхронизации с `ibases.v8i` (при запуске / по интервалу / по расписанию), улучшенный экспорт в `ibases.v8i`.

## [0.1.6] — 2026-08-11

Выравнивание заголовков колонок списка баз, сохранение настроек синхронизации общей кнопкой окна настроек.

## [0.1.5] — 2026-08-11

Экспорт и синхронизация с `ibases.v8i` (режимы: отключена/загрузка/выгрузка/двусторонняя), сохранение размеров/позиции/состояния главного окна.

## [0.1.4] — 2026-08-10

Иерархия групп («группа в группе»), табличное представление списка баз, отдельная группа «Закреплённые», копирование строки подключения, добавление тегов прямо в строке.

## [0.1.2] — 2026-08-07

Скрытие пустых групп при активном фильтре «Только избранные».

## [0.1.1] — 2026-08-06

Окно настроек приложения, секция «Установленные платформы», сервис поиска версий платформы, окно выбора версии платформы.

## [0.1.0] — 2026-08-06

Первоначальный выпуск: запуск баз в режимах «1С:Предприятие» и «Конфигуратор», выбор типа клиента и разрядности, управление списком баз, группы с цветовой маркировкой, импорт из `ibases.v8i`, экспорт/импорт списка в JSON, очистка локального кеша 1С, светлая и тёмная темы.
