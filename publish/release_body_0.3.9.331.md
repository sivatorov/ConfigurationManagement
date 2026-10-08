# v0.3.9.331 — Исправления по issues #324, #352

Windows/WPF и Linux/Avalonia.

## Исправлено

- **Скачивание дистрибутивов вместо HTML-страниц (issue #352)** — кнопка «Скачать»
  сохраняла как «.zip» страницу каталога со списком релизов, а «Скачать цепочку» —
  промежуточные страницы скачивания каждого файла (их адреса тоже заканчиваются на
  «.zip»). Скачивание ([`OneCUpdatesService`](Configuration%20Management/Services/OneCUpdatesService.cs))
  теперь разрешает **конечный адрес дистрибутива**, проходя по цепочке страниц:
  каталог релизов → страница файлов релиза (`version_files`) → промежуточная страница
  скачивания файла → ссылка `transfer_file`/прямая ссылка на архив. Каждый HTML-ответ
  анализируется (приоритет: `setup*.zip` → полный `*.zip` → `setup*.exe`/`*.rar`/`*.7z` →
  `1cv8.cf` → `transfer_file` → `version_files`), сохраняется только бинарный контент;
  авторизация на portal.1c.ru — та же (`OneCUpdatesLoginFlow`); защита от зацикливания
  (посещённые адреса, максимум 5 переходов) и от страницы входа. Итоговый файл получает
  расширение конечного адреса (`.rar`/`.exe`/`.7z` вместо «.zip»).
- **Окно выше, таблица цепочки видна (issue #352)** — окно «Проверка обновлений» выше по
  умолчанию (660), а при появлении таблицы вариантов цепочки поднимается до ~760 (не выше
  рабочей области экрана) — 2–3 строки «№ / Список версий» видны без прокрутки. Обе версии
  окна: WPF ([`UpdateCheckWindow.xaml`](Configuration%20Management/Views/UpdateCheckWindow.xaml))
  и Avalonia ([`UpdateCheckWindow.Avalonia.cs`](Configuration%20Management/Views/UpdateCheckWindow.Avalonia.cs)).
- **Монитор серверов 1С — быстрый поиск rac (issue #324, 0.3.9.330)** — общий поиск
  платформы рекурсивно сканировал корни поиска до глубины 6 (в логе «поиск rac занял
  4717 мс»); добавлен быстрый путь по стандартному макету `<корень>\1cv8\<версия>\bin`
  (только каталоги версий, новейшие первыми, без рекурсии), тяжёлый поиск — запасной для
  нестандартных установок; из длительности rac-команды в журнале больше не «выглядит»
  время поиска.
- **Имя кластера в поле подключения (issue #324, 0.3.9.330)** — WPF-ComboBox с шаблоном
  ModernComboBox показывал выбранный элемент через `ToString()` («Конфигурация…» вместо
  имени): `DisplayMemberPath` заменён на явный `ItemTemplate`, добавлены читаемые
  `ToString()` у [`RacClusterRow`](Configuration%20Management/ViewModels/RacClusterRow.cs) и
  [`RacCluster`](Configuration%20Management/Models/RacModels.cs) («Имя (порт)»); в Avalonia
  `ItemsSource` комбо кластеров обновляется после загрузки списка.
- **`job list` на rac 8.5.4.1878 (issue #324, 0.3.9.330)** — платформа отклоняет оба
  известных формата (`--cluster=<uuid>` и `--cluster <uuid>`): добавлен третий формат —
  позиционный `<uuid>` без имени параметра ([`RacClient.GetJobsAsync`](Configuration%20Management/Services/RacClient.cs));
  рабочий формат кэшируется в памяти и на диске ([`RacJobListFormatStore`](Configuration%20Management/Services/RacJobListFormatStore.cs));
  при неуспехе всех форматов — один сводный WARN и просьба прислать `rac job list --help`.

## Как проверить

1. **Скачивание обновления (#352):** «Проверка обновлений» (F9) для базы с более свежим
   релизом → «Скачать» — в файле реальный дистрибутив (архив), а не страница со списком
   релизов; расширение соответствует скачанному дистрибутиву. «Скачать цепочку» — каждый
   файл цепочки является дистрибутивом своей версии.
2. **Монитор серверов (#324):** подключение к `localhost:27545` — поиск rac занимает доли
   секунды, в поле подключения имя кластера, вкладка «Задания» заполнена.

## Тесты

Полный набор `dotnet test` зелёный (**1996**); кросс-сборка Linux
(`dotnet build -p:BuildLinux=true`) без ошибок. Новые:
[`OneCUpdatesDistributionResolutionTests`](ConfigurationManagement.Tests/OneCUpdatesDistributionResolutionTests.cs)
(+14: цепочка «каталог → version_files → страница файла → transfer_file» на fake-HTTP-стеке,
разрешение реального файла, защита от зацикливания и страницы входа, подстановка расширения),
тесты форматов `job list`, `ToString` кластерных строк и быстрого поиска rac.

## Сборка

- Windows: single-file self-contained WPF (`net10.0-windows`, win-x64) — `ConfigurationManagement.exe`.
- Linux: single-file self-contained Avalonia (`net10.0`, linux-x64) — `ConfigurationManagement`,
  пакет `configuration-management_0.3.9.331_amd64.deb`.
- Контрольные суммы — в `SHA256SUMS.txt`.

Issues остаются открытыми — ждём подтверждения от репортеров.
