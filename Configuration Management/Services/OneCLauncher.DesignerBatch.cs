using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Сервис запуска платформы 1С:Предприятие.
/// </summary>
public static partial class OneCLauncher
{
    /// <summary>Операции DESIGNER без интерактивного UI (выгрузка, тест).</summary>
    public enum DesignerBatchOperation
    {
        DumpIB,
        DumpCfg,
        TestAndRepair,
        /// <summary>Восстановление данных ИБ из выгрузки .dt (/RestoreIB"path").</summary>
        RestoreIB,
        /// <summary>
        /// Загрузка конфигурации из файла .cf и обновление конфигурации БД
        /// (/LoadCfg"path.cf" /UpdateDBCfg). Используется заданиями по расписанию (issue #286).
        /// </summary>
        LoadCfg,
        /// <summary>Установка блокировки сеансов ИБ (/LockIB"строка сеансов").</summary>
        LockIB,
        /// <summary>Снятие блокировки сеансов ИБ (/LockIB"").</summary>
        UnlockIB
    }

    /// <summary>
    /// Информация о запущенной пакетной операции DESIGNER (выгрузка .dt/.cf или тест),
    /// передаваемая через события <see cref="OneCLauncher.DesignerBatchStarted"/> /
    /// <see cref="OneCLauncher.DesignerBatchCompleted"/>.
    /// </summary>
    public sealed class DesignerBatchInfo
    {
        public DesignerBatchInfo(DesignerBatchOperation operation, string infobaseName, string? outputPath,
            string? logPath = null, string? commandLine = null)
        {
            Operation = operation;
            InfobaseName = infobaseName;
            OutputPath = outputPath;
            LogPath = logPath;
            CommandLine = commandLine;
        }

        /// <summary>Тип выполняемой операции.</summary>
        public DesignerBatchOperation Operation { get; }

        /// <summary>Имя информационной базы, для которой выполняется операция.</summary>
        public string InfobaseName { get; }

        /// <summary>Путь к файлу выгрузки (.dt/.cf); может быть пустым для тестирования.</summary>
        public string? OutputPath { get; }

        /// <summary>Путь к временному файлу лога операции (/Out), заполняется по завершении.</summary>
        public string? LogPath { get; }

        /// <summary>Командная строка запуска 1cv8 (для диагностики).</summary>
        public string? CommandLine { get; }

        /// <summary>Код возврата процесса 1cv8 (заполняется по завершении).</summary>
        public int ExitCode { get; set; } = -1;

        /// <summary>Успешно ли завершилась операция (код 0 и файл создан).</summary>
        public bool Success { get; set; }

        /// <summary>Сообщение об ошибке с текстом лога 1С (при неуспехе).</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Человекочитаемое название операции для индикатора и подсказки.</summary>
        public string OperationLabel => Operation switch
        {
            DesignerBatchOperation.DumpIB => LocalizationManager.T("Launcher.OperationDumpIB"),
            DesignerBatchOperation.DumpCfg => LocalizationManager.T("Launcher.OperationDumpCfg"),
            DesignerBatchOperation.TestAndRepair => LocalizationManager.T("Launcher.OperationTestAndRepair"),
            DesignerBatchOperation.RestoreIB => LocalizationManager.T("Launcher.OperationRestoreIB"),
            DesignerBatchOperation.LoadCfg => LocalizationManager.T("Launcher.OperationLoadCfg"),
            DesignerBatchOperation.LockIB => LocalizationManager.T("Launcher.OperationLockIB"),
            DesignerBatchOperation.UnlockIB => LocalizationManager.T("Launcher.OperationUnlockIB"),
            _ => LocalizationManager.T("Launcher.OperationGeneric")
        };
    }

