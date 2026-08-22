#if LINUX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Configuration_Management.Models;

namespace Configuration_Management.Services
{
    /// <summary>
    /// Поиск и разбор установленных версий платформы 1С на Linux (Этап 5).
    /// Ищет каталоги установки в /opt/1cv8, /opt/1cv8.x86_64, ~/.1cv8,
    /// /usr/share/1cv8, /usr/bin и дополнительных папках.
    /// Бинарники — БЕЗ расширения .exe: 1cv8, 1cv8c, 1cv8s, 1cv8a, ragent.
    /// Разрядность определяется по каталогу и/или ELF-классу бинарника (readelf).
    /// </summary>
    public static class PlatformVersionService
    {
        private static readonly object _extraRootsLock = new();
        private static List<string> _additionalPaths = new();

        /// <summary>Имена исполняемых файлов платформы 1С на Linux (без .exe).</summary>
        public static readonly string[] OneCBinaryNames =
        {
            "1cv8", "1cv8c", "1cv8s", "1cv8a", "ragent", "rmngr", "rphost"
        };

        /// <summary>Стандартные корни установки платформы 1С на Linux.</summary>
        private static readonly string[] DefaultRoots =
        {
            "/opt/1cv8",
            "/opt/1cv8.x86_64",
            "/opt/1cv8.x86",
            "/usr/share/1cv8",
            "/usr/local/1cv8",
            "/usr/bin"
        };

