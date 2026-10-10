# План выпуска 0.3.12.3 — issues #324 и #352

Дата плана: 2026-10-10
Текущая версия: 0.3.12.2 → целевая: **0.3.12.3**
Issues: #324 «Серверы 1С» (компоновка панели + кнопка максимизации), #352 «Цепочки обновлений для базы» (одиночное скачивание — фоном, как цепочка). Issue #330 — пропускаем.

---

## 0. Контекст (что выяснено при изучении кода)

Проект двухцелевой (dual-target): Windows = WPF (`net10.0-windows`), Linux = Avalonia (`net10.0`, символ `LINUX`).
Поэтому **каждое UI-изменение делается в двух вариантах**: WPF (`.xaml` + `.xaml.cs`) и Avalonia (`*.Avalonia.cs`, UI строится кодом).

### Issue #324 — окно «Серверы 1С»
- WPF-вариант: `Views/ServerMonitorWindow.xaml` + `Views/ServerMonitorWindow.xaml.cs`.
  - Строка 0 — панель подключения (адрес/порт/логин/пароль) + `StackPanel` кнопок, в котором сидят: Подключиться, Обновить, **галочка «Автообновление» + метка «Интервал, с» + ComboBox** (xaml строки ~81–87), Диагностика сети…, Закрыть. Именно из-за них строка не влезает по ширине, кнопки «справа» обрезаются.
  - Строка 1 (`Grid.Row="1"`, xaml ~117) — «Кластер:» + ComboBox + статус.
- Avalonia-вариант: `Views/ServerMonitorWindow.Avalonia.cs` (класс `ServerMonitorWindow : ModalWindowBase`, UI кодом):
  - `fields` (строка 0) + `buttons` (строка 1, строки кода 125–132: `connectButton, refreshButton, autoRefreshCheck, intervalLabel, intervalBox, diagnosticsButton, closeButton`).
  - `clusterPanel` (строка 2, код 150–161).
  - Размер окна: `Width=1280, Height=720, MinWidth=980, MinHeight=520` (код 41–44) — **не меняем** (по пожеланию).
- Кнопка максимизации:
  - На **Windows** (WPF) у окна стандартная системная рамка (`CanResize`), кнопка максимизации уже есть ОС — изменений не требуется.
  - На **Linux** (Avalonia) окно наследует `ModalWindowBase` (`Views/ModalWindowBase.cs`, `#if LINUX`). При выключенной настройке «Системный заголовок окна» рисуется собственный chrome с кнопками **Свернуть** и **Закрыть** (`DialogWindowControlKind { Minimize, Close }`, код ~584–590, ~1097–1217). Кнопки «Развернуть» нет — её и добавляем.
  - Ключи локализации `"Window.Minimize"`, `"Window.Maximize"` в `Localization/Languages/ru.json`/`en.json` **уже существуют** (ru.json:7–8). Проверить при реализации наличие `"Window.Restore"`; если нет — добавить.

### Issue #352 — окно проверки обновлений конфигурации («цепочки»)
- WPF-вариант: `Views/UpdateCheckWindow.xaml` + `Views/UpdateCheckWindow.xaml.cs`.
- Avalonia-вариант: `Views/UpdateCheckWindow.Avalonia.cs`.
- Одиночное скачивание — метод `DownloadUpdateFileAsync`:
  - Avalonia: строки 545–562 — `Task.Run(_updates.DownloadUpdateAsync(url, targetPath, progress, CancellationToken.None))`, токен `CancellationToken.None`, **в `BackgroundDownloadManager` не регистрируется** → после закрытия окна качается «невидимо», отменить нельзя.
  - WPF: тот же метод, строки 463–482, та же проблема.
