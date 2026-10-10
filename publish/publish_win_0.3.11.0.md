# Публикация Windows single-file EXE для релиза 0.3.11.0 (issue #358)

В релизе 0.3.10.3 Windows-сборка была выложена только как ZIP-архив
(`…win-x64.zip`), а загрузчик автообновления (`GitHubReleaseService`, шаблон
`releases/download/{tag}/ConfigurationManagement.exe` и Atom-фолбэк) ожидает
ассет **ConfigurationManagement.exe**. Результат: автообновление скачивало ZIP
и «устанавливало» его на место exe — «не запускается» (issue #358).

С 0.3.11.0 правило публикации: **обязательный ассет `ConfigurationManagement.exe`**
(bare exe, single-file self-contained), ZIP — только дополнительным ассетом.

## Процедура

1. **Сборка** (Windows, Release; свойства PublishSingleFile/SelfContained уже в csproj):

   ```powershell
   dotnet publish "Configuration Management/Configuration Management.csproj" -c Release -r win-x64
   ```

   Артефакт: `Configuration Management/bin/Release/net10.0-windows/win-x64/publish/ConfigurationManagement.exe`.
   Скопировать в `dist/win-x64/ConfigurationManagement.exe`.

2. **Проверка запуска** на чистой машине (или хотя бы локально): запустить exe,
   убедиться, что версия в заголовке — 0.3.11.0 и приложение открывает базу.

3. **Контрольная сумма**:

   ```powershell
   Get-FileHash dist/win-x64/ConfigurationManagement.exe -Algorithm SHA256
   ```

4. **ZIP (опционально, доп. ассет)**:

   ```powershell
   Compress-Archive -Path dist/win-x64/ConfigurationManagement.exe `
                    -DestinationPath dist/win-x64/ConfigurationManagement-0.3.11.0-win-x64.zip
   ```

5. **Выкладка в релиз** (тег релиза, например `v0.3.11.0`):

   ```powershell
   gh release upload v0.3.11.0 dist/win-x64/ConfigurationManagement.exe --clobber
   gh release upload v0.3.11.0 dist/win-x64/ConfigurationManagement-0.3.11.0-win-x64.zip --clobber
   ```

   Имя exe должно быть ровно `ConfigurationManagement.exe` — по нему
   `GitHubReleaseService.FindAsset` делает точный (первый) проход выбора ассета.

6. **SHA256SUMS.txt** — доп. ассет со всеми контрольными суммами релиза.

## Проверки после публикации

- Автообновление с 0.3.10.3 (и старше) на 0.3.11.0: скачивается
  `ConfigurationManagement.exe`, применяется через PowerShell-помощник,
  приложение перезапускается.
- Защита загрузчика (issue #358, `Services/UpdatePayload.cs`): если сервер
  всё же отдал ZIP — exe извлекается из архива и ставится; HTML — файл
  удаляется, показывается «не удалось скачать обновление». Журнал решения —
  `%TEMP%\ConfigurationManagement\update\update-payload.log`.

## Linux

.deb собирается скриптом `publish/build_deb_win_0.3.11.0.py` (копия 0.3.10.3,
версия читается из csproj), AppImage — `package/linux/appimage.sh`.
