Исправлено в версии **0.3.9.332** (Windows/WPF и Linux/Avalonia).

**Что было.** В окне «Скачивание версии платформы 1С» список «Выбор файла» оставался пустым: реальный HTML каталога `releases.1c.ru` содержит HTML-сущности в атрибутах href — амперсанд в адресе `version_files?nick=…&ver=…` приходил закодированным, без декодирования порталу уходил испорченный второй параметр («amp;ver»), сервер не находил версию и отвечал страницей без дистрибутивов.

**Как исправлено:**

1. **Декодирование href (первопричина пустого списка).** [`OneCPlatformCatalogParser.NormalizeHref`](Configuration%20Management/Services/OneCPlatformCatalogParser.cs) декодирует HTML-сущности и убирает пробельные символы в ссылках на страницы файлов релиза — список файлов заполняется.
2. **Поиск по дереву версий.** Новый [`PlatformVersionTreeBuilder.Filter`](Configuration%20Management/Services/PlatformVersionTreeBuilder.cs) фильтрует листья по подстроке без учёта регистра, не мутируя исходное дерево; в окне поле «Поиск версии…» ([`PlatformDownloadWindow.xaml`](Configuration%20Management/Views/PlatformDownloadWindow.xaml) / [Avalonia](Configuration%20Management/Views/PlatformDownloadWindow.Avalonia.cs)).
3. **«Свернуть все»/«Развернуть все».** У [`PlatformCatalogNode`](Configuration%20Management/Services/PlatformVersionTreeBuilder.cs) появилось свойство `IsExpanded` (INotifyPropertyChanged), привязанное к `TreeViewItem.IsExpanded`; кнопки управляют деревом целиком.
4. **«Запустить установщик» убран** — окно теперь только скачивает файл (установка — через Ctrl+F9); при пустой странице файлов версии показывается статус «Страница файлов версии {0} не содержит распознанных дистрибутивов».

**Как проверить:**
1. Обновитесь до **0.3.9.332**, откройте «Скачивание версии платформы 1С», нажмите «Проверить каталог» и выберите версию.
2. Список «Выбор файла» должен заполниться; поиск (например, «8.3.27») фильтрует дерево, «Свернуть все»/«Развернуть все» работают.

Тесты: полный набор `dotnet test` зелёный (**2044**), кросс-сборка Linux без ошибок; расширены [`OneCPlatformCatalogParserTests.cs`](ConfigurationManagement.Tests/OneCPlatformCatalogParserTests.cs) (декодирование HTML-сущностей) и [`PlatformVersionTreeBuilderTests.cs`](ConfigurationManagement.Tests/PlatformVersionTreeBuilderTests.cs) (`Filter`).
Файлы для установки — на странице релиза [v0.3.9.332](https://github.com/sivatorov/ConfigurationManagement/releases/tag/v0.3.9.332).
