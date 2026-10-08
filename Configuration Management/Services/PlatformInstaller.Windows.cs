#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Установка дистрибутива технологической платформы 1С:Предприятие на Windows:
/// распаковка zip через <see cref="IArchiveService.ExtractArchive"/>, поиск
/// <c>setup.exe</c>, сборка аргументов тихой установки (Inno Setup-совместимые
/// <c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART</c>, опционально <c>/DIR=</c>),
/// проверка подписи Authenticode, запуск с повышением прав (<c>Verb="runas"</c>)
/// и ожидание появления новой версии в пересканировании
/// <see cref="PlatformVersionService.FindInstalledVersionInfos"/>.
/// Файл собирается только на Windows (<c>#if WINDOWS</c>); Linux-реализация —
/// <c>PlatformInstaller.Linux.cs</c>.
/// </summary>
public static class PlatformInstaller
{
    /// <summary>Аргументы тихой установки по умолчанию (Inno Setup): без окон,
    /// без диалоговых сообщений и без перезагрузки.</summary>
    public const string DefaultSilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    /// <summary>Ключ локализации: операция отменена пользователем.</summary>
    public const string ErrorCancelled = "PlatformUpdate.Error.Cancelled";

    /// <summary>Ключ локализации: не удалось распаковать дистрибутив.</summary>
    public const string ErrorExtractFailed = "PlatformUpdate.Error.ExtractFailed";

    /// <summary>Ключ локализации: скачанный файл не является zip-архивом (issue #334:
    /// вместо дистрибутива могла прийти HTML-страница входа портала/ошибки).</summary>
    public const string ErrorNotZipFile = "PlatformUpdate.Error.NotZipFile";

    /// <summary>Ключ локализации: в дистрибутиве не найден setup.exe.</summary>
    public const string ErrorSetupNotFound = "PlatformUpdate.Error.SetupNotFound";

    /// <summary>Ключ локализации: установка не завершилась за отведённое время.</summary>
    public const string ErrorTimedOut = "PlatformUpdate.Error.TimedOut";

    /// <summary>Ключ локализации: установщик завершился, но версия не появилась при пересканировании.</summary>
    public const string ErrorVersionNotDetected = "PlatformUpdate.Error.VersionNotDetected";

    /// <summary>Ключ локализации: не удалось удалить каталог старой версии платформы.</summary>
    public const string ErrorDeleteFailed = "PlatformUpdate.Error.DeleteFailed";

    /// <summary>Таймаут удаления каталога версии по умолчанию (каталоги могут быть большими).</summary>
    internal static readonly TimeSpan DeleteTimeoutDefault = TimeSpan.FromMinutes(5);

    /// <summary>Таймаут установки по умолчанию (крупные дистрибутивы, «долгие» машины).</summary>
    internal static readonly TimeSpan SetupTimeoutDefault = TimeSpan.FromMinutes(15);

    /// <summary>Максимальное время ожидания появления новой версии после завершения установщика.</summary>
    internal static readonly TimeSpan PollTimeoutDefault = TimeSpan.FromSeconds(120);

    /// <summary>Шаг опроса пересканирования установленных версий.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Собирает аргументы тихой установки. Чистая функция: по умолчанию возвращает
    /// <see cref="DefaultSilentArgs"/>; при заданном каталоге добавляет
    /// <c>/DIR="<каталог>"</c>. Внутренние кавычки пути заменяются на апостроф
    /// (кавычки в путях Windows недопустимы и ломают синтаксис <c>/DIR="..."</c>),
    /// символ «&» (разделитель команд cmd) экранируется как «^&».
    /// </summary>
    /// <param name="installDirectory">Каталог установки; null — каталог по умолчанию (1С).</param>
    public static string BuildSilentArguments(string? installDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
            return DefaultSilentArgs;

        var escaped = installDirectory.Trim()
            .Replace("\"", "'")
            .Replace("&", "^&");
        return $"{DefaultSilentArgs} /DIR=\"{escaped}\"";
    }