        public static void SetAdditionalSearchPaths(IEnumerable<string>? paths)
        {
            lock (_extraRootsLock)
            {
                _additionalPaths = paths?
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim().TrimEnd('/', '\\'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
            }
        }

        public static IReadOnlyList<string> GetAdditionalSearchPaths()
        {
            lock (_extraRootsLock)
            {
                return _additionalPaths.ToList();
            }
        }

        /// <summary>Корни поиска установки 1С для указанной разрядности.</summary>
        private static IEnumerable<string> GetInstallRoots(string architecture)
        {
            var roots = new List<string>(DefaultRoots);

            var is64 = architecture == "64" || architecture == "x64";
            if (is64)
            {
                roots.Add("/opt/1cv8/x86_64");
                roots.Add("/opt/1cv8/amd64");
            }
            else
            {
                roots.Add("/opt/1cv8/i386");
                roots.Add("/opt/1cv8/i686");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                roots.Add(Path.Combine(home, ".1cv8"));
                roots.Add(Path.Combine(home, "1cv8"));
                roots.Add(Path.Combine(home, ".local", "share", "1cv8"));
            }

            lock (_extraRootsLock)
            {
                roots.AddRange(_additionalPaths);
            }

            return roots
                .Select(r => r.Trim().TrimEnd('/', '\\'))
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Корни bin для указанной разрядности (только существующие).</summary>
        public static IReadOnlyList<string> GetSearchRoots(string architecture)
        {
            var roots = new List<string>();
            foreach (var root in GetInstallRoots(architecture))
            {
                var bin = ResolveBinDirectory(root);
                if (bin is not null)
                    roots.Add(bin);
            }
            return roots;
        }

        /// <summary>Находит установленные версии платформы 1С на Linux.</summary>
        public static List<PlatformVersionInfo> FindInstalledVersionInfos(IEnumerable<string>? additionalPaths = null)
        {
            var result = new List<PlatformVersionInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var roots = new List<string>(GetInstallRoots("64"));
            if (additionalPaths != null)
            {
                roots.AddRange(additionalPaths.Where(p => !string.IsNullOrWhiteSpace(p)));
            }

            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                ScanRoot(root, result, seen);

            return result
                .OrderByDescending(v => v.Display, new VersionDisplayComparer())
                .ToList();
        }

        public static List<string> FindInstalledVersions(IEnumerable<string>? additionalPaths = null)
            => FindInstalledVersionInfos(additionalPaths).Select(v => v.Display).ToList();

        /// <summary>Все каталоги версий нужной разрядности как пары (версия, bin).</summary>
        public static List<(string Version, string BinDir)> FindPlatformVersionDirs(string architecture)
        {
            var archKey = architecture == "64" ? "64" : "32";
            var results = new List<(string Version, string BinDir)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var info in FindInstalledVersionInfos())
            {
                ParseVariant(info.Display, out var ver, out var arch);
                if (string.IsNullOrWhiteSpace(ver))
                    continue;
                var a = arch is "64" or "32" ? arch : DetectArchitecture(info.Path);
                if (a != archKey)
                    continue;
                var bin = ResolveBinDirectory(info.Path);
                if (bin is not null && seen.Add(bin))
                    results.Add((ver, bin));
            }

            return results;
        }

        /// <summary>Каталог bin для указанной версии и разрядности (если найден), иначе null.</summary>
        public static string? ResolveVersionBinDirectory(string version, string architecture)
        {
            ParseVariant(version ?? string.Empty, out var cleanVersion, out _);
            if (string.IsNullOrWhiteSpace(cleanVersion))
                return null;

            foreach (var root in GetInstallRoots(architecture))
            {
                var bin = ResolveBinDirectory(Path.Combine(root, cleanVersion));
                if (bin is not null)
                    return bin;
            }

            // Ищем рекурсивно по дополнительным папкам (нестандартная вложенность).
            lock (_extraRootsLock)
            {
                foreach (var root in _additionalPaths)
                {
                    var found = FindVersionBinRecursive(root, cleanVersion, depth: 0, maxDepth: 6);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        private static string? FindVersionBinRecursive(string path, string version, int depth, int maxDepth)
        {
            if (depth > maxDepth || !Directory.Exists(path))
                return null;
            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal))
                        continue;

                    if (string.Equals(name, version, StringComparison.OrdinalIgnoreCase))
                    {
                        var bin = ResolveBinDirectory(dir);
                        if (bin is not null)
                            return bin;
                    }

                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("docs", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("readme", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("common", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sub = FindVersionBinRecursive(dir, version, depth + 1, maxDepth);
                    if (sub != null)
                        return sub;
                }
            }
            catch
            {
                // нет доступа
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Разбор каталогов
        // ------------------------------------------------------------------

        private static void ScanRoot(string root, List<PlatformVersionInfo> result, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;

            try
            {
                // 1. Каталоги версий внутри корня: /opt/1cv8/8.3.27.2214/bin/1cv8
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (!LooksLikeVersion(name))
                        continue;
                    if (ResolveBinDirectory(dir) is null)
                        continue;
                    var arch = DetectArchitecture(dir);
                    if (!seen.Add(dir))
                        continue;
                    result.Add(new PlatformVersionInfo { Display = FormatVariant(name, arch), Path = dir });
                }

                // 2. Сам корень — каталог версии (например ~/.1cv8/8.3.27.1).
                if (LooksLikeVersion(Path.GetFileName(root)))
                {
                    if (ResolveBinDirectory(root) is not null && seen.Add(root))
                    {
                        var arch = DetectArchitecture(root);
                        result.Add(new PlatformVersionInfo
                        {
                            Display = FormatVariant(Path.GetFileName(root), arch),
                            Path = root
                        });
                    }
                }

                // 3. /usr/bin/1cv8 — симлинк на конкретную версию.
                if (IsBinSymlinkRoot(root))
                    TryAddUsrBinSymlink(result, seen);
            }
            catch
            {
                // нет доступа
            }
        }

        private static bool IsBinSymlinkRoot(string root)
        {
            var trimmed = root.TrimEnd('/', '\\');
            return string.Equals(trimmed, "/usr/bin", StringComparison.Ordinal) ||
                   string.Equals(trimmed, "/usr/local/bin", StringComparison.Ordinal);
        }

        private static void TryAddUsrBinSymlink(List<PlatformVersionInfo> result, HashSet<string> seen)
        {
            try
            {
                var link = "/usr/bin/1cv8";
                if (!File.Exists(link))
                    return;
                var target = File.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName;
                if (string.IsNullOrEmpty(target))
                    return;
                var binDir = Path.GetDirectoryName(target);
                if (string.IsNullOrEmpty(binDir))
                    return;

                // При раскладке <версия>/bin каталог версии на уровень выше,
                // при плоской раскладке каталог цели сам является каталогом версии.
                var versionDir = LooksLikeVersion(Path.GetFileName(binDir))
                    ? binDir
                    : Path.GetDirectoryName(binDir);
                if (string.IsNullOrEmpty(versionDir))
                    return;
                var name = Path.GetFileName(versionDir);
                if (!LooksLikeVersion(name))
                    return;
                var arch = DetectArchitecture(versionDir);
                if (seen.Add(versionDir))
                    result.Add(new PlatformVersionInfo { Display = FormatVariant(name, arch), Path = versionDir });
            }
            catch
            {
                // ignore
            }
        }

        private static bool HasOneCBinary(string binDir)
        {
            try
            {
                return OneCBinaryNames.Any(n => File.Exists(Path.Combine(binDir, n)));
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksLikeVersion(string name)
        {
            var parts = name.Split('.');
            return parts.Length >= 3 && parts.All(p => p.Length > 0 && int.TryParse(p, out _));
        }

        /// <summary>Определяет разрядность по каталогу и/или ELF-классу бинарника.</summary>
        private static string DetectArchitecture(string dir)
        {
            var lower = dir.Replace('\\', '/').ToLowerInvariant();

            if (lower.Contains("i386") || lower.Contains("i686") ||
                lower.Contains("x86-32") || lower.EndsWith("/x86", StringComparison.Ordinal) ||
                lower.Contains("1cv8.x86/") || lower.Contains("/x86/"))
                return "32";

            if (lower.Contains("x86_64") || lower.Contains("amd64"))
                return "64";

            var readelf = DetectArchViaReadelf(dir);
            if (readelf != null)
                return readelf;

            // По умолчанию (например, /opt/1cv8, ~/.1cv8) — 64-бит.
            return "64";
        }

        /// <summary>
        /// Каталог с исполняемыми файлами платформы для каталога версии.
        /// Раскладка отличается между дистрибутивами и версиями платформы:
        /// у одних бинарники лежат в подкаталоге bin, у других прямо в каталоге
        /// версии (например /opt/1cv8/x86_64/8.3.27.2214/1cv8). Проверяются оба
        /// варианта, возвращается тот, где бинарники действительно есть.
        /// </summary>
        private static string? ResolveBinDirectory(string? versionDir)
        {
            if (string.IsNullOrWhiteSpace(versionDir))
                return null;

            var bin = Path.Combine(versionDir, "bin");
            if (Directory.Exists(bin) && HasOneCBinary(bin))
                return bin;

            if (Directory.Exists(versionDir) && HasOneCBinary(versionDir))
                return versionDir;

            return null;
        }

        private static string? DetectArchViaReadelf(string dir)
        {
            var binDir = ResolveBinDirectory(dir);
            if (binDir is null)
                return null;
            // Установка может быть неполной: у версии с одним тонким клиентом
            // файла 1cv8 нет, разрядность читаем с любого доступного бинарника.
            var bin = OneCBinaryNames
                .Select(n => Path.Combine(binDir, n))
                .FirstOrDefault(File.Exists);
            if (bin is null)
                return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "readelf",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-h");
                psi.ArgumentList.Add(bin);
                using var p = Process.Start(psi);
                if (p is null)
                    return null;
                var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(3000);
                if (text.Contains("ELF64", StringComparison.OrdinalIgnoreCase))
                    return "64";
                if (text.Contains("ELF32", StringComparison.OrdinalIgnoreCase))
                    return "32";
            }
            catch
            {
                // readelf может отсутствовать в системе
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Форматирование и сравнение версий
        // ------------------------------------------------------------------

        public static string FormatVariant(string version, string architecture)
            => $"{version} ({architecture})";

        public static void ParseVariant(string variant, out string version, out string architecture)
        {
            version = variant;
            architecture = "64";
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

        /// <summary>Линия платформы: «8.3.27.1688 (64)» → «8.3».</summary>
        public static string GetVersionLine(string variant)
        {
            ParseVariant(variant, out var version, out _);
            var parts = version.Split('.');
            return parts.Length >= 2
                ? string.Join(".", parts.Take(2))
                : (string.IsNullOrEmpty(version) ? "—" : version);
        }

        /// <summary>Группа сборки: «8.3.27.1688 (64)» → «8.3.27».</summary>
        public static string GetVersionBuildGroup(string variant)
        {
            ParseVariant(variant, out var version, out _);
            var parts = version.Split('.');
            return parts.Length >= 3
                ? string.Join(".", parts.Take(3))
                : GetVersionLine(variant);
        }

        /// <summary>Подпись разрядности в стиле стартера 1С: «x64» / «x32».</summary>
        public static string FormatArchitectureLabel(string? architecture)
        {
            if (string.IsNullOrWhiteSpace(architecture))
                return "";
            var a = architecture.Trim();
            if (a is "64" or "x64" or "X64")
                return "x64";
            if (a is "32" or "x32" or "X32" or "x86")
                return "x32";
            return a;
        }

        /// <summary>Числовое сравнение строк версий (без суффикса разрядности). >0 если a новее b.</summary>
        public static int CompareVersionStrings(string a, string b)
        {
            ParseVariant(a, out var va, out _);
            ParseVariant(b, out var vb, out _);
            var pa = ToIntArray(va);
            var pb = ToIntArray(vb);
            var len = Math.Max(pa.Length, pb.Length);
            for (var i = 0; i < len; i++)
            {
                var x = i < pa.Length ? pa[i] : 0;
                var y = i < pb.Length ? pb[i] : 0;
                if (x != y)
                    return x.CompareTo(y);
            }
            return 0;
        }

        private static int[] ToIntArray(string v)
        {
            var s = v.Split(new[] { '.', ' ', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<int>();
            foreach (var p in s)
            {
                if (int.TryParse(p, out var n))
                    list.Add(n);
                else
                    break;
            }
            return list.ToArray();
        }

        /// <summary>Дерево выбора платформы: линия (8.3) → группа сборок (8.3.27) → версия.</summary>
        public static List<PlatformVersionGroup> BuildGroupedTree(IEnumerable<PlatformVersionInfo> infos)
        {
            var list = infos?.ToList() ?? new List<PlatformVersionInfo>();
            var roots = new List<PlatformVersionGroup>();

            var byLine = list
                .GroupBy(i => GetVersionLine(i.Display))
                .OrderByDescending(g => g.Key, new VersionDisplayComparer());

            foreach (var lineGroup in byLine)
            {
                var lineNode = new PlatformVersionGroup { Name = lineGroup.Key, Kind = PlatformNodeKind.Line };

                var byBuild = lineGroup
                    .GroupBy(i => GetVersionBuildGroup(i.Display))
                    .OrderByDescending(g => g.Key, new VersionDisplayComparer());

                foreach (var buildGroup in byBuild)
                {
                    var buildNode = new PlatformVersionGroup { Name = buildGroup.Key, Kind = PlatformNodeKind.BuildGroup };

                    foreach (var info in buildGroup.OrderByDescending(i => i.Display, new VersionDisplayComparer()))
                    {
                        var leaf = new PlatformVersionGroup
                        {
                            Name = info.Display,
                            Variant = info.Display,
                            Path = info.Path,
                            Kind = ParseVariantKind(info.Display),
                            Versions = { info }
                        };
                        buildNode.Children.Add(leaf);
                    }
                    lineNode.Children.Add(buildNode);
                }
                roots.Add(lineNode);
            }

            return roots;
        }

        private static PlatformNodeKind ParseVariantKind(string variant)
        {
            ParseVariant(variant, out _, out var arch);
            return arch == "64" ? PlatformNodeKind.LeafX64
                : arch == "32" ? PlatformNodeKind.LeafX32
                : PlatformNodeKind.Leaf;
        }

        /// <summary>Сравнивает строки версий по старшинству чисел.</summary>
        private sealed class VersionDisplayComparer : IComparer<string>
        {
            public int Compare(string? x, string? y)
            {
                if (x is null && y is null) return 0;
                if (x is null) return -1;
                if (y is null) return 1;
                return CompareVersionStrings(x, y);
            }
        }
    }

    /// <summary>Адаптер интерфейса <see cref="IPlatformVersionService"/> для Linux.</summary>
    public sealed class PlatformVersionServiceAdapter : IPlatformVersionService
    {
        public List<string> FindInstalledVersions(IEnumerable<string>? additionalPaths = null)
            => PlatformVersionService.FindInstalledVersions(additionalPaths);
    }
}
#endif