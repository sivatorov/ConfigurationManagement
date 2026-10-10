# Результат сборки 0.3.11.0

Дата: 2026-10-10 (UTC+3, Europe/Moscow)
Версия csproj: `0.3.11.0` (`FileVersion` + `InformationalVersion`) — проверено
(FileVersion в exe = 0.3.11.0)
HEAD: `3519cc0f1b00a57d1d1a41797086d32e8eeded72` (суффикс ProductVersion exe через SourceLink)
Среда: Windows 11, .NET SDK 10 (net10.0-windows / net10.0), кросс-сборка Linux из Windows
Тесты: **2203 теста, 0 ошибок** (прогнаны до сборки, по условию задачи).

## Сборка

### Windows (WPF, net10.0-windows, win-x64)
- Скрипт: `Configuration Management\build-windows-single-file.ps1` (Release, self-contained single-file).
- Сборка прошла с первой попытки: **Ошибок: 0** (предупреждения CS8625/CS8602/CS8604 —
  прежние, не связанные с релизом).
- Версия: `FileVersion = 0.3.11.0`, `ProductVersion = 0.3.11.0+3519cc0f1b00a57d1d1a41797086d32e8eeded72`,
  `ProductName = Управление конфигурациями 1С`.
- **Обязательный ассет релиза** (issue #358): bare single-file exe
  `dist/win-x64/ConfigurationManagement.exe` — MZ-сигнатура подтверждена
  (`4D 5A`), native PE, НЕ zip.
- ZIP-архив (доп. ассет): `publish/out-0.3.11.0/ConfigurationManagement-0.3.11.0-win-x64.zip` —
  содержит ровно один файл `ConfigurationManagement.exe` (84 148 036 байт), SHA256 внутри
  архива совпадает с отдельным exe.

### Кросс-сборка Linux (Avalonia, net10.0, linux-x64)
- Скрипт: `Configuration Management\build-linux-single-file.ps1` (`ForceLinux=true` → net10.0 + Avalonia).
- Результат: **Ошибок: 0**; ELF-magic подтверждён (`7f 45 4c 46`).

### .deb
- Скрипт: `publish/build_deb_win_0.3.11.0.py` (копия 0.3.10.3; версия читается из
  `InformationalVersion` csproj = 0.3.11.0; бинарь из `dist/linux-x64/`; ar-архив на Windows,
  детерминированно: uid=gid=0, mtime=0, gzip mtime=0).
- Результат: `package/linux/deb/out/configuration-management_0.3.11.0_amd64.deb`.

## Артефакты

Копии для релиза (каталог `publish/out-0.3.11.0/`):

| Файл | Размер (байт) | Размер (MiB) | SHA-256 |
|---|---|---|---|
| `ConfigurationManagement.exe` | 84 148 036 | ≈ 80.2 | `bfb66bc5bf2680682c4e2c10f25fd448f2bc7d698b5268e9a75d0a1030641c75` |
| `ConfigurationManagement-0.3.11.0-win-x64.zip` | 78 335 158 | ≈ 74.7 | `2ef07f4681789a9f9f32a3f4a446f4c69d9c26f194429ce73322db2858bf6b28` |
| `ConfigurationManagement` (linux-x64) | 52 618 227 | ≈ 50.2 | `3168bdaf0b8de61f2e75d742daf69fa61d105ec8d23f680aed93da1c6149a40b` |
| `configuration-management_0.3.11.0_amd64.deb` | 45 391 386 | ≈ 43.3 | `ec1005b617b0b0957c27667b5c826721dd8bf671a6e9c5f02b0af4b7f74e3a74` |
| `SHA256SUMS.txt` | — | — | 4 записи (хэш + два пробела + имя, LF, UTF-8 без BOM); SHA256 самого файла: `3ff932e27df496f66d36f8410d69d221dad241f31db8a8d50b323bddef935441` |

Исходные (в `dist/` и `package/linux/deb/out/`):
- `Configuration Management/dist/win-x64/ConfigurationManagement.exe` — 84 148 036 байт;
- `Configuration Management/dist/linux-x64/ConfigurationManagement` — 52 618 227 байт;
- `package/linux/deb/out/configuration-management_0.3.11.0_amd64.deb` — 45 391 386 байт.

## Проверки артефактов

### Версия
- Windows exe: `FileVersion = 0.3.11.0`, `ProductVersion = 0.3.11.0+3519cc0f…`.
- ZIP: единственная запись `ConfigurationManagement.exe` (84 148 036 байт), SHA256 внутри
  архива совпадает с exe вне архива (`bfb66bc5…`) → содержимое ZIP идентично проверенному exe.
- .deb: `control Version: 0.3.11.0` — совпадает.

### Windows exe (обязательный ассет #358)
- MZ-сигнатура: первые 2 байта `4D 5A` — native PE single-file, не ZIP/HTML.
- Smoke-запуск: `ConfigurationManagement.exe --list` — exit 0, в stdout список из 7 баз
  (Accounting, buh_empty, hrmcorp, IRONSKILLS: База знаний, trade11_empty, ut11.6, Троговля 11).

### .deb (publish/check_deb_win_0.3.11.0.py)
- ar-members: `debian-binary`, `control.tar.gz`, `data.tar.gz` — корректны.
- `control Version: 0.3.11.0` — совпадает.
- data.tar.gz: `usr/bin/ConfigurationManagement` (mode `0o755`, ELF-magic `7f 45 4c 46`,
  52 618 227 байт — совпадает с исходным бинарём),
  `usr/share/applications/configuration-management.desktop`,
  `usr/share/icons/hicolor/256x256/apps/configuration-management.png`,
  `usr/share/doc/configuration-management/copyright`.
- `DEBIAN/md5sums`: 4 записи, совпадают с md5 содержимого data.tar.gz — **OK**.

### Smoke-запуск
- **Windows**: `ConfigurationManagement.exe --list` — exit 0, список 7 баз (см. выше).
- **Linux**: запуск невозможен — Linux/WSL на машине нет. Верифицировано структурно:
  ELF-magic (`7F 45 4C 46`), размер, SHA256, сборка с 0 ошибок.

### Тесты
- По условию задачи: **2203 теста, 0 ошибок** (прогнаны до сборки).

## Пропущено

- **AppImage** (`package/linux/appimage.sh`): **пропущен** — требует Linux/WSL; WSL на этой
  машине не установлен (в 0.3.10.3 AppImage также не собирался — пропущен по той же причине;
  release-ассетов AppImage в прошлых релизах не было).
- **Linux smoke-запуск**: недоступен (нет Linux/WSL) — см. выше.

## Примечания

- git-команды НЕ выполнялись, issues НЕ комментировались, релиз НЕ создавался,
  артефакты НЕ загружались на GitHub — по условию задачи.
- Новые файлы: `publish/check_deb_win_0.3.11.0.py`, `publish/build_result_0.3.11.0.md`;
  артефакты (`dist/*`, `package/linux/deb/out/*`, `publish/out-0.3.11.0/*`) в git НЕ
  добавляются (в .gitignore/.codeassistantignore).
- Порядок выкладки в релиз (когда будет выполняться публиковавшая сторона) — по
  `publish/publish_win_0.3.11.0.md`: первым ассетом строго `ConfigurationManagement.exe`
  (по нему `GitHubReleaseService.FindAsset` делает точный выбор), ZIP — доп. ассетом,
  затем `SHA256SUMS.txt`.