    /// <summary>
    /// Проверяет, запущен ли текущий процесс с правами администратора
    /// (WindowsPrincipal; паттерн <c>UpdateService.IsCurrentProcessElevated</c>).
    /// </summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Проверяет подпись Authenticode исполняемого файла через
    /// <see cref="X509Certificate2.CreateFromSignedFile"/>. Не бросает исключений:
    /// <c>false</c> — подпись отсутствует, не читается или файл недоступен.
    /// </summary>
    public static bool HasValidSignature(string exePath)
    {
        try
        {
            // Создание X509Certificate2.CreateFromSignedFile помечено как obsolete в .NET 9+
            // (SYSLIB0057: «Use X509CertificateLoader instead»), однако у X509CertificateLoader
            // нет аналога чтения подписи Authenticode из PE-файла — подавляем предупреждение.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate2.CreateFromSignedFile(exePath);
#pragma warning restore SYSLIB0057
            return true;
        }
        catch
        {
            // CryptographicException — нет подписи; IOException — файл недоступен;
            // ArgumentNullException — пустой путь; прочее — та же семантика «нет подписи».
            return false;
        }
    }

    /// <summary>
    /// Ищет <c>setup.exe</c> в корне распакованного дистрибутива и его подкаталогах
    /// (глубина вложения до 3, регистронезависимо; возвращается первый найденный).
    /// Структура архива дистрибутива меняется между версиями платформы, поэтому поиск
    /// выполняется и в корне, и во вложенных каталогах.
    /// </summary>
    /// <returns>Полный путь к <c>setup.exe</c> либо null, если он не найден.</returns>
    public static string? FindSetupExecutable(string extractedDir)
    {
        if (string.IsNullOrWhiteSpace(extractedDir) || !Directory.Exists(extractedDir))
            return null;

        return FindSetupCore(extractedDir, depth: 0);
    }

