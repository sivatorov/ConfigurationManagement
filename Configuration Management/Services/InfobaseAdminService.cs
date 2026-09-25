using System.Diagnostics;
using System.IO;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Реализация <see cref="IInfobaseAdminService"/> (Этап 6 дорожной карты StartManager,
/// функция №29 + консоль администрирования серверов).
/// Исполняемые файлы платформы ищутся в каталоге установленной платформы 1С рядом
/// с <c>1cv8</c>/<c>1cv8.exe</c> тем же способом, что и лаунчер: через
/// <see cref="PlatformVersionService.ResolveVersionBinDirectory"/> и
/// <see cref="PlatformVersionService.FindPlatformVersionDirs"/>.
/// </summary>
public sealed class InfobaseAdminService : IInfobaseAdminService
{
    private readonly IAppLogger _logger;

    public InfobaseAdminService(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CheckIntegrity(Infobase infobase)
    {
        if (infobase?.Connection?.Type != ConnectionType.File)
            return false;

        var baseDir = infobase.Connection.FilePath;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            _logger.Warn($"Проверка целостности: не задан путь к файловой базе «{infobase.Name}».");
            return false;
        }

        var binDir = ResolveBinDirectory(infobase);
        if (binDir is null)
        {
            _logger.Warn(
                $"Проверка целостности: не найден каталог платформы 1С для базы «{infobase.Name}».");
            return false;
        }

        var exe = FindInBinDir(binDir, "chdbfl");
        if (exe is null)
        {
            _logger.Warn(
                $"Проверка целостности: не найден исполняемый файл chdbfl в каталоге платформы {binDir}.");
            return false;
        }

        // Проверка целостности выполняется над файлом базы: <путь>\1Cv8.1CD.
        var dbFile = Path.Combine(baseDir, "1Cv8.1CD");
        return Launch(exe, $"\"{dbFile}\"", $"Проверка целостности базы «{infobase.Name}» (chdbfl)");
    }

    /// <inheritdoc />
    public bool OpenServerAdminConsole(Infobase? infobase)
    {
        // Консоль администрирования серверов 1С не связана с конкретной базой (issue #295):
        // запускаем оснастку/rac всегда, какая бы строка ни была выбрана в списке (или вообще
        // без выбора). База — только источник настроек платформы; если её нет или она файловая,
        // используется новейшая установленная платформа нужной разрядности.
        var baseLabel = infobase is null ? string.Empty : $" для базы «{infobase.Name}»";

        var binDir = ResolveBinDirectory(infobase);
        if (binDir is null)
        {
            _logger.Warn(
                $"Консоль администрирования: не найден каталог платформы 1С{baseLabel}.");
            return false;
        }

#if WINDOWS
        // На Windows консоль администрирования серверов 1С — оснастка MMC,
        // поставляемая с платформой рядом с 1cv8.exe (1CV8Servers.msc).
        var snapIn = Path.Combine(binDir, "1CV8Servers.msc");
        if (File.Exists(snapIn))
            return Launch(snapIn, string.Empty,
                $"Консоль администрирования серверов 1С{baseLabel}",
                shellExecute: true);
#endif

        // Общий (кросс-платформенный) вариант — командный клиент администрирования rac
        // (Remote Administration Client), присутствующий в каталоге платформы обеих ОС.
        var rac = FindInBinDir(binDir, "rac");
        if (rac is null)
        {
            _logger.Warn(
                $"Консоль администрирования: не найден rac в каталоге платформы {binDir}.");
            return false;
        }

        return Launch(rac, string.Empty,
            $"Консоль администрирования серверов 1С{baseLabel}");
    }

    /// <summary>
    /// Разрешает каталог <c>bin</c> установленной платформы нужной разрядности:
    /// при выбранной базе — по её настройкам (точная версия, затем новейшая
    /// установленная), без базы — новейшая установленная версия (issue #295).
    /// </summary>
    private static string? ResolveBinDirectory(Infobase? infobase)
    {
        var arch = OneCLauncher.ResolveArchitecture(infobase?.Architecture, infobase?.PlatformVersion);
        var archKey = arch == OneCArchitecture.x64 ? "64" : "32";

        PlatformVersionService.ParseVariant(infobase?.PlatformVersion ?? string.Empty,
            out var cleanVersion, out _);
        if (!string.IsNullOrWhiteSpace(cleanVersion))
        {
            var dir = PlatformVersionService.ResolveVersionBinDirectory(cleanVersion, archKey);
            if (dir is not null && Directory.Exists(dir))
                return dir;
        }

        // Запасной вариант — новейшая из установленных версий нужной разрядности.
        string? bestDir = null;
        string bestVersion = string.Empty;
        foreach (var (version, binDir) in PlatformVersionService.FindPlatformVersionDirs(archKey))
        {
            if (bestDir is null || CompareVersions(version, bestVersion) > 0)
            {
                bestDir = binDir;
                bestVersion = version;
            }
        }
        return bestDir;
    }

    /// <summary>Числовое сравнение версий 1С («8.3.27.1688»). >0 если a новее b.</summary>
    private static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v)
        {
            return (v ?? string.Empty)
                .Split(new[] { '.', ' ', '(' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => int.TryParse(p, out _))
                .Select(int.Parse)
                .ToArray();
        }

        var pa = Parts(a);
        var pb = Parts(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb)
                return va.CompareTo(vb);
        }
        return 0;
    }

    /// <summary>
    /// Ищет исполняемый файл платформы в каталоге bin с учётом платформенного
    /// расширения: <c>.exe</c> на Windows, без расширения на Linux.
    /// </summary>
    private static string? FindInBinDir(string binDir, string baseName)
    {
#if WINDOWS
        var names = new[] { baseName + ".exe", baseName };
#else
        var names = new[] { baseName };
#endif

        foreach (var name in names)
        {
            var candidate = Path.Combine(binDir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        // Дополнительный регистронезависимый поиск в каталоге (например chdbfl на Linux).
        try
        {
            foreach (var file in Directory.EnumerateFiles(binDir))
            {
                foreach (var name in names)
                {
                    if (Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
        }
        catch
        {
            // Каталог может быть недоступен на чтение — просто возвращаем null.
        }

        return null;
    }

    private bool Launch(string fileName, string arguments, string what, bool shellExecute = false)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = shellExecute
            });
            _logger.Info($"{what}: запущен {fileName} {arguments}".Trim());
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"{what}: не удалось запустить {fileName}. {ex.Message}", ex);
            return false;
        }
    }
}