- Цепочка (эталон для подражания): `DownloadChainAsync` (Avalonia 905–974, WPF ~680–740) — регистрирует `chain:{name}` в `BackgroundDownloadManager.Default`, передаёт `backgroundEntry.Cancellation.Token`, `ReportProgress` в колбэке прогресса, `Complete`/`Fail` в catch/finally, `catch (OperationCanceledException)` — только журнал.
- Механизм индикации готов и трогать его не надо: `Services/BackgroundDownloadManager.cs` (Start/ReportProgress/Complete/Fail/Cancel/CancelAll), индикатор главного окна — `ViewModels/MainViewModel.Downloads.cs` (агрегированный текст, прогресс, команда отмены). Поздние отчёты прогресса после завершения менеджер игнорирует сам.
- Одиночное скачивание платформы (`PlatformUpdateViewModel.DownloadOnlyAsync`, строки 523–640) уже маршрутизируется через менеджер (0.3.12.2, issue #334) — **не трогаем**.

---

## 1. Issue #324 — окно «Монитор серверов 1С»

### 1.1. Перенос автообновления к строке кластера

**WPF — `Views/ServerMonitorWindow.xaml`:**
1. Из `StackPanel` кнопок строки 0 (строки ~81–87) удалить: `AutoRefreshCheckBox`, `TextBlock «ServerMonitor.AutoRefreshInterval»`, `AutoRefreshIntervalCombo`.
2. В `StackPanel` строки 1 (кластер, ~117) добавить после ComboBox кластера (перед/после статус-текста — по визуалу; предлагается: Кластер → ComboBox → разделитель → галочка → «Интервал, с» → ComboBox → статус):
   - `CheckBox AutoRefreshCheckBox` (те же биндинги `IsAutoRefreshEnabled`, Margin `14,0,8,0`);
   - `TextBlock ServerMonitor.AutoRefreshInterval`;
   - `ComboBox AutoRefreshIntervalCombo` (Width=64, те же Items/SelectedIndex из code-behind).
3. `Views/ServerMonitorWindow.xaml.cs` — логика (источник Items, обработчики) не меняется, только положение элементов в разметке.

**Avalonia — `Views/ServerMonitorWindow.Avalonia.cs`:**
1. Из `buttons` (код 125–132) убрать `autoRefreshCheck, intervalLabel, intervalBox`.
2. В `clusterPanel` (код 150–161) добавить после `_clusterCombo`: `autoRefreshCheck`, `intervalLabel`, `intervalBox` (создание остаётся на строках 104–123 без изменений; при необходимости разделитель `Separator`/отступ Margin).
3. Кнопки строки 1 станут: Подключиться, Обновить, Диагностика сети…, Закрыть — влезают по ширине.
4. Строку 4 (статус/ошибка/autoRefreshHint) не трогаем.

### 1.2. Кнопка максимизации (Avalonia/Linux)

**`Views/ModalWindowBase.cs`:**
1. Добавить `protected virtual bool SupportsMaximizeButton => false;` (по умолчанию выключено — поведение остальных диалогов не меняется).
2. В построении кнопок chrome (код ~584–590): при `SupportsMaximizeButton` добавить перед/после «Свернуть» кнопку `DialogWindowControlKind.Maximize`:
   - tooltip: `WindowState == Maximized ? T("Window.Restore") : T("Window.Maximize")` (обновлять при переключении состояния, либо статично «Развернуть» — минимальный вариант);
   - клик: `WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;`
3. В `DialogWindowControlKind` (код 1097) добавить `Maximize`; в фабрике геометрии (~1109, 1216) — иконку «квадрат» (например `M4,4 L12,4 L12,12 L4,12 Z` — прямоугольная рамка; при Maximized можно рисовать «двойной квадрат», опционально).
4. Скругление при развороте уже обрабатывается наблюдателем `WindowStateProperty` (код 519–522) — ничего дополнительно.
5. Проверить, что при `SystemDecorations.Full` (системный заголовок включён) кнопка не дублируется с системной — кнопка рисуется только в собственном chrome, это уже так.

**`Views/ServerMonitorWindow.Avalonia.cs`:**
- `protected override bool SupportsMaximizeButton => true;`

Размер по умолчанию (1280×720) и MinWidth/MinHeight не меняем. На Windows максимизация уже есть средствами ОС — WPF-вариант не трогаем.

**Локализация (`Localization/Languages/ru.json`, `en.json`):**
- Добавить `"Window.Restore"` («Свернуть в окно» / «Restore»), если ключа нет; `"Window.Maximize"` уже есть.

### 1.3. СерверMonitor — что НЕ делаем (по решению пользователя)
- Переработка формы со справочником серверов и данными по базам — позже.
- Вкладка «Задания» остаётся (фиксы rac job list 0.3.11.0 не откатываем).

---

## 2. Issue #352 — одиночное скачивание через BackgroundDownloadManager

Единообразно в **обоих** вариантах окна (`UpdateCheckWindow.Avalonia.cs` и `UpdateCheckWindow.xaml.cs`), по образцу `DownloadChainAsync`:

**`DownloadUpdateFileAsync` (Avalonia 545–562 / WPF 463–482):**
1. Вычислить идентификатор и стартовать фоновую запись **до** `Task.Run`:
   ```csharp
   var downloadId = $"update:{row.Name}:{Path.GetFileName(targetPath)}";
   var entry = BackgroundDownloadManager.Default.Start(downloadId, Path.GetFileName(targetPath));
   ```
   (Title — имя файла, как у цепочки — `_row.Name`; для одиночного файла информативнее имя файла.)
2. Токен отмены: передавать `entry.Cancellation.Token` в `_updates.DownloadUpdateAsync` вместо `CancellationToken.None`.
3. Прогресс: в колбэке `Progress<double>` дополнительно `BackgroundDownloadManager.Default.ReportProgress(downloadId, v)` (Avalonia — внутри уже существующего `Dispatcher.UIThread.Post`).
4. Успех (`savedPath` не пуст): `if (entry.IsActive) BackgroundDownloadManager.Default.Complete(downloadId);`
5. Неудача (`savedPath` пуст):
   - если `entry.Cancellation.Token.IsCancellationRequested` — состояние уже Cancelled из `Cancel()`, ничего не помечаем (или `Fail(downloadId, "Main.Downloads.Cancelled")` по аналогии с `PlatformUpdateViewModel`, строки 593–596 — выбрать единый стиль с цепочкой);
   - иначе `BackgroundDownloadManager.Default.Fail(downloadId, "Updates.NetworkError")`.
6. Обёртка try/catch:
   - `catch (OperationCanceledException)` — только журнал (`_logger.Info("… отменена")`), без диалога;
   - `catch (Exception ex)` — `_logger.Error`, `Fail(downloadId)`, существующий `_dialogs.ShowError/ShowWarning` оставить;
   - `finally` — `if (entry.IsActive) Complete(downloadId)` НЕ делать при неудаче (в отличие от цепочки, у которой финальный `Complete` в finally закрывает частично успешную попытку; для одиночного файла проще явно Complete только при успехе, Fail/Cancelled — в остальных ветках). Допустим и вариант «как у цепочки» (Complete в finally при IsActive) — выбрать один стиль при реализации и применить в обоих окнах.
7. `row.IsDownloading`/`UpdateProgressDisplay()` — без изменений (управляются в `OnDownloadRow`).

**Побочные эффекты, которые надо учесть:**
- Диалоги `_dialogs.ShowInfo/ShowWarning` после закрытия окна: цепочка ведёт себя так же — приемлемо; проверить, что `AvaloniaDialogService` не требует Owner-окно (у цепочки работает — значит ок).
- Обновление `row.Progress` из фонового потока после закрытия окна — безвредно (VM живёт с окном; окно закрыто — подписок нет). Проверить отсутствие NRE при закрытии окна во время скачивания (у цепочки та же схема — риск такой же, регрессии не ожидается).
- Повторное нажатие «Скачать» того же файла при активной загрузке: `Start` вернёт существующую запись (идемпотентно) — но нужно ли блокировать кнопку? У цепочки гвард `IsChainDownloading`. Минимально: оставить как есть (запись не дублируется, второй `Task.Run` продолжит писать в тот же файл — существующее поведение; допустимо для точечного фикса). Опционально: `row.CanDownload`-гвард по `IsDownloading` уже есть в `OnDownloadRow`.

**Файлы к изменению:**
- `Configuration Management/Views/UpdateCheckWindow.Avalonia.cs` — `DownloadUpdateFileAsync`.
- `Configuration Management/Views/UpdateCheckWindow.xaml.cs` — `DownloadUpdateFileAsync` (тот же код, `Dispatcher.UIThread.Post` не нужен).

**Не трогаем:** `Services/BackgroundDownloadManager.cs`, `ViewModels/MainViewModel.Downloads.cs`, `PlatformUpdateViewModel.cs`, разметку `UpdateCheckWindow.xaml`.

---

## 3. Версия, changelog, release-инфраструктура

1. `Configuration Management/Configuration Management.csproj` (строки 62–65): `Version`/`AssemblyVersion`/`FileVersion`/`InformationalVersion` → `0.3.12.3`.
2. `CHANGELOG.md`: новая секция `## [0.3.12.3] — <дата выпуска>` сверху, по образцу 0.3.12.2:
   - «Исправлено (issue #324)»: перенос галочки автообновления и интервала к строке кластера; кнопка максимизации окна «Серверы 1С» (Linux, собственный chrome); ширина по умолчанию не изменилась.
   - «Исправлено (issue #352)»: одиночное скачивание файла обновления конфигурации регистрируется в `BackgroundDownloadManager` — прогресс и отмена в индикаторе главного окна, загрузка продолжается после закрытия окна (Windows и Linux).
3. `README.md`: обновить версию, если там фигурирует (проверить при реализации).
4. Release-скрипты (`publish/`) — копируются под новую версию на этапе выпуска (вне рамок кода; по образцу `build_deb_win_0.3.12.2.py`, `check_deb_win_0.3.12.2.py`, `create_release_0.3.12.2.ps1`).

---

## 4. Тесты

### 4.1. `ConfigurationManagement.Tests/BackgroundDownloadManagerTests.cs` — дополнить
Поведение менеджера уже покрыто хорошо (Start/ReportProgress/Complete/Fail/Cancel/CancelAll/ClearFinished). Добавить 1–2 теста под сценарий одиночного скачивания:
- `ReportProgress_AfterCompleteOrCancel_IsIgnored` — поздние отчёты прогресса не «оживляют» запись (гарантия для гонки «окно закрыто → Complete → поздний ReportProgress»);
- `Cancel_SingleUpdateEntry_PropagatesToToken` — симуляция маршрутизации одиночного скачивания: `Start("update:База:file.cf", …)` → `Cancel` → токен сигнализирует, состояние Cancelled (аналог существующего `Download_ContinuesAfterWindowClose_Simulation`, но с id-форматом одиночного скачивания).

### 4.2. `ConfigurationManagement.Tests/PlatformUpdateViewModelTests.cs` — без изменений
`PlatformUpdateViewModel` и `PlatformDownloadViewModel` в этом выпуске не меняются; существующие тесты фоновых загрузок платформы остаются зелёными (регрессионная проверка).

### 4.3. `ConfigurationManagement.Tests/OneCUpdatesUrlTests.cs` — без изменений
Логика URL/планировщика (`UpdateChainBuilder`, `UpdateChainDownloadPlanner`) не меняется.

### 4.4. Чего тестами НЕ покрываем
Компоновка окна монитора и код-бехайнд `UpdateCheckWindow` (WPF/Avalonia UI) юнит-тестами не покрываются — закрываются ручной проверкой (раздел 6).

---

## 5. Порядок сборки/проверки

1. **Windows (WPF):** `dotnet build "Configuration Management/Configuration Management.csproj" -c Debug` (на Windows по умолчанию собирается WPF-вариант).
2. **Linux-сборка на Windows (Avalonia):** `dotnet build "Configuration Management/Configuration Management.csproj" -c Debug -p:ForceLinux=true` — обязательна, т.к. все Avalonia-изменения (`ModalWindowBase`, `ServerMonitorWindow.Avalonia.cs`, `UpdateCheckWindow.Avalonia.cs`) компилируются только под этим символом.
3. **Тесты:** `dotnet test ConfigurationManagement.Tests` (тесты собираются под Windows-таргет с `InternalsVisibleTo`).
4. **Ручная проверка #324 (Linux-сборка):**
   - окно «Серверы 1С» открывается 1280×720; галочка «Автообновление» и «Интервал, с» — во второй строке рядом с выбором кластера; в первой строке — адрес/порт/логин/пароль и 4 кнопки, ничего не обрезается;
   - при выключенном «Системном заголовке окна» в chrome есть кнопка «Развернуть»: разворачивает на весь экран, повторный клик возвращает размер; при включённом системном заголовке — системная кнопка максимизации, дублей нет;
   - автообновление (вкл/выкл, смена интервала) работает после переноса; статус/ошибка/autoRefreshHint на месте.
5. **Ручная проверка #352 (Windows + Linux):**
   - окно проверки обновлений → «Скачать» одиночный файл: в главном окне появляется индикатор «Скачивается: <файл> (N%)» с прогрессом;
   - закрыть окно обновлений во время скачивания — загрузка продолжается, индикатор живёт, по завершении исчезает (Completed);
   - «Отменить» в индикаторе главного окна — загрузка останавливается, запись помечается отменённой, без диалога об ошибке;
   - разрыв сети во время скачивания — запись Failed, стандартное предупреждение «Ошибка сети»;
   - цепочка (регресс) — ведёт себя как в 0.3.12.2; одновременные цепочка + одиночный файл — агрегированный индикатор корректен.
6. **Выпуск:** собрать/подписать пакет по образцу 0.3.12.2 (`publish/build_deb_win_0.3.12.3.py` и т.п.), обновить CHANGELOG, создать release.

---

## 6. Сводка изменений по файлам

| Файл | Issue | Изменение |
|---|---|---|
| `Views/ServerMonitorWindow.xaml` | #324 | Убрать автообновление из строки 0, добавить в строку кластера (строка 1) |
| `Views/ServerMonitorWindow.xaml.cs` | #324 | Проверить, что логика ComboBox интервала не привязана к позиции (правок скорее всего нет) |
| `Views/ServerMonitorWindow.Avalonia.cs` | #324 | Перенести `autoRefreshCheck/intervalLabel/intervalBox` из `buttons` в `clusterPanel`; `override SupportsMaximizeButton => true` |
| `Views/ModalWindowBase.cs` | #324 | `virtual SupportsMaximizeButton`, кнопка Maximize в chrome (вид, геометрия, тоггл WindowState), ключ `Window.Restore` при необходимости |
| `Localization/Languages/ru.json`, `en.json` | #324 | Добавить `Window.Restore`, если отсутствует |
| `Views/UpdateCheckWindow.Avalonia.cs` | #352 | `DownloadUpdateFileAsync`: регистрация в `BackgroundDownloadManager.Default`, токен отмены, ReportProgress/Complete/Fail, catch OperationCanceledException |
| `Views/UpdateCheckWindow.xaml.cs` | #352 | То же для WPF-варианта |
| `Configuration Management.csproj` | оба | Версия → 0.3.12.3 |
| `CHANGELOG.md` | оба | Секция 0.3.12.3 |
| `ConfigurationManagement.Tests/BackgroundDownloadManagerTests.cs` | #352 | +1–2 теста (игнорирование позднего прогресса; отмена записи одиночного скачивания) |
