# Результат сборки 0.3.12.2

Дата: 2026-10-10 (UTC+3, Europe/Moscow)
Версия csproj: `0.3.12.2` (`FileVersion` + `InformationalVersion`) — проверено
(FileVersion в exe = 0.3.12.2)
HEAD: `e36cff299d18b5a03798dfb8fdad3514b99a8074` (суффикс ProductVersion exe через SourceLink)
Среда: Windows 11, .NET SDK 10 (net10.0-windows / net10.0), кросс-сборка Linux из Windows
Тесты: **2277 passed, 0 failed** (`dotnet test -c Release`, ConfigurationManagement.Tests —
прогнаны до сборки).

## Сборка

### Windows (WPF, net10.0-windows, win-x64)
- Скрипт: `Configuration Management\build-windows-single-file.ps1` (Release, self-contained single-file).
- Сборка прошла с первой попытки: **Ошибок: 0** (предупреждения CS8625/CS8602/CS8604 —
  прежние, не связанные с релизом).
- Версия: `FileVersion = 0.3.12.2`, `ProductVersion = 0.3.12.2+e36cff299d18b5a03798dfb8fdad3514b99a8074`,
  `ProductName = Управление конфигурациями 1С`.
- **Обязательный ассет релиза** (issue #358): bare single-file exe
  `dist/win-x64/ConfigurationManagement.exe` — MZ-сигнатура подтверждена
  (`4D 5A`), native PE, НЕ zip.
- ZIP-архив (доп. ассет): `publish/out-0.3.12.2/ConfigurationManagement-0.3.12.2-win-x64.zip` —
  содержит ровно один файл `ConfigurationManagement.exe` (84 159 231 байт), размер внутри
  архива совпадает с отдельным exe.

### Кросс-сборка Linux (Avalonia, net10.0, linux-x64)
- Скрипт: `Configuration Management\build-linux-single-file.ps1` (`ForceLinux=true` → net10.0 + Avalonia).
- Результат: **Ошибок: 0**; ELF-magic подтверждён (`7f 45 4c 46`, через проверку .deb).

### .deb
- Скрипт: `publish/build_deb_win_0.3.12.2.py` (копия 0.3.12.1; версия читается из
  `InformationalVersion` csproj = 0.3.12.2; бинарь из `dist/linux-x64/`; ar-архив на Windows,
  детерминированно: uid=gid=0, mtime=0, gzip mtime=0).
- Результат: `package/linux/deb/out/configuration-management_0.3.12.2_amd64.deb`.

## Артефакты

Копии для релиза (каталог `publish/out-0.3.12.2/`):

| Файл | Размер (байт) | Размер (MiB) | SHA-256 |
|---|---|---|---|
| `ConfigurationManagement.exe` | 84 159 231 | ≈ 80.3 | `d1f0c380b8e0238a039d67b0cc700f8ee4effdda0b4443d4500556d2cf933f7d` |
| `ConfigurationManagement-0.3.12.2-win-x64.zip` | 78 345 726 | ≈ 74.7 | `cb6befc93b0603d2f76bb9edd59435082ffe6e0d9e8963196d9ed474c95eb68a` |
| `ConfigurationManagement` (linux-x64) | 52 625 651 | ≈ 50.2 | `36afa563d717c665391b1bc8eb3b60be0110ef43ebe50f36ad9bd98601c921bf` |
| `configuration-management_0.3.12.2_amd64.deb` | 45 398 880 | ≈ 43.3 | `e9366fc12927339559fe0fbae42a6eca167f08b6663688894289eee71557d05a` |
| `SHA256SUMS.txt` | 405 байт | — | 4 записи (хэш + два пробела + имя, LF, UTF-8 без BOM); SHA256 самого файла: `a94181b8185e87d0e9554e948c24fba8d5c0a42c4176b8ad15abfa2228f915e9` |

Исходные (в `dist/` и `package/linux/deb/out/`):
- `Configuration Management/dist/win-x64/ConfigurationManagement.exe` — 84 159 231 байт;
- `Configuration Management/dist/linux-x64/ConfigurationManagement` — 52 625 651 байт;
- `package/linux/deb/out/configuration-management_0.3.12.2_amd64.deb` — 45 398 880 байт.

## Проверки артефактов

### Версия
- Windows exe: `FileVersion = 0.3.12.2`, `ProductVersion = 0.3.12.2+e36cff29…`.
- ZIP: единственная запись `ConfigurationManagement.exe` (84 159 231 байт) — размер
  внутри архива совпадает с exe вне архива.
- .deb: `control Version: 0.3.12.2` — совпадает.

### Windows exe (обязательный ассет #358)
- MZ-сигнатура: первые 2 байта `4D 5A` — native PE single-file, не ZIP/HTML.
- Smoke-запуск: `ConfigurationManagement.exe --list` — exit 0, в stdout список из 7 баз
  (Accounting, buh_empty, hrmcorp, IRONSKILLS: База знаний, trade11_empty, ut11.6, Троговля 11).

### .deb (publish/check_deb_win_0.3.12.2.py)
- ar-members: `debian-binary`, `control.tar.gz`, `data.tar.gz` — корректны.
- `control Version: 0.3.12.2` — совпадает.
- data.tar.gz: `usr/bin/ConfigurationManagement` (mode `0o755`, ELF-magic `7f 45 4c 46`,
  52 625 651 байт — совпадает с исходным бинарём),
  `usr/share/applications/configuration-management.desktop`,
  `usr/share/icons/hicolor/256x256/apps/configuration-management.png`,
  `usr/share/doc/configuration-management/copyright`.
- `DEBIAN/md5sums`: 4 записи, совпадают с md5 содержимого data.tar.gz — **OK**.

### Smoke-запуск
- **Windows**: `ConfigurationManagement.exe --list` — exit 0, список 7 баз (см. выше).
- **Linux**: запуск невозможен — Linux/WSL на машине нет. Верифицировано структурно:
  ELF-magic (`7F 45 4C 46`), размер, SHA256, сборка с 0 ошибок.

### Тесты
- **2277 passed, 0 failed** (`dotnet test -c Release`, прогнаны до сборки).

## Пропущено

- **AppImage** (`package/linux/appimage.sh`): **пропущен** — требует Linux/WSL; WSL на этой
  машине не установлен (в 0.3.12.0, 0.3.12.1, 0.3.11.0 и 0.3.10.3 AppImage также не собирался —
  пропущен по той же причине; release-ассетов AppImage в прошлых релизах не было).
- **Linux smoke-запуск**: недоступен (нет Linux/WSL) — см. выше.

## Примечания

- git-команды НЕ выполнялись (кроме `git rev-parse HEAD` для записи в этот файл),
  issues НЕ комментировались, релиз НЕ создавался, артефакты НЕ загружались на GitHub,
  коммит/пуш НЕ выполнялись — по условию задачи.
- Код приложения НЕ менялся — только сборка и фиксация результатов.
- Новые файлы: `publish/build_result_0.3.12.2.md`, `publish/release_body_0.3.12.2.md`;
  артефакты (`dist/*`, `package/linux/deb/out/*`, `package/linux/deb/staging/*`,
  `publish/out-0.3.12.2/*`) в git НЕ добавляются (в .gitignore/.codeassistantignore).
- Изменений основного кода НЕ требуется — обе сборки (Windows и Linux) прошли с 0 ошибок
  с первой попытки.
- Порядок выкладки в релиз (когда будет выполняться публиковавшая сторона) — по
  `publish/publish_win_0.3.11.0.md`: первым ассетом строго `ConfigurationManagement.exe`
  (по нему `GitHubReleaseService.FindAsset` делает точный выбор), ZIP — доп. ассетом,
  затем `SHA256SUMS.txt`.
