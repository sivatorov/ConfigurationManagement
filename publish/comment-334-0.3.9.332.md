Исправлено в версии **0.3.9.332** (Windows/WPF и Linux/Avalonia).

**Что было.** В окне «Обновление платформы 1С» (Ctrl+F9): кнопки и служебные элементы располагались неудачно (лишние «Закрыть» и «Выбрать файл установщика…»), архивы «обновление-сборка дистрибутива» (`*_updsetup*`/`update-setup`) ломали автовыбор и установку («setup.exe не найден в архиве»), после «Только скачать» панель прогресса исчезала, а удаление старых версий не имело нормального диалога.

**Как исправлено** ([`PlatformUpdateWindow.xaml`](Configuration%20Management/Views/PlatformUpdateWindow.xaml) / [Avalonia](Configuration%20Management/Views/PlatformUpdateWindow.Avalonia.cs), [`PlatformDistributionPicker.cs`](Configuration%20Management/Services/PlatformDistributionPicker.cs), [`PlatformInstaller.Windows.cs`](Configuration%20Management/Services/PlatformInstaller.Windows.cs), [`PlatformUpdateViewModel.cs`](Configuration%20Management/ViewModels/PlatformUpdateViewModel.cs)):

1. **Раскладка окна.** Команды разложены в 2 строки, «Закрыть» и «Выбрать файл установщика…» убраны — окно компактнее, всё нужное видно без прокрутки.
2. **Архивы `*_updsetup*`/`update-setup`.** Новый `PlatformDistributionPicker.IsUpdateSetupPackage` распознаёт архивы «обновление-сборка дистрибутива»: при автовыборе они уходят в конец списка и не предлагаются для типа «Полный клиент» (вручную остаются доступны). Установка: если `setup.exe` в распакованном архиве нет, [`PlatformInstaller`](Configuration%20Management/Services/PlatformInstaller.Windows.cs) ищет любой exe с «setup» в имени и распаковывает вложенные zip-архивы (zip внутри zip, один уровень); вместо голого «setup.exe не найден» в журнал пишется фактическое содержимое архива.
3. **Панель прогресса.** После «Только скачать» больше не скрывается: прогресс и журнал остаются на экране до фактического результата.
4. **«Удалить старые версии…».** Новый диалог [`PlatformOldVersionsWindow`](Configuration%20Management/Views/PlatformOldVersionsWindow.xaml.cs) (и [Avalonia-версия](Configuration%20Management/Views/PlatformOldVersionsWindow.Avalonia.cs)) с чеклистом версий: новейшая, используемые базами и запущенными процессами в список не включаются, удаление — по подтверждению, с журналом операций.

**Как проверить:**
1. Обновитесь до **0.3.9.332**, откройте Ctrl+F9, выберите версию, у портала которой есть `*_updsetup*`-архив.
2. «Скачать и установить» должен распаковать вложенный zip и найти установщик; «Только скачать» — оставить панель прогресса на экране.
3. «Удалить старые версии…» — открывается чеклист без новейшей/используемых/запущенных версий.

Тесты: полный набор `dotnet test` зелёный (**2044**), кросс-сборка Linux без ошибок; расширены [`PlatformDistributionPickerTests.cs`](ConfigurationManagement.Tests/PlatformDistributionPickerTests.cs) и [`PlatformInstallerWindowsTests.cs`](ConfigurationManagement.Tests/PlatformInstallerWindowsTests.cs) (updsetup-архивы, вложенные zip), [`PlatformUpdateViewModelTests.cs`](ConfigurationManagement.Tests/PlatformUpdateViewModelTests.cs).
Файлы для установки — на странице релиза [v0.3.9.332](https://github.com/sivatorov/ConfigurationManagement/releases/tag/v0.3.9.332).
