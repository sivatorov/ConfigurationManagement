using System;
using System.IO;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Общий поиск исполняемых файлов платформы 1С:Предприятие в каталоге <c>bin</c>
/// установленной версии (<c>1cv8</c>/<c>1cv8.exe</c>, <c>rac</c>, <c>chdbfl</c> и т.п.).
/// Единая логика для сервиса администрирования ИБ
/// (<see cref="InfobaseAdminService"/>) и клиента rac встроенного монитора серверов 1С
/// (<see cref="RacClient"/>) — без дублирования (вынесено из InfobaseAdminService, 0.3.9.123).
/// </summary>
public static class OneCPlatformLocator
{
    /// <summary>
    /// Разрешает каталог <c>bin</c> установленной платформы нужной разрядности:
    /// при выбранной базе — по её настройкам (точная версия, затем новейшая
    /// установленная), без базы — новейшая установленная версия (issue #295).
    /// </summary>
    public static string? ResolveBinDirectory(Infobase? infobase)
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

    /// <summary>
    /// Ищет исполняемый файл платформы в каталоге bin с учётом платформенного
    /// расширения: <c>.exe</c> на Windows, без расширения на Linux.
    /// </summary>
    public static string? FindInBinDir(string binDir, string baseName)
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

    /// <summary>
    /// Находит исполняемый файл <c>rac</c> (Remote Administration Client) в каталоге bin
    /// установленной платформы (при <paramref name="infobase"/> — с учётом её настроек,
    /// иначе — новейшая установленная версия). Возвращает полный путь или null.
    /// </summary>
    /// <remarks>
    /// Для варианта без базы (монитор серверов, issue #324) результат кэшируется на 5 минут:
    /// поиск сканирует каталоги установленных платформ и вызывается на КАЖДУЮ rac-команду
    /// (в цикле обновления монитора их 8), заметно добавляя ко времени подключения.
    /// Кэш проверяет существование файла и сбрасывается по TTL (после установки новой
    /// платформы путь обновится сам).
    /// </remarks>
    public static string? FindRacExecutable(Infobase? infobase = null)
    {
        if (infobase is not null)
            return FindRacExecutableCore(infobase);

        lock (RacCacheLock)
        {
            if (_cachedRacPath is not null &&
                DateTime.UtcNow - _cachedRacAt < RacCacheTtl &&
                File.Exists(_cachedRacPath))
                return _cachedRacPath;
        }

        var found = FindRacExecutableCore(null);
        lock (RacCacheLock)
        {
            _cachedRacPath = found;
            _cachedRacAt = DateTime.UtcNow;
        }

        return found;
    }

    private static string? FindRacExecutableCore(Infobase? infobase)
    {
        var binDir = ResolveBinDirectory(infobase);
        return binDir is null ? null : FindInBinDir(binDir, "rac");
    }

    private static readonly object RacCacheLock = new();
    private static string? _cachedRacPath;
    private static DateTime _cachedRacAt;
    private static readonly TimeSpan RacCacheTtl = TimeSpan.FromMinutes(5);

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
}