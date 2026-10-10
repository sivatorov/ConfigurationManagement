# Результат сборки 0.3.12.1

Дата: 2026-10-10 (UTC+3, Europe/Moscow)
Версия csproj: `0.3.12.1` (`FileVersion` + `InformationalVersion`) — проверено
(FileVersion в exe = 0.3.12.1)
HEAD: `ac4b2cf5fa9a1e676368ccb934f0c01be92c59e8` (суффикс ProductVersion exe через SourceLink)
Среда: Windows 11, .NET SDK 10 (net10.0-windows / net10.0), кросс-сборка Linux из Windows
Тесты: **2259 passed** (прогнаны до сборки, по условию задачи).

## Сборка

### Windows (WPF, net10.0-windows, win-x64)
- Скрипт: `Configuration Management\build-windows-single-file.ps1` (Release, self-contained single-file).
- Сборка прошла с первой попытки: **Ошибок: 0** (предупреждения CS8625/CS8602/CS8604 —
  прежние, не связанные с релизом).
- Версия: `FileVersion = 0.3.12.1`, `ProductVersion = 0.3.12.1+ac4b2cf5fa9a1e676368ccb934f0c01be92c59e8`,
  `ProductName = Управление конфигурациями 1С`.
- **Обязательный ассет релиза** (issue #358): bare single-file exe
  `dist/win-x64/ConfigurationManagement.exe` — MZ-сигнатура подтверждена
  (`4D 5A`), native PE, НЕ zip.
- ZIP-архив (доп. ассет): `publish/out-0.3.12.1/ConfigurationManagement-0.3.12.1-win-x64.zip` —
  содержит ровно один файл `ConfigurationManagement.exe` (84 157 629 байт), SHA256 внутри
  архива совпадает с отдельным exe.

### Кросс-сборка Linux (Avalonia, net10.0, linux-x64)
- Скрипт: `Configuration Management\build-linux-single-file.ps1` (`ForceLinux=true` → net10.0 + Avalonia).
- Результат: **Ошибок: 0**; ELF-magic подтверждён (`7f 45 4c 46`, через проверку .deb).

### .deb
- Скрипт: `publish/build_deb_win_0.3.12.1.py` (копия 0.3.12.0; версия читается из
  `InformationalVersion` csproj = 0.3.12.1; бинарь из `dist/linux-x64/`; ar-архив на Windows,
  детерминированно: uid=gid=0, mtime=0, gzip mtime=0).
- Результат: `package/linux/deb/out/configuration-management_0.3.12.1_amd64.deb`.

## Артефакты

Копии для релиза (каталог `publish/out-0.3.12.1/`):

| Файл | Размер (байт) | Размер (MiB) | SHA-256 |
|---|---|---|---|
| `ConfigurationManagement.exe` | 84 157 629 | ≈ 80.3 | `8ee671c71f80f526be9dc01ca2cdeb692b1466f9f7fe3611537abaa8e477047a` |
| `ConfigurationManagement-0.3.12.1-win-x64.zip` | 78 343 378 | ≈ 74.7 | `0e38fb957efbed6a327cf8f8a0587e1c6a4e9ac07c1ad62e02c2df9bff0bcf3c` |
| `ConfigurationManagement` (linux-x64) | 52 624 563 | ≈ 50.2 | `de07cb6fe9737d0c3cce961418bc51212e494febfcaf3ed38b7916990e25edf0` |
| `configuration-management_0.3.12.1_amd64.deb` | 45 397 808 | ≈ 43.3 | `a5852597f203fc9a89aec0654a0da6fe08ab94d38dbd4af437dcc2d08838b38f` |
| `SHA256SUMS.txt` | — | — | 4 записи (хэш + два пробела + имя, LF, UTF-8 без BOM); SHA256 самого файла: `1d051297b64d1a5de1b4b8540f98e44d3c6b77aec2b261ecad540e88d7527d64` |

Исходные (в `dist/` и `package/linux/deb/out/`):
- `Configuration Management/dist/win-x64/ConfigurationManagement.exe` — 84 157 629 байт;
- `Configuration Management/dist/linux-x64/ConfigurationManagement` — 52 624 563 байт;
- `package/linux/deb/out/configuration-management_0.3.12.1_amd64.deb` — 45 397 808 байт.

## Проверки артефактов

### Версия
- Windows exe: `FileVersion = 0.3.12.1`, `ProductVersion = 0.3.12.1+ac4b2cf5…`.
- ZIP: единственная запись `ConfigurationManagement.exe` (84 157 629 байт), SHA256 внутри
  архива совпадает с exe вне архива (`8ee671c7…`) → содержимое ZIP идентично проверенному exe.
- .deb: `control Version: 0.3.12.1` — совпадает.

### Windows exe (обязательный ассет #358)
- MZ-сигнатура: первые 2 байта `4D 5A` — native PE single-file, не ZIP/HTML.
- Smoke-запуск: `ConfigurationManagement.exe --list` — exit 0, в stdout список из 7 баз
  (Accounting, buh_empty, hrmcorp, IRONSKILLS: База знаний, trade11_empty, ut11.6, Троговля 11).

### .deb (publish/check_deb_win_0.3.12.1.py)
- ar-members: `debian-binary`, `control.tar.gz`, `data.tar.gz` — корректны.
- `control Version: 0.3.12.1` — совпадает.
- data.tar.gz: `usr/bin/ConfigurationManagement` (mode `0o755`, ELF-magic `7f 45 4c 46`,
  52 624 563 байт — совпадает с исходным бинарём),
  `usr/share/applications/configuration-management.desktop`,
  `usr/share/icons/hicolor/256x256/apps/configuration-management.png`,
  `usr/share/doc/configuration-management/copyright`.
- `DEBIAN/md5sums`: 4 записи, совпадают с md5 содержимого data.tar.gz — **OK**.

### Smoke-запуск
- **Windows**: `ConfigurationManagement.exe --list` — exit 0, список 7 баз (см. выше).
- **Linux**: запуск невозможен — Linux/WSL на машине нет. Верифицировано структурно:
  ELF-magic (`7F 45 4C 46`), размер, SHA256, сборка с 0 ошибок.

### Тесты
- По условию задачи: **2259 passed** (прогнаны до сборки).

## Пропущено

- **AppImage** (`package/linux/appimage.sh`): **пропущен** — требует Linux/WSL; WSL на этой
  машине не установлен (в 0.3.12.0, 0.3.11.0 и 0.3.10.3 AppImage также не собирался — пропущен
  по той же причине; release-ассетов AppImage в прошлых релизах не было).
- **Linux smoke-запуск**: недоступен (нет Linux/WSL) — см. выше.

## Примечания

- git-команды НЕ выполнялись (кроме `git rev-parse HEAD` для записи в этот файл),
  issues НЕ комментировались, релиз НЕ создавался, артефакты НЕ загружались на GitHub —
  по условию задачи.
- Новые файлы: `publish/build_deb_win_0.3.12.1.py`, `publish/check_deb_win_0.3.12.1.py`,
  `publish/build_result_0.3.12.1.md`; артефакты (`dist/*`, `package/linux/deb/out/*`,
  `package/linux/deb/staging/*`, `publish/out-0.3.12.1/*`) в git НЕ добавляются
  (в .gitignore/.codeassistantignore).
- Изменений основного кода НЕ требуется — обе сборки (Windows и Linux) прошли с 0 ошибок
  с первой попытки.
- Порядок выкладки в релиз (когда будет выполняться публиковавшая сторона) — по
  `publish/publish_win_0.3.11.0.md`: первым ассетом строго `ConfigurationManagement.exe`
  (по нему `GitHubReleaseService.FindAsset` делает точный выбор), ZIP — доп. ассетом,
  затем `SHA256SUMS.txt`.
