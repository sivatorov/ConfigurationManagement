#if LINUX
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.Services
{
    public static partial class OneCLauncher
    {
        // ====================================================================
        // Пакетные операции DESIGNER
        // ====================================================================

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

        /// <summary>Информация о запущенной пакетной операции DESIGNER.</summary>
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

            public DesignerBatchOperation Operation { get; }
            public string InfobaseName { get; }
            public string? OutputPath { get; }
            public string? LogPath { get; }
            public string? CommandLine { get; }
            public int ExitCode { get; set; } = -1;
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }

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

        /// <summary>Запускает конфигуратор в пакетном режиме (выгрузка .dt/.cf или тест).</summary>
        public static bool RunDesignerBatch(Infobase infobase, DesignerBatchOperation operation, string? outputPath = null,
            BackupCredential? credential = null)
        {
            var arch = ResolveArchitecture(infobase.Architecture, infobase.PlatformVersion);
            var exePath = FindExecutable(infobase.PlatformVersion, arch, null, OneCLaunchMode.Configurator);
            if (string.IsNullOrEmpty(exePath))
            {
                var otherArch = arch == OneCArchitecture.x64 ? OneCArchitecture.x86 : OneCArchitecture.x64;
                exePath = FindExecutable(infobase.PlatformVersion, otherArch, null, OneCLaunchMode.Configurator);
            }
            if (string.IsNullOrEmpty(exePath))
                return false;

            if (IsDesignerBlocked(infobase, out _))
                return false;

            if (operation is DesignerBatchOperation.DumpIB or DesignerBatchOperation.DumpCfg)
            {
                if (string.IsNullOrWhiteSpace(outputPath))
                    return false;
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); }
                    catch { return false; }
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

            // Ключи вида /DumpIB"path" — по грамматике ключа, НЕ строки подключения: кавычку внутри
            // пути удвоением не экранируют, поэтому путь с «"» недопустим (см. IsSafeCliValue) —
            // безопасно выгрузить его невозможно, отказываемся.
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

            // /Out — путь к временному логу, всегда системный GUID-файл (без пользовательских
            // данных), поэтому экранирование не требуется (вектора инъекции нет).
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
                var process = LinuxProcessEnvironment.Start(psi);
                var info = new DesignerBatchInfo(operation, infobase.Name, outputPath, outLog, $"{exePath} {arguments}");
                RegisterBatchProcess(infobase, process, info);
                DesignerBatchStarted?.Invoke(null, info);
                return true;
            }
            catch (Exception ex)
            {
                GetLogger()?.Error(string.Format(LocalizationManager.T("Launcher.OperationStartFailedFormat"), ex.Message, exePath, arguments), ex);
                return false;
            }
        }

        /// <summary>Токен подключения базы для сопоставления с командной строкой процесса.</summary>
        public static string GetBaseConnectionToken(Infobase infobase)
        {
            var conn = infobase.Connection;
            return conn.Type switch
            {
                ConnectionType.File => (conn.FilePath ?? string.Empty).Trim().TrimEnd('\\', '/'),
                ConnectionType.WebServer => (conn.WebUrl ?? string.Empty).Trim(),
                _ => $"{conn.GetServerWithPort()}\\{conn.DatabaseName}".Trim()
            };
        }

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
                    try { CompleteDesignerBatch(process, info); }
                    catch { }
                    DesignerBatchCompleted?.Invoke(null, info);
                };
            }
            catch
            {
                // процесс мог уже завершиться
            }
        }

        private static void CompleteDesignerBatch(Process process, DesignerBatchInfo info)
        {
            try { info.ExitCode = process.HasExited ? process.ExitCode : -1; }
            catch { info.ExitCode = -1; }

            var logText = ReadLogFile(info.LogPath);

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
            info.ErrorMessage = sb.ToString();
        }

        private static string ReadLogFile(string? logPath)
        {
            if (string.IsNullOrWhiteSpace(logPath))
                return string.Empty;
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
                            break;
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
                // занят
            }
            finally
            {
                try { File.Delete(logPath); } catch { }
            }
            return string.Empty;
        }

        private static string TruncateLogTail(string text, int maxChars)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length <= maxChars)
                return text;
            return "…" + text.Substring(text.Length - maxChars);
        }

        /// <summary>Проверяет блокировку запуска конфигуратора перед пакетной операцией.</summary>
        public static bool IsDesignerBlocked(Infobase infobase, out string? reason)
        {
            reason = null;
            PruneDeadBatchProcesses();

            if (_activeBatchProcesses.Count > 0)
            {
                var otherName = _activeBatchProcesses.First().Value?.ProcessName ?? "1cv8";
                reason = string.Format(LocalizationManager.T("Launcher.AnotherOperationRunningFormat"), otherName);
                return true;
            }

            var token = GetBaseConnectionToken(infobase);
            if (!string.IsNullOrWhiteSpace(token) && IsConfiguratorRunningForBase(token))
            {
                reason = LocalizationManager.T("Launcher.ConfiguratorForBaseRunning");
                return true;
            }

            return false;
        }

        private static void PruneDeadBatchProcesses()
        {
            foreach (var kvp in _activeBatchProcesses)
            {
                if (kvp.Value == null || kvp.Value.HasExited)
                    _activeBatchProcesses.TryRemove(kvp.Key, out _);
            }
        }

        /// <summary>Ищет запущенный конфигуратор (1cv8) для базы по командной строке из /proc.</summary>
        private static bool IsConfiguratorRunningForBase(string baseToken)
        {
            try
            {
                foreach (var process in LinuxProc.Enumerate1C())
                {
                    var name = process.Name;
                    var cmd = process.CmdLine;
                    var n = name ?? string.Empty;
                    if (!n.StartsWith("1cv8", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var c = cmd ?? string.Empty;
                    if (c.Contains("DESIGNER", StringComparison.OrdinalIgnoreCase) &&
                        c.Contains(baseToken, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // /proc недоступен
            }
            return false;
        }
    }
}
#endif