    /// <summary>
    /// Запускает конфигуратор в пакетном режиме: выгрузка .dt / .cf или тестирование ИБ.
    /// Формат аргументов как у командной строки 1С (без пробела между ключом и значением в кавычках).
    /// </summary>
    public static bool RunDesignerBatch(Infobase infobase, DesignerBatchOperation operation, string? outputPath = null,
        BackupCredential? credential = null)
    {
        var arch = ResolveArchitecture(infobase.Architecture, infobase.PlatformVersion);
        var exePath = FindExecutable(infobase.PlatformVersion, arch, null, OneCLaunchMode.Configurator);

        // Платформа может быть установлена только в одной разрядности (например,
        // 32-бит в Program Files (x86) при глобальной настройке по умолчанию «64»).
        // Если для выбранной разрядности 1cv8.exe не найден — пробуем противоположную.
        if (string.IsNullOrEmpty(exePath) ||
            exePath.EndsWith("1CEStart.exe", StringComparison.OrdinalIgnoreCase))
        {
            var otherArch = arch == OneCArchitecture.x64 ? OneCArchitecture.x86 : OneCArchitecture.x64;
            var fallback = FindExecutable(infobase.PlatformVersion, otherArch, null, OneCLaunchMode.Configurator);
            if (!string.IsNullOrEmpty(fallback) &&
                !fallback.EndsWith("1CEStart.exe", StringComparison.OrdinalIgnoreCase))
                exePath = fallback;
        }

        if (string.IsNullOrEmpty(exePath) ||
            exePath.EndsWith("1CEStart.exe", StringComparison.OrdinalIgnoreCase))
        {
            GetLogger()?.Warn(LocalizationManager.T("Launcher.ConfiguratorExeNotFound"));
            return false;
        }

        // Проверка блокировки запуска конфигуратора: уже запущен конфигуратор этой базы
        // (в т.ч. открытый вручную вне приложения) или идёт другая выгрузка/операция DESIGNER.
        if (IsDesignerBlocked(infobase, out var blockReason))
        {
            GetLogger()?.Warn(
                string.Format(LocalizationManager.T("Launcher.ConfiguratorBlockedFormat"), blockReason));
            return false;
        }

        if (operation is DesignerBatchOperation.DumpIB or DesignerBatchOperation.DumpCfg)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                return false;
            // Каталог назначения должен существовать
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex)
                {
                    GetLogger()?.Error(
                        string.Format(LocalizationManager.T("Launcher.CreateDirFailedFormat"), dir, ex.Message), ex);
                    return false;
                }
            }
        }
        else if (operation is DesignerBatchOperation.RestoreIB or DesignerBatchOperation.LoadCfg)
        {
            // Восстановление и загрузка конфигурации требуют существующий исходный файл.
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
                return false;
        }

        var connectionArg = BuildConnectionArgument(infobase);
        var authArg = BuildAuthArgument(infobase);
        // Переопределённые учётные данные сценария резервирования: если они заданы явно
        // (UseInfobaseAuth == false), используем их вместо авторизации базы.
        if (credential is { UseInfobaseAuth: false })
            authArg = BuildCredentialsArg(credential.User, credential.Password);

        // Важно: у 1С ключи вида /DumpIB"C:\path\file.dt" (значение сразу в кавычках). Это НЕ строка
        // подключения: кавычку внутри пути экранировать удвоением нельзя, поэтому путь с «"»
        // недопустим (см. IsSafeCliValue) — безопасно выгрузить его невозможно, отказываемся.
        string opArg = operation switch
        {
            DesignerBatchOperation.DumpIB when IsSafeCliValue(outputPath) => $"/DumpIB\"{outputPath}\"",
            DesignerBatchOperation.DumpCfg when IsSafeCliValue(outputPath) => $"/DumpCfg\"{outputPath}\"",
            DesignerBatchOperation.TestAndRepair => "/IBCheckAndRepair -TestOnly",
            DesignerBatchOperation.RestoreIB when IsSafeCliValue(outputPath) => $"/RestoreIB\"{outputPath}\"",
            // Загрузка новой конфигурации из .cf и обновление конфигурации БД.
            DesignerBatchOperation.LoadCfg when IsSafeCliValue(outputPath) => $"/LoadCfg\"{outputPath}\" /UpdateDBCfg",
            // Блокировка сеансов файловой ИБ: /LockIB"строка сеансов". Строка строится
            // в SessionLockOptions.BuildSessionLockString(); выходной файл не создаётся,
            // поэтому в outputPath передаётся именно строка сеансов.
            DesignerBatchOperation.LockIB when IsSafeCliValue(outputPath) => $"/LockIB\"{outputPath}\"",
            // Снятие блокировки: /LockIB с пустой строкой сеансов.
            DesignerBatchOperation.UnlockIB => "/LockIB\"\"",
            _ => ""
        };
        if (string.IsNullOrEmpty(opArg))
            return false;

        // /Out — путь к временному логу, всегда системный GUID-файл, без пользовательских данных,
        // поэтому экранирование не требуется (вектора инъекции нет).
        var outLog = Path.Combine(Path.GetTempPath(), $"1c_batch_{Guid.NewGuid():N}.log");
        var arguments = $"DESIGNER {connectionArg}{authArg} {opArg} /DisableStartupDialogs /DisableStartupMessages /Out\"{outLog}\"";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
            };
            var process = Process.Start(psi);
            var info = new DesignerBatchInfo(operation, infobase.Name, outputPath, outLog, $"{exePath} {arguments}");
            RegisterBatchProcess(infobase, process, info);
            DesignerBatchStarted?.Invoke(null, info);
            return true;
        }
        catch (Exception ex)
        {
            GetLogger()?.Error(
                string.Format(LocalizationManager.T("Launcher.OperationStartFailedFormat"), ex.Message, exePath, arguments), ex);
            return false;
        }
    }

    /// <summary>Токен подключения базы для сопоставления с командной строкой процесса конфигуратора.</summary>
    public static string GetBaseConnectionToken(Infobase infobase)
    {
        var conn = infobase.Connection;
        return conn.Type switch
        {
            ConnectionType.File => (conn.FilePath ?? string.Empty).Trim().TrimEnd('\\'),
            ConnectionType.WebServer => (conn.WebUrl ?? string.Empty).Trim(),
            _ => $"{conn.GetServerWithPort()}\\{conn.DatabaseName}".Trim()
        };
    }

    /// <summary>Регистрирует запущенный процесс пакетной операции и удаляет его по завершении.</summary>
    private static void RegisterBatchProcess(Infobase infobase, Process? process, DesignerBatchInfo info)
    {
        var token = GetBaseConnectionToken(infobase);
        if (process is null || string.IsNullOrWhiteSpace(token))
            return;

        _activeBatchProcesses[token] = process;
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                _activeBatchProcesses.TryRemove(token, out _);
                // Читаем лог, определяем успех операции и формируем сообщение об ошибке.
                try { CompleteDesignerBatch(process, info); }
                catch { /* не должны ронять поток обработчика */ }
                // Оповещаем об окончании операции (индикатор выгрузки в главном окне).
                DesignerBatchCompleted?.Invoke(null, info);
            };
        }
        catch
        {
            /* процесс мог уже завершиться */
        }
    }

    /// <summary>
    /// По завершении процесса 1cv8 читает лог /Out, определяет успех операции
    /// (код возврата 0 и наличие файла выгрузки) и заполняет <paramref name="info"/>.
    /// При неуспехе формирует человекочитаемое сообщение с текстом лога 1С.
    /// </summary>
    private static void CompleteDesignerBatch(Process process, DesignerBatchInfo info)
    {
        try { info.ExitCode = process.HasExited ? process.ExitCode : -1; }
        catch { info.ExitCode = -1; }

        // Читаем лог операции (файл мог ещё дописываться — ждём стабилизации размера).
        var logText = ReadLogFile(info.LogPath);

        // Успех: код возврата 0 и (для выгрузки) создан и не пуст файл назначения.
        bool ok = info.ExitCode == 0;
        if (ok && info.Operation is DesignerBatchOperation.DumpIB or DesignerBatchOperation.DumpCfg)
        {
            ok = !string.IsNullOrWhiteSpace(info.OutputPath) &&
                 File.Exists(info.OutputPath) &&
                 new FileInfo(info.OutputPath).Length > 0;
        }

        info.Success = ok;
        if (ok)
            return;

        var sb = new StringBuilder();
        sb.AppendLine(string.Format(LocalizationManager.T("Launcher.OperationFailedFormat"), info.OperationLabel));
        sb.AppendLine(string.Format(LocalizationManager.T("Launcher.ExitCodeFormat"), info.ExitCode));
        if (!string.IsNullOrWhiteSpace(info.OutputPath))
            sb.AppendLine(string.Format(LocalizationManager.T("Launcher.FileFormat"), info.OutputPath));
        if (!string.IsNullOrWhiteSpace(logText))
        {
            sb.AppendLine();
            sb.AppendLine(LocalizationManager.T("Launcher.MessageHeader1C"));
            sb.Append(TruncateLogTail(logText, 3000));
        }
        if (!string.IsNullOrWhiteSpace(info.CommandLine))
        {
            sb.AppendLine();
            sb.AppendLine(LocalizationManager.T("Launcher.CommandLineHeader"));
            sb.AppendLine(info.CommandLine);
        }
        info.ErrorMessage = sb.ToString();
    }

    /// <summary>Читает содержимое временного лога 1С и удаляет файл.</summary>
    private static string ReadLogFile(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return string.Empty;

        // Ждём, пока файл перестанет расти (1С дописывает лог даже после выхода процесса).
        for (var i = 0; i < 30; i++)
        {
            try
            {
                if (!File.Exists(logPath))
                    break;
                var f = new FileInfo(logPath);
                if (f.Length > 0)
                {
                    var len1 = f.Length;
                    Thread.Sleep(120);
                    var len2 = new FileInfo(logPath).Length;
                    if (len1 == len2)
                        break; // размер стабилен — можно читать
                }
            }
            catch
            {
                break;
            }
            Thread.Sleep(80);
        }

        try
        {
            if (File.Exists(logPath))
                return File.ReadAllText(logPath);
        }
        catch
        {
            /* занят другим процессом — пропускаем */
        }
        finally
        {
            try { File.Delete(logPath); } catch { /* ignore */ }
        }
        return string.Empty;
    }

    /// <summary>Возвращает хвост текста (последние <paramref name="maxChars"/> символов).</summary>
    private static string TruncateLogTail(string text, int maxChars)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length <= maxChars)
            return text;
        return "…" + text.Substring(text.Length - maxChars);
    }

    /// <summary>Удаляет завершившиеся процессы из реестра активных операций.</summary>
    private static void PruneDeadBatchProcesses()
    {
        foreach (var kvp in _activeBatchProcesses)
        {
            if (kvp.Value == null || kvp.Value.HasExited)
                _activeBatchProcesses.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// Проверяет блокировку запуска конфигуратора перед выгрузкой .dt/.cf или тестированием.
    /// Возвращает true, если запуск нужно заблокировать; <paramref name="reason"/> описывает причину.
    /// </summary>
    public static bool IsDesignerBlocked(Infobase infobase, out string? reason)
    {
        reason = null;
        PruneDeadBatchProcesses();

        // 1. Уже идёт другая выгрузка / пакетная операция DESIGNER, запущенная приложением.
        if (_activeBatchProcesses.Count > 0)
        {
            var otherName = _activeBatchProcesses.First().Value?.ProcessName ?? "1cv8.exe";
            reason = string.Format(LocalizationManager.T("Launcher.AnotherOperationRunningFormat"), otherName);
            return true;
        }

        // 2. Конфигуратор этой базы уже запущен (в т.ч. открыт вручную вне приложения).
        var token = GetBaseConnectionToken(infobase);
        if (!string.IsNullOrWhiteSpace(token) && IsConfiguratorRunningForBase(token))
        {
            reason = LocalizationManager.T("Launcher.ConfiguratorForBaseRunning");
            return true;
        }

        return false;
    }

    /// <summary>Ищет запущенный процесс конфигуратора (1cv8.exe) для указанной базы по командной строке.</summary>
    private static bool IsConfiguratorRunningForBase(string baseToken)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process " +
                "WHERE Name='1cv8.exe' OR Name='1cv8x64.exe'");
            foreach (var obj in searcher.Get())
            {
                var cmd = obj["CommandLine"] as string ?? string.Empty;
                if (cmd.Contains("DESIGNER", StringComparison.OrdinalIgnoreCase) &&
                    cmd.Contains(baseToken, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Нет прав на чтение командной строки процессов других пользователей или WMI недоступен.
        }
        return false;
    }
}