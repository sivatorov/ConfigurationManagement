# Результат сборки 0.3.12.3

Дата: 2026-10-10 (UTC+3, Europe/Moscow)
Версия csproj: `0.3.12.3` (`FileVersion` + `InformationalVersion`) — проверено
(FileVersion в exe = 0.3.12.3, `control Version:` в .deb = 0.3.12.3)
HEAD: `a962075b55497ca59b252473bb0f1bf3610f0bf8` (суффикс ProductVersion exe через SourceLink)
Среда: Windows 11, .NET SDK 10 (net10.0-windows / net10.0), кросс-сборка Linux из Windows
Тесты: **2279 passed, 0 failed** (`dotnet test -c Release`, ConfigurationManagement.Tests —
прогнаны до сборки).

## Сборка

### Windows (WPF, net10.0-windows, win-x64)
- Скрипт: `Configuration Management\build-windows-single-file.ps1` (Release, self-contained single-file).
- Сборка прошла с первой попытки: **Ошибок: 0** (предупреждения CS8625/CS8602/CS8604 —
  прежние, не связанные с релизом).
- Версия: `FileVersion = 0.3.12.3`, `ProductVersion = 0.3.12.3+a962075b55497ca59b252473bb0f1bf3610f0bf8`,
  `ProductName = Управление конфигурациями 1С`.
