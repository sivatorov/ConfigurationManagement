# v0.3.9.332 — Исправления по issues #352, #334, #330, #355

Windows/WPF и Linux/Avalonia.

## Исправлено

- **Проверка обновлений (F9): скачивание не доходило до дистрибутива (issue #352, комментарий 7OH от 2026-10-08)** — реальный портал на промежуточной странице скачивания отдаёт кнопку «Скачать дистрибутив», адрес которой не содержит `transfer_file` и не является архивом, поэтому путь из 0.3.9.331 не находил следующую ссылку:
  - **якорные ссылки по подписи** — [`OneCUpdatesService.SelectDistributionCandidates`](Configuration%20Management/Services/OneCUpdatesService.cs) делает подпись якоря кандидатом первого приоритета: [`AddDownloadAnchorCandidates`](Configuration%20Management/Services/OneCUpdatesService.cs) добавляет href ссылок с подписью «Скачать дистрибутив»/«Скачать файл»/Download (HTML-сущности в href декодируются, заглушки «#»/«javascript:» пропускаются);
  - **`transfer_file`/`additional_file` вне href** — новый [`FileEndpointUrlRegex`](Configuration%20Management/Services/OneCUpdatesService.cs) находит адреса передачи/дополнительных файлов в любом месте ответа страницы — JS-редиректы, onclick-обработчики, встроенный JSON (запасной путь, если кнопка не найдена);
  - **расширение из query-параметров** — [`GetDistributionExtension`](Configuration%20Management/Services/OneCUpdatesService.cs) при отсутствии распознанного расширения в пути проверяет имя файла в query-параметрах `path`/`file`/`filename` (адрес `transfer_file?path=…\file.cf` несёт расширение только в query).
- **Обновление платформы 1С (Ctrl+F9, issue #334, комментарий 7OH от 2026-10-08)**:
  - **раскладка окна** — команды в [`PlatformUpdateWindow.xaml`](Configuration%20Management/Views/PlatformUpdateWindow.xaml) / [`.xaml.cs`](Configuration%20Management/Views/PlatformUpdateWindow.xaml.cs) и [`PlatformUpdateWindow.Avalonia.cs`](Configuration%20Management/Views/PlatformUpdateWindow.Avalonia.cs) разложены в 2 строки, лишние «Закрыть» и «Выбрать файл установщика…» убраны;
  - **`*_updsetup*`/`update-setup`-архивы** — [`PlatformDistributionPicker.IsUpdateSetupPackage`](Configuration%20Management/Services/PlatformDistributionPicker.cs) распознаёт архивы «обновление-сборка дистрибутива»: при автовыборе они уходят в конец списка и не предлагаются для типа «Полный клиент»; [`PlatformInstaller`](Configuration%20Management/Services/PlatformInstaller.Windows.cs) при отсутствии `setup.exe` ищет любой exe с «setup» в имени и распаковывает вложенные zip-архивы (zip внутри zip, один уровень); вместо голого «setup.exe не найден» в журнал пишется фактическое содержимое архива;
  - **панель прогресса не скрывается после «Только скачать»** — [`PlatformUpdateViewModel`](Configuration%20Management/ViewModels/PlatformUpdateViewModel.cs) больше не завершает занятость досрочно: прогресс и журнал остаются на экране до фактического результата;
  - **диалог «Удалить старые версии…»** — новое окно [`PlatformOldVersionsWindow`](Configuration%20Management/Views/PlatformOldVersionsWindow.xaml.cs) (WPF и Avalonia [`PlatformOldVersionsWindow.Avalonia.cs`](Configuration%20Management/Views/PlatformOldVersionsWindow.Avalonia.cs)) с чеклистом версий: новейшая, используемые базами и запущенными процессами в список не включаются, удаление — по подтверждению с журналом.
- **Скачивание версии платформы 1С (issue #330): список файлов оставался пустым** — первопричина: реальный HTML каталога `releases.1c.ru` содержит HTML-сущности в атрибутах href — амперсанд в адресе `version_files?nick=…&ver=…` приходил закодированным, без декодирования порталу уходил испорченный второй параметр («amp;ver»), сервер не находил версию и отвечал страницей без дистрибутивов; [`OneCPlatformCatalogParser.NormalizeHref`](Configuration%20Management/Services/OneCPlatformCatalogParser.cs) декодирует HTML-сущности и убирает пробельные символы в ссылках на страницы файлов релиза. Кнопка «Запустить установщик» из окна убрана — окно только скачивает файл.

## Добавлено

- **Двойной клик по колонкам «№ релиза»/«Конфигурация» открывает свойства базы на вкладке «Платформа» (issue #355)** — двойной клик по этим колонкам строки списка баз открывает окно свойств сразу на вкладке «Платформа»: WPF — [`MainWindow.Events.cs`](Configuration%20Management/Views/MainWindow.Events.cs) распознаёт колонку по `Tag` (колонки переупорядочиваются через `Grid.SetColumn`, индекс не годится), Avalonia — [`MainWindow.Avalonia.Tree.cs`](Configuration%20Management/Views/MainWindow.Avalonia.Tree.cs); метод [`InfobasePropertiesTabs.GetTabIndex`](Configuration%20Management/Models/InfobasePropertiesTabs.cs) выбирает начальную вкладку в [`ConnectionSettingsWindow`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml.cs) и [Avalonia-версии](Configuration%20Management/Views/ConnectionSettingsWindow.Avalonia.cs); подсказка колонок — «Двойной клик — свойства базы на вкладке «Платформа»».
- **Поиск по дереву версий и «Свернуть все»/«Развернуть все» в окне «Скачивание версии платформы 1С» (issue #330, комментарий 7OH)** — [`PlatformVersionTreeBuilder.Filter`](Configuration%20Management/Services/PlatformVersionTreeBuilder.cs) фильтрует листья по подстроке без учёта регистра, не мутируя исходное дерево; [`PlatformCatalogNode.IsExpanded`](Configuration%20Management/Services/PlatformVersionTreeBuilder.cs) (INotifyPropertyChanged) привязан к `TreeViewItem.IsExpanded`, кнопки и поле «Поиск версии…» — в [`PlatformDownloadWindow.xaml`](Configuration%20Management/Views/PlatformDownloadWindow.xaml) / [`.xaml.cs`](Configuration%20Management/Views/PlatformDownloadWindow.xaml.cs) и [`PlatformDownloadWindow.Avalonia.cs`](Configuration%20Management/Views/PlatformDownloadWindow.Avalonia.cs); при пустой странице файлов версии показывается статус «Страница файлов версии {0} не содержит распознанных дистрибутивов».

## Как проверить

1. **Скачивание обновления (#352):** «Проверка обновлений» (F9) → «Скачать» — скачивается реальный дистрибутив даже когда промежуточная страница отдаёт кнопку «Скачать дистрибутив» без `transfer_file` в адресе.
2. **Обновление платформы (#334):** Ctrl+F9 — команды в две строки; updsetup-архив при автовыборе в конце списка, при установке распаковывается (включая zip внутри zip); после «Только скачать» панель прогресса остаётся на экране; «Удалить старые версии…» — чеклист без новейшей и используемых версий.
3. **Скачивание платформы (#330):** окно скачивания — список файлов версии заполняется (исправлено декодирование `&` в href); в дереве версий работает поиск и «Свернуть все»/«Развернуть все».
4. **Свойства базы (#355):** двойной клик по колонкам «№ релиза»/«Конфигурация» открывает свойства базы сразу на вкладке «Платформа».

## Тесты

Полный набор `dotnet test` зелёный (**2044**); кросс-сборка Linux
(`dotnet build -p:BuildLinux=true`) без ошибок. Новый файл
[`InfobasePropertiesTabsTests.cs`](ConfigurationManagement.Tests/InfobasePropertiesTabsTests.cs);
расширены: [`OneCUpdatesDistributionResolutionTests.cs`](ConfigurationManagement.Tests/OneCUpdatesDistributionResolutionTests.cs)
(якорь по подписи, `transfer_file`/`additional_file` вне href, расширение из query),
[`PlatformDistributionPickerTests.cs`](ConfigurationManagement.Tests/PlatformDistributionPickerTests.cs)
и [`PlatformInstallerWindowsTests.cs`](ConfigurationManagement.Tests/PlatformInstallerWindowsTests.cs)
(updsetup-архивы, вложенные zip), [`PlatformVersionTreeBuilderTests.cs`](ConfigurationManagement.Tests/PlatformVersionTreeBuilderTests.cs)
(`Filter`), [`PlatformDownloadViewModelTests.cs`](ConfigurationManagement.Tests/PlatformDownloadViewModelTests.cs)
и [`PlatformUpdateViewModelTests.cs`](ConfigurationManagement.Tests/PlatformUpdateViewModelTests.cs)
(панель прогресса, статус «нет файлов»), [`OneCPlatformCatalogParserTests.cs`](ConfigurationManagement.Tests/OneCPlatformCatalogParserTests.cs)
(декодирование HTML-сущностей).

## Сборка

- Windows: single-file self-contained WPF (`net10.0-windows`, win-x64) — `ConfigurationManagement.exe`.
- Linux: single-file self-contained Avalonia (`net10.0`, linux-x64) — `ConfigurationManagement`,
  пакет `configuration-management_0.3.9.332_amd64.deb`.
- Контрольные суммы — в `SHA256SUMS.txt`.

Issues остаются открытыми — ждём подтверждения от репортеров.