    private static string? FindSetupCore(string directory, int depth)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(Path.GetFileName(file), "setup.exe", StringComparison.OrdinalIgnoreCase))
                return file;
        }

        if (depth >= 3)
            return null;

        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var found = FindSetupCore(sub, depth + 1);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Запускает <c>setup.exe</c> с повышением прав (<c>Verb="runas"</c>, UAC),
    /// ожидает завершения до <paramref name="timeout"/> с учётом токена отмены.
    /// На границе повышения прав <see cref="Process.Start(ProcessStartInfo)"/> может
    /// вернуть null (или выбросить исключение при отказе пользователя от UAC) — тогда
    /// возвращается результат с <see cref="InstallerRunResult.Started"/> == false и кодом 0:
    /// успех в этом случае определяется пересканированием установленных версий.
    /// По истечении таймаута процесс принудительно завершается (<c>Kill</c>) и
    /// устанавливается <see cref="InstallerRunResult.TimedOut"/>. При отмене процесса
    /// (ct) процесс также завершается и бросается <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<InstallerRunResult> RunSetupAsync(
        string setupExe, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        return await RunSetupCoreAsync(setupExe, arguments, timeout, ct, StartSetupProcess).ConfigureAwait(false);
    }

    /// <summary>Запускает установщик через ShellExecute с глаголом runas; null — запуск
    /// не состоялся (отказ UAC/граница прав/ошибка).</summary>
    private static IInstallerProcess? StartSetupProcess(ProcessStartInfo psi)
    {
        try
        {
            var process = Process.Start(psi);
            return process is null ? null : new SetupProcess(process);
        }
        catch
        {
            // Отказ пользователя от запроса повышения прав (Win32Exception) и прочие
            // ошибки запуска — успех определится пересканированием установленных версий.
            return null;
        }
    }

    /// <summary>
    /// Ядро <see cref="RunSetupAsync"/> с инжектируемым запускателем (для тестов):
    /// собирает ProcessStartInfo { FileName, Arguments, UseShellExecute=true, Verb="runas" }
    /// и ожидает завершения процесса с polling по таймауту и токену отмены.
    /// </summary>
    internal static async Task<InstallerRunResult> RunSetupCoreAsync(
        string setupExe, string arguments, TimeSpan timeout, CancellationToken ct,
        Func<ProcessStartInfo, IInstallerProcess?> starter)
    {
        var psi = new ProcessStartInfo
        {
            FileName = setupExe,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        };

        IInstallerProcess? process;
        try
        {
            process = starter(psi);
        }
        catch
        {
            return new InstallerRunResult(Started: false, ExitCode: 0, TimedOut: false);
        }

        if (process is null)
            return new InstallerRunResult(Started: false, ExitCode: 0, TimedOut: false);

        using (process)
        {
            var deadline = DateTime.UtcNow + timeout;
            try
            {
                while (!process.HasExited)
                {
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow >= deadline)
                    {
                        process.Kill();
                        return new InstallerRunResult(Started: true, ExitCode: process.ExitCode, TimedOut: true);
                    }

                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw;
            }

            return new InstallerRunResult(Started: true, ExitCode: process.ExitCode, TimedOut: false);
        }
    }

    /// <summary>
    /// Определяет, установлена ли версия <paramref name="version"/> среди строк списка
    /// установленных версий (Display). Чистая функция: каждый Display приводится к
    /// чистому номеру через <see cref="PlatformVersionService.ParseVariant"/> (суффикс
    /// разрядности «(64)» отбрасывается), совпадение — численное
    /// (<see cref="OneCPlatformCatalogParser.CompareVersions"/> == 0).
    /// </summary>
    public static bool DetectNewVersionInstalled(string version, IReadOnlyList<string> installedDisplays)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        foreach (var display in installedDisplays ?? Array.Empty<string>())
        {
            PlatformVersionService.ParseVariant(display, out var cleanDisplay, out _);
            if (string.IsNullOrWhiteSpace(cleanDisplay))
                continue;

            if (OneCPlatformCatalogParser.CompareVersions(version.Trim(), cleanDisplay.Trim()) == 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Полный сценарий установки платформы на Windows из zip-дистрибутива:
    /// распаковка во временный каталог, поиск <c>setup.exe</c>, предупреждение о
    /// подписи (не блокирует), сборка аргументов тихой установки, запуск с UAC и
    /// ожидание появления новой версии при пересканировании (poll до
    /// <see cref="PollTimeoutDefault"/>, шаг <see cref="PollInterval"/>).
    /// Временный каталог удаляется в любом случае (<c>finally</c>).
    /// </summary>
    /// <param name="zipPath">Путь к zip-файлу дистрибутива.</param>
    /// <param name="version">Устанавливаемая версия (например «8.3.27.2214»).</param>
    /// <param name="installDirectory">Каталог установки; null — по умолчанию (1С).</param>
    /// <param name="log">Журнал этапов (сообщения для UI); может быть null.</param>
    /// <param name="ct">Токен отмены операции.</param>
    public static async Task<PlatformInstallResult> InstallFromZipAsync(
        string zipPath, string version, string? installDirectory,
        IProgress<string>? log, CancellationToken ct)
    {
        var archive = AppServices.GetRequiredService<IArchiveService>();
        return await InstallFromZipCoreAsync(zipPath, version, installDirectory, log, ct,
                archive.ExtractArchive,
                (exe, args, timeout, token) => RunSetupAsync(exe, args, timeout, token),
                ScanInstalledDisplays)
            .ConfigureAwait(false);
    }

    /// <summary>Список строк Display установленных версий платформы.</summary>
    private static IReadOnlyList<string> ScanInstalledDisplays()
        => PlatformVersionService.FindInstalledVersionInfos()
            .Select(v => v.Display)
            .ToList();

    /// <summary>
    /// Пересканирует установленные версии платформы. Отдельного кэша нет —
    /// список всегда читается из файловой системы через
    /// <see cref="PlatformVersionService.FindInstalledVersionInfos"/>; метод
    /// служит явной точкой вызова из UI («Проверить снова») после установки
    /// или удаления версий (зеркало Linux-реализации).
    /// </summary>
    public static void RefreshInstalledCache()
    {
        PlatformVersionService.FindInstalledVersionInfos();
    }

    /// <summary>
    /// Удаляет каталог установленной версии платформы на Windows (этап 0.3.9.215):
    /// выполняется <c>Remove-Item -LiteralPath '<путь>' -Recurse -Force</c> через
    /// PowerShell с повышением прав (<c>Verb="runas"</c>, UAC — паттерн
    /// <c>UpdateService.LaunchUpdater</c>), ожидается завершение до
    /// <see cref="DeleteTimeoutDefault"/>; при успехе вызывается
    /// <see cref="RefreshInstalledCache"/>. Результат: <c>(Success: true, null)</c> —
    /// каталог удалён; <c>(false, ErrorCancelled)</c> — отмена;
    /// <c>(false, ErrorDeleteFailed)</c> — отказ UAC/ошибка запуска/ненулевой код.
    /// Записи реестра Uninstall и ярлыки не трогаются (вне цикла, см. план).
    /// </summary>
    /// <param name="version">Версия с путём каталога (<see cref="PlatformVersionInfo.Path"/>).</param>
    /// <param name="log">Журнал этапов (сообщения для UI); может быть null.</param>
    /// <param name="ct">Токен отмены операции.</param>
    public static async Task<(bool Success, string? ErrorKey)> DeleteVersionDirectoryAsync(
        PlatformVersionInfo version, IProgress<string>? log, CancellationToken ct)
    {
        var result = await DeleteVersionDirectoryCoreAsync(
                version, log, ct, StartDeleteProcess, RefreshInstalledCache)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Ядро <see cref="DeleteVersionDirectoryAsync"/> с инжектируемым запускателем и
    /// обновлением кэша (для тестов): собирает <see cref="ProcessStartInfo"/>
    /// { FileName = powershell, Arguments = Remove-Item…, UseShellExecute = true,
    /// Verb = "runas", WindowStyle = Hidden } и ожидает завершения процесса до
    /// <see cref="DeleteTimeoutDefault"/>. null от запускателя (отказ UAC/ошибка),
    /// таймаут или ненулевой код возврата — <c>(false, ErrorDeleteFailed)</c>;
    /// отмена — <c>(false, ErrorCancelled)</c>.
    /// </summary>
    internal static async Task<(bool Success, string? ErrorKey)> DeleteVersionDirectoryCoreAsync(
        PlatformVersionInfo version, IProgress<string>? log, CancellationToken ct,
        Func<ProcessStartInfo, IInstallerProcess?> startProcess, Action refreshCache)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var dir = (version?.Path ?? string.Empty).Trim();
            if (dir.Length == 0)
                return (false, ErrorDeleteFailed);

            log?.Report($"Удаление каталога {dir}...");
            var psi = new ProcessStartInfo
            {
                FileName = ResolvePowerShellPath(),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                Arguments = BuildRemoveItemArguments(dir),
            };

            IInstallerProcess? process;
            try
            {
                process = startProcess(psi);
            }
            catch
            {
                // Исключение запускателя (отказ UAC/граница прав) — тот же сбой запуска.
                process = null;
            }

            if (process is null)
            {
                log?.Report("Не удалось запустить удаление (отказ UAC или ошибка запуска).");
                return (false, ErrorDeleteFailed);
            }

            using (process)
            {
                var deadline = DateTime.UtcNow + DeleteTimeoutDefault;
                while (!process.HasExited)
                {
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow >= deadline)
                    {
                        process.Kill();
                        log?.Report("Удаление не завершилось за отведённое время.");
                        return (false, ErrorDeleteFailed);
                    }

                    await Task.Delay(200, ct).ConfigureAwait(false);
                }

                if (process.ExitCode != 0)
                {
                    log?.Report($"Remove-Item завершился с кодом {process.ExitCode}.");
                    return (false, ErrorDeleteFailed);
                }
            }

            refreshCache();
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, ErrorCancelled);
        }
    }

    /// <summary>Аргументы powershell.exe для удаления каталога:
    /// <c>-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Remove-Item
    /// -LiteralPath '<путь>' -Recurse -Force"</c>. Апострофы пути экранируются
    /// удвоением (правило одинарных кавычек PowerShell).</summary>
    private static string BuildRemoveItemArguments(string directoryPath)
    {
        var escaped = directoryPath.Replace("'", "''");
        var command = $"Remove-Item -LiteralPath '{escaped}' -Recurse -Force";
        return $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"";
    }

    /// <summary>Запускает powershell.exe через ShellExecute с runas; null — запуск
    /// не состоялся (отказ UAC/граница прав/ошибка).</summary>
    private static IInstallerProcess? StartDeleteProcess(ProcessStartInfo psi)
    {
        try
        {
            var process = Process.Start(psi);
            return process is null ? null : new SetupProcess(process);
        }
        catch
        {
            // Отказ пользователя от запроса повышения прав (Win32Exception) и прочие
            // ошибки запуска — результат несёт ErrorDeleteFailed.
            return null;
        }
    }

    /// <summary>
    /// Путь к PowerShell: предпочитается 64-битная версия — из 32-битного процесса
    /// %SystemRoot%\System32 перенаправляется на SysWOW64, поэтому сначала пробуется
    /// Sysnative (паттерн <c>UpdateService.ResolvePowerShellPath</c>). Запасной
    /// вариант — powershell.exe из PATH.
    /// </summary>
    private static string ResolvePowerShellPath()
    {
        var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(sysRoot))
            return "powershell.exe";

        var candidates = new[]
        {
            Path.Combine(sysRoot, "Sysnative", "WindowsPowerShell", "v1.0", "powershell.exe"),
            Path.Combine(sysRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "powershell.exe";
    }

    /// <summary>
    /// Ядро <see cref="InstallFromZipAsync"/> с инжектируемыми зависимостями (для тестов):
    /// распаковка, поиск setup.exe, запуск установщика и пересканирование версий.
    /// </summary>
    internal static async Task<PlatformInstallResult> InstallFromZipCoreAsync(
        string zipPath, string version, string? installDirectory,
        IProgress<string>? log, CancellationToken ct,
        Func<string, string, bool> extractArchive,
        Func<string, string, TimeSpan, CancellationToken, Task<InstallerRunResult>> runSetup,
        Func<IReadOnlyList<string>> scanInstalled)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "cm_platforminst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            // Ранняя проверка отмены — внутри try, чтобы вернуть результат Cancelled,
            // а не пробросить исключение наружу.
            ct.ThrowIfCancellationRequested();

            // issue #334: «setup.exe не найден в архиве» при скачивании HTML вместо zip.
            // Проверяем magic-байты ДО распаковки и даём понятное сообщение.
            if (!IsZipArchive(zipPath))
            {
                var sizeText = File.Exists(zipPath) ? new FileInfo(zipPath).Length.ToString() : "?";
                log?.Report(
                    $"Файл не является zip-архивом (размер {sizeText} байт): возможно, " +
                    "скачана страница входа портала или ошибки. Повторите проверку каталога.");
                return new PlatformInstallResult(Success: false, ErrorKey: ErrorNotZipFile, ExitCode: -1);
            }

            log?.Report($"Распаковка дистрибутива в {tmpDir}...");
            if (!extractArchive(zipPath, tmpDir))
            {
                log?.Report("Не удалось распаковать дистрибутив.");
                return new PlatformInstallResult(Success: false, ErrorKey: ErrorExtractFailed, ExitCode: -1);
            }

            var setupExe = FindSetupExecutable(tmpDir);
            if (setupExe is null)
            {
                log?.Report("setup.exe не найден в дистрибутиве.");
                return new PlatformInstallResult(Success: false, ErrorKey: ErrorSetupNotFound, ExitCode: -1);
            }

            if (!HasValidSignature(setupExe))
                log?.Report("Предупреждение: подпись Authenticode не проверена или отсутствует.");

            var arguments = BuildSilentArguments(installDirectory);
            log?.Report($"Запуск установки: {setupExe} {arguments}");

            var runResult = await runSetup(setupExe, arguments, SetupTimeoutDefault, ct).ConfigureAwait(false);
            if (runResult.TimedOut)
            {
                log?.Report("Установка не завершилась за отведённое время.");
                return new PlatformInstallResult(Success: false, ErrorKey: ErrorTimedOut, ExitCode: runResult.ExitCode);
            }

            log?.Report($"Установщик завершился с кодом {runResult.ExitCode}; ждём появления версии {version}...");

            // Итоговый критерий успеха — фактическое появление версии в пересканировании
            // (надёжнее кода возврата, особенно при UAC-границе: Process.Start мог вернуть null).
            var deadline = DateTime.UtcNow + PollTimeoutDefault;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (DetectNewVersionInstalled(version, scanInstalled()))
                    return new PlatformInstallResult(Success: true, ErrorKey: null, ExitCode: runResult.ExitCode);

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }

            log?.Report($"Версия {version} не обнаружена после установки.");
            return new PlatformInstallResult(Success: false, ErrorKey: ErrorVersionNotDetected, ExitCode: runResult.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return new PlatformInstallResult(Success: false, ErrorKey: ErrorCancelled, ExitCode: -1);
        }
        finally
        {
            TryDeleteDirectory(tmpDir);
        }
    }

    /// <summary>True — файл является zip-архивом (magic-байты «PK»). Ошибки чтения — false.</summary>
    internal static bool IsZipArchive(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[2];
            if (stream.Read(header) != header.Length)
                return false;
            return header[0] == (byte)'P' && header[1] == (byte)'K';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Рекурсивно удаляет каталог; ошибки игнорируются (лучше осиротевший
    /// каталог, чем падение).</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Занят процессом/антивирусом — попытка при следующем запуске очистится сама.
        }
    }

    /// <summary>Результат запуска установщика.</summary>
    /// <param name="Started">Процесс удалось запустить (true); false — граница UAC/отказ, успех определится пересканированием.</param>
    /// <param name="ExitCode">Код возврата установщика.</param>
    /// <param name="TimedOut">Установка не завершилась за отведённое время (процесс завершён принудительно).</param>
    public sealed record InstallerRunResult(bool Started, int ExitCode, bool TimedOut);

    /// <summary>Результат полного сценария установки из zip-дистрибутива.</summary>
    /// <param name="Success">Версия появилась в пересканировании после установки.</param>
    /// <param name="ErrorKey">Ключ локализации ошибки; null — успех.</param>
    /// <param name="ExitCode">Код возврата установщика (или -1, если установщик не запускался).</param>
    public sealed record PlatformInstallResult(bool Success, string? ErrorKey, int ExitCode);

    /// <summary>Абстракция процесса установщика (обёртка над <see cref="Process"/> для тестов).</summary>
    internal interface IInstallerProcess : IDisposable
    {
        bool HasExited { get; }

        int ExitCode { get; }

        void Kill();
    }

    /// <summary>Адаптер над реальным <see cref="Process"/>.</summary>
    private sealed class SetupProcess : IInstallerProcess
    {
        private readonly Process _process;

        public SetupProcess(Process process) => _process = process;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public void Kill()
        {
            try { _process.Kill(); } catch { /* уже завершился */ }
        }

        public void Dispose() => _process.Dispose();
    }
}
#endif