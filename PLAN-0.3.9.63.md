# PLAN — 0.3.9.63 «Управление конфигурациями 1С»

План исправления issue **#298** «3.9.62 не стартует» (последний комментарий не от @sivatorov —
требует исправления) для репозитория
[`sivatorov/ConfigurationManagement`](https://github.com/sivatorov/ConfigurationManagement).

- Локальная копия: `f:\Yandex.Disk\h\Configuration_Management`.
- Текущая версия: **0.3.9.62** (четыре поля в `Configuration Management/Configuration Management.csproj`,
  верхняя запись `CHANGELOG.md` [0.3.9.62] от 2026-09-25).
- Режим: Архитектор (только планирование). Исправление выполняется **отдельной задачей**
  в режиме `code`. Issue после исправления НЕ закрывается — публикуется только комментарий
  «что исправлено и в какой версии».
- Правило: после изменения версия увеличивается, изменения описываются в CHANGELOG и README.

---

## 0. Контекст: что произошло (результаты разведки)

### 0.1 Симптом (issue #298)

- **Автор:** @7OH, создан 2026-09-25T14:21:24Z, комментариев: 1 (последний @7OH 2026-09-25T14:41:19Z:
  «Линукс версия не сломана. Ждём фикс под винду»).
- Ошибка при старте Windows-версии 0.3.9.62:
  `System.Windows.Markup.XamlParseException` — «DynamicResourceExtension невозможно задать
  в свойстве "BasedOn" типа "Style". "DynamicResourceExtension" можно задать только
  в параметре DependencyProperty объекта DependencyObject».
- Стек: `MainWindow.InitializeComponent()` ← `MainWindow.xaml:line 1`
  (строка 1 в стеке — стандартное поведение WPF: ошибка парсинга корневого файла XAML
  приписывается первой строке; реальное проблемное место — внутри ресурсов/разметки окна).
- Linux/Avalonia-версия работает (комментарий @7OH).

### 0.2 Первопричина — найдены точные места

WPF не поддерживает `DynamicResource` в `BasedOn` у `Style`: `Style.BasedOn` — обычное
CLR-свойство, а не DependencyProperty, поэтому `DynamicResourceExtension` там недопустим
(только `StaticResource` или `x:Static`). Проверено поиском по ВСЕМ `*.xaml` проекта
(39 вхождений `BasedOn`): **ровно 3 невалидных** — все в
[`MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml), все — стили кнопок
управления окном (свернуть/развернуть/закрыть), добавленные для компактного режима (#296):

| Строка | Кнопка | Сейчас (невалидно) | Должно стать |
|--------|--------|---------------------|--------------|
| [`MainWindow.xaml:224`](Configuration Management/Views/MainWindow.xaml:224) | MinimizeButton | `BasedOn="{DynamicResource WindowControlButton}"` | `BasedOn="{StaticResource WindowControlButton}"` |
| [`MainWindow.xaml:244`](Configuration Management/Views/MainWindow.xaml:244) | MaximizeButton | `BasedOn="{DynamicResource WindowControlButton}"` | `BasedOn="{StaticResource WindowControlButton}"` |
| [`MainWindow.xaml:270`](Configuration Management/Views/MainWindow.xaml:270) | CloseButton | `BasedOn="{DynamicResource WindowControlCloseButton}"` | `BasedOn="{StaticResource WindowControlCloseButton}"` |

**Дополнительные улики:**

- Комментарий прямо над ресурсами окна уже утверждает, что кнопки
  «вынесены в общие ресурсы приложения (App.xaml) … переиспользуются через StaticResource»
  ([`MainWindow.xaml:61-63`](Configuration Management/Views/MainWindow.xaml:61)) — код
  противоречит собственному комментарию. Значит, `StaticResource` → `DynamicResource`
  в этих трёх местах — случайная/незадокументированная правка.
- Базовые стили определены в [`App.xaml:86`](Configuration Management/App.xaml:86)
  (`WindowControlButton`) и [`App.xaml:198`](Configuration Management/App.xaml:198)
  (`WindowControlCloseButton`); внутри App.xaml они уже наследуются через `StaticResource`
  (строки 186, 198, 285) — рабочая схема в проекте есть.
- Ни темы, ни другие окна эти стили не переопределяют (поиск `WindowControlButton` по всем
  `*.xaml`: только App.xaml и MainWindow.xaml). Стиль кнопок статичен — зависит от
  attached-свойств `WindowChromeHelper.*` и статичных кистей, от темы не зависит.

### 0.3 Когда внесена регрессия

- CHANGELOG 0.3.9.58 (issue #296) описывает появление `DataTrigger` на `CompactMode`
  у кнопок управления окном — т.е. локальные стили с `BasedOn` появились тогда.
  В 0.3.9.61 Windows-версия работала (пользователь тестировал фикс #283 и подтвердил).
- CHANGELOG 0.3.9.62 / `publish/release_body_0.3.9.62.md` / `publish/comment-284-0.3.9.62.md`
  описывают только #284 (динамическая балансировка загрузки) — правка кнопок в них не упомянута.
- Вывод: замена `StaticResource` → `DynamicResource` внесена в цикле подготовки 0.3.9.62
  как незадокументированная правка и не была проверена запуском Windows-сборки.

### 0.4 Почему Linux не сломан

- На Avalonia главное окно строится программно: кнопки управления окном создаются кодом
  (`MainWindow.Avalonia.cs:788-808`, класс `WindowControlButton`, `ApplyCompactMode`
  в `MainWindow.Avalonia.cs:1799`), WPF-файл `MainWindow.xaml` в Linux-сборке не участвует.
- Правка затрагивает ТОЛЬКО `MainWindow.xaml` → Linux/Avalonia не изменяется и не регрессирует.

### 0.5 Почему замена на StaticResource корректна и безопасна

1. `Application.Resources` (App.xaml) загружается до создания окон → обе ссылки резолвятся
   на этапе парсинга `MainWindow.xaml`.
2. `WindowControlCloseButton` уже наследует `WindowControlButton` через `StaticResource`
   в том же словаре App.xaml — прецедент внутри проекта.
3. Стили не зависят от темы (см. 0.2) → потеря «динамичности» невозможна.
4. `DataTrigger` на `CompactMode` (строки 226-229, 246-249, 272-275) остаются внутри
   локальных стилей — компактность заголовка окна (#296) сохраняется в полном объёме.
5. `Window.Resources` в `MainWindow.xaml` не содержит ключей, конфликтующих с App.xaml, —
   теневых переопределений нет.

---

## 1. Этап 1 — исправление кода (одна задача в режиме code)

> Одна задача = один issue (#298) = один коммит. Версия поднимается в ЭТОЙ же задаче
> (этап 2), т.к. исправление одно и выпускается сразу релизом 0.3.9.63.

### Задача 1.1 — #298: убрать DynamicResource из BasedOn кнопок окна (WPF)

**Файл:** [`Configuration Management/Views/MainWindow.xaml`](Configuration Management/Views/MainWindow.xaml)

**Точные правки (3 замены, ничего больше не трогать):**

1. Строка 224: `BasedOn="{DynamicResource WindowControlButton}"` →
   `BasedOn="{StaticResource WindowControlButton}"` (кнопка «Свернуть»).
2. Строка 244: `BasedOn="{DynamicResource WindowControlButton}"` →
   `BasedOn="{StaticResource WindowControlButton}"` (кнопка «Развернуть/Восстановить»).
3. Строка 270: `BasedOn="{DynamicResource WindowControlCloseButton}"` →
   `BasedOn="{StaticResource WindowControlCloseButton}"` (кнопка «Закрыть»).

**Не менять:** триггеры `DataTrigger CompactMode`, вложенный контент (Path), обработчики
`Click`, шаблоны в App.xaml, темы, Avalonia-файлы.

**Подстраховка (проверить, НЕ менять без необходимости):**
- поиском `BasedOn="\{DynamicResource` по всем `*.xaml` убедиться, что невалидных вхождений
  больше не осталось (после правки должно быть 0);
- поиском `WindowControlButton` убедиться, что остальные ссылки — StaticResource.

**Риски:**
- Если `WindowControlButton` будет удалён/переименован из App.xaml — StaticResource упадёт
  на парсинге с явной ошибкой (это нормальное поведение, и сейчас ресурс на месте).
- Ложное срабатывание «ресурс не найден» невозможно: ключи определены в одном
  `Application.Resources` до загрузки окна.

**Как проверить (Windows):**
1. `dotnet build` (или `SKIP_PUBLISH=1 .\build-windows-single-file.ps1`) — компиляция без ошибок XAML.
2. Запуск приложения: главное окно открывается, ошибка `XamlParseException` отсутствует.
3. Кнопки «Свернуть/Развернуть/Закрыть» кликабельны и выглядят как раньше.
4. Включить компактный режим — кнопки уменьшаются (44×30), отключить — возвращаются (46×34).
5. Переключить светлую/тёмную тему — кнопки окна не меняются (как и раньше).
6. Открыть любое диалоговое окно (Настройки, Сценарии резервирования) — рендер без ошибок.

**Как проверить (Linux/Avalonia):**
- Правка не касается Avalonia, но по регламенту собрать Linux-сборку (см. этап 4) и убедиться,
  что она запускается и кнопки окна работают (свернуть/развернуть/закрыть, компактность).

---

## 2. Этап 2 — версия, CHANGELOG, README

### 2.1 Выбор версии: **0.3.9.63**

- История: серия микро-версий `0.3.9.y` (промежуточные выпуски по issues); 0.3.9.62 — последняя.
- Семантика: исправление регрессии (баг-фикс, без новых возможностей) → **patch**.
- Одна правка, один релиз → мажор/минор не повышаем, идём по существующей схеме
  промежуточных выпусков: **0.3.9.63**.

### 2.2 Файлы версии

| Файл | Что сделать |
|------|-------------|
| [`Configuration Management/Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj:62) | Строки 62-65: `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` — все `0.3.9.62` → `0.3.9.63` |
| [`CHANGELOG.md`](CHANGELOG.md:12) | Вставить новый раздел `## [0.3.9.63] — 2026-09-25` ПЕРЕД `## [0.3.9.62]` (после строки 10) |
| [`README.md`](README.md:3) | Строка 3: бейдж `Версия-0.3.9.61-1F6FEB` → `Версия-0.3.9.63-1F6FEB` (заодно закрыть долг 0.3.9.62: бейдж отстал на одну версию) |

**Текст раздела CHANGELOG [0.3.9.63]** (по образцу существующих разделов):

```markdown
## [0.3.9.63] — 2026-09-25

### Исправлено

- **Приложение снова запускается на Windows: убрано DynamicResource из BasedOn
  у кнопок управления окном (#298)** — в 0.3.9.62 кнопки «Свернуть/Развернуть/Закрыть»
  главного окна наследовались через `BasedOn="{DynamicResource ...}"`, а WPF не допускает
  `DynamicResource` в свойстве `BasedOn` у `Style` (это CLR-свойство, а не DependencyProperty) —
  при старте возникала `System.Windows.Markup.XamlParseException`, и приложение не запускалось.
  Замена на `StaticResource` восстановила запуск; компактность кнопок окна (#296, 46×34 → 44×30)
  и остальное поведение сохранены. Linux/Avalonia не затронута (кнопки окна там создаются
  программно и в WPF-разметке не участвуют).

### Версия

- **Версия приложения обновлена до `0.3.9.63`** во всех четырёх полях: `<Version>`,
  `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` в
  [`Configuration Management.csproj`](Configuration%20Management/Configuration%20Management.csproj).
  Весь набор тестов проходит.
```

---

## 3. Этап 3 — артефакты публикации (новые файлы в publish/)

По образцу существующих файлов `publish/release_body_0.3.9.62.md` и `publish/comment-284-0.3.9.62.md`:

1. [`publish/release_body_0.3.9.63.md`](publish/release_body_0.3.9.63.md) — тело GitHub Release:
   - заголовок `## [0.3.9.63] — 2026-09-25`;
   - раздел «Исправлено»: #298 (XamlParseException, причина — DynamicResource в BasedOn,
     что сделано, что не изменилось);
   - раздел «Файлы для установки»: Windows `ConfigurationManagement.exe` (single-file,
     self-contained), Linux `ConfigurationManagement-linux-x64` (single-file, self-contained;
     при необходимости `chmod +x`).
2. [`publish/comment-298-0.3.9.63.md`](publish/comment-298-0.3.9.63.md) — комментарий к issue #298:
   - «Исправлено в версии **0.3.9.63**»;
   - причина: `DynamicResource` в `BasedOn` у `Style` недопустим в WPF (только `StaticResource`
     или `x:Static`); три кнопки управления окном главного окна получили такую ссылку
     в цикле 0.3.9.62 — при старте падала `XamlParseException`;
   - исправление: `BasedOn` переведён на `StaticResource` (стили `WindowControlButton` /
     `WindowControlCloseButton` определены в App.xaml, от темы не зависят), компактность
     кнопок окна сохранена;
   - подтверждение: «Windows-версия снова запускается; Linux/Avalonia не затронута».

---

## 4. Этап 4 — сборка и проверка

| Шаг | Команда/скрипт | Ожидаемый результат |
|-----|----------------|---------------------|
| Юнит-тесты | `dotnet test` (ConfigurationManagement.Tests) | Весь набор проходит (122 теста, как в 0.3.9.62) |
| Windows (exe) | `.\build-windows-single-file.ps1` в `Configuration Management/` | `dist/win-x64/ConfigurationManagement.exe` — single-file, self-contained; запуск на Windows без `XamlParseException` |
| Linux (бинарник) | `.\build-linux-single-file.ps1` в `Configuration Management/` (кросс-компиляция из Windows) | `dist/linux-x64/ConfigurationManagement` — single-file, self-contained; запуск на Linux не регрессировал |
| Ручная проверка WPF | См. «Как проверить (Windows)» в задаче 1.1 | Кнопки окна, компактность, темы, диалоги — без изменений |

---

## 5. Этап 5 — git, публикация на GitHub, комментарий к issue

1. `git add` затронутых файлов: `Configuration Management/Views/MainWindow.xaml`,
   `Configuration Management/Configuration Management.csproj`, `CHANGELOG.md`, `README.md`,
   `publish/release_body_0.3.9.63.md`, `publish/comment-298-0.3.9.63.md`.
2. Коммит: `fix(#298): WPF не стартует — StaticResource вместо DynamicResource в BasedOn кнопок окна`.
3. Тег: `v0.3.9.63` на коммит; пуш ветки и тега.
4. GitHub Release: `v0.3.9.63`, тело — из `publish/release_body_0.3.9.63.md`;
   аттачи: `ConfigurationManagement.exe` (Windows x64), `ConfigurationManagement` (Linux x64).
5. Комментарий к issue **#298** (текст из `publish/comment-298-0.3.9.63.md`).
   Issue не закрывать (по правилам проекта: после исправления — только комментарий).

---

## 6. Риски и меры

| Риск | Мера |
|------|------|
| После замены StaticResource-ссылка упадёт, если ключ в App.xaml отсутствует/переименован | Ключи на месте (App.xaml:86,198); проверить поиском перед правкой |
| Регрессия компактности кнопок окна (#296) | DataTrigger'ы не трогаются; ручная проверка шагов 4-5 задачи 1.1 |
| Ложное ощущение, что «динамичность» потеряна (переключение темы) | Стили кнопок от темы не зависят (TemplateBinding attached-свойств) — проверить переключением темы |
| Linux-сборка «зацепит» WPF-файл | MainWindow.xaml не участвует в Avalonia-сборке; контрольная сборка и запуск |
| README отстал от версии (показывает 0.3.9.61) | Обновить бейдж сразу до 0.3.9.63 |
| Незакрытый долг: бейдж 0.3.9.62 не обновлялся | Указывается намеренно единым переходом к 0.3.9.63 |

---

## Схема работ

```mermaid
flowchart LR
    A[Диагностика #298] --> B[Правка MainWindow.xaml: DynamicResource to StaticResource в BasedOn]
    B --> C[Версия 0.3.9.63 в csproj]
    C --> D[CHANGELOG и README]
    D --> E[Артефакты publish: release body и комментарий]
    E --> F[Тесты и сборка Windows]
    F --> G[Сборка Linux без регрессий]
    G --> H[git commit и тег v0.3.9.63]
    H --> I[GitHub Release]
    I --> J[Комментарий к issue 298]