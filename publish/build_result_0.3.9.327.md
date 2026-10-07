# Результат сборки 0.3.9.327 (issue #351)

Дата: 2026-10-07 (UTC+3, Europe/Moscow)
Версия csproj: `0.3.9.327` (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`) — проверено (FileVersion в exe = 0.3.9.327)
HEAD: локальных коммитов нет — ветка `main` в рабочей копии находится в состоянии «No commits yet» (весь проект в git-индексе, история не подтянута); цикл стартовал с выпущенной версии 0.3.9.323
Рабочее дерево: все правки цикла 0.3.9.324–0.3.9.327 в рабочем дереве/индексе, коммиты не выполнялись
Среда: Windows 11, .NET SDK 10 (net10.0-windows / net10.0), кросс-сборка Linux из Windows
План: `plans/PLAN-0.3.9.324.md`

## Состав цикла (кратко)

| Issue | Версия | Изменение |
|---|---|---|
| #350 → 0.3.9.324 | правка в рабочем дереве | Стрелки ↑/↓ снимают мультивыделение (WPF + Avalonia) |
| #334 + #330 → 0.3.9.325 | правка в рабочем дереве | NotSupportedException CollectionView: каталог платформы через UiDispatch |
| #324 → 0.3.9.326 | правка в рабочем дереве | Серверы 1С: кэш формата job list, fallback имени кластера, тайминги rac |
| #351 → 0.3.9.327 | правка в рабочем дереве | Пропавшие папки: безопасная дедупликация групп, диагностика импорта |

Все исправления присутствуют в рабочем дереве и скомпилированы в обеих ветках (WPF и Avalonia);
версия поднята до 0.3.9.327. Коммит не выполнялся — релиз будет следующей задачей.

## Сборка

### Windows (WPF, net10.0-windows)
- Скрипт: `Configuration Management\build-windows-single-file.ps1` (Release, RID win-x64, self-contained single-file)
- Результат: **Ошибок: 0** (предупреждения CS8625/CS8602 прежние, не связанные с циклом).

### Кросс-сборка Linux (Avalonia, net10.0)
- Скрипт: `Configuration Management\build-linux-single-file.ps1` (`-p:ForceLinux=true` → net10.0 + Avalonia, RID linux-x64)
- Результат: **Ошибок: 0** — Avalonia-ветка компилируется со всеми исправлениями цикла
  (дополнительная проверка `dotnet build -p:BuildLinux=true` тоже 0 ошибок).

### .deb
- Скрипт: `publish/build_deb_win_0.3.9.327.py` (копия образца 323; версия читается из
  `InformationalVersion` csproj = 0.3.9.327; бинарь из `dist/linux-x64/`; ar-архив на Windows,
  детерминированно: uid=gid=0, mtime=0, gzip mtime=0).
- Проверка: `publish/check_deb_win_0.3.9.327.py` (адаптирован: путь DEB и `EXPECTED_VERSION` → 0.3.9.327).
  Результат: **OK** — ar-members корректны, `control Version: 0.3.9.327`,
  `usr/bin/ConfigurationManagement` mode `0o755`, 4 файла data.tar.gz совпадают с `DEBIAN/md5sums`.

## Артефакты

Исходные (в `dist/` и `package/linux/deb/out/`):

| Файл | Размер (байт) | SHA-256 |
|---|---|---|
| `Configuration Management/dist/win-x64/ConfigurationManagement.exe` | 84 074 124 | `bfc58ac3fb2dfc229287c61d6229b80d74b3d9968043fd2f0fb6b821273c9fbe` |
| `Configuration Management/dist/linux-x64/ConfigurationManagement` | 52 559 477 | `51ffcf1092e7dff7fdade2547ade46a6dad88285a3ce70d94d00be3915ec1e7d` |
| `package/linux/deb/out/configuration-management_0.3.9.327_amd64.deb` | 45 332 286 | `2e159a6f855dc13e65e7f1f7b937f43c8ef6ea0778f191290574f026ceb42230` |

Размеры в MiB: Windows ≈ 80.2 MiB, Linux ≈ 50.1 MiB, .deb ≈ 43.2 MiB.

В папке `dist/win-x64` — ровно один файл (без .dll/.pdb/папок рядом); в `dist/linux-x64` — тоже один файл.

Копии для релиза:

| Каталог | Содержимое |
|---|---|
| `publish/out-0.3.9.327-windows/` | `ConfigurationManagement.exe`, `SHA256SUMS.txt` |
| `publish/out-0.3.9.327-linux/` | `ConfigurationManagement`, `configuration-management_0.3.9.327_amd64.deb`, `SHA256SUMS.txt` |

`SHA256SUMS.txt` — хэш + два пробела + имя, LF (формат sha256sum), содержимое совпадает с таблицей выше.

## Проверки артефактов

### Версия в бинарниках
- Windows: `FileVersion = 0.3.9.327`, `ProductVersion = 0.3.9.327`
  (суффикс SHA коммита git/SourceLink не добавляется — локальных коммитов в рабочей копии нет).

### SHA256
- Независимый пересчёт по содержимому копий в `publish/out-0.3.9.327-windows/`
  и `publish/out-0.3.9.327-linux/` выполнен при генерации `SHA256SUMS.txt`.

### Запуск (smoke)
- **Windows**: `ConfigurationManagement.exe --list` — завершился штатно (exit 0), в stdout
  выведен список из 7 баз (Accounting, buh_empty, hrmcorp, IRONSKILLS: База знаний,
  trade11_empty, ut11.6, Троговля 11), stderr пуст. Бинарь работает.
- **Linux**: запуск невозможен — WSL на машине не установлен. Верифицировано структурно:
  ELF-magic, размер, контрольная сумма, сборка с 0 ошибок, состав single-file (один файл).

### .deb (publish/check_deb_win_0.3.9.327.py)
- ar-members: `debian-binary`, `control.tar.gz`, `data.tar.gz`.
- `control Version: 0.3.9.327` — совпадает.
- data.tar.gz: `usr/bin/ConfigurationManagement` (mode 0o755), desktop-файл, иконка, copyright.
- `DEBIAN/md5sums`: 4 записи, совпадают с md5 содержимого data.tar.gz — структура корректна.

### Тесты
- Полный `dotnet test`: **1927 тестов, 0 ошибок** (WPF-ветка, net10.0-windows).
- Кросс-сборка Linux `dotnet build -p:BuildLinux=true`: **0 ошибок**.
- Новые тесты цикла 0.3.9.327: `IbasesV8iImporterTests.cs` (+7) и `GroupNodeViewModelTests.cs` (+2).

## Пропущено

- **AppImage** (`package/linux/appimage.sh`): **пропущен**. Требуется Linux/WSL; WSL на этой
  машине не установлен. AppImage не критичен: exe и linux single-file собраны, .deb собран.
- **Smoke-запуск Linux-бинарника**: пропущен по той же причине (нет Linux-рантайма/подсистемы).
- **GitHub-релизы v0.3.9.324…v0.3.9.327 и комментарии в issues (#350/#334/#330/#324/#351)**:
  НЕ создавались — по условию цикла комментарии публикуются ПОСЛЕ релизов, issues сами
  не закрываются; это отдельная задача (push тегов, релизы, `_post_comments_*`/`_update_state_*`).

## Примечания

- Релиз НЕ создавался, git push НЕ выполнялся, артефакты/скрипты НЕ коммитились —
  это сборка цикла 0.3.9.324–0.3.9.327; публикация будет следующей задачей.
- Новые файлы цикла 0.3.9.327: `Configuration Management/Services/IbasesV8iImporter.cs` (правки),
  `ConfigurationManagement.Tests/IbasesV8iImporterTests.cs` (новый),
  `ConfigurationManagement.Tests/GroupNodeViewModelTests.cs` (+2),
  `publish/build_deb_win_0.3.9.327.py`, `publish/check_deb_win_0.3.9.327.py`,
  `publish/build_result_0.3.9.327.md`; артефакты
  (`dist/*`, `package/linux/deb/out/*`, `publish/out-0.3.9.327-*`) в git НЕ добавляются (в .gitignore).