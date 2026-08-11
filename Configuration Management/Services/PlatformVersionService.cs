using System.IO;

namespace Configuration_Management.Services;

/// <summary>
/// Сервис поиска установленных версий платформы 1С:Предприятие.
/// </summary>
public static class PlatformVersionService
{
    /// <summary>
    /// Ищет установленные варианты платформы 1С в стандартных каталогах
    /// Program Files\1cv8 и Program Files (x86)\1cv8.
    /// Разрядность установки определяется каталогом, в который установлена платформа:
    /// Program Files — 64-битная, Program Files (x86) — 32-битная.
    /// В современных версиях (8.3.22+ и 8.5.x) исполняемый файл называется единообразно
    /// «1cv8.exe» для обеих разрядностей, поэтому разрядность нельзя определить по имени файла.
    /// В результат попадают только реально установленные варианты,
    /// в формате «8.3.25.1234 (32)» / «8.3.25.1234 (64)».
    /// </summary>
    /// <returns>Отсортированный по убыванию список вариантов платформы.</returns>
    public static List<string> FindInstalledVersions()
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // 64-битные версии устанавливаются в Program Files.
        AddVersionsFromRoot(versions, programFiles, "64");

        // 32-битные версии устанавливаются в Program Files (x86).
        AddVersionsFromRoot(versions, programFilesX86, "32");

        // Сортируем по убыванию версии (сначала самые новые).
        return versions
            .OrderByDescending(v => v, new VersionComparer())
            .ToList();
    }

    /// <summary>
    /// Добавляет варианты платформы из указанного корневого каталога установки
    /// (Program Files или Program Files (x86)) с заданной разрядностью.
    /// </summary>
    private static void AddVersionsFromRoot(HashSet<string> versions, string? root, string architecture)
    {
        if (string.IsNullOrEmpty(root))
            return;

        var baseDir = Path.Combine(root, "1cv8");
        if (!Directory.Exists(baseDir))
            return;

        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            var name = Path.GetFileName(dir);
            // Каталог версии платформы имеет вид «8.3.25.1234».
            if (!IsVersionDirectory(name))
                continue;

            // Проверяем, что в каталоге версии действительно есть исполняемый файл клиента.
            var binDir = Path.Combine(dir, "bin");
            if (File.Exists(Path.Combine(binDir, "1cv8.exe")) ||
                File.Exists(Path.Combine(binDir, "1cv8x64.exe")))
            {
                versions.Add(FormatVariant(name, architecture));
            }
        }
    }

    /// <summary>
    /// Формирует строку варианта платформы вида «8.3.25.1234 (64)».
    /// </summary>
    public static string FormatVariant(string version, string architecture)
        => $"{version} ({architecture})";

    /// <summary>
    /// Разбирает вариант платформы вида «8.3.25.1234 (64)» на чистую версию
    /// «8.3.25.1234» и разрядность «64». Если суффикс разрядности отсутствует —
    /// возвращает исходную строку как версию и «32» как разрядность по умолчанию.
    /// </summary>
    public static void ParseVariant(string variant, out string version, out string architecture)
    {
        version = variant;
        architecture = "32";

        if (string.IsNullOrWhiteSpace(variant))
            return;

        var end = variant.LastIndexOf(')');
        var start = variant.LastIndexOf('(');
        if (end < 0 || start < 0 || start > end)
            return;

        var arch = variant.Substring(start + 1, end - start - 1).Trim();
        if (arch == "64" || arch == "32")
        {
            version = variant.Substring(0, start).Trim();
            architecture = arch;
        }
    }

    /// <summary>
    /// Проверяет, является ли имя каталога версией платформы вида «8.3.25.1234».
    /// </summary>
    private static bool IsVersionDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var parts = name.Split('.');
        if (parts.Length < 3 || parts.Length > 4)
            return false;

        foreach (var part in parts)
        {
            if (!int.TryParse(part, out _))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Компаратор для сортировки версий по убыванию.
    /// Учитывает суффикс разрядности «(32)» / «(64)»: в пределах одной версии
    /// 64-битный вариант считается более новым.
    /// </summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == y) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var result = CompareCore(x, y);
            if (result != 0)
                return result;

            // Версии совпадают — сравниваем разрядность (64 > 32).
            return GetArch(x).CompareTo(GetArch(y));
        }

        private static int CompareCore(string x, string y)
        {
            var xParts = CleanVersion(x).Split('.').Select(int.Parse).ToArray();
            var yParts = CleanVersion(y).Split('.').Select(int.Parse).ToArray();

            var length = Math.Max(xParts.Length, yParts.Length);
            for (var i = 0; i < length; i++)
            {
                var xVal = i < xParts.Length ? xParts[i] : 0;
                var yVal = i < yParts.Length ? yParts[i] : 0;
                if (xVal != yVal)
                    return xVal.CompareTo(yVal);
            }

            return 0;
        }

        private static string CleanVersion(string variant)
        {
            ParseVariant(variant, out var version, out _);
            return version;
        }

        private static int GetArch(string variant)
        {
            ParseVariant(variant, out _, out var architecture);
            return architecture == "64" ? 1 : 0;
        }
    }
}