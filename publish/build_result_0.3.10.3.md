# Результат сборки 0.3.10.3

Дата: 2026-10-09 (UTC+3, Europe/Moscow)
Версия csproj: `0.3.10.3` (`InformationalVersion`) — проверено (FileVersion в exe = 0.3.10.3)
HEAD: `3519cc0f1b00a57d1d1a41797086d32e8eeded72` (суффикс ProductVersion exe через SourceLink)
Среда: Windows 11, .NET SDK 10.0.401 (net10.0-windows / net10.0), кросс-сборка Linux из Windows
Тесты: **2140 тестов, 0 ошибок** (прогнаны до сборки, по условию задачи).

## Сборка

### Windows (WPF, net10.0-windows, win-x64)
- Скрипт: `Configuration Management\build-windows-single-file.ps1` (Release, self-contained single-file).
- Примечание: первые две попытки упали с MC1000/SourceLink-ошибкой «Операция в облаке не
  завершена до окончания времени ожидания» (Yandex.Disk, файлы-заглушки в `obj/`).
  После удаления проблемных файлов (`Configuration Management.sourcelink.json`,
  `ConfigurationManagement_MarkupCompile.cache`) сборка прошла: **Ошибок: 0**
  (предупреждения CS8625/CS8602/CS8604 — прежние, не связанные с релизом).
- Версия: `FileVersion = 0.3.10.3`, `ProductVersion = 0.3.10.3+3519cc0f1b00a57d1d1a41797086d32e8eeded72`,
  `ProductName = Управление конфигурациями 1С`.
- ZIP-архив: `publish/out-0.3.10.3/ConfigurationManagement-0.3.10.3-win-x64.zip` —
  содержит ровно один файл `ConfigurationManagement.exe`, SHA256 внутри архива совпадает
  с отдельным exe.

### Кросс-сборка Linux (Avalonia, net10.0, linux-x64)
- Скрипт: `Configuration Management\build-linux-single-file.ps1` (`ForceLinux=true` → net10.0 + Avalonia).
- Результат: **Ошибок: 0**; ELF-magic подтверждён (`7f 45 4c 46`).

### .deb
- Скрипт: `publish/build_deb_win_0.3.10.3.py` (копия 0.3.10.2; версия читается из
  `InformationalVersion` csproj = 0.3.10.3; бинарь из `dist/linux-x64/`; ar-архив на Windows,
  детерминированно: uid=gid=0, mtime=0, gzip mtime=0).
- Результат: `package/linux/deb/out/configuration-management_0.3.10.3_amd64.deb`.

## Артефакты

Копии для релиза (каталог `publish/out-0.3.10.3/`):

| Файл | Размер (байт) | Размер (MiB) | SHA-256 |
|---|---|---|---|
| `ConfigurationManagement.exe` | 84 128 838 | ≈ 80.2 | `577616dde470783803d93d213dedbea39fcda57910b16dca8ee2711b1f06ea66` |
| `ConfigurationManagement-0.3.10.3-win-x64.zip` | 78 315 830 | ≈ 74.7 | `f2193850a77e518696a1056564bddf64a3c0cd4fc296a1f441e17789521b2e9d` |
| `ConfigurationManagement-linux-x64` | 52 606 963 | ≈ 50.2 | `1eeb2f9a68b42eb897ba2dfb000c0de95a44eca0bd5d7c5ae21e9ca1a884621b` |
| `configuration-management_0.3.10.3_amd64.deb` | 45 379 990 | ≈ 43.3 | `885ab2d2d76179e91a617d78d6c1eb852e2e3ae4dccf4eec7f9d6371395ec887` |
| `SHA256SUMS.txt` | — | — | 4 записи (хэш + два пробела + имя, LF), пересчитаны независимо |

Исходные (в `dist/` и `package/linux/deb/out/`):
- `Configuration Management/dist/win-x64/ConfigurationManagement.exe` — 84 128 838 байт;
- `Configuration Management/dist/linux-x64/ConfigurationManagement` — 52 606 963 байт;
- `package/linux/deb/out/configuration-management_0.3.10.3_amd64.deb` — 45 379 990 байт.

## Проверки артефактов

### Версия
- Windows exe: `FileVersion = 0.3.10.3`, `ProductVersion = 0.3.10.3+3519cc0f…`.
- ZIP: единственная запись `ConfigurationManagement.exe`, SHA256 совпадает с exe
  вне архива → содержимое ZIP идентично проверенному exe.
- .deb: `control Version: 0.3.10.3` — совпадает.

### .deb (publish/check_deb_win_0.3.10.3.py)
- ar-members: `debian-binary`, `control.tar.gz`, `data.tar.gz` — корректны.
- `control Version: 0.3.10.3` — совпадает.
- data.tar.gz: `usr/bin/ConfigurationManagement` (mode `0o755`),
  `usr/share/applications/configuration-management.desktop`,
  `usr/share/icons/hicolor/256x256/apps/configuration-management.png`,
  `usr/share/doc/configuration-management/copyright`.
- `DEBIAN/md5sums`: 4 записи, совпадают с md5 содержимого data.tar.gz — **OK**.

### Smoke-запуск
- **Windows**: `ConfigurationManagement.exe --list` — exit 0, в stdout список из 7 баз
  (Accounting, buh_empty, hrmcorp, IRONSKILLS: База знаний, trade11_empty, ut11.6, Троговля 11).
- **Linux**: запуск невозможен — WSL на машине не установлен. Верифицировано структурно:
  ELF-magic (`7F 45 4C 46`), размер, SHA256, сборка с 0 ошибок.

### Тесты
- Полный `dotnet test`: **2140 тестов, 0 ошибок** (выполнены до сборки).

## Пропущено

- **AppImage** (`package/linux/appimage.sh`): **пропущен** — требует Linux/WSL; WSL на этой
  машине не установлен (как и в циклах 0.3.9.331/0.3.9.332/0.3.10.2). exe, linux single-file
  и .deb собраны и проверены.

## Примечания

- git-команды НЕ выполнялись, issues НЕ комментировались, релиз НЕ создавался — по условию задачи.
- Новые файлы: `publish/build_deb_win_0.3.10.3.py`, `publish/check_deb_win_0.3.10.3.py`,
  `publish/build_result_0.3.10.3.md`; артефакты (`dist/*`, `package/linux/deb/out/*`,
  `publish/out-0.3.10.3/*`) в git НЕ добавляются (в .gitignore/.codeassistantignore).
