#if LINUX
using System.Diagnostics;
using System.IO;
using System.Text;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.Services
{
    /// <summary>Результат «регистрации» COM-коннектора (на Linux не используется).</summary>
    public sealed record ComConnectorRegistrationResult(
        bool Success,
        string? PlatformVersion,
        string? BinDirectory,
        bool ProgIdVisible,
        string? VerificationNote,
        IReadOnlyList<ComConnectorRegistrationItem> Items);

    /// <summary>Результат регистрации отдельного COM-модуля (не используется на Linux).</summary>
    public sealed record ComConnectorRegistrationItem(
        string DllPath,
        bool Success,
        string? Error);

    /// <summary>Интерфейс регистрации COM-коннекторов (no-op на Linux).</summary>
    public interface IOneCComConnectorRegistrar
    {
        ComConnectorRegistrationResult Register(string? platformVersion, string architecture);
    }

    /// <summary>
    /// Замена COM-коннектора 1С на Linux. На Linux COM (V83.COMConnector) отсутствует,
    /// поэтому чтение сведений о конфигурации выполняется БЕЗ COM: по эвристике файла
    /// 1Cv8.1CD и/или через пакетный режим конфигуратора (DESIGNER). Подключение через
    /// COM недоступно — <see cref="Connect"/> возвращает null.
    /// </summary>
    public sealed class OneCComConnector : IOneCComConnector
    {
        private readonly IAppLogger _logger;

        public OneCComConnector(IAppLogger logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public string? LastError { get; private set; }

        /// <inheritdoc />
        public string? LastUsedProgId { get; private set; } = null;

        /// <inheritdoc />
        public string? LastUsedPlatformVersion { get; private set; } = null;

        /// <summary>
        /// Доступность COM-коннектора на Linux всегда равна false (COM отсутствует).
        /// Метод добавлен для совместимости с общим кодом.
        /// </summary>
        public static bool IsComConnectorAvailable() => false;

        /// <summary>No-op: на Linux нечего сбрасывать (кэш доступности отсутствует).</summary>
        public static void ResetAvailabilityCache()
        {
        }

        /// <summary>
        /// No-op: на Linux шаблон имени COM-коннектора не используется (COM отсутствует),
        /// кэшировать нечего. Добавлен для совместимости с общим кодом настройки
        /// шаблона (issue #175).
        /// </summary>
        public static void ApplyTemplate(string? template)
        {
        }

        /// <summary>
        /// No-op: на Linux COM-коннектор отсутствует, сбрасывать вердикты о его недоступности
        /// нечего. Добавлен для совместимости с общим кодом кнопки «Определить» (issue #174).
        /// </summary>
        public static void ResetComVerdicts()
        {
        }

        /// <inheritdoc />
        public string BuildConnectString(Infobase infobase) => BuildConnectionString(infobase);

        /// <inheritdoc />
        public OneCComConnection? Connect(Infobase infobase, int timeoutMs = 8000)
        {
            LastError = LocalizationManager.T("Com.LinuxUnavailable") + " " +
                        LocalizationManager.T("Com.LinuxConfigViaFileDesigner");
            return null;
        }

        /// <inheritdoc />
        public OneCConfigInfo? ReadConfigurationInfo(Infobase infobase, int timeoutMs = 8000,
            Action<string>? onStage = null)
        {
            if (infobase is null)
                return null;

            LastError = null;

            // Сообщаем этап в диалог прогресса (issue #174). На Linux COM нет — читаем через
            // конфигуратор, поэтому текст соответствует способу чтения.
            onStage?.Invoke(LocalizationManager.T("Connection.DetectStageRead"));

            // 1. Файловая база: эвристика по 1Cv8.1CD (версия) + попытка через DESIGNER (имя).
            if (infobase.Connection.Type == ConnectionType.File)
            {
                var viaFile = TryReadFromFileBase(infobase.Connection.FilePath);
                if (viaFile is { } fi)
                    return fi;

                var viaDesigner = TryReadViaDesigner(infobase, timeoutMs);
                if (viaDesigner is { } di)
                    return di;

                LastError ??= LocalizationManager.T("Com.LinuxFileAndDesignerFailed");
                return null;
            }

            // 2. Клиент-серверная база: только через DESIGNER (без COM).
            var viaCs = TryReadViaDesigner(infobase, timeoutMs);
            if (viaCs is { } cs)
                return cs;

            LastError ??= LocalizationManager.T("Com.LinuxDesignerFailed");
            return null;
        }

        /// <summary>Чтение через пакетный режим конфигуратора (DESIGNER /DumpCfg).</summary>
        private OneCConfigInfo? TryReadViaDesigner(Infobase infobase, int timeoutMs)
        {
            try
            {
                var arch = ResolveArchForBase(infobase);
                var exe = FindDesigner(infobase.PlatformVersion, arch);
                if (exe is null)
                    return null;

                var tmp = Path.Combine(Path.GetTempPath(), $"1c_cfg_{Guid.NewGuid():N}.cf");
                try
                {
                    var connArg = OneCLauncher.BuildConnectionArgument(infobase);
                    var authArg = OneCLauncher.BuildAuthArgument(infobase);
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
                    };
                    psi.ArgumentList.Add("DESIGNER");
                    psi.ArgumentList.Add(connArg);
                    if (!string.IsNullOrWhiteSpace(authArg))
                        psi.ArgumentList.Add(authArg.Trim());
                    psi.ArgumentList.Add("/DumpCfg");
                    psi.ArgumentList.Add($"\"{tmp}\"");
                    psi.ArgumentList.Add("/DisableStartupDialogs");
                    psi.ArgumentList.Add("/DisableStartupMessages");

                    using var p = LinuxProcessEnvironment.Start(psi);
                    if (p is null)
                        return null;

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        LastError = LocalizationManager.T("Com.LinuxDesignerTimeout");
                        return null;
                    }

                    if (p.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                        return null;

                    return new OneCConfigInfo(
                        ReadConfigNameFromDump(tmp) ?? string.Empty,
                        ReadVersionFromDump(tmp) ?? string.Empty);
                }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadConfigNameFromDump(string cfFile)
        {
            // Имя конфигурации в выгруженном .cf обычно недоступно простым парсингом;
            // возвращаем пустую строку (версия читается отдельно).
            return string.Empty;
        }

        private static string? ReadVersionFromDump(string cfFile)
        {
            try
            {
                var bytes = File.ReadAllBytes(cfFile);
                return FindVersionString(bytes);
            }
            catch
            {
                return null;
            }
        }

        private static string? FindDesigner(string platformVersion, string archKey)
        {
            PlatformVersionService.ParseVariant(platformVersion ?? string.Empty, out var version, out _);
            var arch = archKey == "32" ? OneCArchitecture.x86 : OneCArchitecture.x64;

            var bin = PlatformVersionService.ResolveVersionBinDirectory(version, archKey);
            if (bin != null)
            {
                var path = Path.Combine(bin, "1cv8");
                if (File.Exists(path))
                    return path;
            }

            // Поиск по установленным версиям.
            foreach (var (_, binDir) in PlatformVersionService.FindPlatformVersionDirs(archKey))
            {
                var path = Path.Combine(binDir, "1cv8");
                if (File.Exists(path))
                    return path;
            }

            // Симлинк /usr/bin/1cv8.
            return File.Exists("/usr/bin/1cv8") ? "/usr/bin/1cv8" : null;
        }

        private static string ResolveArchForBase(Infobase infobase)
        {
            var mode = (infobase.Architecture ?? string.Empty).Trim().ToLowerInvariant();
            PlatformVersionService.ParseVariant(infobase.PlatformVersion ?? string.Empty, out _, out var versionArch);
            if (versionArch == "32" || versionArch == "64")
                return versionArch;
            return mode is "32" or "x86" ? "32" : "64";
        }

        /// <summary>Эвристика: читает версию конфигурации из 1Cv8.1CD (версия N.N.N.N).</summary>
        private OneCConfigInfo? TryReadFromFileBase(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            string? cdPath = null;
            if (File.Exists(filePath) && filePath.EndsWith(".1CD", StringComparison.OrdinalIgnoreCase))
                cdPath = filePath;
            else if (Directory.Exists(filePath))
            {
                cdPath = Path.Combine(filePath, "1Cv8.1CD");
                if (!File.Exists(cdPath))
                    cdPath = Directory.EnumerateFiles(filePath, "1Cv8.1CD", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }

            if (string.IsNullOrEmpty(cdPath) || !File.Exists(cdPath))
            {
                LastError = LocalizationManager.T("Com.LinuxFileBaseNotFound");
                return null;
            }

            const int maxBytes = 4 * 1024 * 1024;
            byte[] data;
            try
            {
                using var fs = new FileStream(cdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var len = (int)Math.Min(fs.Length, maxBytes);
                data = new byte[len];
                _ = fs.Read(data, 0, len);
            }
            catch
            {
                return null;
            }

            var version = FindVersionString(data);
            if (string.IsNullOrEmpty(version))
            {
                LastError = LocalizationManager.T("Com.LinuxExtractVersionFailed");
                return null;
            }

            return new OneCConfigInfo(string.Empty, version);
        }

        private static string? FindVersionString(byte[] data)
        {
            var candidates = new List<string>();
            var sb = new StringBuilder();
            for (var i = 0; i < data.Length; i++)
            {
                var b = data[i];
                if ((b >= '0' && b <= '9') || b == '.')
                {
                    sb.Append((char)b);
                }
                else
                {
                    FlushCandidate(sb, candidates);
                    sb.Clear();
                }
            }
            FlushCandidate(sb, candidates);

            // Берём наиболее «похожую» на версию (максимум числовых частей).
            string? best = null;
            var bestParts = -1;
            foreach (var c in candidates)
            {
                var parts = c.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts.Length > bestParts)
                {
                    best = c;
                    bestParts = parts.Length;
                }
            }
            return best;
        }

        private static void FlushCandidate(StringBuilder sb, List<string> candidates)
        {
            if (sb.Length == 0)
                return;
            var s = sb.ToString();
            if (s.StartsWith(".") || s.EndsWith("."))
                return;
            candidates.Add(s);
        }

        private static string BuildConnectionString(Infobase infobase)
        {
            var c = infobase.Connection;
            if (c is null)
                return string.Empty;
            return c.Type switch
            {
                ConnectionType.File => $"File=\"{EscapeConnectValue(c.FilePath)}\"",
                ConnectionType.WebServer => $"WS=\"{EscapeConnectValue(c.WebUrl)}\"",
                _ => $"Srvr=\"{EscapeConnectValue(c.GetServerWithPort())}\";Ref=\"{EscapeConnectValue(c.DatabaseName)}\""
            };
        }

        /// <summary>Экранирует значение строки подключения 1С: кавычка внутри удваивается.</summary>
        private static string EscapeConnectValue(string value) => value.Replace("\"", "\"\"");
    }

    /// <summary>Регистрация COM-коннекторов на Linux — no-op (COM отсутствует).</summary>
    public sealed class OneCComConnectorRegistrar : IOneCComConnectorRegistrar
    {
        private readonly IAppLogger _logger;

        public OneCComConnectorRegistrar(IAppLogger logger)
        {
            _logger = logger;
        }

        public ComConnectorRegistrationResult Register(string? platformVersion, string architecture)
        {
            _logger.Warn("Регистрация COM-коннектора запрошена на Linux: COM недоступен, операция не требуется.");
            return new ComConnectorRegistrationResult(
                false,
                platformVersion,
                null,
                false,
                LocalizationManager.T("ComReg.LinuxUnavailableNote"),
                new List<ComConnectorRegistrationItem>());
        }
    }
}
#endif