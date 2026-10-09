Выполнено в версии **0.3.9.332** (Windows/WPF и Linux/Avalonia).

**Что сделано.** Двойной клик по колонкам **«№ релиза»** и **«Конфигурация»** в списке баз открывает окно свойств базы сразу на вкладке **«Платформа»** — не нужно открывать свойства и искать нужную вкладку вручную.

**Как реализовано** ([`MainWindow.Events.cs`](Configuration%20Management/Views/MainWindow.Events.cs), [`MainWindow.Avalonia.Tree.cs`](Configuration%20Management/Views/MainWindow.Avalonia.Tree.cs), [`InfobasePropertiesTabs.cs`](Configuration%20Management/Models/InfobasePropertiesTabs.cs)):

1. WPF: обработчик двойного клика распознаёт колонку по `Tag` элемента (`Configuration`/`ConfigurationVersion`) — колонки переупорядочиваются через `Grid.SetColumn`, поэтому фиксированные индексы не годятся; открывается [`OpenPropertiesOnPlatformTab`](Configuration%20Management/ViewModels/MainViewModel.Commands.cs).
2. Avalonia: то же по координатам клика внутри `InfobaseRowGrid`.
3. Окно свойств ([`ConnectionSettingsWindow`](Configuration%20Management/Views/ConnectionSettingsWindow.xaml.cs) WPF и [Avalonia-версия](Configuration%20Management/Views/ConnectionSettingsWindow.Avalonia.cs)) принимает параметр начальной вкладки — [`InfobasePropertiesTabs.GetTabIndex`](Configuration%20Management/Models/InfobasePropertiesTabs.cs) выбирает «Платформа».
4. У колонок появилась подсказка «Двойной клик — свойства базы на вкладке «Платформа»».

**Как проверить:**
1. Обновитесь до **0.3.9.332**, наведите курсор на колонку «№ релиза» или «Конфигурация» любой базы.
2. Двойной клик — окно свойств открывается на вкладке «Платформа».

Тесты: полный набор `dotnet test` зелёный (**2044**), кросс-сборка Linux без ошибок; новый файл [`InfobasePropertiesTabsTests.cs`](ConfigurationManagement.Tests/InfobasePropertiesTabsTests.cs).
Файлы для установки — на странице релиза [v0.3.9.332](https://github.com/sivatorov/ConfigurationManagement/releases/tag/v0.3.9.332).
