# Результат сборки 0.3.9.332

Дата: 2026-10-09 (UTC+3, Europe/Moscow)
Версия csproj: `0.3.9.332` (`InformationalVersion`) — проверено (FileVersion в exe = 0.3.9.332)
HEAD: `4e0efa687a6a32ba05aa39137564a43c8e8dbcc6` — «0.3.9.329: сброс отбора по тегу (#354), полные каталоги allUpdates + „Версии нет на сайте" (#352), выбор дистрибутива платформы и дерево версий (#330 #334), серверы 1С — имя кластера, статус автообновления, скорость подключения (#324)»; правки цикла 0.3.9.332 в рабочем дереве (НЕ закоммичены, ~184 файла)
Среда: Windows 11, .NET SDK 10 (net10.0-windows / net10.0), кросс-сборка Linux из Windows
Тесты: полный `dotnet test` — **2044 теста, 0 ошибок** (зелёный, выполнен до сборки).

## Сборка

### Windows (WPF, net10.0-windows)
- Скрипт: `Configuration Management\build-windows-single-file.ps1` (Release, RID win-x64, self-contained single-file)
- Результат: **Ошибок: 0** (предупреждения CS8625/CS8602/CS8604 прежние, не связанные с циклом).
- Версия: `FileVersion = 0.3.9.332`, `ProductVersion = 0.3.9.332+4e0efa687a6a32ba05aa39137564a43c8e8dbcc6` (суффикс — SHA коммита HEAD через SourceLink).

### Кросс-сборка Linux (Avalonia, net10.0)
- Скрипт: `Configuration Management\build-linux-single-file.ps1` (`-p:ForceLinux=true` → net10.0 + Avalonia, RID linux-x64)
- Результат: **Ошибок: 0**.

### .deb
- Скрипт: `publish/build_deb_win_0.3.9.332.py` (копия 331; версия читается из
  `InformationalVersion` csproj = 0.3.9.332; бинарь из `dist/linux-x64/`; ar-архив на Windows,
  детерминированно: uid=gid=0, mtime=0, gzip mtime=0).
- Проверка: `publish/check_deb_win_0.3.9.332.py` (адаптирован: путь DEB и `EXPECTED_VERSION` → 0.3.9.332).
  Результат: **OK** — ar-members (`debian-binary`, `control.tar.gz`, `data.tar.gz`) корректны,
  `control Version: 0.3.9.332`, `usr/bin/ConfigurationManagement` mode `0o755`,
  4 файла data.tar.gz совпадают с `DEBIAN/md5sums`.

## Артефакты

Исходные (в `dist/` и `package/linux/deb/out/`):

| Файл | Размер (байт) | SHA-256 |
|---|---|---|
| `Configuration Management/dist/win-x64/ConfigurationManagement.exe` | 84 108 298 | `c64ffddd45f47d5c55a900a82cf5822848283ff689175196fb89d5838485490f` |
| `Configuration Management/dist/linux-x64/ConfigurationManagement` | 52 587 509 | `86c66ae440f58edc97991621b6d36042c5f20e0535ee17941f59c8d13317c2b5` |
| `package/linux/deb/out/configuration-management_0.3.9.332_amd64.deb` | 45 361 296 | `3ccbc996fa1160fe438d0eacf5f8ffc3d6f6a3cd996d1122e3e038f06561babf` |

Размеры в MiB: Windows ≈ 80.2 MiB, Linux ≈ 50.1 MiB, .deb ≈ 43.3 MiB.
Оба бинарника > 50 МБ (для Linux — 52.6 MB) — типичный размер single-file, файлы не пустые.

В папке `dist/win-x64` — ровно один файл (без .dll/.pdb/папок рядом); в `dist/linux-x64` — тоже один файл.

Копии для релиза:

| Каталог | Содержимое |
|---|---|
| `publish/out-0.3.9.332-windows/` | `ConfigurationManagement.exe`, `SHA256SUMS.txt` |
| `publish/out-0.3.9.332-linux/` | `ConfigurationManagement`, `configuration-management_0.3.9.332_amd64.deb`, `SHA256SUMS.txt` |

`SHA256SUMS.txt` — хэш + два пробела + имя, LF (формат sha256sum), содержимое совпадает с таблицей выше.

## Проверки артефактов

### Версия в бинарниках
- Windows: `FileVersion = 0.3.9.332`, `ProductVersion = 0.3.9.332+4e0efa687a6a32ba05aa39137564a43c8e8dbcc6`.
- .deb: `control Version: 0.3.9.332` — совпадает.

### SHA256
- Независимый пересчёт по содержимому копий в `publish/out-0.3.9.332-windows/`
  и `publish/out-0.3.9.332-linux/` выполнен при генерации `SHA256SUMS.txt` — совпадает с таблицей.

### Запуск (smoke)
- **Windows**: `ConfigurationManagement.exe --list` — завершился штатно (**exit 0**), в stdout
  выведен список из 7 баз (Accounting, buh_empty, hrmcorp, IRONSKILLS: База знаний,
  trade11_empty, ut11.6, Троговля 11), stderr пуст. Бинарь работает.
- **Linux**: запуск невозможен — WSL на машине не установлен. Верифицировано структурно:
  ELF-magic (`7F 45 4C 46`), размер (52 587 509), контрольная сумма (`86c66ae4…`),
  сборка с 0 ошибок, состав single-file (один файл).

### .deb (publish/check_deb_win_0.3.9.332.py)
- ar-members: `debian-binary`, `control.tar.gz`, `data.tar.gz`.
- `control Version: 0.3.9.332` — совпадает.
- data.tar.gz: `usr/bin/ConfigurationManagement` (mode 0o755), desktop-файл, иконка, copyright.
- `DEBIAN/md5sums`: 4 записи, совпадают с md5 содержимого data.tar.gz — структура корректна.

### Тесты
- Полный `dotnet test`: **2044 теста, 0 ошибок** (выполнен пользователем до сборки).

## Пропущено

- **AppImage** (`package/linux/appimage.sh`): **пропущен**. Требуется Linux/WSL; WSL на этой
  машине не установлен; в 0.3.9.331 AppImage также не собирался. AppImage не критичен:
  exe и linux single-file собраны, .deb собран.
- **Smoke-запуск Linux-бинарника**: пропущен по той же причине (нет Linux-рантайма/подсистемы).
- **GitHub release, git push, коммит**: НЕ выполнялись — по условию это отдельная задача.

## Примечания

- Релиз НЕ создавался, git push НЕ выполнялся, артефакты/скрипты НЕ коммитились —
  это сборка цикла 0.3.9.332; публикация будет следующей задачей.
- Новые файлы цикла 0.3.9.332 в publish: `publish/build_deb_win_0.3.9.332.py`,
  `publish/check_deb_win_0.3.9.332.py`, `publish/build_result_0.3.9.332.md`;
  артефакты (`dist/*`, `package/linux/deb/out/*`, `publish/out-0.3.9.332-*`) в git НЕ добавляются
  (в .gitignore/.codeassistantignore).
- Артефакты соответствуют прошлому релизу по составу и структуре: Windows exe вырос на
  ~8 КБ (84 108 298 против 84 100 276), Linux — на ~4 КБ (52 587 509 против 52 583 221),
  .deb — на ~4 КБ (45 361 296 против 45 356 886) относительно 0.3.9.331.
