# Результат сборки 0.3.9.328 (issue #352)

Дата: 2026-10-07 (UTC+3, Europe/Moscow)
Версия csproj: `0.3.9.328` (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`) — проверено (FileVersion в exe = 0.3.9.328)
HEAD: `8d8370e2a2d990b0f174bc3190c27af2aef24a73` — «0.3.9.324-0.3.9.327: стрелки и мультивыделение, CollectionView Dispatcher, серверы 1С rac, пропавшие папки (#350 #334 #330 #324 #351)»; правки цикла 0.3.9.328 в рабочем дереве (НЕ закоммичены)
Рабочее дерево: все правки цикла #352 присутствуют в рабочем дереве (`UpdateChainBuilder.cs`, `ConfigUpdateCatalogResult.cs`, `UpdateChainVariant.cs`, `UpdateChainVariantViewModel.cs`, правки `OneCUpdatesService`/`UpdateCheckWindow`*, тесты `UpdateChainBuilderTests` и др.), версия поднята до 0.3.9.328, коммит не выполнялся
Среда: Windows 11, .NET SDK 10 (net10.0-windows / net10.0), кросс-сборка Linux из Windows
План: `plans/PLAN-0.3.9.328.md`
Комментарий в issue #352 уже опубликован ранее (см. `publish/comment-352-0.3.9.328.md`, id 6044987210).

## Состав цикла (кратко)

| Issue | Версия | Изменение |
|---|---|---|
| #352 → 0.3.9.328 | правка в рабочем дереве | Цепочки обновлений для базы: окно «Проверка обновлений» (F9), колонка «Список версий», варианты цепочки («снизу вверх»/«оптимальный»), кнопка «Скачать цепочку»; обе платформы (WPF + Avalonia) |

Все исправления присутствуют в рабочем дереве и скомпилированы в обеих ветках (WPF и Avalonia);
версия поднята до 0.3.9.328. Коммит не выполнялся — релиз будет следующей задачей.

## Сборка

### Windows (WPF, net10.0-windows)
- Скрипт: `Configuration Management\build-windows-single-file.ps1` (Release, RID win-x64, self-contained single-file)
- Результат: **Ошибок: 0** (предупреждения CS8625/CS8602 прежние, не связанные с циклом).

### Кросс-сборка Linux (Avalonia, net10.0)
- Скрипт: `Configuration Management\build-linux-single-file.ps1` (`-p:ForceLinux=true` → net10.0 + Avalonia, RID linux-x64)
- Результат: **Ошибок: 0** — Avalonia-ветка компилируется со всеми исправлениями цикла
  (дополнительная проверка `dotnet build -p:BuildLinux=true` тоже 0 ошибок).

### .deb
- Скрипт: `publish/build_deb_win_0.3.9.328.py` (копия 327; версия читается из
  `InformationalVersion` csproj = 0.3.9.328; бинарь из `dist/linux-x64/`; ar-архив на Windows,
  детерминированно: uid=gid=0, mtime=0, gzip mtime=0).
- Проверка: `publish/check_deb_win_0.3.9.328.py` (адаптирован: путь DEB и `EXPECTED_VERSION` → 0.3.9.328).
  Результат: **OK** — ar-members корректны, `control Version: 0.3.9.328`,
  `usr/bin/ConfigurationManagement` mode `0o755`, 4 файла data.tar.gz совпадают с `DEBIAN/md5sums`.

## Артефакты

Исходные (в `dist/` и `package/linux/deb/out/`):

| Файл | Размер (байт) | SHA-256 |
|---|---|---|
| `Configuration Management/dist/win-x64/ConfigurationManagement.exe` | 84 085 288 | `32a49d43703d4d755be9348d5875e7fef83f54dcd0c353743315f24f4a8d7180` |
| `Configuration Management/dist/linux-x64/ConfigurationManagement` | 52 570 613 | `adfd866bf6453d9d080553f1ae20ced51db57267c8a5e1156383dfc57680dd16` |
| `package/linux/deb/out/configuration-management_0.3.9.328_amd64.deb` | 45 343 770 | `8a03d3b62da842b6c55276865d23ccadd7ebb4b4454df33a46a6a4730a5ce80b` |

Размеры в MiB: Windows ≈ 80.2 MiB, Linux ≈ 50.1 MiB, .deb ≈ 43.2 MiB.

В папке `dist/win-x64` — ровно один файл (без .dll/.pdb/папок рядом); в `dist/linux-x64` — тоже один файл.

Копии для релиза:

| Каталог | Содержимое |
|---|---|
| `publish/out-0.3.9.328-windows/` | `ConfigurationManagement.exe`, `SHA256SUMS.txt` |
| `publish/out-0.3.9.328-linux/` | `ConfigurationManagement`, `configuration-management_0.3.9.328_amd64.deb`, `SHA256SUMS.txt` |

`SHA256SUMS.txt` — хэш + два пробела + имя, LF (формат sha256sum), содержимое совпадает с таблицей выше.

## Проверки артефактов

### Версия в бинарниках
- Windows: `FileVersion = 0.3.9.328`, `ProductVersion = 0.3.9.328+8d8370e2a2d990b0f174bc3190c27af2aef24a73`
  (суффикс — SHA коммита HEAD через SourceLink; в отличие от прошлого цикла, в рабочей копии
  теперь есть локальный HEAD-коммит 8d8370e… от цикла 0.3.9.324–0.3.9.327).

### SHA256
- Независимый пересчёт по содержимому копий в `publish/out-0.3.9.328-windows/`
  и `publish/out-0.3.9.328-linux/` выполнен при генерации `SHA256SUMS.txt`.

### Запуск (smoke)
- **Windows**: `ConfigurationManagement.exe --list` — завершился штатно (exit 0), в stdout
  выведен список из 7 баз (Accounting, buh_empty, hrmcorp, IRONSKILLS: База знаний,
  trade11_empty, ut11.6, Троговля 11), stderr пуст. Бинарь работает.
- **Linux**: запуск невозможен — WSL на машине не установлен. Верифицировано структурно:
  ELF-magic (`7F 45 4C 46`), размер (52 570 613), контрольная сумма
  (`adfd866b…`), сборка с 0 ошибок, состав single-file (один файл).

### .deb (publish/check_deb_win_0.3.9.328.py)
- ar-members: `debian-binary`, `control.tar.gz`, `data.tar.gz`.
- `control Version: 0.3.9.328` — совпадает.
- data.tar.gz: `usr/bin/ConfigurationManagement` (mode 0o755), desktop-файл, иконка, copyright.
- `DEBIAN/md5sums`: 4 записи, совпадают с md5 содержимого data.tar.gz — структура корректна.

### Тесты
- Полный `dotnet test`: **1941 тест, 0 ошибок** (WPF-ветка, net10.0-windows).
- Кросс-сборка Linux `dotnet build -p:BuildLinux=true`: **0 ошибок**.
- Новые тесты цикла 0.3.9.328: `UpdateChainBuilderTests.cs` (+9) и тесты парсера
  колонки «Список версий» (`OneCPlatformCatalogParserTests`/`PlatformUpdateServiceTests`, +5).

## Пропущено

- **AppImage** (`package/linux/appimage.sh`): **пропущен**. Требуется Linux/WSL; WSL на этой
  машине не установлен. AppImage не критичен: exe и linux single-file собраны, .deb собран.
- **Smoke-запуск Linux-бинарника**: пропущен по той же причине (нет Linux-рантайма/подсистемы).
- **GitHub release v0.3.9.328 и git push**: НЕ выполнялись — по условию цикла это отдельная задача
  (комментарий в issue #352 уже опубликован ранее; issues самостоятельно не закрываются).

## Примечания

- Релиз НЕ создавался, git push НЕ выполнялся, артефакты/скрипты НЕ коммитились —
  это сборка цикла 0.3.9.328; публикация будет следующей задачей.
- Новые файлы цикла 0.3.9.328 в publish: `publish/build_deb_win_0.3.9.328.py`,
  `publish/check_deb_win_0.3.9.328.py`, `publish/build_result_0.3.9.328.md`;
  артефакты (`dist/*`, `package/linux/deb/out/*`, `publish/out-0.3.9.328-*`) в git НЕ добавляются (в .gitignore).
- Предыдущее содержимое этого файла (отчёт о публикации комментария в issue #352) сохранено
  в `publish/comment-352-0.3.9.328.md` (id комментария 6044987210).

## Релиз

- **Коммит**: `aff9222` — «0.3.9.328: цепочки обновлений для базы — таблица вариантов,
  граф обновления, кнопка скачать цепочку (#352)» (26 файлов, +2577/−42).
- **Ветка**: `main` = `origin/main` = `aff9222` (push выполнен: `8d8370e..aff9222`).
- **Тег**: `v0.3.9.328` — аннотированный (`git tag -a -m "0.3.9.328"`), запушен в `origin`.
- **Релиз**: https://github.com/sivatorov/ConfigurationManagement/releases/tag/v0.3.9.328
  (published, не draft/prerelease; тело — из `publish/release_body_0.3.9.328.md`).
- **Issue #352**: остался **открытым** (state=OPEN); комментарий от sivatorov
  (id 6044987210) на месте — issues самостоятельно не закрываются.

### Ассеты релиза

| Ассет | Размер (байт) | Примечание |
|---|---|---|
| `ConfigurationManagement.exe` | 84 085 288 | Windows WPF single-file (`publish/out-0.3.9.328-windows/`); размер совпадает с таблицей «Артефакты» ✓ |
| `SHA256SUMS.txt` | 94 | контрольные суммы Windows (`publish/out-0.3.9.328-windows/SHA256SUMS.txt`) |
| `ConfigurationManagement` | 52 570 613 | Linux Avalonia single-file (`publish/out-0.3.9.328-linux/`); размер совпадает ✓ |
| `configuration-management_0.3.9.328_amd64.deb` | 45 343 770 | `.deb`; размер совпадает ✓ |
| `SHA256SUMS-linux-0.3.9.328.txt` | 201 | контрольные суммы Linux — копия `publish/out-0.3.9.328-linux/SHA256SUMS.txt`; GitHub не допускает два ассета с именем `SHA256SUMS.txt`, поэтому загружен с версией в имени (содержимое идентично оригиналу, 201 байт) |
| `ConfigurationManagement-linux-x64` | 52 568 943 | прикреплён автоматически workflow `release.yml` по тегу (Linux-сборка в Actions) |

Всего 6 ассетов: 5 загружены вручную (`gh release upload`), 1 — от Linux-workflow.
Размеры `ConfigurationManagement.exe`, `ConfigurationManagement` и `.deb` совпадают
с SHA-таблицей раздела «Артефакты»; оба SHA-файла содержат хэши этих бинарников.