# История изменений

Все заметные изменения проекта «Управление конфигурациями 1С» фиксируются в этом файле.

Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/),
версионирование — на [Semantic Versioning](https://semver.org/lang/ru/).

> Примечание: история сведена из детальных промежуточных сборок (микро-версий вида
> `0.3.x.y`) к сводным выпускам по основным версиям, чтобы отделить значимые
> возможности от точечных исправлений и регрессий предыдущих сборок.

## [0.3.7.20] — 2026-09-12

### Исправлено

- **Имя COM-коннектора из шаблона действует и без версии платформы у базы, и без перезапуска** (issue [#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)):
  - в [`Services/ComConnectorTemplate.cs`](Configuration%20Management/Services/ComConnectorTemplate.cs) версия платформы требуется только шаблону с плейсхолдерами — готовое имя без них (например `V83.COMConnector_27`) применяется и у базы, где версия платформы не указана; прежде проверка версии стояла раньше проверки плейсхолдеров, и такое имя молча игнорировалось. Шаблон, развернувшийся в пустую строку (`%V4%` при версии `8.3.27`), считается неразвёрнутым и больше не попадает в перебор пустым ProgID;
  - в [`Services/OneCComConnector.cs`](Configuration%20Management/Services/OneCComConnector.cs) значение настройки передаётся коннектору напрямую (`ApplyTemplate`), поэтому шаблон действует сразу, а не после перезапуска: он читался из настроек один раз за сессию, и значение, введённое после первого COM-чтения, не подхватывалось — отказ при этом выглядел как «COM-коннектор 1С не зарегистрирован в системе». Смена имени снимает и прежние вердикты о недоступности COM (`ResetComVerdicts`): они получены для другого набора имён, и сессионная защёлка агента иначе не пустила бы новое имя в фоновом чтении, пока пользователь не выполнит явную команду определения (она вердикты снимала и прежде). Поколение сброса снимается до подготовки запроса и передаётся в [`Services/ComReadHost.cs`](Configuration%20Management/Services/ComReadHost.cs): иначе чтение, начатое до смены имени, могло защёлкнуть недоступность COM уже по новому поколению — и новое имя снова не проверялось бы до перезапуска;
  - кэш заполняется явно и при загрузке настроек профиля ([`ViewModels/MainViewModel.cs`](Configuration%20Management/ViewModels/MainViewModel.cs)), и при смене пользователя в работающем приложении ([`ViewModels/MainViewModel.SwitchUser.cs`](Configuration%20Management/ViewModels/MainViewModel.SwitchUser.cs)) — ленивое чтение файла делало поведение зависящим от того, случилось ли COM-чтение до смены активного профиля. При смене пользователя перечитываются и поля модели представления для обеих настроек COM-чтения (имя коннектора и таймаут определения): иначе окно настроек показывало значения прежнего профиля и записывало их в новый;
  - если шаблон с плейсхолдерами развернуть нельзя, в журнал пишется отдельная запись: сам шаблон, причина (у базы нет версии платформы либо версию не удалось применить к шаблону) и список стандартных кандидатов — прежде этот случай был неотличим от отсутствия коннектора. Запись идёт по базе и подавляется в тех же случаях, что и сообщение об ошибке чтения: COM уже погашен на сессию или приложение закрывается;
  - подсказки настройки уточнены в [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`en.json`](Configuration%20Management/Localization/Languages/en.json).

### Версия

- **Версия поднята до `0.3.7.20`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.7.19] — 2026-09-12

### Добавлено

- **Определение/обновление конфигураций всех баз** ([#236](https://github.com/sivatorov/ConfigurationManagement/issues/236)): в настройках (Настройки → Базы → Список информационных баз, рядом с импортом из ibases.v8i) добавлена кнопка «Определить\обновить конфигурации всех баз», открывающая диалог `Views/DetectConfigurationsWindow` с таблицей баз и колонками «флажок», «имя базы», «текущая конфигурация», «номер релиза». В заголовке колонки-флажка показывается счётчик отмеченных элементов; доступны кнопки «Отметить все», «Снять все» и «Инвертировать». Базы, у которых уже заполнены и имя конфигурации, и номер релиза, автоматически не отмечаются — флажок проставляется, если хотя бы одно из свойств пустое. По кнопке «Определить» для выбранных баз последовательно (по одному COM-коннектору через `ConfigurationInfoService.ReadAndApply`, сериализация `ComReadHost`) определяются имя конфигурации и номер релиза; при успехе флажок снимается, при ошибке остаётся, и окно не закрывается автоматически, пока есть отмеченные строки. Реализовано для обеих платформ (WPF и Avalonia/Linux); новые ключи локализации `DetectConfigs.*` и `Settings.Bases.DetectAllConfigs*`.

## [0.3.7.18] — 2026-09-12

### Исправлено

- **Linux: автообновление перестало молча зависать у сборок, собранных на Windows** ([#225](https://github.com/sivatorov/ConfigurationManagement/issues/225)): временный bash-сценарий помощника (`apply-update-*.sh`) теперь записывается с переводом строк LF, а не CRLF, независимо от ОС сборки. В сборках, собранных на Windows (выпуски автора собираются там), сценарий раньше получал CRLF, из-за чего bash считал `\r` частью команды: первая строка `set -u` отвергалась, следующая команда не находилась, и сценарий умирал почти мгновенно, ничего не заменив, — приложение закрывалось «в тишине», а во временном каталоге оставались `ConfigurationManagement.new` и `apply-update-*.sh`. Запись идёт через [`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs) (`WriteShellScript`: замена `\r\n` → `\n`, UTF-8 без BOM) для обоих сценариев — обычного и привилегированного (через `pkexec`).

- **Linux: журнал помощника обновления `update-helper.log`** ([#225](https://github.com/sivatorov/ConfigurationManagement/issues/225)): помощник пишет отдельный журнал в `~/.config/ConfigurationManagement/update-helper.log` с шагами замены — цель, размер нового файла, свободное место в целевом каталоге, ожидание выхода приложения, результат копирования/переименования и код запуска новой версии; журнал подрезается по достижении порога 512 КБ.

- **Linux: окно хода скачивания обновления** ([#225](https://github.com/sivatorov/ConfigurationManagement/issues/225)): между согласием на обновление и вопросом о перезапуске теперь показывается окно прогресса загрузки (~50 МБ), как в Windows (`UpdateAvailableWindow`). Окно `UpdateProgressWindowAvalonia` показывает процент при известном размере или бегущую полосу, если сервер не сообщает размер, и корректно закрывается перед вопросом о перезапуске.

## [0.3.7.17] — 2026-09-12

### Исправлено

- **Linux: Esc в модальных диалогах закрывает только диалог, работает сразу после открытия и повторяемо** ([#226](https://github.com/sivatorov/ConfigurationManagement/issues/226)): на Linux/X11 окно после открытия не всегда сразу получало клавиатурный фокус, пока пользователь не кликнет по элементу, — поэтому Esc уходил в главное окно или в никуда, а кнопка «Отмена» (IsCancel) срабатывала только после получения фокуса, и диалог переставал реагировать при повторном открытии. Теперь база всех диалогов [`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs) запрашивает активацию/фокус окна при каждом открытии (`OnOpened` → `Activate()`), так что Esc обрабатывается самим диалогом (`OnKeyDown`) с первого нажатия и при повторных открытиях; проверка открытого диалога в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) (`HasOpenModalDialog()`) переведена с `IsActive` на `IsVisible`, чтобы главное окно по Esc гарантированно не уходило в трей, пока открыт диалог. Правка только в Linux/Avalonia; Windows/WPF не затрагивается.

## [0.3.7.16] — 2026-09-12

### Исправлено

- **Имя COM-коннектора из шаблона используется при подключении и логируется** (issue [#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)): в [`Services/OneCComConnector.cs`](Configuration%20Management/Services/OneCComConnector.cs) быстрый отказ по «кэшу доступности» (который проверяет только стандартные имена `V82/V83/V85.COMConnector`) больше не блокирует подключение при заданном непустом шаблоне имени — даже если версию платформы развернуть не удалось, программа доходит до перебора кандидатов (имя из шаблона + стандартный список). В журнал пишутся кандидаты («Кандидаты COM-коннекторов (в порядке перебора): …») и фактически используемый коннектор («Использованный COM-коннектор: …») — как в случае успеха, так и при ошибке. Путь со стандартными именами при пустом шаблоне не изменился.

## [0.3.7.15] — 2026-09-12

### Исправлено

- **Защита от краша при вводе большого размера шрифта в настройках** ([#235](https://github.com/sivatorov/ConfigurationManagement/issues/235)) — размер шрифта теперь ограничен диапазоном 8…72 (как в Microsoft Word) в методе `ReadFontSize()`, который используется и предпросмотром, и сохранением выбора. Ранее ввод значения больше ~35791 вызывал `ArgumentOutOfRangeException` (`FontRenderingEmSize`) и аварийное закрытие WPF-приложения.

## [0.3.7.14] — 2026-09-11

Выпуск сфокусирован на надёжности обновления на Linux (группа issues #225–#231): появление индикатора хода скачивания, журналирование работы сценария-помощника, исправление дефекта, из-за которого обновление не срабатывало из сборок, собранных на Windows. Дополнительно влиты три новых PR: правка редактируемого ComboBox в WPF, восстановление Linux updater log после merge-конфликта и стабилизация компактного режима Windows.

### Добавлено

- **Linux: индикатор хода скачивания обновления** (fix #225): добавлено окно [`Services/UpdateProgressWindow.Avalonia.cs`](Configuration%20Management/Services/UpdateProgressWindow.Avalonia.cs:21) — показывается только во время загрузки, без владельца (вне панели задач), отражает прогресс по байтам принятого файла; интеграция в [`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs:562).

### Исправлено

- **Linux: сценарий-помощник обновления пишет журнал** (fix #225): в [`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs:508) добавлен `EnsureUpdaterLogPath` — весь вывод помощника уходит в `update-helper.log` рядом с `errors.log` (цель, размер файла, свободное место, время ожидания процесса, результат копирования/смены прав/переименования, код запуска новой версии); журнал подрезается при превышении 512 КБ.

- **Журнал помощника: не умирать без журнала, честный код перезапуска, отметка таймаута** (fix #225): в [`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs:842) помощник продолжает работать, если журнал недоступен, корректно передаёт код перезапуска приложения и фиксирует факт достижения таймаута ожидания завершения основного процесса.

- **Linux: обновление не срабатывало из сборок, собранных на Windows** (fix [#225](https://github.com/sivatorov/ConfigurationManagement/issues/225)): запись сценария вынесена в [`WriteShellScript`](Configuration%20Management/Services/UpdateService.Avalonia.cs:541), которая приводит переводы строк к виду, понятному bash (`\r\n → \n`). Рабочая копия, выгруженная на Windows (autocrlf), давала сценарий с CRLF, и bash отвергал `set -u` — обновление закрывало приложение, не заменяя файл. Обе ветки замены (обычная и с повышением прав) теперь идут через этот метод.

- **Linux: восстановлен метод `EnsureUpdaterLogPath`** (PR [#233](https://github.com/sivatorov/ConfigurationManagement/pull/233)): при ручном разрешении merge-конфликта из [`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs:508) пропало объявление метода, а вызов остался — Linux-цель перестала собираться (CS0103). Объявление возвращено в исходном виде.

- **Windows: текст редактируемого списка уезжал под нижний край** (issue [#216](https://github.com/sivatorov/ConfigurationManagement/issues/216), PR [#232](https://github.com/sivatorov/ConfigurationManagement/pull/232)): в шаблоне `ModernComboBox` в [`Themes/DarkTheme.xaml`](Configuration%20Management/Themes/DarkTheme.xaml:375) и [`Themes/LightTheme.xaml`](Configuration%20Management/Themes/LightTheme.xaml) полю `PART_EditableTextBox` задан `Style="{x:Null}"` (чтобы не применялся неявный стиль `ModernTextBox` с `MinHeight=36`, растягивавший поле и смещавший текст на 7,5 точки ниже центра) и `HorizontalScrollBarVisibility=Hidden`. Текст снова центрирован, высота редактируемых списков совпадает с соседними полями.

- **Windows: компактный режим давал разную раскладку в зависимости от пути** (issue [#214](https://github.com/sivatorov/ConfigurationManagement/issues/214), PR [#234](https://github.com/sivatorov/ConfigurationManagement/pull/234)): в [`Themes/ThemeManager.cs`](Configuration%20Management/Themes/ThemeManager.cs) и [`Views/MainWindow.Columns.cs`](Configuration%20Management/Views/MainWindow.Columns.cs) устранены четыре источника расхождения раскладки — масштабирование отступов, заданных привязкой (снимавших Binding), захват метрик поддерева отдельным проходом, обход строки от корня шаблона и корректный возврат в обычный режим через `ClearValue`. Раскладка больше не зависит от того, каким путём применена компактность (запуск/тумблер/поиск).

### Версия

- **Версия поднята до `0.3.7.14`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок (0 предупреждений, 0 ошибок).

## [0.3.7.13] — 2026-09-11

Сводные исправления по шести issues (#228, #226, #221, #216, #214, #175): на Linux добавлена ручная кнопка «Проверить обновления» на вкладке «О программе»; Esc снова закрывает активный модальный диалог, а не главное окно; устранён остаточный верхний отступ правой панели — левая колонка выровнена по верхнему краю; текст в поле «Размер» шрифта корректно отцентрован по вертикали; компактный режим стабилизирован при старте/тумблере/поиске; кастомный шаблон COM-коннектора разворачивается с корректными суффиксами при неполной версии.

### Исправлено

- **Linux: добавлена кнопка «Проверить обновления» на вкладке «О программе»** ([#228](https://github.com/sivatorov/ConfigurationManagement/issues/228)): в [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs) рядом с переключателями автоматической проверки добавлена ручная кнопка, вызывающая `CheckForUpdatesManualAsync()` из [`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs) и явно сообщающая результат («актуальная версия» / «ошибка» / «доступно обновление»), как в WPF-версии. Правка только в Linux/Avalonia; Windows/WPF не затрагивается.

- **Linux: Esc закрывает активный модальный диалог, не закрывая главное окно** ([#226](https://github.com/sivatorov/ConfigurationManagement/issues/226)): обработчик клавиши Esc перенесён в базу всех диалогов [`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs) (`OnKeyDown`) — по Esc закрывается именно открытый модальный диалог, а главное окно по-прежнему уходит в трей/закрывается только когда диалогов нет. Правка только в Linux/Avalonia; Windows/WPF не затрагивается.

- **Linux: устранён остаточный верхний отступ правой панели** ([#221](https://github.com/sivatorov/ConfigurationManagement/issues/221)): в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) левая колонка выровнена по верхнему краю (`leftStack.Margin=12`), как в Windows-версии — блок запуска правой панели начинается вровень с верхней панелью/левой колонкой. Правка только в Linux/Avalonia; Windows/WPF не затрагивается.

- **Текст в поле «Размер» шрифта корректно отцентрован по вертикали** ([#216](https://github.com/sivatorov/ConfigurationManagement/issues/216)): в [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs) центровка применяется после материализации шаблона (`TemplateApplied`) к внутреннему полю `PART_EditableText` через `VerticalContentAlignment=Center`, чтобы текст редактируемого ComboBox совпадал с поведением Windows. Правка только в Linux/Avalonia; Windows/WPF не затрагивается.

- **Компактный режим больше не «разъезжается» при старте/тумблере/поиске** ([#214](https://github.com/sivatorov/ConfigurationManagement/issues/214)): в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) пересчёт выравнивания через `QueueHeaderAlign` гарантированно запускается после пересборки дерева и смены состояния — по `SearchText`, `ShowRightPanelDetails` и `ApplyCompactMode`. Компенсатор заголовка считается от актуальной ширины строк после раскладки, поэтому выравнивание не расходится при двойном переключении тумблера, клике в поиск и его очистке. Правка только в Linux/Avalonia; Windows/WPF не затрагивается.

- **Кастомный шаблон COM-коннектора корректно разворачивается с суффиксами при неполной версии** ([#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)): в [`Services/ComConnectorTemplate.cs`](Configuration%20Management/Services/ComConnectorTemplate.cs) метод `TrimTrailingSeparators` корректно обрабатывает суффиксы после версии — например `V%V12%.COMConnector` для `8.3` даёт `V83.COMConnector` вместо усечённого `V83`. Развёртывание совпадает с интерактивным предпросмотром в окне настроек и используется и при подключении, и в диагностике.

### Версия

- **Версия поднята до `0.3.7.13`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок (0 предупреждений, 0 ошибок).

## [0.3.7.12] — 2026-09-11

Исправление по PR #227 (Linux): обновление с повышением прав через `pkexec` больше не закрывает приложение до ответа PolicyKit — окно остаётся отзывчивым, а при отказе или недоступности прав приложение продолжает работать и показывает запасной диалог со ссылкой на страницу выпуска.

### Исправлено

- **Linux: приложение больше не закрывается до ответа на запрос PolicyKit** ([PR #227](https://github.com/sivatorov/ConfigurationManagement/pull/227), симптом из [#153](https://github.com/sivatorov/ConfigurationManagement/issues/153)): раньше `LaunchPrivilegedUpdater` возвращал `true` сразу после `Process.Start`, и `ShutdownNow()` вызывался, пока пользователь ещё не ввёл пароль — приложение молча закрывалось, ничего не обновив. Теперь сценарий-помощник, получив права, первым делом создаёт файл-маркер, а приложение ждёт либо этот маркер, либо ранний выход `pkexec` (отказ PolicyKit даёт код 126, сбой запуска 127), с таймаутом 3 минуты. При отказе или недоступности `pkexec` приложение остаётся работать и показывает запасной диалог с кликабельной ссылкой на страницу выпуска. Реализовано в [`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs). Правка только в Linux/Avalonia; Windows/WPF не затрагивается.
- **Linux: скачивание обновления больше не блокирует поток интерфейса**: цепочка диалогов переведена на `async` (`ShowUpdateDialogAsync`/`ShowSelfUpdateDialogAsync`/`ShowPrivilegedUpdateDialogAsync`), синхронные `DownloadNewBinaryAsync(...).GetAwaiter().GetResult()` заменены на `await`, `dpkg -S` (ждёт до 15 секунд) вынесен в `Task.Run`. Повторные проверки обновлений во время активной цепочки отсекаются через `Interlocked`.
- **Linux: бинарник пишется на диск потоком, а не буферизуется целиком в памяти**: добавлен `HttpCompletionOption.ResponseHeadersRead` (тело не читается в память полностью) с явным таймаутом чтения тела.
- **Linux: сценарий обновления, исполняемый от root, больше не лежит в общем каталоге**: вместо предсказуемого `%TEMP%/ConfigurationManagement/update` используется `Directory.CreateTempSubdirectory("cm-update-")` с правами 0700 — соседний пользователь больше не может подменить содержимое между записью и запуском от root.
- **Linux: после обновления с повышением прав приложение снова открывается**: переменные графического сеанса (`DISPLAY`, `XAUTHORITY`, `WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR`, `XDG_SESSION_TYPE`, `DBUS_SESSION_BUS_ADDRESS`) передаются помощнику и подставляются при перезапуске через `runuser` (иначе `runuser` не наследует окружение сеанса, и окно не возвращалось).
- **Linux: замена подготавливается до того, как приложению разрешено закрыться**: помощник сначала копирует новый файл рядом с целью и делает `chmod`, и только потом ставит маркер, по которому приложение выходит; при отказе PolicyKit временные файлы и опустевший каталог убираются.

### Версия

- **Версия поднята до `0.3.7.12`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок (0 предупреждений, 0 ошибок).

## [0.3.7.11] — 2026-09-10

Сводные исправления по шести issues (#226, #225, #221, #216, #214, #175): Esc на Linux закрывает только диалог, а не всё приложение; автообновление в пакетной установке deb теперь реально обновляет бинарник (через pkexec/sudo с подтверждением), а single-file — без прав заменой файла; правая панель начинается вровень с верхней панелью поиска; текст в поле «Размер» шрифта отцентрован и на Linux; компактный режим окончательно стабилизирован (компенсатор исключён из компактизации навсегда); функционал кастомного COM-коннектора (#175) дополнительно верифицирован.

### Исправлено

- **Linux: Esc больше не закрывает всё приложение, а закрывает только активный диалог** ([#226](https://github.com/sivatorov/ConfigurationManagement/issues/226)): обработка клавиши Esc в главном окне теперь проверяет, открыт ли какой-либо модальный диалог, через новый метод `HasOpenModalDialog()` в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs). Если диалог открыт — Esc закрывает его, а главное окно остаётся живым; закрытие приложения по Esc возможно только когда нет открытых модальных окон. Правка только в Linux/Avalonia; Windows/WPF не затрагивается.

- **Linux: автообновление теперь действительно обновляет программу в пакетной установке** ([#225](https://github.com/sivatorov/ConfigurationManagement/issues/225)): раньше в пакетной установке (deb в `/usr/bin`, AppImage) обновление лишь показывало диалог с ссылкой на страницу выпуска, но не заменяло исполняемый файл. Теперь в single-file сборке (без прав на запись) обновление выполняется реальной заменой файла, а для deb-установки запрашивает повышение прав через `pkexec`/`sudo` с явным подтверждением пользователя. Реализовано в [`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs); добавлены ключи локализации `Update.AdminPromptDeb`/`Update.AdminPromptGeneric` в [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json). Правка только в Linux/Avalonia; Windows/WPF не затрагивается.

- **Linux: правая панель начинается вровень с верхней панелью поиска** ([#221](https://github.com/sivatorov/ConfigurationManagement/issues/221)): панель поиска перенесена внутрь левой колонки, а правая панель выровнена по её верхнему краю — исчез «конский отступ» над блоком запуска. Правка в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs). Только Linux/Avalonia-сборка.

- **Текст в поле «Размер» шрифта отцентрован по вертикали и на Linux** ([#216](https://github.com/sivatorov/ConfigurationManagement/issues/216)): исправлен селектор центровки в [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs) — вместо `PART_EditableText` теперь используется потомок `OfType<ComboBox>().Descendant().OfType<TextBox>()`, чтобы внутреннее поле ввода редактируемого списка корректно центрировало текст. В WPF-сборке центровка уже была обеспечена шаблоном, поэтому правка внесена точечно только в Avalonia-код.

- **Компактный режим окончательно стабилизирован** ([#214](https://github.com/sivatorov/ConfigurationManagement/issues/214)): колонка-компенсатор заголовка (`HeaderOffsetColumn`) навсегда исключена из компактизации ширины в [`Themes/ThemeManager.cs`](Configuration%20Management/Themes/ThemeManager.cs), метод `QueueHeaderAlign()` переведён на стабилизирующий цикл на приоритете `ApplicationIdle` в [`Views/MainWindow.Columns.cs`](Configuration%20Management/Views/MainWindow.Columns.cs), все разрозненные пересчёты выравнивания объединены в единый механизм и добавлен пересчёт по `MainTree.SizeChanged`. Компактный режим больше не «разъезжается» при поиске, очистке поиска и изменении размера окна.

- **Имя кастомного COM-коннектора: функционал подтверждён, правки не требовались** ([#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)): диагностика фактически использованного COM-коннектора уже была реализована в `0.3.7.10` ([`Services/ComReadHost.cs`](Configuration%20Management/Services/ComReadHost.cs) и связанные сервисы). Дополнительно верифицировано без новых изменений кода: развёртывание шаблонов с суффиксами после версии (`V%V12%.COMConnector_%V3%_%V4%` → `V83.COMConnector_27`) совпадает с предпросмотром, поведение стандартного перебора при пустом шаблоне не изменено.

### Версия

- **Версия поднята до `0.3.7.11`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.7.10] — 2026-09-10

Сводные исправления по шести issues (#175, #214, #221, #222, #224, #225): пакетное автообновление на Linux показывает понятный диалог с кликабельной ссылкой, одиночный клик по трею открывает главное окно, восстановлена «Системная рамка окна», убран лишний верхний отступ правой панели, компактный режим больше не разъезжается при поиске, а диагностика кастомного COM-коннектора стала полезной.

### Исправлено

- **Linux: автообновление в пакетной установке (deb в `/usr/bin`, AppImage) показывает понятный диалог с кликабельной ссылкой** ([#225](https://github.com/sivatorov/ConfigurationManagement/issues/225)): при невозможности заменить исполняемый файл без прав приложение больше не показывает бесполезное сообщение с текстом адреса, а открывает новый диалог [`Services/ManualUpdateWindow.Avalonia.cs`](Configuration%20Management/Services/ManualUpdateWindow.Avalonia.cs) с кликабельной гиперссылкой «Открыть страницу выпуска». Если браузер не открылся — адрес копируется в буфер обмена. Диалог с помощником установки сохранён для случая, когда файл можно заменить без прав. Правка только в Linux/Avalonia ([`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs), [`Services/AvaloniaDialogService.cs`](Configuration%20Management/Services/AvaloniaDialogService.cs), новый `Services/ManualUpdateWindow.Avalonia.cs`, ключи локализации `Update.OpenReleasePage`); Windows/WPF не затрагивается.

- **Linux: одиночный клик по иконке трея показывает и фокусирует главное окно** ([#224](https://github.com/sivatorov/ConfigurationManagement/issues/224)): добавлен обработчик `Clicked` для `TrayIcon` в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) — поведение приведено к Windows (первый клик открывает окно). Правая кнопка по-прежнему открывает меню.

- **Linux: исправлено восстановление настройки «Системная рамка окна» для главного окна** ([#222](https://github.com/sivatorov/ConfigurationManagement/issues/222)): сохранённое значение применяется к главному окну после загрузки настроек при запуске (раньше главное окно строилось до загрузки настроек и всегда получало обычную рамку; доп. окна работали). Правка в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs).

- **Linux: убран «конский отступ» над блоком запуска в правой панели** ([#221](https://github.com/sivatorov/ConfigurationManagement/issues/221)): верхний отступ правой панели приведён к стандартному значению (12), как в Windows-версии (issue #167). Правка в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) (`UpdateRightPanelWidth`).

- **Компактный режим больше не «разъезжается» по горизонтали при поиске и очистке поиска** ([#214](https://github.com/sivatorov/ConfigurationManagement/issues/214)): компенсатор заголовка (`HeaderOffsetColumn`) теперь пересчитывается после полной материализации строк виртуализацией WPF. Добавлен `QueueHeaderAlign()` в [`Views/MainWindow.Columns.cs`](Configuration%20Management/Views/MainWindow.Columns.cs), вызывается при загрузке каждой строки (`OnInfobaseRowGrid_Loaded`) и при изменении текста поиска/очистке через крестик (`SearchText` в [`Views/MainWindow.Events.cs`](Configuration%20Management/Views/MainWindow.Events.cs)). Фиксы 0.3.7.6 сохранены.

- **Диагностика подключения через кастомный шаблон COM-коннектора стала полезной** ([#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)): при неуспешном подключении в журнал пишутся все перебранные ProgID в порядке перебора и строка подключения; процесс-агент ([`Services/ComReadHost.cs`](Configuration%20Management/Services/ComReadHost.cs)) сообщает родителю последний реально использованный ProgID и при отказе; диагностика в окне свойств базы показывает фактическое имя, развёрнутое по шаблону (например `V83.COMConnector_27`), даже если оно не зарегистрировано. Развёртывание шаблонов с суффиксами после версии (`V%V12%.COMConnector_%V3%_%V4%` → `V83.COMConnector_27`) проверено и совпадает с предпросмотром. Поведение стандартного перебора при пустом шаблоне не изменено.

### Версия

- **Версия поднята до `0.3.7.10`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.7.9] — 2026-09-10

Исправление по issue #153 (пакетная установка): автообновление в пакетной сборке (deb в `/usr/bin`, AppImage) больше не закрывает приложение, если заменить исполняемый файл нельзя.

### Исправлено

- **Автообновление больше не закрывает приложение в пакетной установке deb/AppImage** ([#153](https://github.com/sivatorov/ConfigurationManagement/issues/153)): в режиме автообновления цель установки теперь определяется и проверяется через `GetSelfUpdateBlocker` **до** скачивания бинарника — так же, как в интерактивном режиме с вопросом. Раньше автоматический режим в пакетной установке (deb в `/usr/bin`, AppImage) доходил до запуска помощника и закрывал приложение, но заменить файл помощник не мог: каталог не на запись. Со стороны это выглядело как самопроизвольный выход через несколько секунд после запуска. Теперь в такой ситуации вместо закрытия окна показывается информационный диалог с ручной инструкцией. Правка только в Linux/Avalonia-ветке ([`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs)); Windows/WPF не затрагивается.

### Версия

- **Версия поднята до `0.3.7.9`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.7.8] — 2026-09-10

Исправление по issue #153 «Linux — висит при запуске в виртуалке» (новый симптом): на сборке в виртуальной машине (VirtualBox/VMware, X11 без композитора, vmwgfx) окно появлялось и даже открывало настройки, но через несколько секунд приложение закрывалось само. Запуск при этом проходил успешно (журнал доходит до «Запуск завершён»), то есть это не старое зависание, а самостоятельное завершение процесса уже после показа главного окна.

### Исправлено

- **В виртуализации и на программном рендере молчаливый авто-рестарт обновления больше не «закрывает» окно** ([#153](https://github.com/sivatorov/ConfigurationManagement/issues/153)): при старте фоновая проверка обновлений могла найти более новую версию, автоматически скачать её и выполнить перезапуск с заменой бинарника — со стороны это выглядело как самозакрытие приложения через несколько секунд после успешного запуска. В окружениях, где такой авто-рестарт ненадёжен (виртуализация или программный рендер, `Services/LinuxRendering.cs`), фоновое автообновление теперь не применяется молча: вместо закрытия окна показывается стандартный диалог, и решение о замене/перезапуске остаётся за пользователем. На реальном железе с рабочим GPU поведение не меняется. Правка только в Linux/Avalonia-ветке ([`App.axaml.cs`](Configuration%20Management/App.axaml.cs)); Windows/WPF не затрагивается.

### Добавлено

- **Диагностика выхода процесса** ([#153](https://github.com/sivatorov/ConfigurationManagement/issues/153)): при штатном (managed) завершении приложение теперь пишет в консоль и `errors.log` строку «Процесс завершается (managed exit), код возврата N» ([`App.axaml.cs`](Configuration%20Management/App.axaml.cs)). Если окно закрылось, а такой записи в логе нет — процесс завершился нативным сбоем (SIGSEGV/SIGABRT) до управляемых обработчиков, что указывает на рендер/ввод окружения, а не на логику приложения.
- **Пометка закрытия обновлением**: перед перезапуском после применения обновления в `errors.log` добавляется запись «Обновление: помощник запущен, закрываем приложение для перезапуска» ([`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs)), чтобы по журналу было однозначно видно, что закрытие вызвано автообновлением.

### Версия

- **Версия поднята до `0.3.7.8`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.7.7] — 2026-09-10

Доработка по issue #175 «Имя COM-коннектора 1С»: теперь видно, какой именно COM-коннектор фактически использовался при определении свойств базы (в логах и диагностике), шаблон имени поддерживает скобочные группы с автообрезкой, а пустое поле шаблона гарантированно использует стандартный список ProgID.

### Добавлено

- **В журнал и диагностику выводится фактически использованный COM-коннектор** ([#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)): процесс-агент чтения сведений через COM ([`Services/ComReadHost.cs`](Configuration%20Management/Services/ComReadHost.cs)) теперь сообщает родителю ProgID коннектора, который реально установил соединение (а не только первый зарегистрированный кандидат). [`Services/OneCComConnector.cs`](Configuration%20Management/Services/OneCComConnector.cs) логирует успешное подключение с этим ProgID и версией платформы, а при неудаче включает его в запись об ошибке рядом со строкой подключения. Диагностика на форме (диалог прогресса и окно свойств базы) показывает тот же фактический коннектор.

### Изменено

- **Поддержка скобочных групп в шаблоне имени COM-коннектора** ([#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)): в общем [`Services/ComConnectorTemplate.cs`](Configuration%20Management/Services/ComConnectorTemplate.cs) развёртывание теперь понимает скобки вокруг сегментов. Если внутри пары скобок есть плейсхолдер версии — при его наличии остаётся содержимое без скобок, а если часть версии не указана (плейсхолдер пуст) — удаляется вся группа целиком вместе со скобками и разделителями. Например, для шаблона `V%V12%(вася_%V3%)(пупкин_%V4%)` и версии `8.3.27` получится `V83вася_27`. Единообразно и для подключения, и для интерактивного предпросмотра в окне настроек.
- **Пустой шаблон использует стандартный список ProgID**: предпросмотр при пустом поле показывает «—», а в работе `BuildProgIdCandidates` возвращает стандартные `V85/V83/V82/V81.COMConnector`, поэтому подключение не ломается из-за пустого шаблона.

### Версия

- **Версия поднята до `0.3.7.6` → `0.3.7.7`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.7.6] — 2026-09-10

Исправление по issue #214 «Компактный режим 2»: компактный режим теперь применяется корректно сразу при запуске и не «разъезжается» после открытия/сохранения свойств базы — горизонтальное выравнивание больше не требует повторного переключения тумблера.

### Исправлено

- **Компактный режим больше не «прыгает» по горизонтали после запуска и после сохранения свойств базы** ([#214](https://github.com/sivatorov/ConfigurationManagement/issues/214)): сразу после запуска при включённом компактном режиме иконки/колонки списка уезжали влево, а корректное положение восстанавливалось только двойным переключением тумблера; разъезд повторялся после открытия/сохранения свойств базы. Причина — порядок применения компактности и выравнивания заголовка до полной материализации строк дерева: виртуализация WPF достраивает контейнеры строк в проходе разметки уже после события `Loaded`, поэтому пересчёт компенсатора заголовка на `Loaded`-приоритете выполнялся до появления первой строки и колонка-компенсатор (`HeaderOffsetColumn`) оставалась в значении по умолчанию. Доработано:
  - В [`Views/MainWindow.Events.cs`](Configuration%20Management/Views/MainWindow.Events.cs) при старте добавлен финальный пересчёт выравнивания заголовка на приоритете `ApplicationIdle` — он выполняется после того, как виртуализация реализовала строки, поэтому компенсатор получает корректную ширину без ручного переключения тумблера (тот же приём, что в `RevealAndSelectAfterRebuild`).
  - В [`Views/MainWindow.xaml.cs`](Configuration%20Management/Views/MainWindow.xaml.cs) после каждой пересборки дерева (включая сохранение свойств базы) добавлен такой же финальный пересчёт на `ApplicationIdle`, чтобы новые контейнеры строк, созданные виртуализацией после события `Loaded`, тоже учитывались при выравнивании.
  - В [`Themes/ThemeManager.cs`](Configuration%20Management/Themes/ThemeManager.cs) добавлен `ForgetCompactWidth`, а в [`Views/MainWindow.Columns.cs`](Configuration%20Management/Views/MainWindow.Columns.cs) метод `AlignHeaderToData` исключает колонку-компенсатор заголовка из компактизации ширины: её ширина целиком управляется выравниванием и не должна масштабироваться коэффициентом компактности — иначе после первичного применения компакт-режима (когда компенсатор уже получил ненулевую ширину) строки снова разъезжались по горизонтали.

### Версия

- **Версия поднята до `0.3.7.5` → `0.3.7.6`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.7.5] — 2026-09-10

Исправление по issue #216: текст в поле «Размер» шрифта (Настройки → Отображение → Шрифт) теперь отцентрован по вертикали и в WPF, и в Linux/Avalonia-сборке.

### Исправлено

- **Текст в поле «Размер» шрифта отцентрован по вертикали** ([#216](https://github.com/sivatorov/ConfigurationManagement/issues/216)): на Linux/Avalonia число в редактируемом списке размера шрифта прижималось к верху/низу. Причина — `VerticalContentAlignment` у `ComboBox` задаёт только позицию внутреннего поля ввода (`PART_EditableTextBox`), а не выравнивание самого текста; выравнивание текста берётся из `VerticalContentAlignment` этого `TextBox`, который по умолчанию не центрировал. Для списка размера шрифта ([`SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)) добавлено явное центрирование текста внутреннего поля ввода (`VerticalContentAlignment = Center`) — ровно как в шаблоне WPF (`ModernComboBox`, [`LightTheme.xaml`](Configuration%20Management/Themes/LightTheme.xaml) / [`DarkTheme.xaml`](Configuration%20Management/Themes/DarkTheme.xaml)). В WPF-сборке текст уже был отцентрован шаблоном `ModernComboBox`, поэтому правка внесена точечно только в Avalonia-код и не затрагивает другие поля вкладки «Отображение».

### Версия

- **Версия поднята до `0.3.7.4` → `0.3.7.5`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.7.4] — 2026-09-10

Исправления по issue #191 и PR #218: при скрытой колонке «Действия» список больше не уезжает вправо на узком окне — компенсатор выравнивания заголовка перестал зависеть от собственного прошлого значения.

### Исправлено

- **Список уезжал вправо при скрытой колонке «Действия» на узком окне** ([#191](https://github.com/sivatorov/ConfigurationManagement/issues/191)): в методе `AlignHeaderToRows` (`MainWindow.Avalonia.cs`) звёздная колонка имени исключена из обеих сумм ведущих колонок при расчёте компенсатора выравнивания заголовка со строками (`i <= NameRowColumn` → `i < NameRowColumn`, `i <= NameHeaderColumn` → `i < NameHeaderColumn`). Раньше компенсатор зависел от собственного прошлого значения, из-за чего ширина списка росла до десятков тысяч точек, и все колонки, кроме «Названия», уезжали за правый край окна. Ведущие колонки у заголовка и строки одинаковы, а общая ширина общая, поэтому после совмещения имя занимает одинаковое место в обеих сетках. Только Linux/Avalonia-сборка ([`MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs)).

### Версия

- **Версия поднята до `0.3.7.3` → `0.3.7.4`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.7.3] — 2026-09-10

Исправления по issue #213 и PR #219: заголовки фатальных сообщений во всех обработчиках необработанных исключений выводятся через хелпер `TOr(ключ, запасной текст)`, поэтому при сбое до инициализации локализации вместо ключа (`App.Fatal.*`) показывается встроенный читаемый русский текст.

### Исправлено

- **Запасной текст вместо ключа локализации в фатальных сообщениях** ([#213](https://github.com/sivatorov/ConfigurationManagement/issues/213)): все заголовки фатальных сообщений переведены на `TOr(ключ, запасной текст)`, чтобы при сбое до инициализации локализации (когда `LocalizationManager.T` возвращает сам ключ) пользователь видел читаемый текст, а не ключ вида `App.Fatal.Interface`. Переведены обработчики `App.Fatal.Interface` («Ошибка интерфейса»), `App.Fatal.Critical` («Критическая ошибка»), `App.Fatal.BackgroundTask` («Ошибка фоновой задачи»), а также тексты `App.Fatal.InternalError` («Внутренняя ошибка:») и `App.Fatal.Title` («Управление конфигурациями 1С — ошибка»). Реализовано в обеих сборках — Windows/WPF ([`App.xaml.cs`](Configuration%20Management/App.xaml.cs)) и Linux/Avalonia ([`App.axaml.cs`](Configuration%20Management/App.axaml.cs)).

### Версия

- **Версия поднята до `0.3.7.2` → `0.3.7.3`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.7.2] — 2026-09-10

Исправления по issue #178 и PR #220: в отчёте об очистке кэша по базам теперь указывается объём освобождённого места, а написание «кэш» в подсказке про остатки от удалённых баз унифицировано.

### Исправлено

- **Объём освобождённого места в отчёте очистки кэша по базам** ([#178](https://github.com/sivatorov/ConfigurationManagement/issues/178)): при очистке кэша выбранных баз в сообщении `Main.CacheCleaned` теперь дополнительно выводится суммарный объём удалённых каталогов (`{3}`). Размер вычисляется до очистки через `OneCCacheCleaner.GetSize` и форматируется `Infobase.FormatSize`. Реализовано в обеих сборках — Windows/WPF ([`MainViewModel.Tools.cs`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs)) и Linux/Avalonia ([`MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs)); в Avalonia-сборке объём добавлен и в быструю очистку (`QuickClearCache`).
- **Унифицировано написание «кэш» в подсказке `OrphanCacheTooltip`** ([#178](https://github.com/sivatorov/ConfigurationManagement/issues/178)): в русской локализации ([`ru.json`](Configuration%20Management/Localization/Languages/ru.json)) слово «кеша» заменено на «кэша» («Каталоги кэша, не соответствующие...»).

### Версия

- **Версия поднята до `0.3.7.1` → `0.3.7.2`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.7.1] — 2026-09-09

Доработка выпуска 0.3.6.100 по issue #217: новая колонка **«№ релиза»** теперь корректно показывается в настройках отображения, в том числе у пользователей, у которых порядок колонок был сохранён до её появления.

### Исправлено

- **Колонка «№ релиза» в настройках отображения** ([#217](https://github.com/sivatorov/ConfigurationManagement/issues/217)): если в сохранённом порядке колонок ключа `ConfigurationVersion` ещё нет, он автоматически добавляется сразу после «Конфигурации» — колонка появляется в окне «Настройки → Отображение» (список видимых колонок) и её можно скрыть/показать и менять ширину. В WPF-сборке (`MainViewModel.Display.cs`) миграция порядка добавлена по образцу Avalonia-сборки; сами списки колонок в настройках уже содержали «№ релиза». Работает в обеих сборках — Windows/WPF и Linux/Avalonia.

### Версия

- **Версия поднята до `0.3.6.100` → `0.3.7.1`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.100] — 2026-09-09

Реализация доработки issue #217 «Разделение колонки „Конфигурация“»: колонка «Конфигурация» в главном окне разделена на две — **«Конфигурация»** (название конфигурации) и новая **«№ релиза»** (номер версии конфигурации); заголовок колонки «Версия платформы» переименован в **«Платформа»**. Реализовано в обеих сборках — Windows/WPF и Linux/Avalonia.

### Изменено

- **Разделена колонка «Конфигурация»** ([#217](https://github.com/sivatorov/ConfigurationManagement/issues/217)): колонка «Конфигурация» теперь содержит только название конфигурации, а номер её версии вынесен в отдельную новую колонку **«№ релиза»**. WPF: [`Views/MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml); Avalonia: [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs).
- **Переименование заголовка колонки** ([#217](https://github.com/sivatorov/ConfigurationManagement/issues/217)): заголовок «Версия платформы» переименован в **«Платформа»** для краткости и единообразия в обеих сборках (Windows/WPF и Linux/Avalonia).

### Версия

- **Версия поднята до `0.3.6.99` → `0.3.6.100`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.99] — 2026-09-09

Реализация доработки issue #175 «Имя COM-коннектора»: в окне настроек (вкладка «Настройки») под полем шаблона имени COM-коннектора появился **интерактивный предпросмотр** итоговой строки ProgID — редактируемое поле версии платформы (по умолчанию `8.3.45.6789`) и живой вывод результата, обновляющийся при изменении и шаблона, и версии. Логика разворота шаблона вынесена в общий [`Services/ComConnectorTemplate.cs`](Configuration%20Management/Services/ComConnectorTemplate.cs), который используется и при подключении через COM, и в предпросмотре, поэтому поведение всегда согласовано. Для шаблона с несколькими частями реализована **аккуратная обрезка неиспользуемых сегментов версии**: отсутствующий сегмент удаляется вместе с предшествующим разделителем (например, `V%V12%_%V3%_%V4%.ComConnector` + `8.3.27` → `V83_27.ComConnector`). Реализовано в обеих сборках — Windows/WPF и Linux/Avalonia.

### Добавлено

- **Интерактивный предпросмотр имени COM-коннектора** ([#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)): в окне настроек (вкладка «Настройки») под полем шаблона появилось редактируемое поле **версии** (по умолчанию `8.3.45.6789`) и живой **предпросмотр** итоговой строки ProgID, реагирующий на изменение и шаблона, и версии. WPF: [`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml) + [`Views/SettingsWindow.xaml.cs`](Configuration%20Management/Views/SettingsWindow.xaml.cs); Avalonia: [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs). Поле версии используется только для предпросмотра и в настройки не сохраняется.
- **Аккуратная обрезка неиспользуемых сегментов версии** ([#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)): для шаблона с несколькими частями отсутствующий сегмент удаляется вместе с предшествующим разделителем — например `V%V12%_%V3%_%V4%.ComConnector` + `8.3.27` → `V83_27.ComConnector`, а не `V83_27_.ComConnector`.
- **Общая логика разворота в [`Services/ComConnectorTemplate.cs`](Configuration%20Management/Services/ComConnectorTemplate.cs)** ([#175](https://github.com/sivatorov/ConfigurationManagement/issues/175)): разворот шаблона по плейсхолдерам `%V12%`/`%V3%`/`%V4%` с обрезкой разделителей вынесен в общий помощник, входящий в обе сборки; `OneCComConnector.ExpandTemplate` делегирует ему, поэтому поведение при подключении и в предпросмотре совпадает.

### Версия

- **Версия поднята до `0.3.6.98` → `0.3.6.99`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.98] — 2026-09-09

Исправление issue #174 «Кнопка определения свойств конфигурации» по замечаниям пользователя: в сообщении об ошибке чтения через COM больше не пустое имя базы («» вместо реального имени), таймаут определения свойств стал настраиваемым (по умолчанию 30000 мс вместо жёстких 8000 мс), а при определении свойств база с непустым именем корректно попадает в журнал.

### Исправлено

- **Пустое имя базы в сообщении об ошибке** ([#174](https://github.com/sivatorov/ConfigurationManagement/issues/174)): `BuildProbeInfobase` в [`ViewModels/ConnectionSettingsViewModel.cs`](Configuration%20Management/ViewModels/ConnectionSettingsViewModel.cs) теперь заполняет имя базы (приоритет: заданное наименование → `Ref`/`DatabaseName` → имя файла), а в [`Services/OneCComConnector.cs`](Configuration%20Management/Services/OneCComConnector.cs) добавлен защитный `DisplayName`, подставляющий осмысленное имя вместо пустой строки «» во всех сообщениях и записях журнала.
- **Настраиваемый таймаут определения свойств конфигурации** ([#174](https://github.com/sivatorov/ConfigurationManagement/issues/174)): таймаут вынесен из жёстких 8000 мс в настройку `ComDetectTimeoutMs` ([`Models/AppSettings.cs`](Configuration%20Management/Models/AppSettings.cs), по умолчанию **30000 мс**, минимум 1000). Значение резолвится в [`Services/ConfigurationInfoService.cs`](Configuration%20Management/Services/ConfigurationInfoService.cs) и доходит до агента `ComReadHost`, поэтому первое COM-подключение к клиент-серверной базе (холодный старт сервера, лицензии, создание сеанса) больше не обрывается на 8-й секунде. В журнал при ошибке дополнительно пишется применённый таймаут.
- **Настройка таймаута в окне настроек** ([#174](https://github.com/sivatorov/ConfigurationManagement/issues/174)): поле «Таймаут определения свойств конфигурации (мс)» добавлено в [`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml) и [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs); значение сохраняется через `MainViewModel.ComDetectTimeoutMs` (Windows/WPF и Linux/Avalonia).
- **Удалён мёртвый код фонового автодочитывания** ([#174](https://github.com/sivatorov/ConfigurationManagement/issues/174)): удалён неиспользуемый `RefreshConfigurationInfoAsync` и связанное поле `_configInfoFailedKeys` в [`ViewModels/MainViewModel.Tools.cs`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs) — чтение свойств выполняется только по явной команде («Обновить информацию» или кнопка «Определить»), а не при старте/импорте.

### Версия

- **Версия поднята до `0.3.6.97` → `0.3.6.98`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.97] — 2026-09-09

Исправление семи issues #216, #215, #214, #201, #174, #165, #153: центрирование текста в поле «Размер» шрифта, сортировка колонок в окне «Очистка кэша», устранение регрессии горизонтального выравнивания компактного режима, сохранение режима запуска по умолчанию при редактировании базы, логирование маскированной строки подключения при ошибке чтения через COM, дедупликация вложенных папок при импорте v8i/StartManager и диагностика этапов запуска на Linux.

### Исправлено

- **Центрирование текста в поле «Размер» шрифта** ([#216](https://github.com/sivatorov/ConfigurationManagement/issues/216)): текст в поле размера шрифта теперь отцентрован. Файлы: [`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs).
- **Сортировка колонок в окне «Очистка кэша»** ([#215](https://github.com/sivatorov/ConfigurationManagement/issues/215)): добавлена сортировка колонок в окне очистки кэша. Файлы: [`Views/CacheCleanWindow.xaml.cs`](Configuration%20Management/Views/CacheCleanWindow.xaml.cs), [`Views/CacheCleanWindow.Avalonia.cs`](Configuration%20Management/Views/CacheCleanWindow.Avalonia.cs).
- **Компактный режим: устранена регрессия горизонтального выравнивания** ([#214](https://github.com/sivatorov/ConfigurationManagement/issues/214)): исправлена регрессия горизонтального выравнивания компактного режима в [`Themes/ThemeManager.cs`](Configuration%20Management/Themes/ThemeManager.cs).
- **Пара пожеланий: сохранение режима запуска по умолчанию при редактировании базы** ([#201](https://github.com/sivatorov/ConfigurationManagement/issues/201)): исправлено сохранение режима запуска по умолчанию при редактировании базы. Файлы: [`ViewModels/MainViewModel.Commands.cs`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs), [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs).
- **Кнопка определения свойств конфигурации: логирование строки подключения** ([#174](https://github.com/sivatorov/ConfigurationManagement/issues/174)): добавлено логирование строки подключения (маскированной от паролей) при ошибке чтения через COM в [`Services/OneCComConnector.cs`](Configuration%20Management/Services/OneCComConnector.cs).
- **Дублирует папки в родном стартере** ([#165](https://github.com/sivatorov/ConfigurationManagement/issues/165)): добавлена дедупликация вложенных папок при импорте v8i/StartManager в [`Services/IbasesV8iImporter.cs`](Configuration%20Management/Services/IbasesV8iImporter.cs).
- **Linux висит при запуске: диагностика этапов запуска** ([#153](https://github.com/sivatorov/ConfigurationManagement/issues/153)): добавлена диагностика этапов запуска на Linux (`#if LINUX`) в [`App.axaml.cs`](Configuration%20Management/App.axaml.cs).

### Версия

- **Версия поднята до `0.3.6.96` → `0.3.6.97`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.96] — 2026-09-09

Исправление семи issues #214, #210, #204, #200, #188, #174, #163: компактный режим больше не «прыгает» при появлении новых строк дерева баз, удалён оставшийся мёртвый файл окна тегов без точки входа, изолированная регистрация горячих клавиш с отбраковкой значений без модификатора, своевременное обновление кнопки «Смена пользователя», защита `settings.json` от одного испорченного числа NaN/∞, понятные сообщения диалога определения свойств конфигурации и корректное разделение адреса хранилища на оба разделителя при миграции со StartManager.

### Исправлено

- **Компактный режим больше не «прыгает»** ([#214](https://github.com/sivatorov/ConfigurationManagement/issues/214)): строки дерева баз, появляющиеся после первичного применения компакт-режима (фоновая инициализация, виртуализация/прокрутка, пересборка после сохранения свойств базы), теперь тоже компактизируются. В [`Themes/ThemeManager.cs`](Configuration%20Management/Themes/ThemeManager.cs) добавлен `ApplyCompactTree`, в [`Views/MainWindow.Columns.cs`](Configuration%20Management/Views/MainWindow.Columns.cs) строки базы/группы применяют компакт-режим, а в [`Views/MainWindow.xaml.cs`](Configuration%20Management/Views/MainWindow.xaml.cs) пересчитывается выравнивание колонок после пересборки дерева.
- **Удалён оставшийся мёртвый файл окна тегов** ([#210](https://github.com/sivatorov/ConfigurationManagement/issues/210)): разметка `TagInputWindow` была удалена ранее, поэтому класс стал неработоспособен; удалён `Views/TagInputWindow.Avalonia.cs`.
- **Горячие клавиши: значения без модификатора отбраковываются, регистрация изолирована** ([#204](https://github.com/sivatorov/ConfigurationManagement/issues/204)): при чтении настроек буквы/цифры без модификатора отклоняются (`TryParseKeyGesture`, `IsAllowedWithoutModifier`); каждая привязка регистрируется изолированно, регистрация хоткеев вынесена из общего `try` — старое значение больше не отключает Alt+1…9, восстановление последней базы и выравнивание заголовка.
- **Кнопка «Смена пользователя» обновляется после изменения списка профилей** ([#200](https://github.com/sivatorov/ConfigurationManagement/issues/200)): видимость кнопки пересчитывается по событию `ProfilesChanged` в `IProfileService`/`ProfileService`; окно выбора учётной записи ([`Views/LoginWindow.xaml.cs`](Configuration%20Management/Views/LoginWindow.xaml.cs)) применяет активную тему/скин.
- **Одно испорченное число больше не роняет весь settings.json** ([#188](https://github.com/sivatorov/ConfigurationManagement/issues/188)): в [`Services/InfobaseRepository.cs`](Configuration%20Management/Services/InfobaseRepository.cs) добавлен `NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals`, чтобы значение NaN/∞ в одном поле не ломало весь файл; чтение настроек согласовано с этим.
- **Понятные сообщения при определении свойств конфигурации** ([#174](https://github.com/sivatorov/ConfigurationManagement/issues/174)): диалог прогресса и сообщение об ошибке теперь показывают конкретный КОМ-коннектор (ProgID) и версию платформы; подтверждено, что при импорте баз определение свойств запускается только по явной команде, а не автоматически.
- **Импорт из StartManager: адрес хранилища делится на сервер и имя хранилища по обоим разделителям** ([#163](https://github.com/sivatorov/ConfigurationManagement/issues/163)): `StartManagerImporter.BuildRepository` учитывает и `/`, и `\`.

### Версия

- **Версия поднята до `0.3.6.95` → `0.3.6.96`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.95] — 2026-09-09

Завершение исправления issue #153 «Linux - висит при запуске»: в окнах создания информационной базы из шаблона и определения свойств конфигурации индетерминантные индикаторы прогресса больше не держат рендер-цикл занятым на программном рендере/в виртуализации.

### Исправлено

- **Окно создания информационной базы из шаблона: статичная полоса загрузки вместо непрерывной анимации** ([#153](https://github.com/sivatorov/ConfigurationManagement/issues/153)): индетерминантный `ProgressBar` в [`Views/CreateInfobaseWindow.Avalonia.cs`](Configuration%20Management/Views/CreateInfobaseWindow.Avalonia.cs) держал постоянный рендер-цикл на программном рендере и в виртуализации, что давало высокую нагрузку CPU и «зависание» реакции на мышь (как при открытии диалога на VirtualBox/X11 без композитора). Теперь в таких окружениях (`LinuxRendering.DisableAnimations`) рисуется статичная заполненная полоса, как в главном окне.
- **Окно определения свойств конфигурации: то же самое** ([#153](https://github.com/sivatorov/ConfigurationManagement/issues/153)): индетерминантный `ProgressBar` в [`Views/DetectConfigProgressWindow.Avalonia.cs`](Configuration%20Management/Views/DetectConfigProgressWindow.Avalonia.cs) приведён к тому же поведению — при `DisableAnimations` показывается статичная полоса вместо бесконечной анимации.
- Итоговая логика сведена к единому детектору [`Services/LinuxRendering.cs`](Configuration%20Management/Services/LinuxRendering.cs) (прозрачность окна и непрерывные анимации отключаются в виртуализации, при программном рендере и на X11 без композитора), как уже сделано для главного окна и модальных диалогов.

### Версия

- **Версия поднята до `0.3.6.94` → `0.3.6.95`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.94] — 2026-09-09

Исправление четырёх issues #211, #212, #213, #163: редактируемое поле показа пароля («глаз») в окне свойств базы на Windows (WPF), корректный пересчёт размеров и переписанное сообщение в окне очистки кэша, стабилизация запуска при ошибке назначения `Owner` диалогам и запасной русский текст фатальной ошибки, а также удаление ложного охранника при импорте паролей из StartManager.

### Исправлено

- **Поле показа пароля («глаз») в окне свойств базы стало редактируемым** ([#211](https://github.com/sivatorov/ConfigurationManagement/issues/211)): в WPF-версии [`Views/ConnectionSettingsWindow.xaml`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml) поле было только для чтения; теперь оно редактируется с синхронизацией значения в `PasswordBox` и ViewModel — [`Views/ConnectionSettingsWindow.xaml.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml.cs).
- **Окно очистки кэша: снова считаются размеры, исправлено сообщение об ошибке** ([#212](https://github.com/sivatorov/ConfigurationManagement/issues/212)): `CurrentKind()` вызывался внутри `Task.Run` (чтение WPF-контролов из фонового потока → `InvalidOperationException`), из-за чего размеры не вычислялись. Значение теперь читается до `Task.Run`, добавлена поэтапная обработка ошибок, а текст сообщения переписан — ключ `CacheClean.SizeError` в [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json) больше не обвиняет занятость каталогов 1С. Файлы: [`Views/CacheCleanWindow.xaml.cs`](Configuration%20Management/Views/CacheCleanWindow.xaml.cs), [`Views/CacheCleanWindow.Avalonia.cs`](Configuration%20Management/Views/CacheCleanWindow.Avalonia.cs).
- **Стабилизирован запуск при ошибке назначения `Owner` диалогам** ([#213](https://github.com/sivatorov/ConfigurationManagement/issues/213)): `WpfDialogService` назначал `Owner` самому себе, что вызывало `ArgumentException` («Невозможно указать себя в свойстве Owner»), а фатальная ошибка выводилась ключом локализации (словари ещё пусты до `LocalizationManager.Initialize`). Теперь `Owner` назначается только если `MainWindow` существует и не совпадает с окном; фатальный текст использует встроенный русский запасной вариант (метод `TOr`). Файлы: [`Services/WpfDialogService.cs`](Configuration%20Management/Services/WpfDialogService.cs), [`App.xaml.cs`](Configuration%20Management/App.xaml.cs), [`App.axaml.cs`](Configuration%20Management/App.axaml.cs).
- **Импорт паролей из StartManager: больше не «обнуляются» пароли с байтом шифротекста > 0x7F** ([#163](https://github.com/sivatorov/ConfigurationManagement/issues/163)): удалён ложный охранник в `DecryptPassword` ([`Services/StartManagerImporter.cs`](Configuration%20Management/Services/StartManagerImporter.cs)), из-за которого любой такой пароль обнулялся (не вставал в поле / «удалялся на нет»).

### Версия

- **Версия поднята до `0.3.6.93` → `0.3.6.94`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.93] — 2026-09-08

Исправление семи issues #204–#210: ужесточение отбраковки «горячих клавиш», валидация значений с двойной кавычкой в аргументах запуска 1С, корректная «Отмена» при смене языка интерфейса на Windows, строгий разбор расписания синхронизации и защита шаблона даты/времени, подтверждение замены пользовательской темы, безопасный порядок удаления учётной записи и удаление мёртвых окон без точек входа.

### Исправлено

- **Горячие клавиши** ([#204](https://github.com/sivatorov/ConfigurationManagement/issues/204)): нажатие одной клавиши без модификатора теперь отбраковывается (допустимы только F1–F24, Delete, Insert) — в [`Controls/HotkeyBox.cs`](Configuration%20Management/Controls/HotkeyBox.cs) (WPF) и [`Controls/HotkeyBox.Avalonia.cs`](Configuration%20Management/Controls/HotkeyBox.Avalonia.cs). Исправлено имя действия «Сбросить теги» в предупреждении о конфликте — добавлен ключ локализации `Main.ClearTags`. Хоткей панели информации `Ctrl+D` (#172) добавлен в проверку дублей Linux/Avalonia.
- **Значение с двойной кавычкой больше не выпадает из аргументов запуска 1С** ([#205](https://github.com/sivatorov/ConfigurationManagement/issues/205)): при сохранении базы значения, содержащие `"` или управляющий символ, не принимаются — показывается сообщение. Новая валидация `ValidateCliArgs` в [`ViewModels/ConnectionSettingsViewModel.cs`](Configuration%20Management/ViewModels/ConnectionSettingsViewModel.cs) для WPF и Avalonia, ключи `Connection.InvalidCliChar*` в [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json).
- **Windows: «Отмена» в настройках не отменяла смену языка интерфейса** ([#206](https://github.com/sivatorov/ConfigurationManagement/issues/206)): язык теперь применяется только при «Сохранить», а не сразу при выборе; «Отмена» ничего не меняет и не записывает. Изменены [`Views/SettingsWindow.Language.cs`](Configuration%20Management/Views/SettingsWindow.Language.cs) и [`Views/SettingsWindow.xaml.cs`](Configuration%20Management/Views/SettingsWindow.xaml.cs).
- **Windows: расписание синхронизации и шаблон даты времени** ([#207](https://github.com/sivatorov/ConfigurationManagement/issues/207)): строгий разбор времени суток (`TimeSpan.TryParseExact` `hh\:mm`/`h\:mm`, меньше суток) в [`ViewModels/MainViewModel.Sync.cs`](Configuration%20Management/ViewModels/MainViewModel.Sync.cs); защита `DateTime.Now.ToString` от `FormatException` с откатом к `yyyyMMdd_HHmmss` в [`ViewModels/MainViewModel.Tools.cs`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs); валидация полей при сохранении с сообщением `Settings.Ibases.ScheduleTimeInvalid`.
- **Windows: пользовательская тема перезаписывалась без подтверждения и терялась при сбое переименования** ([#208](https://github.com/sivatorov/ConfigurationManagement/issues/208)): подтверждение замены при создании/импорте темы (`FindCustomScheme`); в `RenameCustomScheme` сначала сохраняется новый файл, старый удаляется только после успешной записи — [`ViewModels/SettingsViewModel.cs`](Configuration%20Management/ViewModels/SettingsViewModel.cs), ключ `Settings.CreateSchemeReplace`.
- **Удаление учётной записи: каталог данных удалялся до записи реестра профилей** ([#209](https://github.com/sivatorov/ConfigurationManagement/issues/209)): порядок изменён — `profiles.json` сохраняется первым, каталог данных удаляется только после успешной записи; ошибка записи больше не подавляется, [`Services/ProfileService.cs`](Configuration%20Management/Services/ProfileService.cs) возвращает `bool` + лог, профиль возвращается в список при сбое; ключ `Profiles.DeleteFailedSave`.
- **Три окна остались в сборке без точек входа** ([#210](https://github.com/sivatorov/ConfigurationManagement/issues/210)): удалены мёртвые окна `GroupSettingsWindow`, `TagInputWindow` и WPF-пара `ProfilesWindow` (`ProfilesWindow.Avalonia.cs` сохранён).

### Версия

- **Версия поднята до `0.3.6.92` → `0.3.6.93`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.92] — 2026-09-08

Реализация ISSUE #201 «Пара пожеланий»: новое действие «Свернуть» (не в трей) после запуска базы/конфигуратора, настраиваемый режим запуска базы по умолчанию («1С:Предприятие»/«Конфигуратор») при двойном клике и быстрая авторизация для Конфигуратора «как для 1С:Предприятия».

### Добавлено

- **Действие «Свернуть» (не в трей) после запуска базы/конфигуратора** ([#201](https://github.com/sivatorov/ConfigurationManagement/issues/201)): в настройку «После запуска базы или конфигуратора» добавлен режим «Свернуть» — главное окно сворачивается в панель задач, оставаясь в ней (в отличие от «Свернуть в трей»). Новое значение `Minimize` в [`Models/AfterLaunchAction.cs`](Configuration%20Management/Models/AfterLaunchAction.cs), обработка в [`Views/MainWindow.Tray.cs`](Configuration%20Management/Views/MainWindow.Tray.cs) (WPF) и [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) (Linux), пункт списка в [`Views/SettingsWindow.Display.cs`](Configuration%20Management/Views/SettingsWindow.Display.cs), [`Views/SettingsWindow.Platforms.cs`](Configuration%20Management/Views/SettingsWindow.Platforms.cs) и [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs).
- **Режим запуска базы по умолчанию** ([#201](https://github.com/sivatorov/ConfigurationManagement/issues/201)): в окне настроек базы появилось поле «Режим запуска по умолчанию» — «Автоматически (1С:Предприятие)», «1С:Предприятие» или «Конфигуратор». При двойном клике на базе она открывается в указанном режиме: WPF — [`Views/MainWindow.Events.cs`](Configuration%20Management/Views/MainWindow.Events.cs) (`OnInfobaseTree_PreviewMouseDoubleClick`), Avalonia — карточка строки в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs). Значение хранится в [`Models/Infobase.cs`](Configuration%20Management/Models/Infobase.cs) (`DefaultLaunchMode`) и переносится через [`ViewModels/ConnectionSettingsViewModel.cs`](Configuration%20Management/ViewModels/ConnectionSettingsViewModel.cs).
- **Быстрая авторизация для Конфигуратора «как для 1С:Предприятия»** ([#201](https://github.com/sivatorov/ConfigurationManagement/issues/201)): в окне настроек подключения на вкладке «Авторизация» в группе Конфигуратора появился флаг «Авторизация как для 1С:Предприятия». При включении поля авторизации Конфигуратора блокируются, а в его настройки копируются логин, пароль и режим входа «1С:Предприятия». UI: [`Views/ConnectionSettingsWindow.xaml`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml) (WPF) и [`Views/ConnectionSettingsWindow.Avalonia.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.Avalonia.cs); признак хранится в [`Models/Infobase.cs`](Configuration%20Management/Models/Infobase.cs) (`ConfiguratorUseEnterpriseAuth`) и применяется в [`ViewModels/ConnectionSettingsViewModel.cs`](Configuration%20Management/ViewModels/ConnectionSettingsViewModel.cs) (`ApplyTo`).
- **Локализация** новых строк в [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json).

### Версия

- **Версия поднята до `0.3.6.91` → `0.3.6.92`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.91] — 2026-09-08

Исправление трёх мелких issues интерфейса: согласование счётчика «Избранного» между вкладкой, назначенными хоткеями Alt+1…9 и списком горячих клавиш (#194), затемнение иконок недоступных кнопок панели команд сразу, как в контекстном меню (#197), и стабилизация отступов/компактности главного окна при открытии окна настроек — без непреднамеренных «прыжков» отступов (#199).

### Исправлено

- **Счётчик «Избранного» согласован между вкладкой, счётчиком и хоткеями** ([#194](https://github.com/sivatorov/ConfigurationManagement/issues/194)): слоты Alt+1…9 теперь соответствуют только текущим избранным базам. [`ViewModels/MainViewModel.Commands.cs`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs) и [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs) (`SyncFavoriteHotkeys`) больше не хранят ключи баз, которые есть в списке, но больше не являются избранными, — такие слоты удаляются при пересчёте. После правки базы через диалог подключения WPF-версия тоже вызывает `SyncFavoriteHotkeys` (как уже делала Avalonia), чтобы снятие/установка звезды в окне редактирования не оставляло расхождение числа баз во вкладке «Избранное», в счётчике и в списке горячих клавиш.
- **Недоступные кнопки панели затемняются сразу, как в контекстном меню** ([#197](https://github.com/sivatorov/ConfigurationManagement/issues/197)): у стиля `IconButton` появился триггер недоступности (`IsEnabled=false` → `Opacity 0.4`) — в [`Themes/LightTheme.xaml`](Configuration%20Management/Themes/LightTheme.xaml), [`Themes/DarkTheme.xaml`](Configuration%20Management/Themes/DarkTheme.xaml) и [`Themes/Controls.axaml`](Configuration%20Management/Themes/Controls.axaml) (Avalonia). Раньше кнопки панели команд гасились только по наведению, а недоступные выглядели как доступные; теперь состояние видно сразу, как в пунктах контекстного меню.
- **Отступы/компактность главного окна стабильны при открытии настроек** ([#199](https://github.com/sivatorov/ConfigurationManagement/issues/199)): в WPF-версии открытие окна настроек больше не повторно масштабирует главное окно. `SettingsWindow.Display.cs` при установке значения переключателя компактного режима подавляет событие `Checked`/`Unchecked`, и [`Views/SettingsWindow.xaml.cs`](Configuration%20Management/Views/SettingsWindow.xaml.cs) (`OnCompactMode_Toggled`) не вызывает `ApplyCompactMode` на этапе инициализации — «прыжок» отступов исчезает, компактный вид остаётся стабильным и без самопроизвольного возврата.

### Версия

- **Версия поднята до `0.3.6.90` → `0.3.6.91`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.90] — 2026-09-08

Исправление ISSUE #191 «Linux/Avalonia: при скрытой колонке "Действия" пропадают все остальные колонки списка»: при выключении показа колонки «Действия» на Linux шапка и строки строят колонки общим построителем [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) (`AddListColumns`), который всегда добавляет колонку «Действия» в сетку — нулевой ширины, когда колонка скрыта. Так число колонок заголовка и строк (включая строки групп) совпадает независимо от `ShowActionsColumn`, и держатся на нём выравнивание (`QueueHeaderAlign`/`AlignHeaderToRows`), минимальная ширина области (`UpdateListMinWidth`) и применение ширины при перетаскивании (`ApplyColumnWidth`).

### Исправлено

- **Заголовок и строки всегда согласованы по числу колонок** ([#191](https://github.com/sivatorov/ConfigurationManagement/issues/191)): заголовок, строка базы и строка группы строят колонки единым методом `AddListColumns`, который добавляет скрытую колонку «Действия» нулевой ширины (как в строке базы, issue #158). Панель кнопок «Действия» у строки группы теперь, как и у строки базы и в заголовке, не строится, когда колонка выключена: при скрытой «Действия» в нулевую колонку не попадают невидимые кнопки с обработчиками, а все остальные колонки («Версия платформы», «Режим запуска», «Сервер/База» и др.) остаются на своих местах без горизонтального ползунка. Правка только в Linux/Avalonia части.

### Версия

- **Версия поднята до `0.3.6.89` → `0.3.6.90`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.89] — 2026-09-08

Исправление ISSUE #188 «Первый запуск нового профиля: настройки не сохраняются, при закрытии две ошибки интерфейса (NaN в размерах окна)»: на первом запуске профиля, где ещё нет settings.json, размеры окна в WPF (`Width`/`Height`) ещё не заданы и равны `NaN`. Эти значения попадали в сохранение раскладки окна, и сериализация настроек падала на «.NET number values such as positive and negative infinity cannot be written as valid JSON», из-за чего настройки не сохранялись, а при закрытии появлялись два окна «Ошибка интерфейса».

### Исправлено

- **Размеры окна сохраняются фактическими, а не `NaN`** ([#188](https://github.com/sivatorov/ConfigurationManagement/issues/188)): [`Views/MainWindow.Events.cs`](Configuration%20Management/Views/MainWindow.Events.cs) при закрытии берёт `ActualWidth`/`ActualHeight` вместо `Width`/`Height`. Добавлен проверяющий метод `SaveValidatedWindowLayout`, который отбрасывает невалидную геометрию (`NaN`, бесконечность, нулевой или отрицательный размер) и в этом случае **не перезаписывает раскладку**, оставляя прежние значения — так исключается исключение сериализации JSON в [`Services/InfobaseRepository.cs`](Configuration%20Management/Services/InfobaseRepository.cs) (`SaveSettings`). Проверка применяется и к ветке развёрнутого окна (`RestoreBounds`).
- **На первом запуске нового профиля настройки корректно сохраняются**: прочие настройки пишутся через `SaveSettings` как и раньше, а окно «Ошибка интерфейса» при закрытии больше не появляется. Правка только в WPF-части, Linux/Avalonia не затрагивается.

### Версия

- **Версия поднята до `0.3.6.88` → `0.3.6.89`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.88] — 2026-09-08

Исправление ISSUE #189 «Окно выбора учётной записи показывает ключи локализации вместо подписей»: при наличии нескольких учётных записей окно авторизации на Windows строилось раньше, чем поднималась локализация, поэтому вместо подписей отображались ключи (`Auth.Title`, `Auth.SelectAccountHint`, `Auth.Login`, `Common.Cancel`). Локализация теперь инициализируется до показа окна входа (как это уже сделано для Linux/Avalonia), а язык выбранного профиля применяется после входа.

### Исправлено

- **Окно входа больше не показывает ключи локализации** ([#189](https://github.com/sivatorov/ConfigurationManagement/issues/189)): [`App.xaml.cs`](Configuration%20Management/App.xaml.cs) поднимает локализацию (`LocalizationManager.Instance.Initialize(...)`) **до** `LoginWindow.ShowLogin`, читая стартовые настройки для языка профиля, активного с прошлого запуска. Раньше на Windows окно входа строилось по пустому словарю — `{loc:Loc Auth.Title}` и другие подписи в [`Views/LoginWindow.xaml`](Configuration%20Management/Views/LoginWindow.xaml) отдавали сырые ключи. Порядок приведён в соответствие с Linux/Avalonia ([`App.axaml.cs`](Configuration%20Management/App.axaml.cs)).
- **Язык выбранного профиля применяется после входа**: после `Initialize` (которая выходит сразу, если словарь уже поднят ради окна входа) вызывается `ApplyPreferredLanguage(settings.Language)` — по тем же правилам, что и на Linux. Это гарантирует корректные подписи на старте и сохранённый язык активного профиля.

### Версия

- **Версия поднята до `0.3.6.87` → `0.3.6.88`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.87] — 2026-09-08

Реализация ISSUE #200 «Смена пользователя»: в работающее приложение добавлена отдельная кнопка «Смена пользователя» на верхней панели рядом с настройками и настраиваемая горячая клавиша. Кнопка видна только при наличии нескольких учётных записей; по ней открывается тот же диалог выбора/входа, что и при запуске, но без перезапуска программы — при успехе активный профиль переключается и данные главного окна (список баз, группы, избранное, тема, язык, горячие клавиши) перезагружаются, при отмене состояние не меняется.

### Добавлено

- **Кнопка «Смена пользователя»** ([#200](https://github.com/sivatorov/ConfigurationManagement/issues/200)): добавлена на верхнюю панель рядом с кнопкой настроек в [`Views/MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml) (Windows/WPF) и в сборку панели в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) (Linux/Avalonia). Видна только при `Profiles.Count > 1` (привязка к новому свойству `SwitchUserVisible`).
- **Смена пользователя без перезапуска** ([#200](https://github.com/sivatorov/ConfigurationManagement/issues/200)): команда `SwitchUserCommand` ([`ViewModels/MainViewModel.SwitchUser.cs`](Configuration%20Management/ViewModels/MainViewModel.SwitchUser.cs) для WPF, команда в [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs) для Avalonia) показывает тот же диалог `LoginWindow.ShowLogin`, что и при запуске; при успехе вызывает `profileService.SetCurrentProfile(...)` и перезагружает данные главного окна (`ReloadAllData` / `ReloadAfterProfileSwitch` — список баз, группы, избранное, свёрнутые группы, тему/схему, язык и горячие клавиши). Отмена или выбор текущей записи ничего не меняют.
- **Настраиваемый хоткей «Смена пользователя»** ([#200](https://github.com/sivatorov/ConfigurationManagement/issues/200)): новое поле `HotkeySwitchUser` ([`Models/AppSettings.cs`](Configuration%20Management/Models/AppSettings.cs)) и его настройка на вкладке «Клавиши» ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml) и [`Views/SettingsWindow.Hotkeys.cs`](Configuration%20Management/Views/SettingsWindow.Hotkeys.cs) — WPF; [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs) — Avalonia). Регистрация привязки — [`Views/MainWindow.Hotkeys.cs`](Configuration%20Management/Views/MainWindow.Hotkeys.cs) и [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs).
- **Локализация**: ключи `Main.SwitchUser`, `Main.SwitchUserTooltip`, `Settings.Hotkeys.SwitchUser` добавлены в [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json).

### Версия

- **Версия поднята до `0.3.6.86` → `0.3.6.87`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.86] — 2026-09-08

Исправление ISSUE #193 «Не входит после ввода пароля от учётки»: при наличии нескольких учётных записей вход через окно авторизации на Windows молча завершал приложение после успешного ввода пароля, а выбор учётки без пароля приводил к падению. Причина — окно входа создавалось первым и становилось главным окном приложения, а его закрытие при `ShutdownMode=OnLastWindowClose` (по умолчанию) гасило приложение до появления главного окна. Дополнительно вход защищён от необработанных исключений с понятным сообщением об ошибке.

### Исправлено

- **Вход перестал молча завершать приложение на Windows** ([#193](https://github.com/sivatorov/ConfigurationManagement/issues/193)): [`App.xaml.cs`](Configuration%20Management/App.xaml.cs) на время показа окна авторизации переключает `ShutdownMode` на `OnExplicitShutdown` и возвращает прежний режим после показа главного окна — как это уже было сделано для Linux/Avalonia в [`App.axaml.cs`](Configuration%20Management/App.axaml.cs). Раньше окно входа, будучи первым созданным окном (`Application.MainWindow`), при закрытии после успешного входа роняло приложение до инициализации главного окна.
- **Безопасный вход в обеих платформах** ([#193](https://github.com/sivatorov/ConfigurationManagement/issues/193)): [`Views/LoginWindow.xaml.cs`](Configuration%20Management/Views/LoginWindow.xaml.cs) и [`Views/LoginWindow.Avalonia.cs`](Configuration%20Management/Views/LoginWindow.Avalonia.cs) оборачивают проверку пароля и фиксацию входа в `try/catch` — любой сбой (например, при проверке PBKDF2-хэша) показывается понятным сообщением в окне (`Auth.LoginError`) вместо необработанного исключения, роняющего приложение.
- **Локализация нового сообщения**: ключ `Auth.LoginError` добавлен в [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json).
- Учётка без пароля обрабатывается безопасно: проверка пропускается (`HasPassword == false` → вход без запроса), а корректное хэширование/проверка PBKDF2-SHA256 не изменились ([`Services/ProfileService.cs`](Configuration%20Management/Services/ProfileService.cs)).

### Версия

- **Версия поднята до `0.3.6.85` → `0.3.6.86`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.85] — 2026-09-08

Исправление ISSUE #174 «Определить» свойства конфигурации и лишнее фоновое чтение: кнопка «Определить» в окне настроек подключения держала поток интерфейса весь таймаут (окно не отвечало ~8,3 с на недоступном сервере), после пяти отказов подряд COM-защёлка глушила повторные попытки до перезапуска, а при каждом старте/импорте фоново дочитывались свойства всех баз с пустыми полями — лишняя работа, которая на недоступных серверах «глохла» и не запоминала неудачу.

### Исправлено

- **Кнопка «Определить» больше не блокирует окно настроек** ([#174](https://github.com/sivatorov/ConfigurationManagement/issues/174)): чтение свойств конфигурации вынесено в фоновый поток (`Task.Run`), а поверх показывается модальный диалог прогресса с этапами «Создаём COM-подключение с версией платформы …» → «Подключение к базе для чтения свойств».
  - **Чтение в фоне с диалогом прогресса** ([`Views/ConnectionSettingsWindow.xaml.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml.cs), [`Views/ConnectionSettingsWindow.Avalonia.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.Avalonia.cs)): обработчик «Определить» стал асинхронным — читает через `ReadConfiguration`, поля обновляются в UI-потоке через `ApplyConfiguration`, интерфейс остаётся отзывчивым.
  - **Диалог прогресса на обеих платформах** ([`Views/DetectConfigProgressWindow.cs`](Configuration%20Management/Views/DetectConfigProgressWindow.cs), [`Views/DetectConfigProgressWindow.Avalonia.cs`](Configuration%20Management/Views/DetectConfigProgressWindow.Avalonia.cs)): индетерминантный индикатор и строка текущего этапа; этапы приходят из коннектора через обратный вызов `onStage`.
  - **Этапы из коннектора**: [`Services/ConfigurationInfoService.cs`](Configuration%20Management/Services/ConfigurationInfoService.cs), [`Services/IOneCComConnector.cs`](Configuration%20Management/Services/IOneCComConnector.cs), [`Services/OneCComConnector.cs`](Configuration%20Management/Services/OneCComConnector.cs) — передают текст этапа в диалог прогресса.
  - **Рефакторинг ViewModel** ([`ViewModels/ConnectionSettingsViewModel.cs`](Configuration%20Management/ViewModels/ConnectionSettingsViewModel.cs)): добавлены `BuildProbeInfobase`, `ReadConfiguration` (безопасно в фоне) и `ApplyConfiguration` (в UI-потоке); `DetermineConfiguration` сохранён как комбинация.
- **COM-защёлка снимается перед повторной попыткой по кнопке** ([#174](https://github.com/sivatorov/ConfigurationManagement/issues/174)): обработчик «Определить» вызывает `OneCComConnector.ResetComVerdicts()` (кэш реестра и сессионную защёлку агента), как соседняя команда меню «Обновить информацию», — после пяти отказов кнопка снова пробует, а не отвечает «другим сообщением» по устаревшей защёлке.
- **Фоновое дочитывание свойств при старте/импорте отключено** ([#174](https://github.com/sivatorov/ConfigurationManagement/issues/174)): автоматические вызовы убраны из инициализации главного окна и команды обновления списка ([`ViewModels/MainViewModel.Tools.cs`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs), [`ViewModels/MainViewModel.Commands.cs`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs)); свойства читаются только по явной команде «Обновить информацию».
  - **Неудачное чтение запоминается**: `RefreshConfigurationInfoAsync` пропускает базы, чьё чтение уже не удалось в этом сеансе (ключ по ID или строке подключения), чтобы не повторять бесполезные попытки на каждой загрузке; явная команда сбрасывает пометку и пробует снова.

### Версия

- **Версия поднята до `0.3.6.84` → `0.3.6.85`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.84] — 2026-09-08

Исправление ISSUE #165 «Дублирование папок при синхронизации с родным стартером 1С (ibases.v8i)»: повторные синхронизации со штатным стартером под Windows всё ещё порождали дубликаты вложенных папок, хотя лог показывал, что модель групп приложения остаётся дедуплицированной (3 группы → 3). Причина оказалась не в импорте/модели, а в самом файле `ibases.v8i`: одна и та же вложенная папка могла присутствовать в нём как две секции-группы в разных представлениях — с именем-листом (`Name=«Бухгалтерия», Folder=«Учёт»`) и с полным путём в заголовке секции (`Name=«Учёт\Бухгалтерия»`). Экспортёр схлопывал секции только по имени листа, поэтому обе писались в файл, и стартер 1С рисовал их как две отдельные папки. Теперь при экспорте секции-группы приводятся к единому каноническому виду (имя — лист, `Folder` — путь родителя с нативным разделителем) и дедуплицируются по полному пути папки; логирование экспорта и импорта дополнено каноническими путями групп для контроля.

### Исправлено

- **Дубликаты вложенных папок при синхронизации со стартером 1С** ([#165](https://github.com/sivatorov/ConfigurationManagement/issues/165)): лог 0.3.6.83 показывал «групп было 3, стало 3, дубликатов 0», но папки в 1С дублировались — значит, дубли жили в самом `ibases.v8i` как две секции одной папки.
  - **Канонизация и дедупликация секций-групп по полному пути** ([`Services/IbasesV8iExporter.cs`](Configuration%20Management/Services/IbasesV8iExporter.cs)): новый `NormalizeAndDedupeGroupSections` приводит каждую секцию-группу к единому виду (`Name` — имя листа, `Folder` — путь родителя через нативный разделитель) и устраняет совпадения по полному пути, которые прежний `Deduplicate` по одному имени не видел.
  - **Помощники канонизации пути**: `BuildGroupPath`, `SplitLeafAndParent`, `NormalizeGroupPath`, `NormalizeGroupName`, `SplitGroupPath` — единый разбор имени и `Folder` секции независимо от того, хранится ли в заголовке лист или полный путь.
  - **Логирование путей групп**: экспорт теперь пишет число устранённых дубликатов секций-групп и канонический список путей папок в файле; импорт — канонические пути групп после слияния ([`Services/IbasesV8iImporter.cs`](Configuration%20Management/Services/IbasesV8iImporter.cs)). По логу видно, что именно записалось в `ibases.v8i`, и требуется перепроверка пользователем на реальном наборе с дублями.

### Версия

- **Версия поднята до `0.3.6.83` → `0.3.6.84`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.83] — 2026-09-08

Исправление ISSUE #163 «Импорт из StartManager не находит данные»: импортёр `StartManagerImporter` переписан под формат реальных файлов StartManager 1.4, из-за расхождения с которыми он раньше вообще не доходил до слияния. Имя секции `v8config.smc` — это GUID информационной базы, совпадающий с `ID=` в `ibases.v8i`, а строки подключения в файлах StartManager нет вовсе — поэтому теперь секции сводятся со списком баз 1С по GUID, а подключение, имя и группа берутся из `ibases.v8i`, тогда как из StartManager переносятся только его надстройки (авторизации, хранилище, версия конфигурации, описание). Секции без пары в списке баз (следы удалённых баз) пропускаются и заново не создаются.

### Исправлено

- **Импорт из StartManager не находил данные** ([#163](https://github.com/sivatorov/ConfigurationManagement/issues/163)): многолетние правки слияния (0.3.6.68/74/76) не помогали, потому что импортёр искал ключи строки подключения (SPath/SRVS/DBName/Name/WS/URL…), которых в реальных файлах StartManager 1.4 нет.
  - **Связывание секций `v8config.smc` с `ibases.v8i` по GUID** ([`Services/StartManagerImporter.cs`](Configuration%20Management/Services/StartManagerImporter.cs)): имя секции — это идентификатор базы из списка 1С; по нему ищется пара в `ibases.v8i`. Подключение, имя и группа (раздел `Folder=`) приходят из `ibases.v8i`, надстройки StartManager (логины/пароли/хранилище/версия конфигурации/описание) накладываются поверх.
  - **Пропуск записей без пары**: секции StartManager, которым нет соответствия в списке баз (база удалена), пропускаются и не создаются заново.
  - **`settings.cnf` читается как XML**: пути платформы 1С берутся из элементов `V81AppFile…V84AppFile`, а не из отсутствующего ключа `V8AppPath`; разбор INI оставлен запасным вариантом.
  - **Определение кодировки по BOM**: StartManager 1.4 пишет `v8config.smc` в UTF-8 с BOM; ранее файл читался в Windows-1251, из-за чего ломались русские `Description` и BOM попадал в имя первой секции.
  - **Расшифровка паролей ключом `SLAVKA240601` через кодовую страницу 1251**: зарегистрирован провайдер кодовых страниц, шифротекст читается по cp1251. Пароли с кириллицей шифруются иным способом и этим алгоритмом не расшифровываются — такие пропускаются и оставляются пустыми, чтобы в поле не попал мусор.
- **Сохранена рабочая логика слияния**: `Merge`, `MergeRepository`, `MergeAuthSettings` и журнал через `IAppLogger` перенесены без изменений; теперь они достижимы, поскольку импортёр доходит до слияния.

### Версия

- **Версия поднята до `0.3.6.82` → `0.3.6.83`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.82] — 2026-09-08

Исправление ISSUE #196 «Доступность кнопки очистки кеша» и ISSUE #198 «Переход к базе в списке очистки кеша»: кнопка «Очистить кеш» верхней панели становилась недоступной, когда под курсором была папка (группа), хотя список баз в окне всё равно позволял отметить любые базы; кроме того, при открытии окна очистки для конкретной базы список не прокручивался к ней, и отмеченная галкой база могла остаться за пределами видимой области. Теперь кнопка доступна и при выделении папки — в этом случае окно открывается без предустановленных галок, и пользователь сам отмечает нужные базы; при открытии для конкретной базы список прокручивается к ней и ставит её в фокус. Правки внесены в обе реализации — Windows/WPF и Linux/Avalonia.

### Исправлено

- **Кнопка «Очистить кеш» недоступна при выделении папки** ([#196](https://github.com/sivatorov/ConfigurationManagement/issues/196)): `CanExecute` команды `ClearCacheCommand` в верхней панели требовал выделенную базу (`SelectedInfobase != null`), поэтому при выборе группы (папки) кнопка гасла. Теперь команда доступна, пока в списке есть хоть одна база; при открытии по папке окно очистки не предзаполняет ни одной галки — пользователь отмечает базы самостоятельно.
  - **Windows/WPF** ([`ViewModels/MainViewModel.cs`](Configuration%20Management/ViewModels/MainViewModel.cs)): предикат `CanExecute` заменён на `p => p is Infobase ? true : Infobases.Count > 0`.
  - **Linux/Avalonia** ([`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs)): предикат заменён аналогично; команда теперь передаёт базу строки параметром в `OpenCacheClean`, что совпадает с поведением Windows-версии.
- **Список не прокручивался к отмеченной базе** ([#198](https://github.com/sivatorov/ConfigurationManagement/issues/198)): при открытии окна очистки для конкретной базы (например, выделенной в главном окне или базы строки колонки «Действия») отмеченная галка могла находиться за пределами видимой области длинного списка. Теперь после показа окна список прокручивается к этой базе и ставит на неё фокус.
  - **Windows/WPF** ([`Views/CacheCleanWindow.xaml.cs`](Configuration%20Management/Views/CacheCleanWindow.xaml.cs)): добавлен метод `ScrollToDefault`, вызываемый в обработчике `Loaded` и прокручивающий строку через `BringIntoView` с последующим фокусом на флажке; запоминается строка базы, отмеченной по умолчанию.
  - **Linux/Avalonia** ([`Views/CacheCleanWindow.Avalonia.cs`](Configuration%20Management/Views/CacheCleanWindow.Avalonia.cs)): поле `_basesScroll` хранит `ScrollViewer`; метод `ScrollToDefault` вызывается в обработчике `Opened` и прокручивает список через `BringIntoView` с последующим фокусом на флажке.

### Версия

- **Версия поднята до `0.3.6.81` → `0.3.6.82`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.81] — 2026-09-08

Исправление ISSUE #195 «Падение при открытии окна очистки кеша» и ISSUE #202 «Ошибка при открытии окна очистки кэша»: окно «Очистка кэша 1С» падало при открытии, когда расчёт размера кеша (выполняемый в фоновом потоке после показа окна) завершался исключением — например, из-за недоступных/занятых каталогов кеша или сбоя доступа к файловой системе. Расчёт размера теперь защищён: при ошибке окно остаётся открытым, сбой пишется в журнал (`IAppLogger`/`FileAppLogger`), а пользователю показывается понятное сообщение. Правка внесена в обе реализации — Windows/WPF и Linux/Avalonia.

### Исправлено

- **Окно «Очистка кэша 1С» падало при открытии** ([#195](https://github.com/sivatorov/ConfigurationManagement/issues/195), [#202](https://github.com/sivatorov/ConfigurationManagement/issues/202)): обработчики `Loaded`/`Opened`, запускающие расчёт размера кеша, являются `async void`, и любое исключение внутри (сбой доступа к каталогам кеша, исключение из `OneCCacheCleaner`, ошибка локализации) уходило в контекст синхронизации UI и роняло приложение.
  - **Windows/WPF** ([`Views/CacheCleanWindow.xaml.cs`](Configuration%20Management/Views/CacheCleanWindow.xaml.cs)): тело `RefreshCacheSizesAsync` и `RefreshOrphanSizeAsync` обёрнуто в `try/catch`; при ошибке выполняется запись в журнал и показ сообщения, окно не закрывается.
  - **Linux/Avalonia** ([`Views/CacheCleanWindow.Avalonia.cs`](Configuration%20Management/Views/CacheCleanWindow.Avalonia.cs)): тело `async void RefreshCacheSizes` и `RefreshOrphanSize` обёрнуто в `try/catch` с теми же логом и сообщением пользователю.
- **Форматирование размера могло падать на пустых единицах измерения** (`FormatSize`): если строка `CacheClean.SizeUnits` из локализации пуста или содержит мусор, обращение к `units[0]` давало `IndexOutOfRangeException`. Теперь при пустом списке единиц используется «B».
- **Пользователю показывается сообщение об ошибке расчёта размера**: добавлен ключ `CacheClean.SizeError` в [`ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`en.json`](Configuration%20Management/Localization/Languages/en.json); сбой больше не «проглатывается» молча.

### Версия

- **Версия поднята до `0.3.6.80` → `0.3.6.81`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.80] — 2026-09-08

Исправление ISSUE #178 «Окно Очистка кэша 1С: размеры по базам всегда 0, очистка не находит кэш, остатки не удаляются». Переписано определение каталогов кэша 1С: теперь каталоги ищутся по карте `IdConnStrMap` из файла `1cv8u.pfl`, которую платформа ведёт в корне пользовательского кэша, а не по имени базы/ID (платформа называет каталог кэша собственным GUID, не связанным с базой). Правка внесена в общий сервис очистки — работает в обеих реализациях (Windows/WPF и Linux/Avalonia).

### Исправлено

- **Размеры кэша по базам показывали 0 Б** ([`Services/OneCCacheCleaner.cs`](Configuration%20Management/Services/OneCCacheCleaner.cs)): каталоги кэша определялись по имени базы/`Infobase.Id`, которое не совпадает с реальным именем каталога (собственный GUID платформы). Теперь каталоги берутся из карты `IdConnStrMap` файла `1cv8u.pfl` (`%APPDATA%\1C\1cv8` на Windows, `~/.1cv8/1C/1cv8` на Linux) как все GUID-каталоги, сопоставленные строке соединения базы. Файл читается с учётом UTF-8 BOM и CRLF, результат кэшируется до изменения файла; отсутствие файла или пустая карта обрабатываются без падения.
- **Очистка сообщала «кэш не найден», хотя каталог есть на диске**: поиск и удаление выполняются по реальным GUID-каталогам из карты, поэтому найденные для базы каталоги корректно очищаются (в том числе все варианты написания хоста — без порта/с портом).
- **«Остатки от удалённых баз» не удалялись**: `BuildProtectedNames` теперь защищает каталоги **живых** баз (GUID из карты + имена `Srvr__…__Ref__…__` + ID и имя базы), поэтому каталоги живых баз больше не попадают в «остатки», а настоящие осиротевшие каталоги (из карты, но без совпадения по строке соединения ни с одной текущей базой) удаляются.
- **Второй каталог клиент-серверной базы на Linux** (`Srvr__<сервер>__Ref__<база>__`, КэшМодулей/КэшРолей/IRSettings.xml) учитывается в расчёте размера и очистке этой базы.
- **Хост сравнивается без учёта порта с обеих сторон** (`NormalizeHost`): платформа хранит одну базу и как `host`, и как `host:port`, а порт в настройках может быть и отдельным полем, и вписанным в имя сервера.

### Версия

- **Версия поднята до `0.3.6.79` → `0.3.6.80`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.79] — 2026-09-06

Технический микровыпуск с исправлением UI: устранена вертикальная обрезка текста в поле выбора шаблона даты/времени (формат отметки даты и времени) на вкладке «Базы» окна «Настройки». Правка внесена в обе реализации — Windows/WPF и Linux/Avalonia.

### Исправлено

- **Вертикальная обрезка текста в поле шаблона даты/времени** на вкладке «Базы» окна «Настройки»: строка формата отметки даты и времени обрезалась снизу из-за фиксированной высоты поля.
  - **Windows/WPF** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml)): у элемента `ComboBox x:Name="ExportTimestampFormatComboBox"` убрана фиксированная `Height="34"` (конфликтовавшая с `MinHeight=36` стиля), вместо неё задано `MinHeight="38"`, добавлены `Padding="10,5"` и `VerticalContentAlignment="Center"` — строка формата снова помещается по вертикали.
  - **Linux/Avalonia** ([`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): `AutoCompleteBox timestampBox` получил `MinHeight = 38` (согласовано с Windows-версией), а внутренний редактируемый `TextBox` — локальный стиль с `VerticalContentAlignment = VerticalAlignment.Center` и `Padding = new Thickness(6, 4)`; стиль задан локально в `Styles` самого поля, чтобы не затронуть другие поля ввода окна.

### Версия

- **Версия поднята до `0.3.6.78` → `0.3.6.79`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок.

## [0.3.6.78] — 2026-09-06

Технический выпуск: рефакторинг и устранение предупреждений компилятора и статических анализаторов без изменения поведения приложения. Обе сборки (Windows/WPF и Linux/Avalonia) проходят без ошибок и без предупреждений.

### Рефакторинг

- **Устранены предупреждения nullable в чтении сведений о конфигурации (WPF)** ([`Services/OneCComConnector.cs`](Configuration%20Management/Services/OneCComConnector.cs)): в `ReadConfigurationInfo` после явной проверки `infobase is null` введена локальная ненулевая ссылка `ib` и убран избыточный `?.` — сняты `CS8602`/`CS8604` без изменения логики.
- **Null-селекторы дерева заменены на пустые коллекции (Linux/Avalonia)**: вместо возврата `null` из `FuncTreeDataTemplate` для листьев теперь возвращается `Array.Empty<T>()` — поведение дерева не меняется, сняты `CS8603` ([`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs), [`Views/GroupSettingsWindow.Avalonia.cs`](Configuration%20Management/Views/GroupSettingsWindow.Avalonia.cs), [`Views/GroupPickerWindow.Avalonia.cs`](Configuration%20Management/Views/GroupPickerWindow.Avalonia.cs), [`Views/PlatformVersionPickerWindow.Avalonia.cs`](Configuration%20Management/Views/PlatformVersionPickerWindow.Avalonia.cs), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)).
- **Защита от null-состояния `_vm` в обработчиках перетаскивания (Linux/Avalonia)**: добавлены ранние проверки `if (_vm is null) return;` по уже принятому в файле паттерну — сняты `CS8602` в `OnTreeDragPointerMoved`, `OnTreeDrop`, `ApplyDrop` и `IsDropAllowed` ([`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs)).
- **Избыточный `?.` при чтении имени/версии конфигурации на Linux**: возврат `new OneCConfigInfo(...)` дополнен `?? string.Empty` — сняты `CS8604` без изменения результата для валидных дампов ([`Services/OneCComConnector.Linux.cs`](Configuration%20Management/Services/OneCComConnector.Linux.cs)).

### Исправлено

- **Устаревший `ToggleButton.Checked` заменён на `IsCheckedChanged` (CS0618)**: переключатель палитры в окне настроек переведён на современное событие; повторная перерисовка при снятии отметки гасится внутренней проверкой в `SelectPalette` ([`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)).
- **CA1416: платформозависимые вызовы закрыты явными проверками ОС**:
  - освобождение COM-объектов (`Marshal.FinalReleaseComObject`) теперь выполняется только на Windows (`OperatingSystem.IsWindows()`); на других ОС `Dispose` становится no-op — поведение не меняется ([`Models/OneCComConnection.cs`](Configuration%20Management/Models/OneCComConnection.cs));
  - установка Unix-прав ярлыка `File.SetUnixFileMode` обёрнута в `OperatingSystem.IsLinux()` ([`Services/InfobaseMaintenanceService.Linux.cs`](Configuration%20Management/Services/InfobaseMaintenanceService.Linux.cs)).
- **CS8625: `TransparencyLevelHint = null` заменён на `Array.Empty<WindowTransparencyLevel>()`**: пустой список эквивалентен `null` по поведению Avalonia, но не нарушает контракт ненулевого типа ([`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs), [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs)).

### Версия

- **Версия поднята до `0.3.6.77` → `0.3.6.78`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок и без предупреждений.

## [0.3.6.77] — 2026-09-06

Технический выпуск: очистка репозитория от временных артефактов анализа и удаление мёртвых конвертеров, не входящих ни в одну сборку. Поведение приложения не изменено — только чистка и согласование версии.

### Удалено

- **Временные артефакты анализа в корне репозитория** (не отслеживаются git): `issue_status.txt`, `issues.json`, `issues_analysis.txt`, `open_issues.json`, `open_issues_summary.txt` — подтверждённый пользователем мусор, удалены.
- **Мёртвые конвертеры Avalonia** из [`Configuration Management/Converters/Avalonia/`](Configuration%20Management/Converters/Avalonia/), не используемые ни в одной привязке/разметке (`*.xaml`/`*.axaml`/`*.cs`) и не входящие ни в одну сборку:
  - `BooleanToGridLengthConverter.Avalonia.cs`
  - `ColumnVisibilityConverter.Avalonia.cs`
  - `DoubleToGridLengthConverter.Avalonia.cs`
  - `GroupOffsetConverter.Avalonia.cs`
  - `IconKeyToGeometryConverter.Avalonia.cs`
  - `InverseBoolToVisibilityConverter.Avalonia.cs`
  - `NameColumnWidthConverter.Avalonia.cs`
- Уже отсутствующие на диске неиспользуемые конвертеры `GroupFullPathConverter.Avalonia.cs`, `NullToBoolConverter.Avalonia.cs`, `MultiValueToArrayConverter.Avalonia.cs` не были включены в состав. Оставлены только используемые Avalonia-конвертеры: `GroupColorConverter`, `GroupTextColorConverter`, `LevelToThicknessConverter` и `IconHelper`.

### Версия

- **Версия поднята до `0.3.6.76` → `0.3.6.77`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
- Обе сборки — **Windows/WPF** (`dotnet build "Configuration Management/Configuration Management.csproj"`) и **Linux/Avalonia** (`-p:ForceLinux=true`) — проходят без ошибок и без новых критических предупреждений.

## [0.3.6.76] — 2026-09-06

Выпуск после влития веток `linux-fixes` (PR #179, #181, #182) с дополнительными исправлениями issues #183, #174, #163 и устранением остатков регрессии 0.3.6.75 (issues #178, #180).

### Исправлено

- **Настройка «Автоматически обновлять приложение» теперь работает (issue #183)**: при включённом автообновлении новая версия скачивается и применяется молча (без диалога); при выключенном — показывается окно предложения обновления, как раньше. Поведение согласовано на обеих платформах (Windows/WPF и Linux/Avalonia) ([`Services/UpdateService.cs`](Configuration%20Management/Services/UpdateService.cs), [`Services/UpdateService.Avalonia.cs`](Configuration%20Management/Services/UpdateService.Avalonia.cs)).
- **Двойной клик по служебному узлу «Закреплённые»/«Без группы» сохраняет состояние (issue #180, остаток)**: в `OnInfobaseTree_PreviewMouseLeftButtonDown` ([`Views/MainWindow.Events.cs`](Configuration%20Management/Views/MainWindow.Events.cs)) переключение переведено на `ToggleGroupExpandedCommand`, состояние теперь сохраняется через `SetGroupCollapsed` по внутреннему маркеру `Pinned`/`NoGroup`, без записи локального значения в контейнер — после пересборки дерева/перезапуска состояние узла не откатывается.
- **Окно «Очистка кэша 1С» на Linux/Avalonia: размеры снова отображаются (issue #178, регрессия 0.3.6.75)**: чтение типа кэша (`CurrentKind()`) вынесено из фоновой задачи `Task.Run` в поток интерфейса — устранена `InvalidOperationException "Call from invalid thread"`, из-за которой метод `RefreshCacheSizes` обрывался на третьем замере ([`Views/CacheCleanWindow.Avalonia.cs`](Configuration%20Management/Views/CacheCleanWindow.Avalonia.cs)).
- **Определение свойств конфигурации: корректный ProgID и версия (issue #174)**: кнопка «Определить» больше не отчитывается первым кандидатом списка вслепую (`V85.COMConnector`), а использует первый реально зарегистрированный COM-коннектор; имя коннектора сверяется с версией платформы базы (`ProgIdMatchesPlatform`); при несовпадении выводится внятное пояснение с советом задать шаблон имени COM-коннектора в настройках ([`Services/OneCComConnector.cs`](Configuration%20Management/Services/OneCComConnector.cs), [`Services/ConfigurationInfoService.cs`](Configuration%20Management/Services/ConfigurationInfoService.cs), [`Views/ConnectionSettingsWindow.xaml.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml.cs), [`Views/ConnectionSettingsWindow.Avalonia.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.Avalonia.cs)).
- **Импорт из StartManager: слияние пустых значений (issue #163)**: источник (StartManager) считается авторитетным — если в нём поле пустое (имя базы, хранилище, авторизация «Предприятия»/«Конфигуратора»), соответствующее значение у существующей базы очищается/сбрасывается, а не игнорируется; непустой источник по-прежнему восстанавливает удалённые вручную данные. Изменения логируются ([`Services/StartManagerImporter.cs`](Configuration%20Management/Services/StartManagerImporter.cs)).
- **Остатки кэша от удалённых баз на Linux (issue #178)** ([PR #181](https://github.com/sivatorov/ConfigurationManagement/pull/181)): каталоги `*.deleting_*` и «остатки» от удалённых баз теперь находятся и очищаются в окне «Очистка кэша 1С»; правки внесены по вердиктам трёх аудитов ([`Services/OneCCacheCleaner.cs`](Configuration%20Management/Services/OneCCacheCleaner.cs)).
- **Группа не сворачивается, если внутри неё выделена база (issue #180)** ([PR #182](https://github.com/sivatorov/ConfigurationManagement/pull/182)): раскрытие/сворачивание веток переведено с установки локального значения `IsExpanded` на контейнере на установку на модели, поэтому блокирующий приоритет над OneWay-привязкой больше не мешает сворачиванию ([`Views/MainWindow.Tree.cs`](Configuration%20Management/Views/MainWindow.Tree.cs)).
- **Окно выбора цвета под Linux/Avalonia (PR #179)**: кнопки больше не обрезаются и помещаются в окне, комментарий к ресурсам шаблона переписан описательно.

### Версия

- **Версия поднята до `0.3.6.75` → `0.3.6.76`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.75] — 2026-09-05

Выпуск с исправлениями issue #153 «Linux — висит при запуске», #178 «Окно "Очистка кэша 1С"» и #180 «Группа не сворачивается, если внутри неё выделена база».

### Исправлено

- **Зависание при запуске на Linux (issue #153)** ([`Services/LinuxRendering.cs`](Configuration%20Management/Services/LinuxRendering.cs)): свойства `OpaqueWindow` и `DisableAnimations` переведены на вычисляемые свойства, чтобы они учитывали `Virtualized`, `SoftwareRender` и `NoCompositorAssumed`. Раньше из-за порядка инициализации статических полей автоопределение VM/композитора не влияло на непрозрачность окна, и без переменных окружения окно оставалось прозрачным. Также `ReadDriGpuDrivers` теперь читает `/sys/class/drm/card*/device/uevent` (где реально лежит `DRIVER=`), а не `card*/uevent`, поэтому детектор драйверов qxl/vmwgfx/virtio_gpu/bochs/vboxvideo/qemu срабатывает.
- **Окно «Очистка кэша 1С» — остатки на Linux (issue #178)**: закрыт оставшийся пункт 3 — корень `~/.1cv8/1C/1cv8` теперь включается в скан остатков, но каталог считается остатком только если он найден в `IdConnStrMap` и его строка соединения не совпадает ни с одной базой (служебные каталоги платформы не затрагиваются). Дополнительно исправлены найденные в ходе регрессии проблемы: каталоги `*.deleting_*` теперь находятся и чистятся; размер «остатков» считается по выбранным галкам; в отчёт об очистке добавлен объём; подпись «Остатки от удалённых баз <размер>» больше не обрезается; слово унифицировано на «кэш» ([`Services/OneCCacheCleaner.cs`](Configuration%20Management/Services/OneCCacheCleaner.cs), [`Views/CacheCleanWindow.xaml`](Configuration%20Management/Views/CacheCleanWindow.xaml), [`ViewModels/MainViewModel.Tools.cs`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs)).
- **Группа не сворачивается, если внутри неё выделена база (issue #180)**: в WPF-части ([`Views/MainWindow.Tree.cs`](Configuration%20Management/Views/MainWindow.Tree.cs)) раскрытие ветки при восстановлении выделения переведено с установки локального значения `IsExpanded` на контейнере (которое блокировало сворачивание из-за приоритета над OneWay-привязкой) на установку на модели. Также ([`Views/MainWindow.Events.cs`](Configuration%20Management/Views/MainWindow.Events.cs)) двойной клик по служебным узлам «Закреплённые»/«Без группы» теперь корректно переключает состояние модели, не оставляя локального значения в контейнере.

### Версия

- **Версия поднята до `0.3.6.74` → `0.3.6.75`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.74] — 2026-09-05

Выпуск с исправлениями стабильности на Linux и корректной миграции данных при работе с родным стартером.

### Исправлено

- **Зависание при запуске на VirtualBox/KDE NEON (X11) (issue #153)**: усилен детектор виртуализации и программного рендера, добавлена диагностика окружения при старте. Программа корректно определяет графическое окружение и не зависает при запуске в виртуализированных средах.
- **Пропадание и невозможность перетаскивания безрамочных модальных окон (issue #177)**: ошибки построения окна теперь логируются, окно больше не пропадает, а его появление и перетаскивание работают корректно на обеих платформах.
- **Миграция со StartManager (issue #163)**: удалённые вручную авторизации (Хранилище/Предприятие/Конфигуратор) теперь восстанавливаются из родного стартера при миграции; добавлен лог слияния для контроля процесса.
- **Синхронизация с родным стартером под Windows (issue #165)**: добавлена диагностика количества групп и устранённых дубликатов, чтобы расхождения при синхронизации были видны и объяснимы.
- **Определение свойств конфигурации (issue #174)**: при ошибке выводится её деталь, использованный ProgID и версия платформы (работает на обеих платформах — Windows/WPF и Linux/Avalonia).

### Добавлено

- **Подсказка для шаблона имени COM-коннектора (issue #175)**: в поле «Имя COM-коннектора 1С» добавлен Watermark с примером шаблона, чтобы пользователю было понятно, как его заполнить.

### Версия

- **Версия поднята до `0.3.6.73` → `0.3.6.74`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.73] — 2026-09-05

Выпуск с исправлением issue #178 «Окно "Очистка кэша 1С": размеры по базам всегда 0, очистка не находит кэш, остатки не удаляются». Платформа 1С называет каталог кеша собственным GUID, а не именем/ID базы, поэтому прежний поиск по ID и имени не находил кеш: в колонках «Программный» и «Пользовательский» у всех баз был 0 Б, очистка сообщала «кеш не найден», а каталоги живых баз попадали в «остатки от удалённых баз». Теперь база сопоставляется с каталогом кеша через карту `IdConnStrMap` из файла `1cv8u.pfl` (строка соединения → GUID каталога), с учётом нескольких записей на базу и вариантов хоста с портом и без. Исправлено и само удаление: перед рекурсивным удалением снимается атрибут `ReadOnly` (платформа кладёт в кеш такие файлы, из-за чего удаление падало и оставляло каталоги `.deleting_*`), а счётчики удалённого считают только реально удалённые каталоги. Верхние суммы размера теперь складываются по каталогам баз, а не по корню кеша целиком (без служебных файлов платформы). Работает на обеих платформах (Windows/WPF и Linux/Avalonia).

### Исправлено

- **Сопоставление базы с каталогом кеша через `IdConnStrMap` (issue #178)**: методы `LoadIdConnStrMap`, `GetGuidCacheNames` и `MatchesBase` в [`Services/OneCCacheCleaner.cs`](Configuration%20Management/Services/OneCCacheCleaner.cs) читают файл `1cv8u.pfl` из корня пользовательского кеша (`%APPDATA%\1C\1cv8` на Windows, `~/.1cv8/1C/1cv8` на Linux) и разбирают пары «строка соединения → GUID». Хост сравнивается без учёта порта, учитываются несколько записей на одну базу; результат кэшируется до изменения файла.
- **Каталог клиент-серверной базы `Srvr__…__Ref__…__` (issue #178)**: для клиент-серверных баз дополнительно учитывается каталог вида `Srvr__<сервер>__Ref__<база>__` (варианты для сервера с портом и без). Имена, полученные из карты и из клиент-серверного каталога, добавляются в «защищённые» (`BuildProtectedNames`), поэтому каталоги живых баз больше не попадают в «остатки от удалённых баз».
- **Размеры по каталогам баз (issue #178)**: `GetSize(kind, infobases)` суммирует только каталоги, принадлежащие текущим базам, а не весь корень кеша со служебными файлами платформы (`helpsynt.dat`, логи и т. п.). Окно очистки ([`Views/CacheCleanWindow.xaml.cs`](Configuration%20Management/Views/CacheCleanWindow.xaml.cs) и [`Views/CacheCleanWindow.Avalonia.cs`](Configuration%20Management/Views/CacheCleanWindow.Avalonia.cs)) использует новый перегруженный метод для верхних сумм; размеры по отдельным базам считаются по найденным каталогам.
- **Удаление с учётом атрибута `ReadOnly` (issue #178)**: `TryDeleteDirectory` рекурсивно снимает атрибут `ReadOnly` со всех файлов и каталогов перед удалением (`ClearReadOnlyAttributes`), после чего `Directory.Delete` не падает с `UnauthorizedAccessException`, и каталоги `.deleting_*` больше не остаются.
- **Счётчик реально удалённого и исключение `.deleting_*` (issue #178)**: `TryDeleteDirectory` возвращает признак успеха, и счётчики `Clear`/`ClearOrphans` увеличиваются только при фактическом удалении, а не при попытке. Временные каталоги `*.deleting_*` не считаются остатками и не переименовываются повторно (`IsDeletingName` в `EnumerateOrphanDirectories`).

### Версия

- **Версия поднята до `0.3.6.72` → `0.3.6.73`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.72] — 2026-09-05

Выпуск с реализацией issue #175 «Имя КОМ». Добавлена настройка **«Имя COM-коннектора 1С»** в окне настроек (вкладка «Настройки»): заданный шаблон разворачивается по версии платформы каждой базы (плейсхолдеры `%V12%`/`%V3%`/`%V4%`) и пробуется первым в переборе ProgID при подключении через COM. Это позволяет подключаться к разным версиям платформы без ручной перерегистрации COM-коннектора. Пустое значение — стандартные `V85/V83/V82/V81.COMConnector`. Работает на обеих платформах (Windows/WPF и Linux/Avalonia).

### Добавлено

- **Настраиваемый шаблон имени COM-коннектора (issue #175)**: свойство `ComConnectorNameTemplate` в [`Models/AppSettings.cs`](Configuration%20Management/Models/AppSettings.cs). Методы `BuildProgIdCandidates`, `ExpandTemplate` и `Digits` в [`Services/OneCComConnector.cs`](Configuration%20Management/Services/OneCComConnector.cs) разворачивают шаблон по версии платформы базы (`%V12%` — первые две цифры, `%V3%` — третья, `%V4%` — четвёртая) и ставят полученный ProgID первым в перебор (без дублей со стандартным списком). `Connect`/`ConnectRead`/`ConnectCore` используют список кандидатов с учётом шаблона.
- **Агентский процесс COM-чтения**: [`Services/ComReadHost.cs`](Configuration%20Management/Services/ComReadHost.cs) принимает опциональное четвёртое поле запроса с кастомным перечнем ProgID; при его отсутствии агент использует стандартный `KnownProgIds`. `ParseResponse`/`DetailAllowed`/`IsKnownProgId` проверяют имя ProgID по фактическому списку перебора.
- **Настройка в UI**: поле «Имя COM-коннектора 1С» добавлено на вкладку «Настройки» в WPF ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), сохранение в [`Views/SettingsWindow.xaml.cs`](Configuration%20Management/Views/SettingsWindow.xaml.cs)) и в Avalonia ([`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)). Свойства ViewModel `ComConnectorNameTemplate` добавлены в [`ViewModels/MainViewModel.Commands.cs`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs) (WPF) и [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs); сохранение — в `SaveSettings()` ([`ViewModels/MainViewModel.Launch.cs`](Configuration%20Management/ViewModels/MainViewModel.Launch.cs)). Настройка применяется после перезапуска.
- **Локализация**: ключи `Settings.General.ComConnectorTemplate`, `Settings.General.ComConnectorTemplateTooltip` и `Settings.General.ComConnectorTemplateHint` добавлены в `ru.json` и `en.json`.

### Версия

- **Версия поднята до `0.3.6.71` → `0.3.6.72`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.71] — 2026-09-04

Выпуск с реализацией issue #174 «Кнопка определения свойств конфигурации». В окне редактирования свойств базы (вкладка «Платформа») под полями «Конфигурация» и «Версия» добавлена кнопка **«Определить»**, которая автоматически определяет наименование и версию конфигурации по настройкам подключения: через COM-коннектор на Windows и эвристикой по файловой базе/конфигуратором на Linux. Найденные значения заполняются в соответствующие поля и сохраняются.

### Добавлено

- **Кнопка «Определить» в свойствах базы (issue #174)**: метод `DetermineConfiguration` ([`ViewModels/ConnectionSettingsViewModel.cs`](Configuration%20Management/ViewModels/ConnectionSettingsViewModel.cs)) формирует базу из текущих настроек подключения и вызывает `ConfigurationInfoService.ReadAndApply` (COM на Windows / эвристика на Linux), после чего обновляет поля `ConfigurationName` и `ConfigurationVersion`. Кнопка размещена на вкладке «Платформа» рядом с полями конфигурации: в WPF — в [`Views/ConnectionSettingsWindow.xaml`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml) с обработчиком `OnDetectConfiguration_Click` ([`Views/ConnectionSettingsWindow.xaml.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml.cs)), в Avalonia — в [`Views/ConnectionSettingsWindow.Avalonia.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.Avalonia.cs). Если определить свойства не удалось, показывается информационное сообщение.
- **Локализация**: ключи `Connection.DetectConfig`, `Connection.DetectConfigTooltip`, `Connection.DetectConfigFailed` и `Connection.DetectConfigTitle` добавлены в `ru.json` и `en.json`.

### Версия

- **Версия поднята до `0.3.6.70` → `0.3.6.71`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.70] — 2026-09-04

Выпуск с реализацией issue #173 «Пожелание - быстрая настройка колонок». По правому клику на заголовке колонки списка баз появляется контекстное меню с пунктами **«Скрыть колонку»** и **«Открыть настройки колонок»**. Первый сразу скрывает выбранную колонку (как в диспетчере задач Windows), второй открывает окно настроек сразу на подвкладке «Колонки». Работает на обеих платформах (Windows/WPF и Linux/Avalonia).

### Добавлено

- **Контекстное меню заголовков колонок (issue #173)**: правый клик по заголовку любой колонки данных («Версия платформы», «Режим запуска», «Действия», «Сервер/База», «Последний запуск», «Размер», «Конфигурация») открывает меню с пунктом «Скрыть колонку» — колонка скрывается сразу, как галка видимости в настройках. Реализовано в WPF через `ContextMenu` в [`Views/MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml) и обработчики в [`Views/MainWindow.Columns.cs`](Configuration%20Management/Views/MainWindow.Columns.cs); в Avalonia — контекстное меню прикрепляется в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) (`AttachColumnContextMenu`).
- **Переход к настройкам на вкладку «Колонки» (issue #173)**: пункт «Открыть настройки колонок» открывает окно настроек сразу на подвкладке **Отображение → Колонки**. Добавлен метод `SelectColumnsTab()` в [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs) и в WPF-версии ([`Views/SettingsWindow.Display.cs`](Configuration%20Management/Views/SettingsWindow.Display.cs)); во вложенном `TabControl` раздела «Отображение» задано имя `DisplaySubTabs` ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml)).
- **Единая команда скрытия колонки в модели**: метод `SetColumnVisible(key, visible)` в [`ViewModels/MainViewModel.Display.cs`](Configuration%20Management/ViewModels/MainViewModel.Display.cs) (WPF) и [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs) переиспользует `ApplyDisplaySettings`, поэтому скрытие колонки сохраняется и перестраивает список теми же механизмами, что и правка в настройках.
- **Локализация**: ключи `Column.HideColumn` и `Settings.Columns.OpenSettings` добавлены в `ru.json` и `en.json`.

### Версия

- **Версия поднята до `0.3.6.69` → `0.3.6.70`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.69] — 2026-09-04

Выпуск с релизом issue #172: добавлен настраиваемый хоткей для переключения подробностей правой панели информации. Заготовка фичи существовала ещё со времён невыпущенной версии 0.3.6.65; в этом выпуске она доведена до релиза — задано значение по умолчанию `Ctrl+D`, комбинация настраивается в **Настройки → Горячие клавиши → «Панель информации (подробности)»** и работает на обеих платформах (Windows/WPF и Linux/Avalonia).

### Добавлено

- **Настраиваемый хоткей для переключения подробностей правой панели информации (issue #172)**: свойство `HotkeyRightPanelDetails` ([`Models/AppSettings.cs`](Configuration%20Management/Models/AppSettings.cs)), команда `ToggleRightPanelDetailsCommand` в [`ViewModels/MainViewModel.Commands.cs`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs) и регистрация комбинации в [`Views/MainWindow.Hotkeys.cs`](Configuration%20Management/Views/MainWindow.Hotkeys.cs) (WPF) и [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) (Avalonia). По умолчанию — `Ctrl+D`; при желании сочетание меняется в настройках (строка «Панель информации (подробности)»). Хоткей переключает видимость подробных сведений правой панели и работает как в полном, так и в компактном режиме.

### Версия

- **Версия поднята до `0.3.6.68` → `0.3.6.69`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.68] — 2026-09-04

Выпуск с исправлением повторного импорта баз из StartManager (issue #163). Теперь импорт работает в режиме слияния: при повторном запуске записи сопоставляются с уже существующими базами не только по имени, но и по идентификатору (ID) и по строке подключения, поэтому авторизации (хранилище / Предприятие / Конфигуратор) дополняются/перезаписываются в существующих базах, а не только добавляются новые. Так «удалённые вручную» авторизации восстанавливаются из StartManager.

### Исправлено

- **Режим слияния при повторном импорте из StartManager (issue #163)**: метод `FindExisting` ([`Services/StartManagerImporter.cs`](Configuration%20Management/Services/StartManagerImporter.cs)) находит существующую базу для обновления по трём критериям — точное имя, идентификатор (ID), нормализованная строка подключения (путь к файловой базе, сервер+имя базы или URL веб-публикации). Раньше сопоставление шло только по имени, из-за чего при переименованной в приложении базе (или ином имени в StartManager) слияние не выполнялось: вместо обновления создавалась новая база, а авторизации существующей не восстанавливались. Логика `Merge` (дополнение/перезапись хранилища, Предприятия и Конфигуратора без затирания пустых значений) сохранена полностью; первичный импорт и поведение без совпадений (добавление новой базы) не изменены.

### Версия

- **Версия поднята до `0.3.6.67` → `0.3.6.68`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.67] — 2026-09-04

Выпуск с исправлением автообновления на Windows (issue #161): когда приложение установлено в защищённую папку (например `C:\Program Files\ConfigurationManagement\`), где у обычного пользователя нет прав на запись, PowerShell-помощник замены exe теперь запускается с повышением прав через UAC. Раньше `Move-Item` получал «Access to the path is denied» (после 10 попыток — FATAL) и обновление не устанавливалось.

### Исправлено

- **Автообновление при установке в Program Files (issue #161)**: перед запуском помощника проверяется доступность целевого каталога установки на запись; если он защищён, а текущий процесс запущен не от администратора — помощник стартует через `ShellExecute` с глаголом `runas` (запрос UAC). Если каталог доступен или приложение уже работает с правами администратора — поведение прежнее (обычный скрытый запуск). Логика повторных попыток `Move-Item`, ожидания завершения процесса и перезапуска сохранена полностью. Linux-ветка (`UpdateService.Avalonia.cs`) не затрагивается.

### Версия

- **Версия поднята до `0.3.6.66` → `0.3.6.67`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.66] — 2026-09-04

Выпуск с усилением исправления зависания при запуске на Linux/X11 в виртуальных машинах без композитора (issue #153). Непрозрачное окно со сбросом `ExtendClientAreaToDecorationsHint=false` и снятием прозрачности уже включалось детектором `LinuxRendering`; в этом выпуске статичный (не анимированный `IsIndeterminate`) индикатор загрузки и не блокирующий ввод оверлей применяются не только на программном рендере и в виртуализации, но и на любом X11 без композитора, а детектор программного рендера расширен новыми источниками и ручным флагом `CM_FORCE_SOFTWARE_RENDER=1`.

### Изменено

- **Статичный индикатор загрузки и оверлей также на X11 без композитора (issue #153)**: `DisableAnimations` в [`Services/LinuxRendering.cs`](Configuration%20Management/Services/LinuxRendering.cs) теперь учитывает и `NoCompositorAssumed`, поэтому на X11 без подтверждённого композитора (в т.ч. VirtualBox/KDE NEON) оверлей загрузки рисует статичную заполненную полосу вместо бесконечного индетерминантного индикатора и не перехватывает мышь — окно остаётся отзывчивым, без постоянной перерисовки кадра и высокой нагрузки CPU.

### Улучшено

- **Усилен детектор программного рендера (issue #153)**: в [`Services/LinuxRendering.cs`](Configuration%20Management/Services/LinuxRendering.cs) добавлены новые источники `MESA_LOADER_DRIVER_OVERRIDE` (llvmpipe/softpipe), поддержка значения `true` у `LIBGL_ALWAYS_SOFTWARE`, а также явный ручной флаг `CM_FORCE_SOFTWARE_RENDER=1` (аналог `CM_DISABLE_TRANSPARENCY`) для принудительной диагностики в проблемном окружении.

### Версия

- **Версия поднята до `0.3.6.65` → `0.3.6.66`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.65] — 2026-09-04

Выпуск с реализацией issue #172: добавлен настраиваемый хоткей `HotkeyRightPanelDetails` для переключения подробностей правой панели информации. По умолчанию не назначен; задаётся в **Настройки → Горячие клавиши → строка «Панель информации (подробности)»**. Добавлена локализация в ru.json/en.json.

### Добавлено

- **Настраиваемый хоткей для переключения подробностей правой панели информации (issue #172)**: добавлен `HotkeyRightPanelDetails`, который по умолчанию не назначен и задаётся пользователем в **Настройки → Горячие клавиши → «Панель информации (подробности)»**. Комбинация позволяет быстро показывать/скрывать подробные сведения правой панели. Локализация добавлена в `ru.json` и `en.json`.

### Версия

- **Версия поднята до `0.3.6.64` → `0.3.6.65`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.64] — 2026-09-04

Выпуск с полным устранением issue #171: исправлено исчезновение группы (вместе с её базами) при изменении наименования. Причина была в том, что базы ссылаются на группу строкой полного пути (`Infobase.Group`): при переименовании через `EditGroup` менялось имя группы, но пути баз не пересчитывались — базы не находили узел, группа становилась «пустой» и скрывалась из дерева. Теперь при переименовании или смене родителя добавляется метод `RemapSubtreeInfobasePaths` ([`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs) и WPF-версия в [`ViewModels/MainViewModel.Tools.cs`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs)), который пересчитывает `Infobase.Group` у всех баз подветки, переносит ключи свёрнутых групп, сохраняет и базы, и группы, а затем экспортирует `ibases.v8i`. Метод вызывается из `EditGroup` в Avalonia ([`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs)) и Windows ([`ViewModels/MainViewModel.Commands.cs`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs)).

### Исправлено

- **Группа больше не исчезает при переименовании вместе с её базами (issue #171)**: базы ссылаются на группу строкой полного пути (`Infobase.Group`); при переименовании через `EditGroup` имя группы менялось, но пути баз не пересчитывались, из-за чего базы не находили узел, группа становилась «пустой» и скрывалась из дерева.
- **Пересчёт путей баз подветки при переименовании/смене родителя (issue #171)**: добавлен метод `RemapSubtreeInfobasePaths` ([`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs) и WPF-версия в [`ViewModels/MainViewModel.Tools.cs`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs)), который при переименовании или смене родителя пересчитывает `Infobase.Group` у всех баз подветки, переносит ключи свёрнутых групп и сохраняет и базы, и группы.
- **Повторный экспорт `ibases.v8i` после переименования (issue #171)**: после сохранения баз и групп выполняется экспорт `ibases.v8i`, чтобы иерархия в родном стартере 1С соответствовала новому имени группы; метод вызывается из `EditGroup` в Avalonia ([`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs)) и Windows ([`ViewModels/MainViewModel.Commands.cs`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs)). Исправление действует на обеих платформах — Windows и Linux.

### Версия

- **Версия поднята до `0.3.6.63` → `0.3.6.64`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.63] — 2026-09-04

Выпуск с полным устранением issue #168: устранено падение (Signal 6 / SIGABRT) при открытии дополнительных (модальных) окон на Linux. Причина была двойной. Во-первых, ненадёжный детектор Wayland в `LinuxRendering.IsWayland()` — он возвращал `true` уже по одной переменной окружения `WAYLAND_DISPLAY`, даже если сессия фактически X11 (XWayland), из-за чего модальные окна шли прозрачным путём (`ExtendClientAreaToDecorationsHint` + `Transparent`), что на X11 даёт SIGABRT; теперь `IsWayland()` требует выполнения обоих условий — `XDG_SESSION_TYPE=wayland` И `WAYLAND_DISPLAY`. Во-вторых, показ и центрирование окна относительно непригодного владельца (без проверки видимости/геометрии); укреплены `ShowDialogSync` в [`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs) и `ShowModalSync` в [`Services/AvaloniaDialogService.cs`](Configuration%20Management/Services/AvaloniaDialogService.cs): владелец используется только если видим и имеет размеры, добавлен запасной немодальный путь показа и снятие кадра в `finally`.

### Исправлено

- **Устранено падение (Signal 6 / SIGABRT) при открытии дополнительных окон на Linux (issue #168)**: причиной была прозрачная ветка показа модальных окон на сессиях, ошибочно распознаваемых как Wayland. `LinuxRendering.IsWayland()` возвращал `true` уже по одной переменной `WAYLAND_DISPLAY`, даже в X11/XWayland-сессии, из-за чего окна шли через `ExtendClientAreaToDecorationsHint` + `Transparent`, что на X11 приводит к SIGABRT. Теперь `IsWayland()` требует `XDG_SESSION_TYPE=wayland` И `WAYLAND_DISPLAY`.
- **Безопасный показ/центрирование модальных окон с запасным путём (issue #168)**: владелец окна использовался без проверки видимости/геометрии, из-за чего показ и центрирование выполнялись относительно непригодного владельца. В [`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs) (`ShowDialogSync`) и [`Services/AvaloniaDialogService.cs`](Configuration%20Management/Services/AvaloniaDialogService.cs) (`ShowModalSync`) владелец используется только если видим и имеет размеры, добавлен запасной немодальный путь показа и снятие кадра в `finally`. Изменения изолированы под `#if LINUX/Avalonia`, Windows не затронут.

### Версия

- **Версия поднята до `0.3.6.62` → `0.3.6.63`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.62] — 2026-09-04

Выпуск с полным устранением issue #167: устранён «большой непонятный отступ» (лишний вертикальный зазор) в правой панели и панель «Теги» выровнена по высоте на обеих платформах — Windows и Linux. Первопричина была двойной. Во-первых, правая панель была опущена вниз лишним зазором `Padding`/`Margin`: в WPF — `Padding="12,56"→"12,12"` в [`Views/MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml), в Avalonia — `Margin(12,56)→(12,12)` в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs). Во-вторых, верхний отступ верхней панели поиска `TopBarV=10` не совпадал с нижним отступом панели тегов `8`, из-за чего на Linux группа «Теги» сдвигалась вниз; значение исправлено в [`Services/UiMetrics.Avalonia.cs`](Configuration%20Management/Services/UiMetrics.Avalonia.cs) `TopBarV 10→8` (симметрично `8/8`, как на Windows).

### Исправлено

- **Устранён «большой непонятный отступ» (лишний вертикальный зазор) в правой панели (issue #167)**: правая панель была опущена вниз зазором `Padding`/`Margin` «56» вместо «12» — на Windows в [`Views/MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml) `Padding="12,56"→"12,12"`, на Linux в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs) `Margin(12,56)→(12,12)`. Теперь панель прижата к верху, как и остальные элементы.
- **Выровнена по высоте панель «Теги» на обеих платформах (issue #167)**: верхний отступ верхней панели поиска `TopBarV=10` не совпадал с нижним отступом панели тегов `8`, из-за чего на Linux группа «Теги» сдвигалась вниз относительно остальных панелей. Значение исправлено в [`Services/UiMetrics.Avalonia.cs`](Configuration%20Management/Services/UiMetrics.Avalonia.cs) `TopBarV 10→8` (симметрично `8/8`, как на Windows). На Windows поведение не изменилось.

### Версия

- **Версия поднята до `0.3.6.61` → `0.3.6.62`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.61] — 2026-09-04

Выпуск с полным устранением issue #165: дублирование вложенных папок при синхронизации с родным стартером 1С (`ibases.v8i`), которое сохранялось после фиксов 0.3.6.52/53, больше не возникает. Корневая причина — жёстко зашитый разделитель `«\\»` в ключе `Folder` вместо нативного `Path.DirectorySeparatorChar`: на Linux штатный стартер строит иерархию по `/`, а запись вида `Учёт\Бухгалтерия` воспринималась как имя одной литеральной папки с обратным слешем, из-за чего рядом с правильно вложенной папкой создавался дубль. Теперь `IbasesV8iExporter.ToFolderPath` использует `Path.DirectorySeparatorChar` (на Linux — `/`, на Windows — `\`). Дополнительно устранена вторичная причина — осиротевшие группы (`ParentId` указывал на удалённый дубликат), из-за которых путь строился неполным и дубль воссоздавался: импортёр санитизирует осиротевшие группы, `RemoveDuplicateGroupsByPath` удаляет дубли по полному пути и переназначает `ParentId` детей, а базы привязаны к группам по пути, поэтому не «переезжают».

### Исправлено

- **Дублирование вложенных папок при синхронизации с родным стартером (issue #165)**: найден и устранён корень проблемы, сохранявшийся после фиксов 0.3.6.52/53 — жёстко зашитый разделитель `«\\»` в ключе `Folder` вместо нативного `Path.DirectorySeparatorChar`. На Linux штатный стартер строит иерархию по `/`, а запись `Учёт\Бухгалтерия` воспринималась как имя одной литеральной папки с обратным слешем, что приводило к созданию дубля рядом с правильно вложенной папкой. `IbasesV8iExporter.ToFolderPath` теперь использует `Path.DirectorySeparatorChar` (на Linux — `/`, на Windows — `\`).
- **Санитизация осиротевших групп (issue #165)**: вторичная причина дублей — осиротевшие группы, у которых `ParentId` указывал на уже удалённый дубликат: путь строился неполным, из-за чего дубль воссоздавался. В [`Services/IbasesV8iImporter.cs`](Configuration%20Management/Services/IbasesV8iImporter.cs) добавлены перевешивание родителя для осиротевших групп, усиленный `FindGroupByFullPath` с учётом осиротевших, методы `IsOrphanedParent`/`GroupExistsById`, а `RemoveDuplicateGroupsByPath` удаляет дубли по полному пути и корректно переназначает `ParentId` дочерних групп.
- **Базы не «переезжают» между группами (issue #165)**: базы привязаны к группам по пути, поэтому после удаления дубликатов и переназначения `ParentId` детей они остаются в правильных группах и больше не регенерируют дубли при повторных синхронизациях.

### Версия

- **Версия поднята до `0.3.6.60` → `0.3.6.61`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.60] — 2026-09-04

Выпуск с исправлением issue #160: хоткеи «Развернуть всё» (`Ctrl+Shift++`, `OemPlus`/`Add`) и «Свернуть всё» (`Ctrl+Shift+-`, `OemMinus`/`Subtract`) теперь срабатывают с первого нажатия и больше не конфликтуют с переключением папок мышью. Первопричиной было то, что декларативные `InputBindings`/`KeyBindings` оценивались в фазе всплытия и зависели от фокуса, из-за чего первое нажатие не доходило до команды. Обработка вынесена в туннельную фазу: для WPF — `Window_PreviewKeyDown` в [`Views/MainWindow.Hotkeys.cs`](Configuration%20Management/Views/MainWindow.Hotkeys.cs), для Avalonia — ранний обработчик окна `OnWindowKeyDown` в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs). Вызываются те же команды `ExpandAllGroupsCommand`/`CollapseAllGroupsCommand`, что и у рабочих кнопок, а `e.Handled=true` исключает повторное срабатывание. Команды идемпотентны — переключение папок мышью по-прежнему работает.

### Исправлено

- **Хоткеи «Развернуть всё» / «Свернуть всё» срабатывают с первого нажатия (issue #160)**: раньше декларативные `InputBindings`/`KeyBindings` оценивались в фазе всплытия и зависели от фокуса, поэтому первое нажатие не доходило до команды. Обработка перенесена в туннельную фазу — `Window_PreviewKeyDown` ([`Views/MainWindow.Hotkeys.cs`](Configuration%20Management/Views/MainWindow.Hotkeys.cs)) для WPF и `OnWindowKeyDown` ([`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs)) для Avalonia; вызываются те же команды `ExpandAllGroupsCommand`/`CollapseAllGroupsCommand`, что и у кнопок, а `e.Handled=true` исключает повторное срабатывание.
- **Устранён конфликт хоткеев с переключением мышью (issue #160)**: команды разворачивания/сворачивания идемпотентны, поэтому выделение папки и её раскрытие/сворачивание кликом мыши продолжают работать без побочных эффектов; обработчики объявлены ранними и не зависят от фокуса.

### Версия

- **Версия поднята до `0.3.6.59` → `0.3.6.60`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.59] — 2026-09-04

Выпуск с полным исправлением issue #153: устранено «зависание» окна при запуске на Linux/X11 (в т.ч. в виртуальных машинах без композитора/на программном рендере) и высокая нагрузка CPU (~36%). Первопричиной было то, что безрамное окно всегда запрашивало расширение клиентской области в декорации (`ExtendClientAreaToDecorationsHint=true`), что на X11 без композитора заставляет непрерывно перерисовывать фон; к этому добавлялись полупрозрачные подложки модальных окон и затемняющий оверлей загрузки, включавшие постоянную альфа-компоновку кадра. Теперь прозрачность на Linux автоопределяется по типу сессии (`LinuxRendering`): на X11 композитор не гарантирован, поэтому окно рисуется непрозрачным прямоугольным (без расширения клиентской области и прозрачности), а на Wayland прозрачность безопасна и сохраняется. В непрозрачном режиме сбрасывается `ExtendClientAreaToDecorationsHint=false` и задаётся сплошной фон для главного окна ([`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs)) и всех модальных ([`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs)); «стеклянные» подложки и оверлей загрузки делаются непрозрачными. Принудительный путь флага окружения `CM_DISABLE_TRANSPARENCY=1` сохранён.

### Исправлено

- **Зависание окна и высокая нагрузка CPU при запуске на Linux/X11 (issue #153)**: безрамное окно всегда запрашивало расширение клиентской области в декорации (`ExtendClientAreaToDecorationsHint=true`), что на X11 без композитора заставляет постоянно перерисовывать фон. Добавлено автоопределение `LinuxRendering` ([`Services/LinuxRendering.cs`](Configuration%20Management/Services/LinuxRendering.cs)): на X11 (композитор не гарантирован) окно становится непрозрачным прямоугольным, без расширения и прозрачности; на Wayland прозрачность безопасна и сохраняется.
- **Непрозрачный режим для всех окон (issue #153)**: при непрозрачном рендере сбрасывается `ExtendClientAreaToDecorationsHint=false` и задаётся сплошной фон для главного окна ([`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs)) и всех модальных ([`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs)); полупрозрачные «стеклянные» подложки модальных окон и затемняющий оверлей загрузки делаются непрозрачными, чтобы исключить постоянную альфа-компоновку кадра.
- **Принудительная непрозрачность через `CM_DISABLE_TRANSPARENCY` (issue #153)**: флаг окружения `CM_DISABLE_TRANSPARENCY=1` сохранён как принудительный путь отключения прозрачности в дополнение к автоопределению. Изменения изолированы под `#if LINUX/Avalonia`, Windows не затронут.

### Версия

- **Версия поднята до `0.3.6.58` → `0.3.6.59`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.58] — 2026-09-04

Выпуск с завершением работы над issue #164: имя конфигурации и версия релиза теперь отображаются в правой панели сведений о подключении (новый блок «Конфигурация» со значением `ConfigurationDisplay`) и в колонке «Конфигурация» списка баз. Введённые вручную в свойствах базы «Имя конфигурации» и «Версия конфигурации» читаются из модели информационной базы (`ConfigurationName`/`ConfigurationVersion`), сохраняются и персистятся в `infobases.json`, поэтому ручной ввод переживает перезапуск. На Linux COM-чтение не используется (только ручной ввод/эвристика), а на Windows автозаполнение через COM сохранено, но ручной ввод может его переопределить.

### Добавлено

- **Блок «Конфигурация» в правой панели сведений о подключении (issue #164)**: добавлен блок со значением `ConfigurationDisplay` — имя конфигурации и версия релиза. Реализовано в WPF ([`Views/MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml)) и Avalonia ([`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs)).
- **Ручной ввод имени и версии конфигурации сохраняется между запусками (issue #164)**: введённые в свойствах базы поля «Имя конфигурации» и «Версия конфигурации» читаются из модели ИБ (`ConfigurationName`/`ConfigurationVersion`), сохраняются и персистятся в `infobases.json`, поэтому переживают перезапуск приложения.
- **Поведение автозаполнения по платформам (issue #164)**: на Linux COM-чтение не выполняется — значения остаются из ручного ввода/эвристики; на Windows автозаполнение через COM сохранено, но ручной ввод может его переопределить. Полученные данные отображаются в колонке «Конфигурация» и в правой панели.

### Версия

- **Версия поднята до `0.3.6.57` → `0.3.6.58`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.57] — 2026-09-04

Выпуск с исправлением issue #170: кириллица и другие не-ASCII символы теперь сохраняются в конфиг-файлы (`infobases.json`, `groups.json`, `settings.json`), реестр профилей и экспортные файлы баз в читаемом виде UTF-8, а не в виде `\uXXXX`-последовательностей. Ранее .NET-сериализатор по умолчанию экранировал все не-ASCII символы, из-за чего русские буквы в файлах было невозможно прочитать. Внесённые вручную значения программа принимала, корректно показывала, но каждый раз перезаписывала обратно в юникод. Теперь задан `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, который пишет не-ASCII литерально; при этом чтение старых файлов с `\uXXXX` полностью совместимо.

### Добавлено

- **Читаемый UTF-8 в конфиг-файлах (issue #170)**: в [`Services/InfobaseRepository.cs`](Configuration%20Management/Services/InfobaseRepository.cs) в оба набора опций сериализации `JsonOptions` (файлы `infobases.json`, `groups.json`, чтение `settings.json`) и `SettingsJsonOptions` (запись `settings.json`) добавлен `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` — кириллица и прочие не-ASCII символы записываются читаемыми символами UTF-8, а не `\uXXXX`-последовательностями. Encoder влияет только на запись, поэтому существующие файлы с `\uXXXX` продолжают корректно читаться.
- **Читаемый UTF-8 в реестре профилей (issue #170)**: в [`Services/ProfileService.cs`](Configuration%20Management/Services/ProfileService.cs) в `JsonOptions` добавлен тот же `Encoder` — имена профилей на кириллице сохраняются в `profiles.json` в читаемом виде.
- **Читаемый UTF-8 в цветовых схемах (issue #170)**: в [`Models/ColorScheme.cs`](Configuration%20Management/Models/ColorScheme.cs) в `JsonOptions` добавлен `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` — названия схем и прочие не-ASCII символы сохраняются читаемо.
- **Читаемый UTF-8 в экспортных файлах баз (issue #170)**: в экспорт баз добавлен тот же `Encoder` в [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs) и [`ViewModels/MainViewModel.Tools.cs`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs) — выгруженные JSON-файлы содержат русские названия баз и групп в читаемом виде.

### Версия

- **Версия поднята до `0.3.6.56` → `0.3.6.57`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.56] — 2026-09-04

Выпуск с исправлениями девяти открытых issues: просмотр паролей в свойствах базы, падение на Linux при открытии доп. окон, большой отступ у тегов, цветовое оформление папок, дублирование вложенных папок в родном стартере, хоткеи «Свернуть/Развернуть всё», окно обновления, высокая нагрузка CPU на Linux в виртуальных машинах и сохранение данных в поле «Конфигурация».

### Добавлено

- **Просмотр паролей в свойствах базы (issue #169)**: для полей пароля хранилища, авторизации предприятия и Конфигуратора добавлены кнопка-«глазик» (показать/скрыть) и кнопка копирования пароля в буфер обмена. Реализовано в WPF ([`Views/ConnectionSettingsWindow.xaml`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml), [`Views/ConnectionSettingsWindow.xaml.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml.cs)) и Avalonia ([`Views/ConnectionSettingsWindow.Avalonia.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.Avalonia.cs)). Добавлены ключи локализации `Connection.ShowPasswordTooltip` / `Connection.CopyPasswordTooltip`.
- **Цветовое оформление папок (issue #166)**: в цветовую схему добавлены общие цвета обычной (`FolderColor`) и избранной (`FavoriteFolderColor`) папки, настраиваемые в редакторе схем; индивидуальная настройка каждой папки сохранена. Добавлены подписи в локализацию (ru/en).

### Исправлено

- **Падение на Linux при открытии доп. окон (issue #168)**: добавлен глобальный обработчик необработанных исключений UI-потока в Avalonia ([`App.axaml.cs`](Configuration%20Management/App.axaml.cs)), защищены вложенные циклы сообщений ([`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs), [`Services/AvaloniaDialogService.cs`](Configuration%20Management/Services/AvaloniaDialogService.cs)) и конструкторы окон в [`MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs).
- **Большой непонятный отступ (issue #167)**: убран верхний margin у левой колонки списка баз в Avalonia, панель «Теги» прижата к поиску, как на Windows ([`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs)).
- **Дублирование вложенных папок в родном стартере (issue #165)**: корневая причина — разделитель пути в Folder при экспорте `ibases.v8i` (`/` вместо `\`); теперь Folder пишется нативным разделителем ([`Services/IbasesV8iExporter.cs`](Configuration%20Management/Services/IbasesV8iExporter.cs)), а импортёр дополнительно канонизирует пути и дедуплицирует вложенные группы идемпотентно ([`Services/IbasesV8iImporter.cs`](Configuration%20Management/Services/IbasesV8iImporter.cs)).
- **Хоткеи «Свернуть всё/Развернуть всё» (issue #160)**: устранён пропуск первого нажатия (виртуализация) и поломка мышиного переключения узлов (local value DP) — команды теперь ведут модель через `node.IsExpanded` ([`ViewModels/MainViewModel.Theme.cs`](Configuration%20Management/ViewModels/MainViewModel.Theme.cs), [`Views/MainWindow.Tree.cs`](Configuration%20Management/Views/MainWindow.Tree.cs)).
- **Окно обновления (issue #157)**: устранён DPI-баг в Win32-хуке (размер окна считался в DIP вместо физических пикселей) — контент больше не обрезается при масштабе >100%; пересчёт высоты по этапу ([`Services/UpdateAvailableWindow.xaml.cs`](Configuration%20Management/Services/UpdateAvailableWindow.xaml.cs)).
- **Высокая нагрузка CPU/зависание на Linux в VM (issue #153)**: найден источник бесконечной перерисовки (индетерминантный индикатор загрузки); добавлен детектор ПО-рендера/VM ([`Services/LinuxRendering.cs`](Configuration%20Management/Services/LinuxRendering.cs)), статичный индикатор и страховочный таймаут в [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs).
- **Сохранение данных в поле «Конфигурация» (issue #164)**: введённые вручную имя и версия конфигурации теперь корректно переносятся из диалога свойств в объект базы и сохраняются ([`ViewModels/MainViewModel.Commands.cs`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs), [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs)).

### Версия

- **Версия поднята до `0.3.6.55` → `0.3.6.56`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.55] — 2026-09-04

Добавлен ручной ввод имени конфигурации и версии релиза конфигурации информационной базы (issue #164). На Windows значения можно оставить для автополучения через COM-коннектор, а на Linux, где COM-соединение недоступно и автополучение не всегда возможно, имя и версию конфигурации теперь можно заполнить вручную в окне свойств базы. Введённые вручную значения сохраняются и не затираются фоновым автообновлением, а в колонке «Конфигурация» списка баз отображаются полученные данные.

### Добавлено

- **Ручной ввод имени и версии конфигурации ИБ в свойствах базы (issue #164)**: в окне свойств информационной базы под полями «Конфигурация» и «Версия конфигурации» добавлена подсказка о возможности ручного ввода ([`Views/ConnectionSettingsWindow.xaml`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml), [`Views/ConnectionSettingsWindow.Avalonia.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.Avalonia.cs)). Поля `ConfigurationName` / `ConfigurationVersion` теперь можно заполнить вручную, что критично для Linux, где COM-соединение недоступно и автополучение имени/версии невозможно.
- **Разграничение автозаполнения по платформам (issue #164)**: в [`Services/ConfigurationInfoService.cs`](Configuration%20Management/Services/ConfigurationInfoService.cs) метод `TryApply` при фоновом автополучении (`overwriteExisting=false`) не перезаписывает уже непустые поля — они считаются введёнными пользователем и заполняются только пустые; перезапись допускается лишь при явной команде пользователя «Обновить информацию» (`overwriteExisting=true`). На Linux COM-чтение не выполняется, поэтому значения остаются ручными, если эвристика по файлу `1Cv8.1CD` или пакетный режим конфигуратора их не вернули.
- **Сохранение ручных значений без затирания (issue #164)**: введённые пользователем имя и версия конфигурации сохраняются и не теряются при фоновом автообновлении данных о конфигурации, а полученные значения отображаются в колонке «Конфигурация» списка баз.
- **Обновление локализации (issue #164)**: добавлены ключи `Connection.ConfigurationManualHint`, `Connection.ConfigurationNameTooltip` и `Connection.ConfigurationVersionTooltip` в [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json).

### Версия

- **Версия поднята до `0.3.6.54` → `0.3.6.55`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.54] — 2026-09-03

Кнопка «Импорт из StartManager» (issue #163) добавлена в Windows/WPF-версию окна настроек: в разделе «Базы» появился пункт импорта рядом с импортом из `ibases.v8i`, который вызывает тот же механизм переноса баз и настроек платформы из StartManager, что и в Linux/Avalonia-версии. Ранее кнопка присутствовала только в Linux-версии, поэтому на Windows её невозможно было найти.

### Добавлено

- **Кнопка «Импорт из StartManager» в Windows/WPF-версию окна настроек (issue #163)**: в раздел «Базы» ([`SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml)) добавлена кнопка импорта рядом с импортом из `ibases.v8i`, а в [`SettingsWindow.Platforms.cs`](Configuration%20Management/Views/SettingsWindow.Platforms.cs) — обработчик `OnImportStartManager_Click`, вызывающий `MainViewModel.ImportFromStartManager()`. Для WPF в [`MainViewModel.Tools.cs`](Configuration%20Management/ViewModels/MainViewModel.Tools.cs) реализован метод `ImportFromStartManager()`: при отсутствии стандартного каталога `%APPDATA%\StartManager14\SMSettings` предлагается выбрать его вручную, затем переносятся базы с авторизацией и путь к платформе 1С добавляется в дополнительные пути поиска.

### Исправлено

- **Функция импорта из StartManager стала доступна на Windows (issue #163)**: ранее кнопка «Импорт из StartManager» присутствовала только в Linux/Avalonia-версии окна настроек; теперь пункт доступен и пользователям Windows/WPF.

### Версия

- **Версия поднята до `0.3.6.53` → `0.3.6.54`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.53] — 2026-09-03

Новая функция импорта из StartManager (issue #163): перенос баз с путями, группами и авторизацией (в т.ч. расшифровка паролей методом Виженера с ключом `SLAVKA`) и настройки расположения платформы из файлов `settings.cnf` / `v8config.smc`. Также доработано исправление дублирования папок в родном стартере (issue #165): дедупликация теперь корректно переназначает родительские связи дочерних групп, поэтому иерархия сохраняется и дубли больше не регенерируются при повторных синхронизациях.

### Добавлено

- **Импорт из StartManager (issue #163)**: добавлен сервис [`StartManagerImporter.cs`](Configuration%20Management/Services/StartManagerImporter.cs), который читает каталог настроек `%APPDATA%\StartManager14\SMSettings` (файлы `settings.cnf` и `v8config.smc`, кодировка Windows-1251), переносит пути и авторизацию баз (хранилище / Предприятие / Конфигуратор) и расшифровывает пароли методом Виженера по ASCII-символам с ключом `SLAVKA`. Пункт «Импорт из StartManager» доступен в настройках в разделе «Базы» (кнопка рядом с импортом из `ibases.v8i`); путь к платформе 1С из StartManager добавляется в дополнительные пути поиска.

### Исправлено

- **Устранено дублирование вложенных папок в родном стартере (issue #165)**: предыдущее исправление (импорт по полному пути + дедупликация унаследованных дублей) не устранило корневую причину. Теперь в [`IbasesV8iImporter.cs`](Configuration%20Management/Services/IbasesV8iImporter.cs) при удалении дубликатов групп (`RemoveDuplicateGroupsByPath`) корректно переназначается `ParentId` дочерних групп, ссылавшихся на удалённый дубликат, — иерархия сохраняется, полные пути продолжают строиться, а дубли больше не создаются заново при последующих синхронизациях.

### Версия

- **Версия поднята до `0.3.6.52` → `0.3.6.53`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.52] — 2026-09-03

Набор исправлений для Linux (Avalonia) и Windows (WPF): устранено дублирование папок в родном стартере при синхронизации групп, исправлено окно обновления (контент больше не обрезается, окно нельзя «схлопнуть» перетаскиванием границы, добавлена диагностика ошибок в лог), хоткеи «Свернуть всё/Развернуть всё» теперь срабатывают с первого нажатия, добавлены настраиваемые хоткеи очистки поиска и сброса тегов, а также снижена нагрузка на CPU на Linux X11/VM за счёт усиленного определения программного рендера/VM и нового флага окружения `CM_DISABLE_TRANSPARENCY=1`.

### Исправлено

- **Устранено дублирование папок в родном стартере при синхронизации (issue #165)**: импорт групп сделан идемпотентным по полному пути, добавлена дедупликация уже унаследованных дубликатов — повторные синхронизации больше не создают копий папок.
- **Окно обновления больше не обрезает контент и его нельзя «схлопнуть» (issues #157, #162)**: прогресс-бар и кнопки больше не обрезаются, окно нельзя изменить перетаскиванием границы, добавлена диагностика ошибок обновления в лог.

### Изменено

- **Хоткеи «Свернуть всё/Развернуть всё» срабатывают с первого нажатия (issue #160)**: устранён пропуск первого нажатия; дополнительно добавлены настраиваемые хоткеи очистки поиска и сброса тегов (по умолчанию `Ctrl+Shift+C` / `Ctrl+Shift+T`).
- **Снижена нагрузка на CPU на Linux X11/VM (issue #153)**: усилено определение программного рендера/виртуальных машин, добавлен флаг окружения `CM_DISABLE_TRANSPARENCY=1` для принудительной непрозрачности окна.

### Версия

- **Версия поднята до `0.3.6.51` → `0.3.6.52`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.51] — 2026-09-03

Исправление автообновления на Windows (WPF): устранён «тихий» провал установки обновления, когда новая версия скачивалась, но не применялась без какого-либо сообщения об ошибке. Добавлено диагностическое логирование bash/powershell-помощника в `%TEMP%`, исправлена кодировка PowerShell-скрипта (UTF-8 BOM), гарантирован запуск 64-битной PowerShell, повышена надёжность замены исполняемого файла (повторные попытки с ожиданием) и обеспечена корректная передача аргументов.

### Исправлено

- **Автообновление на Windows работает надёжно (issue #161)**: устранён «тихий» провал — раньше новая версия могла скачаться, но не установиться без уведомления пользователя. Теперь помощник запускается с гарантированной 64-битной PowerShell (`-ExecutionPolicy Bypass`), PowerShell-скрипт сохраняется с кодировкой UTF-8 BOM (иначе кириллические пути/аргументы ломали разбор), а диагностический лог помощника пишется в `%TEMP%` для разбора сбоев.
- **Повышена надёжность замены исполняемого файла (issue #161)**: замена `ConfigurationManagement.exe` выполняется с повторными попытками и ожиданием, пока целевой процесс не завершится и файл не освободится (антивирус/задержка завершения процесса больше не приводят к молчаливому отказу), а аргументы запуска после перезапуска передаются корректно.

### Версия

- **Версия поднята до `0.3.6.50` → `0.3.6.51`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.50] — 2026-09-03

Набор новых возможностей и исправлений для Linux (Avalonia): реализовано автообновление на Linux — приложение скачивает новый бинарник и устанавливает его после выхода процесса через bash-помощник с автоматическим перезапуском; файловые базы 1С теперь отображаются отдельной иконкой (база данных-цилиндр); добавлены нередактируемые хоткеи очистки; а также устранено зависание и высокая нагрузка CPU при запуске на Linux в X11/виртуальных машинах — окно рисуется полностью непрозрачным при программном рендере.

### Добавлено

- **Автообновление на Linux (issue #161)**: реализовано скачивание нового бинарника и его установка после выхода процесса через bash-помощник, с автоматическим перезапуском приложения.
- **Нередактируемые хоткеи очистки (issue #160)**: добавлены хоткеи — `Ctrl+Shift+C` (очистка строки поиска), `Ctrl+Shift+T` (отключение всех тегов), `Ctrl+Shift+Plus` / `Ctrl+Shift+Minus` (развернуть/свернуть все узлы дерева).

### Изменено

- **Файловые базы 1С отображаются отдельной иконкой (issue #161)**: файловые базы данных теперь показываются иконкой «база данных-цилиндр», отличной от иконки папки по форме.

### Исправлено

- **Устранено зависание и высокая нагрузка CPU при запуске на Linux в X11/виртуальных машинах (issue #153)**: окно рисуется полностью непрозрачным на X11/программном рендере, полупрозрачное оформление сохранено только на Wayland.

### Версия

- **Версия поднята до `0.3.6.49` → `0.3.6.50`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.49] — 2026-09-03

Набор исправлений стабильности и интерфейса для Windows (WPF) и Linux (Avalonia): переключатель «Системный заголовок окна» на Linux применяется мгновенно без перезапуска, колонка «Действия» стала доступна на вкладке «Отображение», окно обновления больше не обрезает контент при увеличении, устранено зависание с высокой нагрузкой CPU при запуске на Linux в X11/виртуальных машинах, а также исправлено сохранение компактного режима правой панели.

### Изменено

- **Переключатель «Системный заголовок окна» на Linux применяется мгновенно, без перезапуска (issue #159)**: смена настройки теперь сразу перестраивает рамку главного окна, не требуя перезапуска приложения.
- **Колонка «Действия» доступна в списке настроек на вкладке «Отображение» (issue #158)**: колонку можно отключить и снова включить.
- **Увеличено и сделано масштабируемым окно обновления (issue #157)**: прогресс, текст и кнопки больше не обрезаются.
- **Устранено зависание и высокая нагрузка CPU при запуске на Linux в X11/виртуальных машинах (issue #153)**: окно рисуется непрозрачным при программном рендере/VM.
- **Исправлено сохранение и восстановление компактного режима правой панели (issue #149)**: убран лишний всплывающий информационный блок в компактном режиме.

### Версия

- **Версия поднята до `0.3.6.48` → `0.3.6.49`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.48] — 2026-09-03

Диалоговые окна (Linux/Avalonia) исправлены: теперь они учитывают настройку «Системный заголовок окна» и их можно перетаскивать. Устранено расхождение «закраски» (полупрозрачной подложки) между окнами.

### Изменено

- **Диалоговые окна учитывают настройку «Системный заголовок окна» (issue #152)** ([`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs)): базовый класс всех диалогов больше не жёстко задаёт `SystemDecorations.None` и `ExtendClientAreaToDecorationsHint = true`. Когда настройка включена, диалог, как и главное окно, использует стандартную системную рамку (`SystemDecorations.Full`) с её кнопками и перетаскиванием; когда выключена — собственный безрамковый режим со «стеклянной» подложкой. Прозрачность и расширение клиентской области применяются только в безрамковом режиме, что исключает конфликт с системной рамкой и падение на Linux (issue #150).
- **Устранено жёсткое переопределение системного заголовка в отдельных окнах (issue #152)** ([`Views/AddEditWindow.Avalonia.cs`](Configuration%20Management/Views/AddEditWindow.Avalonia.cs), [`Views/ColorPickerWindow.Avalonia.cs`](Configuration%20Management/Views/ColorPickerWindow.Avalonia.cs), [`Views/ConnectionStringInputWindow.Avalonia.cs`](Configuration%20Management/Views/ConnectionStringInputWindow.Avalonia.cs), [`Views/CreateInfobaseWindow.Avalonia.cs`](Configuration%20Management/Views/CreateInfobaseWindow.Avalonia.cs), [`Views/LoginWindow.Avalonia.cs`](Configuration%20Management/Views/LoginWindow.Avalonia.cs), [`Views/ProfilesWindow.Avalonia.cs`](Configuration%20Management/Views/ProfilesWindow.Avalonia.cs)): из конструкторов шести окон убрано `SystemDecorations = Full`, а у `AddEditWindow` — переопределение `UseGlassChrome => false`. Теперь все производные окна следуют базовому классу и единообразно реагируют на настройку системного заголовка, а расхождение «закраски» между диалогами устранено.
- **Перетаскивание диалогов работает в обоих режимах (issue #152)**: в безрамковом режиме окно перетаскивается за полосу заголовка (`BeginMoveDrag`) с исключением интерактивных элементов и корректным поведением при развороте; при включённом системном заголовке перетаскивание обеспечивает сама системная рамка.

### Версия

- **Версия поднята до `0.3.6.47` → `0.3.6.48`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.47] — 2026-09-03

В редакторе тем на вкладке «Оформление» возвращён прежний вид: вместо двух живых предпросмотров (светлая и тёмная палитры) снова один живой предпросмотр, отражающий активную цветовую схему, справа в фиксированной колонке; список цветов слева расположен в прокручиваемой колонке.

### Изменено

- **Вкладка «Оформление» возвращена к прежнему виду (issue #155)** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs), [`Views/SettingsWindow.Schemes.cs`](Configuration%20Management/Views/SettingsWindow.Schemes.cs)): отказались от двух живых предпросмотров (`PreviewShellLight`/`PreviewShellDark`) и динамической пропорциональной компоновки колонок (`2* : 3*`). Возвращён один живой предпросмотр, отражающий активную цветовую схему, справа в фиксированной колонке `Auto`; список цветов слева размещён в прокручиваемой колонке `*`.

### Версия

- **Версия поднята до `0.3.6.46` → `0.3.6.47`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.46] — 2026-09-02

В редакторе тем на вкладке «Оформление» предпросмотры снова размещены в одну линию (светлая слева, тёмная справа), как и должно быть; остальные доработки сохранены — увеличенный размер превью, динамические колонки и собственная прокрутка у каждой панели.

### Изменено

- **Предпросмотры темы размещены в одну линию (issue #155)** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): оба живых превью вновь расположены горизонтально — светлая палитра слева, тёмная справа (были друг под другом). Увеличенный размер превью, пропорциональные динамические колонки (левая уже правой) и отдельная вертикальная прокрутка у каждой панели сохранены.

### Версия

- **Версия поднята до `0.3.6.45` → `0.3.6.46`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.45] — 2026-09-02

В редакторе тем на вкладке «Оформление» увеличены превью, левая колонка стала уже правой, а ширины обеих колонок теперь динамически меняются от размера окна настроек. Превью сложены вертикально (светлое сверху, тёмное снизу), у каждой колонки своя вертикальная прокрутка, поэтому обе панели всегда видны при любом размере окна.

### Изменено

- **Увеличены превью темы, левая колонка уже, колонки динамически масштабируются (issue #155)** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): ширина превью поднята с 175 до 210 (обе платформы выровнены на 210), чтобы в макете помещались все элементы и текст не обрезался. Оба превью сложены вертикально в левой колонке (светлое сверху, тёмное снизу), а не горизонтально — благодаря этому левая колонка может быть уже правой. Колонки сделаны пропорциональными (`2* : 3*`, левая уже) — их ширина динамически меняется от размера окна настроек; каждая колонка обёрнута в собственный вертикальный `ScrollViewer`, поэтому при любой высоте окна панели остаются доступными, а список цветов прокручивается внутри своей колонки.

### Версия

- **Версия поднята до `0.3.6.44` → `0.3.6.45`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.44] — 2026-09-02

В окне обновления исчезло пустое место внизу после скачивания — обработчик Win32-сообщения `WM_GETMINMAXINFO` теперь фиксирует только ширину окна (высоту не ограничивает), поэтому `SizeToContent="Height"` снова подстраивает высоту окна под каждый этап, а ширину по-прежнему нельзя изменить мышью.

### Исправлено

- **Пустое место внизу окна обновления после скачивания** ([`Services/UpdateAvailableWindow.xaml.cs`](Configuration%20Management/Services/UpdateAvailableWindow.xaml.cs)): обработчик `WM_GETMINMAXINFO` фиксировал и ширину, и высоту окна (min/max track size равными его размеру), из-за чего `SizeToContent="Height"` переставал подстраивать высоту и после перехода к вопросу о применении обновления внизу оставалось пустое место. Теперь обработчик принудительно фиксирует только ширину (`MinTrackSize.X`/`MaxTrackSize.X` равны фактической ширине), а высоту не ограничивает — её снова подстраивает `SizeToContent="Height"` под каждый этап. Пустое место внизу исчезло, а ширину окна по-прежнему нельзя изменить мышью.

### Версия

- **Версия поднята до `0.3.6.43` → `0.3.6.44`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.43] — 2026-09-02

В редакторе тем на вкладке «Оформление» возвращена прежняя двухколоночная компоновка и доработана так, чтобы все элементы были видны при открытии окна настроек: левая колонка (управление схемой и превью) — фиксированная и всегда видна, а список цветов в правой колонке прокручивается внутри собственной области.

### Изменено

- **Левая колонка редактора тем зафиксирована, список цветов получил собственную прокрутку (issue #155)** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): убран внешний двусторонний `ScrollViewer`, из-за которого компоновка стала неудобной. Вкладка снова разделена на две колонки — слева `Auto` (фиксированная ширина по содержимому: блок управления схемой и два превью под ним), справа `*` с `ScrollViewer` (вертикальная прокрутка) со списком цветов, который занимает оставшуюся высоту. В результате левая колонка не ужимается и не обрезается, все элементы оформления видны при открытии окна, а при нехватке места прокручивается только список цветов.

### Версия

- **Версия поднята до `0.3.6.42` → `0.3.6.43`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.42] — 2026-09-02

Размер окна обновления теперь нельзя изменить мышкой — добавлен жёсткий перехват Win32-сообщения `WM_GETMINMAXINFO`, который принудительно фиксирует min/max track size окна равными его размеру, поэтому обход `ResizeMode="NoResize"` кастомным Window-Chrome больше не позволяет растягивать окно.

### Исправлено

- **Размер окна обновления можно было изменить мышкой** ([`Services/UpdateAvailableWindow.xaml.cs`](Configuration%20Management/Services/UpdateAvailableWindow.xaml.cs)): кастомный Window-Chrome обходил `ResizeMode="NoResize"`, и окно обновления можно было растягивать за края мышью. Добавлен жёсткий перехват Win32-сообщения `WM_GETMINMAXINFO` через `HwndSource`-хук, который принудительно фиксирует min/max track size окна равными его текущему размеру — теперь размер окна обновления нельзя изменить мышкой.

### Версия

- **Версия поднята до `0.3.6.41` → `0.3.6.42`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.41] — 2026-09-02

Весь контент вкладки «Оформление» окна настроек сделан единым прокручиваемым блоком с прокруткой по вертикали и горизонтали, поэтому левая панель редактора тем больше не скрывается и не обрезается при любом размере окна.

### Изменено

- **Левая панель редактора тем больше не скрывается при любом размере окна (issue #155)** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): весь контент вкладки «Оформление» свёрнут в один внешний `ScrollViewer` с прокруткой в обе стороны, а внутренние per-column `ScrollViewer`'ы убраны (в Avalonia правая колонка — это сам `colorsColumn` без собственной прокрутки, колонки измеряются по содержимому). Если окно настроек мало, появляются полосы прокрутки и все элементы оформления остаются доступными — ни один элемент не скрывается и не обрезается.

### Версия

- **Версия поднята до `0.3.6.40` → `0.3.6.41`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.40] — 2026-09-02

Текст кнопки «Обновить после закрытия» в окне обновления переведён в одну строку — кнопка расширена, а перенос слов убран; размер окна зафиксирован (MaxWidth приведён к Width), поэтому окно больше не меняет размер.

### Исправлено

- **Текст кнопки «Обновить после закрытия» переносился на две строки** ([`Services/UpdateAvailableWindow.xaml`](Configuration%20Management/Services/UpdateAvailableWindow.xaml)): у `UpdateAfterCloseButton` ширина увеличена со 190 до 240, а у `UpdateAfterCloseText` убран `TextWrapping="Wrap"` — текст «Обновить после закрытия» теперь размещается в одну строку и не выходит за пределы кнопки.
- **Размер окна обновления зафиксирован**: `MaxWidth` уменьшен с 640 до 600 (равен `Width`), поэтому окно имеет фиксированный размер и его нельзя изменить (`ResizeMode=NoResize` остаётся).

### Версия

- **Версия поднята до `0.3.6.39` → `0.3.6.40`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.39] — 2026-09-02

Доработана компоновка редактора тем на вкладке «Оформление»: превью перенесены в левую колонку под блок управления схемой, а список цветов вынесен в отдельную правую колонку, растянутую по вертикали. Устранено перекрытие верхнего блока управления схемой списком цветов.

### Изменено

- **Превью перенесены в левую колонку редактора тем, а список цветов — в правую (issue #155)** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): нижний горизонтальный блок предпросмотра, ранее занимавший всю ширину окна, перенесён в левую колонку под блок управления схемой — оба живых превью (светлое слева, тёмное справа) размещены горизонтально. Список цветов вынесен в отдельную правую колонку, растянут по вертикали и прокручивается внутри своей колонки; устранено перекрытие верхнего блока управления схемой списком цветов.

### Версия

- **Версия поднята до `0.3.6.38` → `0.3.6.39`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.38] — 2026-09-02

Кнопкам окна обновления заданы фиксированные размеры, а само окно стало шире, чтобы текст «Перезапустить сейчас» и другие надписи всегда были читаемы и не обрезались ни при каком размере окна.

### Исправлено

- **Текст кнопок окна обновления мог обрезаться (issue #148)** ([`Services/UpdateAvailableWindow.xaml`](Configuration%20Management/Services/UpdateAvailableWindow.xaml)): всем кнопкам вместо минимального `MinWidth` задана фиксированная `Width` — `RestartNowButton` = 230, `UpdateAfterCloseButton` = 190, `DownloadButton` = 150, `CancelButton` = 100, `DoneCloseButton` = 100, `ErrorCloseButton` = 100, а ширина окна увеличена (`Width` 480→600, `MaxWidth` 520→640), чтобы текст «Перезапустить сейчас» и другие надписи всегда читались и не обрезались ни при каком размере окна.

### Версия

- **Версия поднята до `0.3.6.37` → `0.3.6.38`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.37] — 2026-09-02

Редактор цветовых схем на вкладке «Оформление» переработан по варианту 2 из issue #155: теперь функционал помещается в одно окно без обрезания. Управление схемой осталось слева, список цветов переехал в правую колонку, а оба живых превью (светлое и тёмное) размещены в нижнем горизонтальном блоке на всю ширину. В строке цвета порядок изменён на «образец → hex → название», а само название стало кликабельной подчёркнутой ссылкой вместо отдельной кнопки «Выбрать».

### Изменено

- **Перекомпонована вкладка «Оформление» (issue #155)** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): редактор тем приведён к виду по варианту 2 — сверху две колонки (слева управление схемой: комбобокс и кнопки «Применить/Создать/Переименовать/Удалить/Сбросить/Экспорт/Импорт», справа список цветов выбранной палитры), снизу на всю ширину горизонтальный блок из двух живых превью (светлая палитра слева, тёмная справа). Раньше список цветов и превью делили левую колонку, из-за чего кнопка «Применить» была видна частично.
- **Добавлен живой предпросмотр темы в Linux-версии** ([`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): в Avalonia-редакторе появились два миниатюрных предпросмотра (светлый и тёмный), построенные в коде по образцу WPF (методы `BuildThemePreview`/`PaintThemePreview`); они перекрашиваются при каждом изменении цвета.
- **Изменён порядок элементов в строке цвета (issue #155)** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): теперь сначала идёт образец-кубик цвета, затем его HEX-значение и уже потом название. Название стало кликабельным подчёркнутым (цвет-акцент, курсор-рука) и открывает выбор цвета; отдельная кнопка «Выбрать» (ключ `Settings.ChooseColor`) удалена, вместо неё добавлен ключ-подсказка `Settings.ChooseColorTooltip`. Это позволило заметно сузить список цветов.

### Версия

- **Версия поднята до `0.3.6.36` → `0.3.6.37`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.36] — 2026-09-02

Исправление обрезания текста кнопки «Перезапустить сейчас» в окне обновления — увеличен минимальный размер кнопки, чтобы текст не обрезался справа.

### Исправлено

- **Кнопка «Перезапустить сейчас» обрезалась справа (issue #148)** ([`Services/UpdateAvailableWindow.xaml`](Configuration%20Management/Services/UpdateAvailableWindow.xaml)): у кнопки `RestartNowButton` увеличен `MinWidth` со 180 до 200, чтобы текст «Перезапустить сейчас» не обрезался справа.

### Версия

- **Версия поднята до `0.3.6.35` → `0.3.6.36`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.35] — 2026-09-02

Из окна обновления удалена вводящая в заблуждение подпись `Update.RestartChoiceHint` («Да — перезапустить программу сейчас, Нет — обновить после закрытия»), которая не соответствовала надписям на реальных кнопках «Перезапустить сейчас» / «Обновить после закрытия».

### Исправлено

- **Удалена вводящая в заблуждение подсказка выбора в диалоге обновления** ([`Services/UpdateAvailableWindow.xaml`](Configuration%20Management/Services/UpdateAvailableWindow.xaml)): удалён TextBlock с подписью `Update.RestartChoiceHint` из окна обновления, а ключи `Update.RestartChoiceHint` удалены из [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json). Подпись «Да — перезапустить программу сейчас, Нет — обновить после закрытия» не соответствовала надписям на реальных кнопках «Перезапустить сейчас» / «Обновить после закрытия» и вводила пользователя в заблуждение.

### Версия

- **Версия поднята до `0.3.6.34` → `0.3.6.35`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.34] — 2026-09-02

Исправления выбора разрядности запуска в режиме «Авто» и обрезания кнопки обновления в Windows-версии, внесённые поверх версии 0.3.6.33. Теперь при чистой версии платформы без суффикса «(32)/(64)» выбор разрядности корректно переходит к глобальной «Разрядность по умолчанию» и настройке базы, а текст кнопок диалога обновления «Перезапустить сейчас» и «Скачать» больше не обрезается слева.

### Исправлено

- **Ошибка выбора разрядности в режиме «Авто» (issue #146)** ([`Services/OneCLauncher.cs`](Configuration%20Management/Services/OneCLauncher.cs), [`Services/OneCLauncher.Linux.cs`](Configuration%20Management/Services/OneCLauncher.Linux.cs)): шаг 2 приоритета выбора разрядности (по суффиксу «(32)/(64)» в строке версии платформы) теперь срабатывает только если суффикс реально присутствует в строке `PlatformVersion` (добавлена проверка `hasSuffix`). Раньше `PlatformVersionService.ParseVariant` возвращал `architecture="32"` по умолчанию для чистой версии без суффикса, из-за чего шаг 2 ложно возвращал x86, игнорируя глобальную настройку X64 и настройку базы. Теперь для чистой версии логика корректно переходит к шагу 3 (глобальная «Разрядность по умолчанию») и шагу 4 (настройка базы / priority).
- **Кнопка обновления обрезалась слева в Windows-версии (issue #148)** ([`Services/UpdateAvailableWindow.xaml`](Configuration%20Management/Services/UpdateAvailableWindow.xaml)): у кнопок `DownloadButton` и `RestartNowButton` удалены проблемные `TextOptions.TextFormattingMode="Display"` и `TextOptions.TextRenderingMode="ClearType"`, которые прижимали глифы к левому краю, а `MinWidth` увеличен — DownloadButton 120→130, RestartNowButton 140→180, поэтому текст «Перезапустить сейчас» (ru) и «Restart now» (en) теперь помещается и не обрезается.

### Версия

- **Версия поднята до `0.3.6.33` → `0.3.6.34`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.33] — 2026-09-02

Исправления выбора разрядности запуска и читаемости файлов настроек, внесённые поверх версии 0.3.6.32. Теперь в лаунчер корректно передаётся выбранная в блоке «Текущая сессия» разрядность и она учитывается первым шагом приоритета, а `groups.json` и `infobases.json` сохраняются в читаемом виде с переносами строк и отступами.

### Исправлено

- **Выбор разрядности из блока «Текущая сессия» не передавался в лаунчер (issue #146)** ([`Services/OneCLauncher.cs`](Configuration%20Management/Services/OneCLauncher.cs), [`Services/OneCLauncher.Linux.cs`](Configuration%20Management/Services/OneCLauncher.Linux.cs), [`ViewModels/MainViewModel.cs`](Configuration%20Management/ViewModels/MainViewModel.cs), [`ViewModels/MainViewModel.Display.cs`](Configuration%20Management/ViewModels/MainViewModel.Display.cs), [`ViewModels/MainViewModel.Launch.cs`](Configuration%20Management/ViewModels/MainViewModel.Launch.cs), [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs)): выбранная в «Текущей сессии» разрядность (`SessionArchitectureMode`) теперь передаётся в лаунчер и учитывается первым шагом приоритета. Полный порядок разрешения разрядности стал следующим — 1) «Текущая сессия»; 2) суффикс «(32)/(64)» в версии платформы; 3) глобальная «Разрядность по умолчанию»; 4) «Использовать приоритет базы».
- **Файлы `groups.json` и `infobases.json` сохранялись без форматирования (issue #147)** ([`Services/InfobaseRepository.cs`](Configuration%20Management/Services/InfobaseRepository.cs)): базовые параметры JSON (`JsonOptions`) теперь используют `WriteIndented = true`, поэтому списки групп и информационных баз тоже сохраняются в читаемом виде с переносами строк и отступами (ранее в читаемом виде сохранялся только `settings.json`).

### Версия

- **Версия поднята до `0.3.6.32` → `0.3.6.33`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.31] — 2026-09-02

Выпуск объединяет несколько исправлений стабильности и интерфейса по открытым issues #146–#153: переработан выбор разрядности запуска (добавлен новый приоритет «Использовать приоритет базы»), `settings.json` теперь сохраняется в читаемом виде с переносами строк, исправлены кнопка обновления, сохранение компактного режима правой панели, крах (SIGABRT) при открытии любых диалогов на ПО-рендере/VM, открытие окна настроек при нескольких мониторах, пропавший заголовок окна и зависание Linux при запуске в безрамковом режиме.

### Исправлено

- **Кнопка обновления не обрезает подпись слева (issue #148)** ([`Views/UpdateAvailableWindow.xaml`](Configuration%20Management/Views/UpdateAvailableWindow.xaml)): увеличена ширина и `Padding` левых кнопок `DownloadButton` и `RestartNowButton`, чтобы текст не обрезался слева.
- **Не сохранялся компактный режим правой панели (issue #149)** ([`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs), [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs)): сеттер `ShowRightPanelDetails` теперь сохраняет значение в настройки, а в `Initialize()` оно восстанавливается; добавлено свойство `ShowRightPanelHint`, которое видно только при включённых подробностях и невыбранной базе, поэтому в компактном режиме всплывающая информация больше не появляется.
- **Крах (SIGABRT) при открытии любых диалогов (issue #150)** ([`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs), [`Views/AddEditWindow.Avalonia.cs`](Configuration%20Management/Views/AddEditWindow.Avalonia.cs)): прозрачность модальных окон сведена к `Transparent` без `AcrylicBlur`/`Blur` — запрос blur ронял процесс на ПО-рендере/VM; добавлено виртуальное свойство `UseGlassChrome` (`false` при `SystemDecorations.Full`), а `AddEditWindow` отключает стеклянную обёртку при системном заголовке.
- **Настройки и несколько мониторов (issue #151)** ([`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs)): модальные окна по умолчанию центрируются относительно владельца (`CenterOwner`) вместо экрана — окно настроек открывается на мониторе главного окна.
- **Пропал заголовок окна (issue #152)** ([`Models/AppSettings.cs`](Configuration%20Management/Models/AppSettings.cs), [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs), [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs), [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json), [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json)): добавлено поле `UseSystemTitleBar` и соответствующая настройка — при включении используются `SystemDecorations.Full` без прозрачности, так что системный заголовок окна больше не пропадает.
- **Зависание Linux при запуске (issue #153)** ([`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs), [`Views/ModalWindowBase.cs`](Configuration%20Management/Views/ModalWindowBase.cs)): безрамковое окно с `AcrylicBlur`/`Blur` вызывало непрерывную перерисовку без VSync (~36% CPU) на VM/ПО-рендере; теперь в безрамковом режиме запрашивается только `Transparent` без blur-перерисовки.

### Изменено

- **Перестроен порядок приоритетов разрядности (issue #146)** ([`Services/OneCLauncher.cs`](Configuration%20Management/Services/OneCLauncher.cs), [`Services/OneCLauncher.Linux.cs`](Configuration%20Management/Services/OneCLauncher.Linux.cs)): в `ResolveArchitecture` порядок стал следующим — 1) «Текущая сессия»; 2) суффикс «(32)/(64)» в версии платформы; 3) глобальная «Разрядность по умолчанию»; 4) новый пункт «Использовать приоритет базы», при котором срабатывает явная настройка разрядности базы (вкладка «Разрядность»). Статическое поле `DefaultArchitecture` заменено на `DefaultArchitectureMode` (строка X86/X64/Priority).
- **Читаемые настройки `settings.json` (issue #147)** ([`Services/InfobaseRepository.cs`](Configuration%20Management/Services/InfobaseRepository.cs)): добавлен `SettingsJsonOptions` с `WriteIndented = true` — файл теперь сохраняется с переносами строк и отступами; чтение обратно совместимо.

### Добавлено

- **Пункт «Использовать приоритет базы» в списках выбора разрядности по умолчанию** ([`Views/SettingsWindow.Display.cs`](Configuration%20Management/Views/SettingsWindow.Display.cs), [`Views/SettingsWindow.xaml.cs`](Configuration%20Management/Views/SettingsWindow.xaml.cs), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs), [`ViewModels/MainViewModel.cs`](Configuration%20Management/ViewModels/MainViewModel.cs), [`ViewModels/MainViewModel.Display.cs`](Configuration%20Management/ViewModels/MainViewModel.Display.cs), [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs), [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json), [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json)): добавлена нормализация режима `DefaultArchitectureMode` и локализация ключа `Settings.ArchBasePriority`.
- **Опция «Использовать системный заголовок окна»** в настройках (см. issue #152 выше) с локализацией ключа `Settings.SystemTitleBar`.

### Версия

- **Версия поднята до `0.3.6.30` → `0.3.6.31`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.30] — 2026-09-02

При открытии спонсорской картинки «О программе» (`donat.png`) в полном размере в отдельном окне размер окна теперь равен размеру самой картинки: ширина окна — по ширине картинки, высота — по её пропорциям. Если картинка больше доступной рабочей области экрана, она пропорционально уменьшается и целиком помещается без прокрутки, а размер рабочей области берётся с учётом разрешения и масштаба экрана (DPI).

### Изменено

- **Спонсорская картинка «О программе» в полном размере открывается размером самой картинки** ([`Views/SettingsWindow.Platforms.cs`](Configuration%20Management/Views/SettingsWindow.Platforms.cs)): при клике на картинку `donat.png` она открывается в отдельном окне, ширина которого равна ширине картинки, а высота — по её пропорциям; если картинка больше доступной рабочей области экрана, она пропорционально уменьшается (`Stretch=Uniform`) и целиком помещается в окне без прокрутки; размер рабочей области берётся с учётом разрешения и масштаба экрана (DPI, через `VisualTreeHelper.GetDpi` + `System.Windows.Forms.Screen.WorkingArea`).

### Версия

- **Версия поднята до `0.3.6.29` → `0.3.6.30`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.29] — 2026-09-02

Внешний вид вкладки «О программе» приведён в порядок: кнопка «Проверить обновления» перенесена в строку с текстом версии (рядом с ней), а спонсорская картинка `donat.png` пропорционально уменьшена (MaxWidth 420→240, MaxHeight 560→320), чтобы не уходила за границы окна и не вызывала полную прокрутку.

### Изменено

- **Перенос кнопки «Проверить обновления» в строку версии вкладки «О программе»** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): кнопка проверки обновлений теперь расположена рядом с текстом версии, а не отдельной строкой.
- **Пропорциональное уменьшение спонсорской картинки `donat.png`** ([`Views/SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs)): размеры уменьшены (MaxWidth 420→240, MaxHeight 560→320), чтобы картинка не уходила за границы окна и не вызывала полную прокрутку.

### Версия

- **Версия поднята до `0.3.6.28` → `0.3.6.29`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.28] — 2026-09-02

Новый отчёт пользователя 7OH (issue #146, комментарий https://github.com/sivatorov/ConfigurationManagement/issues/146#issuecomment-5505422453) показал: после импорта баз из файла `ibases.v8i` у базы стоит конкретная версия без выбора разрядности, и при запуске возникает ошибка, хотя задана глобальная «Разрядность по умолчанию» = X64 («приоритет 64, если ничего не задано»). Причина — импортированным базам без явного суффикса разрядности «(32)/(64)» безусловно проставлялось непустое значение `architecture = "32-priority"`. Поскольку глобальная «Разрядность по умолчанию» в `ResolveArchitecture` применяется только при пустой строке разрядности, шаг с глобальной настройкой пропускался и действовал приоритетный режим по стилю 1С с предпочтением 32-бит, поэтому глобальный «приоритет 64» игнорировался — отсюда неверная разрядность или «Платформа не найдена».

### Исправлено

- **Импортированные из `ibases.v8i` базы не учитывали глобальную «Разрядность по умолчанию» (issue #146)** ([`Services/IbasesV8iImporter.cs`](Configuration%20Management/Services/IbasesV8iImporter.cs)): в `ToInfobase` дефолт разрядности изменён с `var architecture = "32-priority"` на `var architecture = string.Empty;` (ветка с явным суффиксом «(32)/(64)» не тронута). Теперь импортированная база без явной разрядности остаётся с пустой строкой разрядности, поэтому в `OneCLauncher.ResolveArchitecture` срабатывает шаг 3 — глобальная «Разрядность по умолчанию». Файл общий для обеих платформ (Windows/WPF и Linux/Avalonia); сборка успешна.

### Версия

- **Версия поднята до `0.3.6.27` → `0.3.6.28`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.27] — 2026-09-02

Коммит `853d99b` (исправление issue #146 в версии 0.3.6.26) обновил порядок приоритетов разрядности только в Windows-лаунчере, но не затронул Linux-двойник, из-за чего в Linux/Avalonia-порте баг выбора разрядности остался: суффикс разрядности в версии платформы проверялся ДО явной настройки базы, и приоритетный режим (32-priority/64-priority) вместе с глобальной настройкой по умолчанию не могли корректно сработать. Метод `ResolveArchitecture` в Linux-порте приведён к тому же корректному порядку приоритетов, что и в Windows.

### Исправлено

- **Приоритет разрядности в Linux-порте (issue #146)** ([`Services/OneCLauncher.Linux.cs`](Configuration%20Management/Services/OneCLauncher.Linux.cs)): метод `ResolveArchitecture` приведён к правильному порядку приоритетов, как в Windows-версии: 1) явная настройка разрядности базы («только 32» / «только 64») — наивысший приоритет; 2) суффикс разрядности в выбранной версии платформы («8.3.27.1688 (64)») — уступает явной настройке базы; 3) глобальная настройка «Разрядность по умолчанию», если в базе ничего не указано; 4) приоритетный режим 32-priority/64-priority по стилю 1С (сравнение лучших установленных версий 32 и 64 через `FindBestVersionDir`/`CompareVersionStrings`). Раньше в Linux-версии суффикс разрядности версии платформы проверялся до явной настройки базы, поэтому приоритетный режим и глобальная настройка по умолчанию не могли корректно сработать. Также обновлён XML-комментарий метода.

### Версия

- **Версия поднята до `0.3.6.26` → `0.3.6.27`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.26] — 2026-09-01

Исправлен выбор разрядности запуска клиента 1С (issue #146). Пользователь сообщил, что после выбора для базы х64-версии платформы (например, «8.3.27 (х64)») при запуске в авторежиме запускалась х86: сменить разрядность на х64 не удавалось, а после успешного выбора обратно на х86 — тоже. Причина была двойной: (1) при выборе той же версии через диалог выбора платформы выполнение прерывалось ранним `return`, поэтому суффикс разрядности «(32)/(64)» не переносился в поле разрядности базы; (2) в методе выбора разрядности суффикс версии имел приоритет над явной настройкой разрядности базы, что противоречило приоритетам, описанным пользователем.

### Исправлено

- **Смена разрядности той же версии платформы (issue #146)** ([`Views/MainWindow.Events.cs`](Configuration%20Management/Views/MainWindow.Events.cs), [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs)): убран ранний `return` при совпадении выбранной и текущей версии платформы в методах `OpenPlatformVersionPicker` и `PickPlatformVersionFor`. Теперь суффикс разрядности «(32)/(64)» всегда переносится в поле разрядности базы даже если версия не изменилась — можно переключить х86 ↔ х64 одной и той же версии в обе стороны.
- **Приоритет явной настройки разрядности базы над суффиксом версии (issue #146)** ([`Services/OneCLauncher.cs`](Configuration%20Management/Services/OneCLauncher.cs)): в `ResolveArchitecture` порядок приоритетов приведён к описанному в issue: явная настройка разрядности базы («только 32» / «только 64») → суффикс разрядности в выбранной версии платформы → глобальная настройка «Разрядность по умолчанию» → приоритетный режим (32-priority / 64-priority) по стилю 1С. Раньше суффикс версии обрабатывался раньше явной настройки базы, из-за чего «только 64» могло игнорироваться, если в версии оставался суффикс «(32)».

### Версия

- **Версия поднята до `0.3.6.25` → `0.3.6.26`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.25] — 2026-09-01

Устаревшие эмодзи-иконки команд заменены на современные векторные иконки Material Design (`materialDesign:PackIcon`) в Windows/WPF-версии. Теперь иконки главного окна (поиск), окна добавления информационной базы, темы поиска, окон групп и профилей стали векторными: они перекрашиваются цветом темы/акцента и выглядят единообразно с остальным интерфейсом.

### Изменено

- **Замена эмодзи-иконок на векторные Material Design (`materialDesign:PackIcon`)** ([`Views/AddEditWindow.xaml`](Configuration%20Management/Views/AddEditWindow.xaml), [`Views/MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml), [`Themes/LightTheme.xaml`](Configuration%20Management/Themes/LightTheme.xaml), [`Themes/DarkTheme.xaml`](Configuration%20Management/Themes/DarkTheme.xaml), [`Views/GroupEditWindow.xaml`](Configuration%20Management/Views/GroupEditWindow.xaml), [`Views/GroupSettingsWindow.xaml`](Configuration%20Management/Views/GroupSettingsWindow.xaml), [`Views/ProfilesPanel.xaml`](Configuration%20Management/Views/ProfilesPanel.xaml), [`Views/ProfilesWindow.xaml`](Configuration%20Management/Views/ProfilesWindow.xaml)): эмодзи 📋💾📦📁🔍🗑 заменены на векторные `materialDesign:PackIcon`: в окне добавления базы — `ClipboardTextOutline`, `ContentSave`, `CubeOutline`, `FolderOutline` (22×22); в главном окне — `Magnify` (поиск); в шаблоне `SearchTextBox` тем — `Magnify`; в окнах групп — `FolderOutline`; в окнах профилей и настроек групп — `DeleteOutline`. Иконки стали векторными, перекрашиваются цветом темы/акцента и выглядят единообразно с остальным интерфейсом.

### Версия

- **Версия поднята до `0.3.6.24` → `0.3.6.25`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.24] — 2026-09-01

Из списка режимов клиента блока «Текущая сессия» убрана подпись «(управляемые формы)» — пункт «Толстый клиент» теперь называется просто «Толстый». Обновлена справка блока: режимы перечислены как «Авто / Обычный (обычные формы, RunModeOrdinaryApplication) / Толстый (управляемые формы, /RunModeManagedApplication) / Тонкий» — с явным пояснением соответствия пунктов режимам форм 1С (issue #144, замечание в комментарии пользователя 7OH).

### Изменено

- **Подпись пункта толстого клиента и справка блока «Текущая сессия» (issue #144)** ([`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json), [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json)): ключ `Main.SessionClientThickManaged` — значение «Толстый (управляемые формы)»/«Thick (managed forms)» заменено на «Толстый»/«Thick» (подпись «(управляемые формы)» убрана). Ключ `Main.CurrentSessionHelp` — буллет режима клиента обновлён на «Авто / Обычный (обычные формы, RunModeOrdinaryApplication) / Толстый (управляемые формы, /RunModeManagedApplication) / Тонкий.» в справке блока, чтобы явно пояснить соответствие пунктов режимам форм 1С (замечание пользователя 7OH в issue #144).

### Версия

- **Версия поднята до `0.3.6.23` → `0.3.6.24`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.23] — 2026-09-01

Исправлено скачивание обновления на Windows/WPF: раньше при загрузке крупного релиз-файла (~79 МБ) провайдер/прокси на ~5% сбрасывал соединение, и скачивание каждый раз начиналось с нуля, а при повторном обрыве показывалась ошибка «не удалось скачать обновление». Теперь скачивание устойчиво к обрывам соединения: частично скачанный файл сохраняется во временном каталоге, а при повторе докачивается с места обрыва через HTTP Range (до 12 попыток с паузой между ними) вместо полного перезапуска.

### Исправлено

- **Устойчивость скачивания обновления к обрывам соединения** ([`Services/UpdateService.cs`](Configuration%20Management/Services/UpdateService.cs)): переписан метод `DownloadAsync` — добавлен цикл повторов с докачкой (до 12 попыток, с паузой между ними), частично скачанный файл сохраняется во временном каталоге. Добавлены методы `DownloadChunkAsync` (скачивание фрагмента с поддержкой ответов `206`/`200` и заголовком `Range`), `ParseContentRangeTotal`, `TryGetFileLength`; добавлены using `System.Net` и `System.Net.Http.Headers`. При обрыве соединения загрузка продолжается с места обрыва через HTTP Range вместо полного перезапуска.

### Версия

- **Версия поднята до `0.3.6.22` → `0.3.6.23`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.22] — 2026-09-01

Уменьшено количество окон при обновлении (Windows/WPF): ошибки скачивания/установки теперь показываются **внутри единого окна обновления** `UpdateAvailableWindow`, а не отдельным модальным окном ошибки поверх. Раньше при ошибке окно обновления закрывалось, после чего открывалось отдельное модальное окно `MaterialMessageWindow` — пользователь видел несколько окон друг за другом. Теперь в `UpdateAvailableWindow` добавлена панель `ErrorPanel` с заголовком «Ошибка обновления», текстом ошибки и кнопкой закрытия; ошибка отображается в том же окне обновления, поэтому весь процесс всегда проходит в одном окне.

### Изменено

- **Ошибки скачивания/установки показываются внутри окна обновления** ([`Services/UpdateAvailableWindow.xaml`](Configuration%20Management/Services/UpdateAvailableWindow.xaml), [`Services/UpdateAvailableWindow.xaml.cs`](Configuration%20Management/Services/UpdateAvailableWindow.xaml.cs)): добавлена панель `ErrorPanel` (этап 5) с заголовком `{loc:Loc Update.ErrorTitle}`, текстом и кнопкой закрытия. Добавлен метод `ShowError(string)`; все вызовы `_service.ShowErrorOnUi(...)` + `Close()` заменены на `ShowError(...)` — ошибки остаются в том же окне обновления, а не показываются отдельным модальным окном поверх.
- **Локализация заголовка ошибки обновления** ([`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json), [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json)): добавлен ключ `Update.ErrorTitle` — «Ошибка обновления» / «Update error».

### Версия

- **Версия поднята до `0.3.6.21` → `0.3.6.22`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.21] — 2026-09-01

В окне выбора версии платформы 1С теперь доступен выбор не только полных 4-компонентных версий (например, `8.3.27.2295`), но и **частичных префиксов** — линии (`8.3`) и группы сборок (`8.3.27`) — с сохранением указания разрядности «(32)/(64)`. Если выбрана частичная версия, при запуске подставляется **максимальная из установленных сборок**, соответствующая префиксу и нужной разрядности; поиск ведётся по **всем каталогам платформ** из настроек, включая дополнительные диски. В Linux/Avalonia-порте эта возможность теперь работает так же, как в Windows/WPF. Кроме того, из списка режимов клиента блока «Текущая сессия» удалён дублирующий пункт «Толстый (обычные формы)» — осталось 4 режима: Авто, Толстый клиент, Тонкий клиент, Обычный режим.

### Добавлено/Изменено

- **Выбор частичной версии платформы в Linux/Avalonia-порте (issue #142)** ([`Views/PlatformVersionPickerWindow.Avalonia.cs`](Configuration%20Management/Views/PlatformVersionPickerWindow.Avalonia.cs), [`Services/PlatformVersionService.Linux.cs`](Configuration%20Management/Services/PlatformVersionService.Linux.cs), [`Services/OneCLauncher.Linux.cs`](Configuration%20Management/Services/OneCLauncher.Linux.cs)): в окне выбора платформы разрешён выбор не только полных 4-компонентных версий (например, `8.3.27.2295`), но и **частичных префиксов** — линии (`8.3`) и группы сборок (`8.3.27`) — с сохранением указания разрядности. Если выбран префикс `8.3.27`, при запуске подставляется максимальная из установленных сборок `8.3.27.*`; если `8.3.27 [х64]` — максимальная из 27-х с отбором по х64; аналогично для 2-компонентного `8.5`. Поиск ведётся по **всем каталогам платформ** из настроек (включая дополнительные диски). На Windows/WPF функция уже была реализована — теперь она работает и в Linux/Avalonia-порте.

### Изменено

- **Список режимов клиента в блоке «Текущая сессия» (issue #144)** ([`Views/MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml), [`Views/MainWindow.Avalonia.cs`](Configuration%20Management/Views/MainWindow.Avalonia.cs), [`Views/SettingsWindow.Avalonia.cs`](Configuration%20Management/Views/SettingsWindow.Avalonia.cs), [`ViewModels/MainViewModel.Display.cs`](Configuration%20Management/ViewModels/MainViewModel.Display.cs), [`ViewModels/MainViewModel.Launch.cs`](Configuration%20Management/ViewModels/MainViewModel.Launch.cs), [`ViewModels/MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs), [`Models/SessionLaunchModes.cs`](Configuration%20Management/Models/SessionLaunchModes.cs), [`Localization/Languages/ru.json`](Configuration%20Management/Localization/Languages/ru.json), [`Localization/Languages/en.json`](Configuration%20Management/Localization/Languages/en.json)): удалён дублирующий пункт «Толстый (обычные формы)»/`ThickOrdinary` из блока «Текущая сессия». Осталось **4 пункта**: Авто, Толстый клиент, Тонкий клиент, Обычный режим. Пункт «Обычный режим» теперь задаёт толстый клиент в обычных формах (сохраняет поведение удалённого дубля). Значение `ThickOrdinary` удалено из enum `SessionLaunchModes`, ключи `Main.SessionClientThickOrdinary` и `Main.SessionThickOrdinaryTooltip` удалены из `ru.json`/`en.json`.

### Версия

- **Версия поднята до `0.3.6.20` → `0.3.6.21`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.20] — 2026-09-01

Исправлено поведение автообновления: раньше при включённой настройке **«Автоматически обновлять приложение»** программа при обнаружении новой версии молча скачивала и устанавливала её **без окна с вопросом**. Теперь в Windows-версии при обнаружении новой версии **всегда** показывается единый диалог `UpdateAvailableWindow` с вопросом «Перезапустить сейчас / Обновить после закрытия» и прогрессом скачивания — независимо от состояния автообновления.

### Исправлено

- **Пропадающее окно с вопросом при автообновлении** ([`UpdateService.cs`](Configuration%20Management/Services/UpdateService.cs) — `CheckForUpdatesAsync`): удалено ветвление по `AutoUpdateEnabled`, при котором при включённом автообновлении новая версия устанавливалась без диалога. Теперь при обнаружении новой версии **всегда** вызывается `ShowUpdateDialog` — показывается единый диалог `UpdateAvailableWindow` с вопросом «Перезапустить сейчас / Обновить после закрытия» и прогрессом скачивания.
- **Комментарий в точке входа** ([`App.xaml.cs`](Configuration%20Management/App.xaml.cs)): комментарий приведён в соответствие с новым поведением (диалог обновления показывается всегда).
- **Локализация тултипа автообновления** ([`ru.json`](Configuration%20Management/Localization/Languages/ru.json), [`en.json`](Configuration%20Management/Localization/Languages/en.json)): переформулирован тултип `Settings.General.AutoUpdateTooltip` — больше не подразумевает молчаливую установку обновления без запроса.

### Версия

- **Версия поднята до `0.3.6.19` → `0.3.6.20`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.19] — 2026-09-01

Переработан живой предпросмотр цветовой схемы в окне настроек (вкладка «Цветовое оформление», issue #137): вместо одного превью с переключателем «светлая/тёмная» теперь показываются **сразу обе палитры вертикально** — сверху светлая, снизу тёмная — без необходимости переключения. Редактор цветов слева по-прежнему редактирует одну выбранную палитру. Само превью стало **уже** (ширина уменьшена с 220 до ~175 пкс), чтобы кнопка «Выбрать цвет» в списке цветов слева полностью помещалась.

### Изменено

- **Предпросмотр цветовой схемы в настройках (issue #137)** ([`SettingsWindow.Schemes.cs`](Configuration%20Management/Views/SettingsWindow.Schemes.cs)): вкладка «Цветовое оформление» окна «Настройки» теперь показывает **сразу обе палитры** — светлую сверху и тёмную снизу — вместо одного превью с переключателем «светлая/тёмная». Редактор цветов слева продолжает редактировать одну выбранную палитру (вариант темы выбирается как раньше).
- **Ширина предпросмотра уменьшена** (issue #137): ширина миниатюры палитры снижена с 220 до ~175 пкс, чтобы кнопка «Выбрать цвет» в списке цветов слева больше не обрезалась и полностью помещалась.

### Версия

- **Версия поднята до `0.3.6.18` → `0.3.6.19`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.18] — 2026-09-01

Доработаны выбор и запуск версии платформы 1С, а также объединён процесс обновления приложения. Теперь можно выбирать **неполную версию платформы** (например, `8.5`, `8.3.27`) с сохранением указания разрядности «(32)/(64)» — при выборе неполной версии программа подбирает **новейшую установленную версию**, соответствующую префиксу и нужной разрядности (полные версии из четырёх частей работают как раньше). При явном выборе тонкого/толстого клиента поиск исполняемого файла (`1cv8.exe` / `1cv8c.exe` / `1cv8x64.exe`) теперь корректно подбирает новейшую установленную версию по указанному префиксу и разрядности и больше не подставляет произвольную старшую версию другой линейки. Два диалога обновления (системный `MessageBox` и красивое окно) объединены в **один красивый диалог**: весь процесс — предложение → скачивание → вопрос «Перезапустить сейчас / Обновить после закрытия» → применение — происходит в одном окне, системный `MessageBox` убран.

### Добавлено

- **Выбор неполной версии платформы 1С (issue #142)** ([`PlatformVersionService.cs`](Configuration%20Management/Services/PlatformVersionService.cs), [`OneCLauncher.cs`](Configuration%20Management/Services/OneCLauncher.cs), [`PlatformVersionPickerWindow.xaml.cs`](Configuration%20Management/Views/PlatformVersionPickerWindow.xaml.cs)): в окне выбора версии платформы можно указать неполную версию (например, `8.5`, `8.3.27`) с сохранением указания разрядности «(32)/(64)». При выборе неполной версии программа автоматически подбирает **новейшую установленную версию**, соответствующую префиксу и нужной разрядности; полные версии (из четырёх частей) работают как раньше.

### Исправлено

- **«Не тот клиент» при явном выборе тонкого/толстого клиента (issue #28)** ([`OneCLauncher.cs`](Configuration%20Management/Services/OneCLauncher.cs)): поиск исполняемого файла (`1cv8.exe` / `1cv8c.exe` / `1cv8x64.exe`) теперь корректно подбирает новейшую установленную версию, соответствующую указанному префиксу версии и разрядности, и не подставляет произвольную старшую версию другой линейки (связано с #142).
- **Единый диалог обновления (issue #143)** ([`UpdateService.cs`](Configuration%20Management/Services/UpdateService.cs), [`UpdateAvailableWindow.xaml.cs`](Configuration%20Management/Views/UpdateAvailableWindow.xaml.cs)): два диалога обновления (некрасивый системный `MessageBox` и красивое окно) объединены в **один красивый диалог**. Весь процесс — предложение → скачивание → вопрос «Перезапустить сейчас / Обновить после закрытия» с ясными формулировками → применение — происходит в одном окне; системный `MessageBox` убран.

### Локализация

- Обновлены строки диалога обновления в [`ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`en.json`](Configuration%20Management/Localization/Languages/en.json) под объединённый процесс обновления.

### Версия

- **Версия поднята до `0.3.6.17` → `0.3.6.18`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.17] — 2026-09-01

Доработана кнопка удаления пользовательского параметра запуска (issue #141): теперь она содержит **иконку и текст «Удалить»**, а не только иконку, и полностью помещается в окне «Параметры запуска».

### Изменено

- **Кнопка «Удалить» пользовательского параметра** ([`LaunchParametersWindow.xaml`](Configuration%20Management/Views/LaunchParametersWindow.xaml)): кнопка `BtnRemoveParam` расширена до 104 пкс и теперь показывает иконку `Delete` (красную) рядом с текстом «Удалить» (`Common.Delete`) — содержимое кнопки больше не обрезается и понятно обозначает действие.

### Версия

- **Версия поднята до `0.3.6.16` → `0.3.6.17`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.16] — 2026-09-01

Исправления по итогам обсуждений в issues #141, #139, #137 и #119: у пользовательских параметров запуска появился необязательный комментарий и исправлена вёрстка кнопок; окно выбора иконки группы перестроено (иконки сверху, цвет ниже); предпросмотр темы в настройках сделан уже, чтобы не теснить кнопки выбора цвета; выравнивание колонок списка баз теперь пересчитывается при раскрытии/сворачивании групп.

### Исправлено

- **Пользовательские параметры запуска (issue #141)** ([`LaunchParametersWindow.xaml`](Configuration%20Management/Views/LaunchParametersWindow.xaml), [`LaunchParametersWindow.xaml.cs`](Configuration%20Management/Views/LaunchParametersWindow.xaml.cs)): добавлено поле «Комментарий» рядом с полем ввода ключа — введённый текст отображается в справочнике вместо общей пометки «Пользовательский параметр» (комментарий хранится в `settings.json` через разделитель табуляции, ключ командной строки подставляется в поле «Параметры» без комментария). Кнопка «Добавить» расширена до 120 пкс, а кнопка «Удалить» — до 36 пкс, чтобы содержимое полностью помещалось.
- **Окно выбора иконки группы (issue #139)** ([`GroupEditWindow.xaml`](Configuration%20Management/Views/GroupEditWindow.xaml)): на вкладке «Иконка» сетка иконок поднята выше пикера цвета иконки, как просил автор (цвет — ниже).
- **Предпросмотр темы в настройках (issue #137)** ([`SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml)): ширина миниатюры предпросмотра уменьшена с 264 до 220 пкс, чтобы не теснить кнопки «Выбрать» у списка цветов в левой колонке.
- **Выравнивание колонок при раскрытии групп (issue #119)** ([`MainWindow.xaml.cs`](Configuration%20Management/Views/MainWindow.xaml.cs), [`MainWindow.Columns.cs`](Configuration%20Management/Views/MainWindow.Columns.cs)): раскрытие/сворачивание узла дерева больше не сбивает компенсатор сдвига заголовка — после изменения состояния группы выравнивание заголовка с данными пересчитывается (`OnMainTree_GroupExpansionChanged`), поэтому колонки больше не «уезжают» отдельно от содержимого.

### Локализация

- Добавлены строки `LaunchParams.CustomCommentTooltip` (ru/en) для подсказки поля комментария пользовательского параметра.

### Версия

- **Версия поднята до `0.3.6.15` → `0.3.6.16`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.15] — 2026-09-01

Исправлена регрессия из issue #140 (выбор доступных серверов хранилища): при открытии окна **«Настройки подключения»** происходил сбой `XamlParseException` («ClipboardPaste is not a valid value for PackIconKind»), из-за чего редактирование базы приводило к ошибке интерфейса. Причина — кнопка «Вставить» на вкладке «Хранилище» использовала несуществующее значение `PackIconKind="ClipboardPaste"`.

### Исправлено

- **Кнопка «Вставить» на вкладке «Хранилище»** ([`ConnectionSettingsWindow.xaml`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml:430)): несуществующее значение иконки `ClipboardPaste` заменено на валидное `ContentPaste` (как в соседних кнопках вставки). Все значения `PackIconKind`, используемые в XAML проекта, дополнительно проверены отражением против перечисления `MaterialDesignThemes.Wpf.PackIconKind` — других невалидных значений нет.

### Версия

- **Версия поднята до `0.3.6.14` → `0.3.6.15`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.14] — 2026-09-01

Исправлено оформление заголовков окон (issue #135): акцентная полоса заголовка диалогов теперь **заливается на всю ширину окна** без незаполненных участков по краям, а у главного окна **цвет акцента при активном состоянии не теряется после смены схемы/темы** — при перекраске берётся фактическое состояние активности окна, а не устаревший кэш.

### Исправлено

- **Полоса заголовка диалогов** ([`WindowChromeHelper.cs`](Configuration%20Management/Views/WindowChromeHelper.cs) — `BuildTitleBar`): у акцентной полосы явно заданы `HorizontalAlignment=Stretch` и нулевые `Margin`, чтобы она занимала всю ширину окна.
- **Акцент активного главного окна** ([`MainWindow.xaml.cs`](Configuration%20Management/Views/MainWindow.xaml.cs)): при смене темы/схемы перекраска шапки использует фактическое `IsActive` окна (`_isActive = IsActive`) вместо возможно устаревшего кэша — акцентная заливка активного окна больше не пропадает.

### Версия

- **Версия поднята до `0.3.6.13` → `0.3.6.14`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.13] — 2026-09-01

Дополнена защита от «зависания» при запуске поверх легаси/повреждённых конфигурационных файлов (issue #64): загрузка `ibases.json` и `groups.json` теперь отбрасывает **пустые элементы списка**, которые могли прийти из старых файлов, — раньше обращение к свойствам такого `null`-элемента могло уронить загрузку (`NullReferenceException`) и оставить процесс без главного окна.

### Улучшено

- **Защита загрузки баз** ([`InfobaseRepository.cs`](Configuration%20Management/Services/InfobaseRepository.cs) — `Load`): из десериализованного списка удаляются `null`-элементы.
- **Защита загрузки групп** ([`InfobaseRepository.cs`](Configuration%20Management/Services/InfobaseRepository.cs) — `LoadGroups`): `null`-элементы отфильтровываются до проверок идентификаторов, чтобы `g.Id` не обращался к пустой ссылке. Ранее уже реализованные `NormalizeForLoad`, карантин повреждённых файлов и восстановление схемы версии сохраняются.

### Версия

- **Версия поднята до `0.3.6.12` → `0.3.6.13`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.12] — 2026-09-01

Дерево выбора версии платформы надёжнее группируется **по линии — первым двум цифрам** (8.2, 8.3, 8.5), как в стартере 1С (issue #9): даже для нестандартного варианта версии с нечисловым сегментом версия попадает в свою линию по двум ведущим числам.

### Изменено

- **Группировка линий версий** ([`PlatformVersionService.cs`](Configuration%20Management/Services/PlatformVersionService.cs) — `GetVersionLine`): при определении линии берутся первые два числовых сегмента версии (например `8.3.27.1688 (64)` → `8.3`), что гарантирует корректное дерево «линия → группа сборок (8.3.27) → сборка» даже при нестандартных строках варианта.

### Версия

- **Версия поднята до `0.3.6.11` → `0.3.6.12`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.11] — 2026-09-01

Исправлен запуск не того клиента (issue #28): при явном выборе толстого/тонкого клиента, когда подходящий исполняемый файл платформы не находился, запасной откат на общий лаунчер `1CEStart.exe` открывал стартер со списком баз («обычное приложение») вместо подключения к выбранной базе в нужном режиме. Теперь для явного типа клиента такой откат запрещён и выводится понятное предупреждение «платформа не найдена».

### Исправлено

- **Запуск явного типа клиента** ([`OneCLauncher.cs`](Configuration%20Management/Services/OneCLauncher.cs) — `FindExecutable`): откат на `1CEStart.exe` допускается только при автоматическом выборе клиента (`clientType == null`) в режиме «Предприятие». Для явного тонкого/толстого клиента (в т.ч. через контекстное меню «Толстый клиент») возвращается `null` с предупреждением, чтобы не запускалось постороннее приложение.

### Версия

- **Версия поднята до `0.3.6.10` → `0.3.6.11`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.10] — 2026-09-01

Исправлен запуск не той версии платформы (issue #29): если у базы выбрана конкретная версия платформы, но её каталог не находился в нужной разрядности, запасной поиск подставлял **произвольную новейшую** установленную версию — и запускалась совсем не та платформа. Теперь запасной поиск ограничивается **той же выбранной версией** и не выбирает чужую.

### Исправлено

- **Выбор исполняемого файла платформы по конкретной версии** ([`OneCLauncher.cs`](Configuration%20Management/Services/OneCLauncher.cs) — `FindExecutable`): во втором проходе (запасной поиск по установленным версиям) при заданном `cleanVersion` теперь отбрасываются каталоги других версий. Если выбранная версия есть в другой разрядности — берётся она; если нет вовсе — показывается предупреждение «платформа не найдена» вместо запуска произвольной версии.

### Версия

- **Версия поднята до `0.3.6.9` → `0.3.6.10`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.9] — 2026-09-01

Исправлен баг смены темы после пользовательской (issue #136): если выбрать свою тему, сохранить, а затем снова в настройках выбрать «Светлая» — цвета не менялись (работала только комбинация «Светлая + сброс цветов»). Причина: метод `GetSchemeForTheme` всегда возвращал активную пользовательскую схему, поэтому выбор встроенной светлой темы подставлял её цвета из пользовательского набора.

### Исправлено

- **Применение встроенной базовой темы после пользовательской** ([`MainViewModel.Theme.cs`](Configuration%20Management/ViewModels/MainViewModel.Theme.cs) — `GetSchemeForTheme`): теперь для встроенной темы «Светлая»/«Тёмная» возвращаются её собственные цвета по умолчанию, если активной является пользовательская или чужая встроенная схема. Если же активная схема — та же встроенная базовая тема, возвращаются сохранённые правки пользователя этой темы. Выбор «Светлой» в редакторе оформления снова меняет цвета без ручного «сброса цветов».

### Версия

- **Версия поднята до `0.3.6.8` → `0.3.6.9`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.8] — 2026-09-01

Список шаблонов при создании базы из шаблона стал удобнее (issue #138): добавлены **кнопки «Свернуть»/«Развернуть»** всех групп, **поиск** по названию/поставщику/версии/папке и флажок **«Подробности»**, который скрывает подпись с доп. информацией (поставщик, версия, путь), делая строки дерева заметно уже и позволяя видеть больше строк.

### Добавлено

- **Панель управления списком шаблонов** ([`CreateInfobaseWindow.xaml`](Configuration%20Management/Views/CreateInfobaseWindow.xaml)): над деревом шаблонов появились поле поиска `TplSearchBox`, кнопки `TplCollapseAll`/`TplExpandAll` и флажок `TplDetails`. Видимость подписи (версия/поставщик/папка) привязана к флажку через `BooleanToVisibilityConverter`.
- **Поиск по шаблонам** ([`CreateInfobaseWindow.xaml.cs`](Configuration%20Management/Views/CreateInfobaseWindow.xaml.cs)): `ApplyTemplateFilter` / `FilterTemplateNodes` / `NodeMatchesQuery` — дерево фильтруется по запросу с сохранением ветвей к совпадающим листьям (по названию, подписи, поставщику, имени конфигурации).
- **Свёртка/развёртка групп**: `OnTplCollapseAll_Click` / `OnTplExpandAll_Click` и `SetAllExpanded` / `WalkAndToggle` рекурсивно раскрывают/скрывают все группы дерева с учётом ленивой генерации контейнеров.
- **Локализация**: ключи `CreateInfobase.TplSearchTooltip`, `TplCollapseAll`, `TplCollapseAllTooltip`, `TplExpandAll`, `TplExpandAllTooltip`, `TplDetails` в [`ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`en.json`](Configuration%20Management/Localization/Languages/en.json).

### Версия

- **Версия поднята до `0.3.6.7` → `0.3.6.8`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.7] — 2026-09-01

Окно выбора иконки для группы/папки больше **не растягивается выше экрана**: раньше из-за автоподбора высоты (`SizeToContent="Height"`) окно могло вырасти до `MaxHeight=880`, и нижняя часть нужной вкладки уходила под панель задач. Теперь у окна фиксированная высота, а содержимое вкладок «Цвет» и «Иконка» прокручивается внутренними скроллбарами (issue #139).

### Исправлено

- **Окно редактирования группы** ([`GroupEditWindow.xaml`](Configuration%20Management/Views/GroupEditWindow.xaml)): убран автоподбор высоты `SizeToContent="Height"`, задана фиксированная высота окна `Height="640"`. Вкладки «Цвет» и «Иконка» уже разделены, а их содержимое (`ColorTabScroller` / `IconTabScroller`) имеет собственную вертикальную прокрутку, поэтому при большом количестве иконок или на маленьком экране окно остаётся в пределах рабочей области и не выходит за панель задач.

### Версия

- **Версия поднята до `0.3.6.6` → `0.3.6.7`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.6] — 2026-09-01

Поле «Сервер хранилища» в окне настройки подключения к информационной базе теперь работает как поле сервера 1С: поддерживает **выбор из списка доступных серверов хранилища** других баз и **кнопку «Вставить» с разделением** — обычно пользователь копирует из 1С единое поле подключения (например `tcp://server:1542/ИмяХранилища`), и оно автоматически делится на адрес сервера и имя хранилища (issue #140).

### Добавлено

- **Выпадающий список серверов хранилища** ([`ConnectionSettingsWindow.xaml`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml)): поле «Адрес сервера» хранилища конфигурации заменено с `TextBox` на редактируемый `ComboBox`, привязанный к `AvailableRepositoryServers`; список собирается из настроек хранилища других баз ([`MainViewModel.Commands.cs`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs) — `GetAvailableRepositoryServers`, [`ConnectionSettingsViewModel.cs`](Configuration%20Management/ViewModels/ConnectionSettingsViewModel.cs) — `AvailableRepositoryServers` / `SetAvailableRepositoryServers`).
- **Кнопка «Вставить» с разделением** рядом с полем сервера хранилища ([`ConnectionSettingsWindow.xaml.cs`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml.cs) — `OnPasteRepositorySplit_Click`): читает буфер обмена, убирает префикс схемы (`tcp://`, `file://` и т.п.) и делит строку по первому `/` на «адрес сервера» и «имя хранилища» (метод `SplitRepositoryConnectionString` в [`ConnectionSettingsViewModel.cs`](Configuration%20Management/ViewModels/ConnectionSettingsViewModel.cs)).
- **Локализация**: ключи `Connection.RepositoryPaste`, `Connection.RepositoryPasteTooltip` в [`ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`en.json`](Configuration%20Management/Localization/Languages/en.json).

### Версия

- **Версия поднята до `0.3.6.5` → `0.3.6.6`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.5] — 2026-09-01

В справочник параметров запуска теперь можно добавлять **свои (пользовательские) ключи командной строки** (issue #141): встроенный список ключей 1С в окне «Конфигуратор параметров запуска» больше не является единственным источником — пользователь может расширить его собственными параметрами, которые сохраняются между запусками и подставляются в строку запуска двойным кликом.

### Добавлено

- **Пользовательские параметры запуска** ([`LaunchParametersWindow.xaml`](Configuration%20Management/Views/LaunchParametersWindow.xaml) / [`LaunchParametersWindow.xaml.cs`](Configuration%20Management/Views/LaunchParametersWindow.xaml.cs)): в блоке «Справочник параметров» появилось поле ввода + кнопка **«Добавить»** для внесения собственного ключа. Пользовательские параметры помечаются в списке как «Пользовательский параметр», подставляются двойным кликом и удаляются кнопкой корзины или клавишей `Del`. Список пользовательских параметров сохраняется глобально и доступен как из диалога запуска с параметрами, так и из окна «Настройки подключения» базы.
- **Хранение пользовательских параметров** ([`AppSettings.cs`](Configuration%20Management/Models/AppSettings.cs)): новое поле `CustomLaunchParameters` (список строк) сохраняется в `settings.json`; загрузка/сохранение и обратный вызов для персиста реализованы в [`MainViewModel.cs`](Configuration%20Management/ViewModels/MainViewModel.cs) (`CustomLaunchParameters`, `SetCustomLaunchParameters`) и [`MainViewModel.Launch.cs`](Configuration%20Management/ViewModels/MainViewModel.Launch.cs).
- **Локализация**: ключи `LaunchParams.CustomMarker`, `LaunchParams.CustomInputTooltip`, `LaunchParams.CustomAdd`, `LaunchParams.CustomRemove` в [`ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`en.json`](Configuration%20Management/Localization/Languages/en.json).

### Версия

- **Версия поднята до `0.3.6.4` → `0.3.6.5`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.4] — 2026-09-01

Процесс автоматического обновления стал нагляднее: во время скачивания новой версии в строке состояния главного окна отображается **индикатор прогресса загрузки**, а после успешного скачивания приложение предлагает выбрать — **«Перезапустить сейчас»** или **«Обновить после закрытия программы»**. Это убирает неожиданный мгновенный перезапуск и позволяет пользователю решить, когда установить обновление.

### Добавлено

- **Индикатор прогресса загрузки обновления в строке состояния** главного окна: в [`MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml) добавлена скрытая по умолчанию панель `UpdateProgressPanel` с текстом `UpdateProgressText` и `ProgressBar UpdateProgressBar`. В [`UpdateService.cs`](Configuration%20Management/Services/UpdateService.cs) добавлены события `DownloadProgressChanged` (double, проценты 0–100, или −1 при неизвестной длине) и `DownloadFinished`; `DownloadAsync` читает поток буфером и сообщает о прогрессе. Подписка на эти события и обновление индикатора в UI-потоке реализованы в [`MainWindow.xaml.cs`](Configuration%20Management/Views/MainWindow.xaml.cs) (обработчики `OnUpdateDownloadProgressChanged` / `OnUpdateDownloadFinished`).
- **Локализация индикатора прогресса и диалога выбора**: ключи `Update.DownloadProgress`, `Update.DownloadProgressFormat`, `Update.RestartOrLater` в [`ru.json`](Configuration%20Management/Localization/Languages/ru.json) и [`en.json`](Configuration%20Management/Localization/Languages/en.json).

### Изменено

- **Выбор «Перезапустить сейчас» / «Обновить после закрытия программы»** ([`UpdateService.cs`](Configuration%20Management/Services/UpdateService.cs)): после успешного скачивания вместо немедленного перезапуска показывается диалог `MessageBox`. При выборе **«Да»** — помощник (`restart: true`) ждёт закрытия, заменяет exe и перезапускает приложение, затем `app.Shutdown`; при выборе **«Нет»** — помощник (`restart: false`) НЕ перезапускает приложение, обновление применяется при естественном завершении процесса. Управляется параметром `restart` у `CreateUpdaterScript`.

### Версия

- **Версия поднята до `0.3.6.3` → `0.3.6.4`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.3] — 2026-09-01

Окно предпросмотра темы вкладки «Цветовое оформление» теперь **всегда видимо и не прокручивается** вместе со списком цветов: живой предпросмотр закреплён справа от настроек, а прокручиваются только сами настройки (тема + редактор цветов). При малой высоте окна предпросмотр имеет собственную внутреннюю прокрутку, поэтому остаётся доступным целиком независимо от размера окна.

### Изменено

- **Вкладка «Цветовое оформление»** в [`SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml): внешний `ScrollViewer` заменён на `Grid` с двумя колонками — левая содержит `ScrollViewer` с настройками (тема + редактор цветов), правая — закреплённый `GroupBox` «Предпросмотр», который всегда виден и не прокручивается вместе с настройками. Сам предпросмотр обёрнут во внутренний `ScrollViewer` с `MaxHeight="520"`, обеспечивающий собственную прокрутку при малой высоте окна.
- **Сохранены имена элементов**: `PreviewShell` и все `Preview*` не переименовывались, метод `RefreshSchemePreview()` в [`SettingsWindow.Schemes.cs`](Configuration%20Management/Views/SettingsWindow.Schemes.cs) не менялся.

### Версия

- **Версия поднята до `0.3.6.2` → `0.3.6.3`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.2] — 2026-09-01

Два тумблера палитры (светлая/тёмная) в редакторе цветов вкладки «Оформление» заменены на **одну кнопку-переключатель** светлой/тёмной темы — по аналогии с кнопкой смены темы главного окна. Теперь выбор редактируемой палитры выполняется одной компактной кнопкой с иконкой и подписью, что упрощает переключение между палитрами одной схемы.

### Изменено

- **Единая кнопка-переключатель палитры** в [`SettingsWindow.xaml`](Configuration%20Management/Views/SettingsWindow.xaml): два тумблера (`MaterialDesignSwitchToggleButton`) заменены на одну кнопку (`PaletteToggleButton`) в стиле `IconButton` с иконкой `PaletteToggleIcon` (`IconSun`/`IconMoon`, как у кнопки смены темы главного окна) и подписью текущей палитры `PaletteStateText`.
- **Логика переключения** в [`SettingsWindow.Schemes.cs`](Configuration%20Management/Views/SettingsWindow.Schemes.cs): обработчик `OnPaletteSwitch_Click` переключает редактируемую палитру на противоположную; `UpdatePaletteButton` обновляет иконку (в тёмной палитре — солнце, в светлой — луна), подсказку и подпись, повторяя поведение кнопки смены темы в главном окне.

### Версия

- **Версия поднята до `0.3.6.1` → `0.3.6.2`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.6.1] — 2026-09-01

Внедрена двухпалитровая модель цветовых схем: у каждой схемы (встроенные «Светлая»/«Тёмная» и пользовательские) теперь две независимые палитры — **LightColors** (для светлой темы) и **DarkColors** (для тёмной). Вариант темы (светлая/тёмная) выбирает активную палитру, поэтому один набор настроек оформления корректно описывает внешний вид в обеих темах без дублирования схем. Старые схемы и настройки мигрируются автоматически (`ColorScheme.Normalize` / `ColorScheme.FromLegacy`), данные не теряются.

### Добавлено

- **Двухпалитровая модель цветовых схем** в [`ColorScheme.cs`](Configuration%20Management/Models/ColorScheme.cs): каждая схема хранит отдельные палитры светлой и тёмной темы (`LightColors`/`DarkColors`); активная палитра выбирается текущим вариантом темы. Встроенные «Светлая»/«Тёмная» и пользовательские схемы теперь одинаково описывают внешний вид в обеих темах.
- **Переключатель «светлая/тёмная» в редакторе «Оформление»** ([`SettingsWindow.Schemes.cs`](Configuration%20Management/Views/SettingsWindow.Schemes.cs)): над списком цветов выбирается редактируемая палитра, что позволяет настроить светлую и тёмную темы одной схемы отдельно. Тумблеры переключения палитры оформлены в стиле Material Design (`MaterialDesignSwitchToggleButton`).
- **Визуальный предпросмотр темы в стиле Material Design**: миниатюрный макет интерфейса в окне настроек перерисовывается цветами текущей схемы и выбранной палитры в реальном времени — при правке любого цвета, смене палитры и смене схемы.
- **Ключи локализации `Settings.Preview`** (ru/en) для подписей предпросмотра.

### Исправлено

- **`XamlParseException` при запуске** (Windows/WPF): прямой `{StaticResource WindowControlCloseButton}` в [`App.xaml`](Configuration%20Management/App.xaml) перенесён после объявления стиля — ресурс резолвится до первого использования.
- **`NullReferenceException` в `OnPaletteSwitch`** ([`SettingsWindow.Schemes.cs`](Configuration%20Management/Views/SettingsWindow.Schemes.cs)): добавлена защита от `null` при переключении палитры.

### Версия

- **Версия поднята до `0.3.5.93` → `0.3.6.1`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.93] — 2026-09-01

Исправлено переключение на встроенную тему после сохранения пользовательской (Windows/WPF и Linux/Avalonia): если выбрать свою тему и сохранить, а затем снова открыть настройки и выбрать «Светлая» — цвета не менялись. Причина — применяемая пользовательская тема записывалась в слот базовой темы (светлой/тёмной), из-за чего выбор «Светлой» возвращал её цвета, а не базовую светлую схему.

### Исправлено

- **Встроенные и пользовательские схемы больше не смешиваются** в [`MainViewModel.Theme.cs`](Configuration%20Management/ViewModels/MainViewModel.Theme.cs): `ApplyColorScheme` теперь пишет в слот базовой темы (светлой/тёмной) только встроенные темы («Светлая»/«Тёмная»). Пользовательская тема применяется как самостоятельная схема и не затирает кастомизацию встроенной. После сохранения своей темы выбор «Светлой» снова возвращает базовую светлую схему.
- **Та же логика на Linux/Avalonia** в [`MainViewModel.Avalonia.cs`](Configuration%20Management/ViewModels/MainViewModel.Avalonia.cs): `ApplyColorScheme` пишет в слот базовой темы только для встроенных тем.
- **Автоочистка уже повреждённых настроек** в [`MainViewModel.cs`](Configuration%20Management/ViewModels/MainViewModel.cs): при загрузке слот базовой темы принимается только от встроенной схемы («Светлая»/«Тёмная»). Если из старых версий в слоте осталась пользовательская тема — она игнорируется, и базовая тема снова получает свои цвета без ручного сброса.

### Версия

- **Версия поднята до `0.3.5.92` → `0.3.5.93`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.92] — 2026-08-31

Заголовки диалоговых окон (Windows/WPF) теперь заливаются акцентным цветом полностью: раньше прямоугольная полоса заголовка не покрывала скруглённые DWM-углы окна (Windows 11, `GlassFrameThickness=-1` + `DwmWindowCornerPreference`), из-за чего в верхних углах шапки просвечивала стеклянная подложка/рабочий стол — «не всё заливало».

### Исправлено

- **Полное покрытие углов шапки диалогов** в [`WindowChromeHelper.cs`](Configuration%20Management/Views/WindowChromeHelper.cs): полосе заголовка (`BuildTitleBar`) задан `CornerRadius` с тем же радиусом скругления, что применяет DWM к углам окна (константа `DwmCornerRadius = 8`, только два верхних угла). Акцентная заливка теперь идёт по форме окна до самых краёв/углов, без просветов. Перетаскивание окна за шапку и кнопка «закрыть» не затронуты.
- **Резолвинг акцентной кисти подтверждён**: `AccentBrush` для диалогов задаётся через `SetResourceReference` и обновляется явно в `ApplyColors` (фикс версии 0.3.5.91) — кисть резолвится в контексте диалога так же, как для главного окна, изменений не потребовалось.

### Версия

- **Версия поднята до `0.3.5.91` → `0.3.5.92`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.91] — 2026-08-31

Исправлена «бесцветность» шапки активного главного окна (Windows/WPF): при активном окне полоса заголовка снова заливается акцентным цветом темы (`AccentBrush`), а не остаётся прозрачной на стеклянном фоне DWM. Причина — в `ApplyColors` кисть акцента искалась по правилу «ключ + "Brush"» (`AccentColorBrush`), которой нет в теме (там кисть называется `AccentBrush`), поэтому `AccentBrush` не обновлялась и активная шапка оставалась без заливки.

### Исправлено

- **Акцентная кисть обновляется явно** в [`ThemeManager.cs`](Configuration%20Management/Themes/ThemeManager.cs): `ApplyColors` теперь, помимо цвета `AccentColor`, напрямую задаёт кисть `AccentBrush` конкретной кистью из схемы. Раньше generic-правило искало `AccentColorBrush` (такой кисти в теме нет), из-за чего `AccentBrush`, на которую шапка ссылается через `DynamicResource`, могла резолвиться в прозрачную, и активное окно выглядело бесцветным.
- **Надёжная перекраска по активности** в [`MainWindow.xaml.cs`](Configuration%20Management/Views/MainWindow.xaml.cs): состояние активности окна хранится флагом `_isActive` (обновляется в `Activated`/`Deactivated`) и используется при перекраске после смены темы и при `Loaded` вместо временного значения `IsActive` — шапка гарантированно получает акцент при активном окне и цвет карточки при неактивном, включая запуск и восстановление окна из трея.

### Версия

- **Версия поднята до `0.3.5.90` → `0.3.5.91`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.90] — 2026-08-31

Шапка главного окна (Windows/WPF) снова реагирует на активность окна: при активном окне она заливается акцентным цветом темы, при неактивном — становится бледнее (цвет карточки). Это возвращает «цвет акцента» активного главного окна, который пропал и из-за которого окно выглядело бесцветным, как у UWP-приложений.

### Изменено

- **Акцентная шапка главного окна по активности** в [`MainWindow.xaml.cs`](Configuration%20Management/Views/MainWindow.xaml.cs): добавлен метод `UpdateTitleBarAppearance(bool active)` и подписки на события `Activated`/`Deactivated`. При активном окне фон полосы заголовка становится `AccentBrush`, при неактивном — `CardBackgroundBrush`; одновременно переключаются цвет заголовка (`ButtonTextBrush`/`TextPrimaryBrush`) и стили кнопок управления окном (`WindowControlButtonOnAccent`/`WindowControlButton`, `WindowControlCloseButtonOnAccent`/`WindowControlCloseButton`), чтобы значки оставались читаемыми на акцентной шапке.
- **Имя полосы заголовка для перекраски** в [`MainWindow.xaml`](Configuration%20Management/Views/MainWindow.xaml): полосе заголовка присвоено имя `TitleBarBorder` (фон задаётся через `SetResourceReference` в коде, а не жёстко в разметке).
- **Варианты кнопок «на акценте»** в [`App.xaml`](Configuration%20Management/App.xaml): добавлены стили `WindowControlButtonOnAccent` и `WindowControlCloseButtonOnAccent` на основе существующих, с базовым цветом значка `ButtonTextBrush` (читается на акцентном фоне). Цвет задан сеттером стиля `BasedOn`, а не локальным значением — иначе локальное значение перекрывало бы шаблонные триггеры и «ломало» бы белое выделение кнопки «закрыть» при наведении.
- **Перекраска шапки при смене темы и при старте**: обработчик смены словаря темы теперь вызывает и `UpdateTitleBarAppearance(IsActive)`, а при `Loaded` шапка сразу окрашивается по текущему состоянию активности окна.

### Версия

- **Версия поднята до `0.3.5.89` → `0.3.5.90`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

## [0.3.5.89] — 2026-08-31

Шапки диалоговых окон (Windows/WPF) теперь заливаются акцентным цветом темы на всю ширину полосы заголовка, а не остаются прозрачными. Это исправляет «неполную» заливку заголовков окошек: раньше полоса заголовка строилась с прозрачным фоном и визуально сливалась с подложкой окна.

### Изменено

- **Акцентная заливка шапки диалоговых окон** в [`WindowChromeHelper.cs`](Configuration%20Management/Views/WindowChromeHelper.cs): фон полосы заголовка (`BuildTitleBar`) задан через `SetResourceReference(Border.BackgroundProperty, "AccentBrush")` вместо прозрачного `Brushes.Transparent` — полоса тянется на всю ширину окна и перекрашивается автоматически при смене темы или цветовой схемы.
- **Читаемый текст заголовка и значка кнопки «закрыть»** поверх акцентной полосы: заголовок окна использует кисть `ButtonTextBrush` (вместо `TextPrimaryBrush`), а значок кнопки закрытия — `ButtonTextBrush` (вместо унаследованного серого `TextSecondaryBrush`); красное hover-выделение кнопки закрытия сохранено за счёт шаблонного триггера стиля.

### Версия

- **Версия поднята до `0.3.5.88` → `0.3.5.89`** во всех четырёх полях `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).

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