- **Обязательный ассет релиза** (issue #358): bare single-file exe
  `dist/win-x64/ConfigurationManagement.exe` — MZ-сигнатура подтверждена
  (`4D 5A`), native PE, НЕ zip.
- ZIP-архив (доп. ассет): `publish/out-0.3.12.3/ConfigurationManagement-0.3.12.3-win-x64.zip` —
  содержит ровно один файл `ConfigurationManagement.exe` (84 159 685 байт), размер внутри
  архива совпадает с отдельным exe.

### Кросс-сборка Linux (Avalonia, net10.0, linux-x64)
- Скрипт: `Configuration Management\build-linux-single-file.ps1` (`ForceLinux=true` → net10.0 + Avalonia).
- Результат: **Ошибок: 0**; ELF-magic подтверждён (`7f 45 4c 46`, через проверку .deb).

### .deb
- Скрипт: `publish/build_deb_win_0.3.12.3.py` (копия 0.3.12.2; версия читается из
  `InformationalVersion` csproj = 0.3.12.3; бинарь из `dist/linux-x64/`; ar-архив на Windows,
  детерминированно: uid=gid=0, mtime=0, gzip mtime=0).
- Результат: `package/linux/deb/out/configuration-management_0.3.12.3_amd64.deb`
  (45 399 190 байт).

## Артефакты

Копии для релиза (каталог `publish/out-0.3.12.3/`):

| Файл | Размер (байт) | Размер (MiB) | SHA-256 |
|---|---|---|---|
| `ConfigurationManagement.exe` | 84 159 685 | ≈ 80.3 | `09cfef4b7716753ce5f6b4b6344add4fa6fc42963862ecc8dbf62d16b85bb450` |
| `ConfigurationManagement-0.3.12.3-win-x64.zip` | 78 346 151 | ≈ 74.7 | `3f64443a5b890fcf897f35b903ff3f0aaa4c349ac06f36f14aa0c7a16024528b` |
| `ConfigurationManagement` (linux-x64) | 52 625 907 | ≈ 50.2 | `52d5802bc24980fbd72834bfc184539ea61d4fc5919217b45dd83d55983a9b5a` |
| `configuration-management_0.3.12.3_amd64.deb` | 45 399 190 | ≈ 43.3 | `10e180cec7c70f628d7311a5920bec6008d50d221a535809a32f9cedc3da8a9d` |
| `SHA256SUMS.txt` | 405 байт | — | 4 записи (хэш + два пробела + имя, LF, UTF-8 без BOM); SHA256 самого файла: `d88773741b057c5a984c5b734e55b8aedf242526bf0cef739218ae0605b257d9` |

Исходные (в `dist/` и `package/linux/deb/out/`):
- `Configuration Management/dist/win-x64/ConfigurationManagement.exe` — 84 159 685 байт;
- `Configuration Management/dist/linux-x64/ConfigurationManagement` — 52 625 907 байт;
- `package/linux/deb/out/configuration-management_0.3.12.3_amd64.deb` — 45 399 190 байт.

## Проверки артефактов

### Версия
- Windows exe: `FileVersion = 0.3.12.3`, `ProductVersion = 0.3.12.3+a962075b…`.
- ZIP: единственная запись `ConfigurationManagement.exe` (84 159 685 байт) — размер
  внутри архива совпадает с exe вне архива.
- .deb: `control Version: 0.3.12.3` — совпадает.

### Windows exe (обязательный ассет #358)
- MZ-сигнатура: первые 2 байта `4D 5A` — native PE single-file, не ZIP/HTML.
- Smoke-запуск: `ConfigurationManagement.exe --list` — exit 0, в stdout список из 7 баз
  (Accounting, buh_empty, hrmcorp, IRONSKILLS: База знаний, trade11_empty, ut11.6, Троговля 11).

### .deb (publish/check_deb_win_0.3.12.3.py)
- ar-members: `debian-binary`, `control.tar.gz`, `data.tar.gz` — корректны.
- `control Version: 0.3.12.3` — совпадает.
- data.tar.gz: `usr/bin/ConfigurationManagement` (mode `0o755`, ELF-magic `7f 45 4c 46`,
  52 625 907 байт — совпадает с исходным бинарём),
  `usr/share/applications/configuration-management.desktop`,
  `usr/share/icons/hicolor/256x256/apps/configuration-management.png`,
  `usr/share/doc/configuration-management/copyright`.
- `DEBIAN/md5sums`: 4 записи, совпадают с md5 содержимого data.tar.gz — **OK**.

### Smoke-запуск
- **Windows**: `ConfigurationManagement.exe --list` — exit 0, список 7 баз (см. выше).
- **Linux**: запуск невозможен — Linux/WSL на машине нет. Верифицировано структурно:
  ELF-magic (`7F 45 4C 46`), размер, SHA256, сборка с 0 ошибок.

### Тесты
- **2279 passed, 0 failed** (`dotnet test -c Release`, прогнаны до сборки).

## Пропущено

- **AppImage** (`package/linux/appimage.sh`): **пропущен** — требует Linux/WSL; WSL на этой
  машине не установлен (в 0.3.12.0, 0.3.12.1, 0.3.12.2, 0.3.11.0 и 0.3.10.3 AppImage также
  не собирался — пропущен по той же причине; release-ассетов AppImage в прошлых релизах не было).
- **Linux smoke-запуск**: недоступен (нет Linux/WSL) — см. выше.

## Примечания

- git-команды НЕ выполнялись (кроме `git rev-parse HEAD` для записи в этот файл),
  issues НЕ комментировались, релиз НЕ создавался, артефакты НЕ загружались на GitHub,
  коммит/пуш НЕ выполнялись — по условию задачи.
- Код приложения НЕ менялся — только сборка и фиксация результатов.
- Новые файлы: `publish/build_deb_win_0.3.12.3.py`, `publish/check_deb_win_0.3.12.3.py`,
  `publish/build_result_0.3.12.3.md`; артефакты (`dist/*`, `package/linux/deb/out/*`,
  `package/linux/deb/staging/*`, `publish/out-0.3.12.3/*`) в git НЕ добавляются
  (в .gitignore/.codeassistantignore).
- Изменений основного кода НЕ требуется — обе сборки (Windows и Linux) прошли с 0 ошибок
  с первой попытки.
- Порядок выкладки в релиз (когда будет выполняться публиковавшая сторона) — по
  `publish/publish_win_0.3.11.0.md`: первым ассетом строго `ConfigurationManagement.exe`
  (по нему `GitHubReleaseService.FindAsset` делает точный выбор), ZIP — доп. ассетом,
  затем `SHA256SUMS.txt`.
