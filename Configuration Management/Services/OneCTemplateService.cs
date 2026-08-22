using System.IO;
using System.Text.RegularExpressions;
using Configuration_Management.Localization;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Configuration_Management.Services;

/// <summary>
/// Поиск установленных шаблонов конфигураций 1С в каталогах tmplts
/// (как в стартере 1С:Предприятие).
/// </summary>
public static class OneCTemplateService
{
    private static readonly object UserPathsLock = new();
    private static List<string> _userTemplatePaths = new();

    /// <summary>Каталоги шаблонов из настроек программы (например H:\1C\upd).</summary>
    public static void SetUserTemplatePaths(IEnumerable<string>? paths)
    {
        lock (UserPathsLock)
        {
            _userTemplatePaths = paths?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().TrimEnd('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
    }

    public static IReadOnlyList<string> GetUserTemplatePaths()
    {
        lock (UserPathsLock)
            return _userTemplatePaths.ToList();
    }

    /// <summary>Тип шаблона для группировки.</summary>
    public enum TemplateKind
    {
        /// <summary>Обычный шаблон конфигурации (.cf) или выгрузки.</summary>
        Configuration,
        /// <summary>Демо-база / демонстрационные данные.</summary>
        Demo,
        /// <summary>Пустая ИБ / заготовка без данных.</summary>
        Empty
    }

    /// <summary>Описание найденного шаблона (как в стартере 1С: из *.mft или путь).</summary>
    public sealed class TemplateInfo
    {
        public required string DisplayName { get; init; }
        public required string FilePath { get; init; }
        public required string RelativePath { get; init; }
        public string Extension => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
        public string RootFolder { get; init; } = "";

        /// <summary>Поставщик из манифеста (Vendor=) или каталог.</summary>
        public string Vendor { get; init; } = "";
        /// <summary>Имя решения из манифеста (Name=) или каталог конфигурации.</summary>
        public string ConfigurationName { get; init; } = "";
        /// <summary>Версия из манифеста (Version=) или каталог версии.</summary>
        public string Version { get; init; } = "";
        /// <summary>
        /// Путь в дереве стартера 1С из Catalog= (через «/»), например
        /// «1С:Управление торговлей/Управление торговлей (демо)».
        /// </summary>
        public string CatalogPath { get; init; } = "";
        /// <summary>Рекомендуемый Destination из манифеста.</summary>
        public string Destination { get; init; } = "";
        /// <summary>Демо / пустая / обычная.</summary>
        public TemplateKind Kind { get; init; } = TemplateKind.Configuration;

        /// <summary>Сегменты CatalogPath для построения дерева.</summary>
        public string[] CatalogSegments =>
            string.IsNullOrWhiteSpace(CatalogPath)
                ? Array.Empty<string>()
                : CatalogPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToArray();

        public override string ToString() => DisplayName;
    }

    /// <summary>Узел дерева шаблонов (группа или лист) — как в окне «Добавление информационной базы» 1С.</summary>
    public sealed class TemplateTreeNode
    {
        public required string Title { get; init; }
        public string? Subtitle { get; init; }
        public TemplateInfo? Template { get; init; }
        public bool IsGroup => Template is null;
        public List<TemplateTreeNode> Children { get; } = new();

        public override string ToString() => Title;
    }

    /// <summary>
    /// Каталог шаблонов по умолчанию у стартера 1С:
    /// <c>%PUBLIC%\Documents\1C\1cv8\tmplts</c>
    /// (туда установщик конфигураций кладёт шаблоны).
    /// </summary>
    public static string GetDefaultTemplatePath()
    {
#if LINUX
        // Linux: ~/.local/share/1C/1cv8/tmplts (с учётом XDG_DATA_HOME).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataBase = !string.IsNullOrWhiteSpace(xdgData)
            ? xdgData
            : string.IsNullOrEmpty(home)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Path.Combine(home, ".local", "share");
        if (!string.IsNullOrEmpty(dataBase))
            return Path.Combine(dataBase, "1C", "1cv8", "tmplts");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "1C", "1cv8", "tmplts");
#else
        var publicDir = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrEmpty(publicDir))
        {
            var path = Path.Combine(publicDir, "Documents", "1C", "1cv8", "tmplts");
            return path;
        }

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (!string.IsNullOrEmpty(common))
            return Path.Combine(common, "1C", "1cv8", "tmplts");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "1C", "1cv8", "tmplts");
#endif
    }

    /// <summary>
    /// Путь к шаблонам, настроенный в стартере 1С (реестр / 1cestart.cfg),
    /// либо стандартный путь по умолчанию.
    /// </summary>
    public static string GetConfiguredOrDefaultTemplatePath()
    {
        foreach (var path in EnumerateConfiguredTemplatePaths())
        {
            if (Directory.Exists(path))
                return path;
        }

        return GetDefaultTemplatePath();
    }

    /// <summary>
    /// Каталоги для поиска шаблонов: сначала настроенный/дефолтный путь 1С, затем остальные.
    /// </summary>
    public static IReadOnlyList<string> GetTemplateRootFolders()
    {
        var roots = new List<string>();

        void Add(string? path, bool requireExists = true)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().TrimEnd('\\', '/'));
                if (requireExists && !Directory.Exists(path)) return;
                if (!roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                    roots.Add(path);
            }
            catch { /* ignore */ }
        }

        // 0) Каталоги из настроек программы (в т.ч. H:\1C\upd)
        lock (UserPathsLock)
        {
            foreach (var p in _userTemplatePaths)
                Add(p);
        }

        // 1) Явно настроенные в стартере 1С
        foreach (var p in EnumerateConfiguredTemplatePaths())
            Add(p);

        // 2) Путь по умолчанию у 1С (Public\Documents\... или ~/.local/share/1C/1cv8/tmplts) —
        //    даже если пока пуст, показываем как основной
        var def = GetDefaultTemplatePath();
        if (Directory.Exists(def))
            Add(def);
        else
            Add(def, requireExists: false); // для подсказки в UI

#if LINUX
        // 2.5) Linux: системные каталоги шаблонов — /opt/1cv8/<версия>/tmplts, /usr/share/1cv8/tmplts
        foreach (var root in new[]
                 {
                     "/opt/1cv8",
                     "/opt/1C/1cv8",
                     "/usr/share/1cv8",
                     "/usr/share/1C/1cv8"
                 })
        {
            Add(Path.Combine(root, "tmplts"));
        }
        try
        {
            foreach (var vdir in Directory.EnumerateDirectories("/opt/1cv8"))
                Add(Path.Combine(vdir, "tmplts"));
        }
        catch { /* каталога может не быть */ }
#endif

        // 3) Дополнительные типичные расположения
        var commonDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var baseDir in new[] { commonDocs, myDocs, appData, localApp })
        {
            if (string.IsNullOrEmpty(baseDir)) continue;
            Add(Path.Combine(baseDir, "1C", "1cv8", "tmplts"));
            Add(Path.Combine(baseDir, "1C", "1Cv8", "tmplts"));
        }

        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            Add(Path.Combine(pf, "1cv8", "tmplts"));
            Add(Path.Combine(pf, "1C", "1cv8", "tmplts"));
        }

        // Только реально существующие — для сканирования
        return roots.Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// Пути из настроек стартера 1С (реестр и 1cestart.cfg).
    /// </summary>
    private static IEnumerable<string> EnumerateConfiguredTemplatePaths()
    {
        var found = new List<string>();

        void Consider(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim().Trim('"');
            // в cfg иногда путь с tmplts, иногда родитель
            if (value.Contains("tmplts", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("1cv8", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("1C", StringComparison.OrdinalIgnoreCase))
            {
                if (!found.Contains(value, StringComparer.OrdinalIgnoreCase))
                    found.Add(value);
            }
        }

#if WINDOWS
        // Реестр стартера (только Windows; на Linux реестра нет).
        foreach (var sub in new[]
                 {
                     @"Software\1C\1cv8\1cestart",
                     @"Software\1C\1Cv8\1cestart",
                     @"Software\1C\1cv8\Common",
                     @"Software\1C\1Cv8\Common",
                     @"Software\1C\1cestart"
                 })
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(sub);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is string s)
                        Consider(s);
                }
            }
            catch { /* ignore */ }
        }
#endif

        // Файлы конфигурации стартера (1cestart.cfg).
        var cfgCandidates = new List<string>();
#if LINUX
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            cfgCandidates.Add(Path.Combine(home, ".1C", "1cestart", "1cestart.cfg"));
            cfgCandidates.Add(Path.Combine(home, ".1cv8", "1CEStart", "1cestart.cfg"));
            cfgCandidates.Add(Path.Combine(home, ".local", "share", "1cv8", "1CEStart", "1cestart.cfg"));
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            cfgCandidates.Add(Path.Combine(appData, "1C", "1CEStart", "1cestart.cfg"));
#else
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            cfgCandidates.Add(Path.Combine(appData, "1C", "1CEStart", "1cestart.cfg"));
            cfgCandidates.Add(Path.Combine(appData, "1C", "1cestart", "1cestart.cfg"));
            cfgCandidates.Add(Path.Combine(appData, "1C", "1cv8", "1cestart.cfg"));
        }
#endif

        foreach (var cfg in cfgCandidates)
        {
            foreach (var linePath in ReadPathsFromCfg(cfg))
                Consider(linePath);
        }

        return found;
    }

    private static IEnumerable<string> ReadPathsFromCfg(string cfgPath)
    {
        if (!File.Exists(cfgPath))
            yield break;

        string[] lines;
        try { lines = File.ReadAllLines(cfgPath); }
        catch { yield break; }

        // Строки вида Key=C:\path (Windows) или Key=/home/.../path (Linux).
#if LINUX
        var pathRx = new Regex(@"\/[^\r\n"";=]+", RegexOptions.Compiled);
#else
        var pathRx = new Regex(@"[A-Za-z]:\\[^\r\n""]+", RegexOptions.Compiled);
#endif
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            // интересны ключи с template / tmplts / conf
            var lower = line.ToLowerInvariant();
            if (lower.Contains("tmplts") || lower.Contains("template") ||
                lower.Contains("config") || lower.Contains("commoncfg"))
            {
                foreach (Match m in pathRx.Matches(line))
                    yield return m.Value.TrimEnd('\\', '/', '"', ';');
            }
        }
    }

    /// <summary>
    /// Сканирует каталоги шаблонов по правилам стартера 1С:
    /// приоритет — файлы-манифесты <c>*.mft</c> (Catalog / Source),
    /// иначе — эвристика по пути Vendor\Config\Version\.cf|.dt.
    /// </summary>
    public static IReadOnlyList<TemplateInfo> FindInstalledTemplates(int maxDepth = 8)
    {
        var result = new List<TemplateInfo>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var primary = GetConfiguredOrDefaultTemplatePath();
        var roots = GetTemplateRootFolders().ToList();
        if (Directory.Exists(primary))
        {
            roots.RemoveAll(r => r.Equals(primary, StringComparison.OrdinalIgnoreCase));
            roots.Insert(0, primary);
        }

        foreach (var root in roots)
        {
            try
            {
                // 1) Манифесты *.mft — канонический способ (как у стартера 1С)
                foreach (var mft in EnumerateFilesSafe(root, "*.mft", maxDepth))
                {
                    if (!seenMft.Add(mft)) continue;
                    try
                    {
                        foreach (var t in ParseManifest(mft, root))
                        {
                            if (!File.Exists(t.FilePath)) continue;
                            if (!seenFiles.Add(t.FilePath)) continue;
                            result.Add(t);
                        }
                    }
                    catch { /* битый mft — пропускаем */ }
                }

                // 2) «Сироты»: .cf/.dt без манифеста (ручные/старые поставки)
                ScanOrphanTemplates(root, root, 0, maxDepth, result, seenFiles);
            }
            catch { /* ignore root */ }
        }

        return result
            .OrderBy(t => t.CatalogPath, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern, int maxDepth)
    {
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (depth > maxDepth) continue;
            IEnumerable<string> files = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch { /* ignore */ }
            foreach (var f in files)
                yield return f;

            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                    stack.Push((sub, depth + 1));
                }
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Разбор 1cv8.mft: заголовок Vendor/Name/Version и секции [Config*] с Catalog + Source.
    /// Catalog задаёт иерархию в списке стартера через «/».
    /// </summary>
    private static IEnumerable<TemplateInfo> ParseManifest(string mftPath, string root)
    {
        string[] lines;
        try { lines = File.ReadAllLines(mftPath, System.Text.Encoding.UTF8); }
        catch
        {
            try { lines = File.ReadAllLines(mftPath, System.Text.Encoding.GetEncoding(1251)); }
            catch { yield break; }
        }

        // Убираем BOM и пустые
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].Trim('\uFEFF', ' ', '\t');

        var vendor = "";
        var name = "";
        var version = "";
        string? section = null;
        var sectionProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void FlushSection()
        {
            // no-op placeholder — yield can't be in local function easily
        }

        var mftDir = Path.GetDirectoryName(mftPath) ?? root;

        // Сначала соберём секции в список
        var sections = new List<Dictionary<string, string>>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (section is not null && sectionProps.Count > 0)
                    sections.Add(new Dictionary<string, string>(sectionProps, StringComparer.OrdinalIgnoreCase));
                section = line.Substring(1, line.Length - 2).Trim();
                sectionProps.Clear();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim().Trim('"');

            if (section is null)
            {
                if (key.Equals("Vendor", StringComparison.OrdinalIgnoreCase)) vendor = val;
                else if (key.Equals("Name", StringComparison.OrdinalIgnoreCase)) name = val;
                else if (key.Equals("Version", StringComparison.OrdinalIgnoreCase)) version = val;
            }
            else
            {
                sectionProps[key] = val;
            }
        }
        if (section is not null && sectionProps.Count > 0)
            sections.Add(new Dictionary<string, string>(sectionProps, StringComparer.OrdinalIgnoreCase));

        foreach (var props in sections)
        {
            if (!props.TryGetValue("Source", out var source) || string.IsNullOrWhiteSpace(source))
                continue;

            // Только cf/dt для создания ИБ (cfu — обновления, не шаблоны создания)
            var sourceNorm = source.Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.GetFullPath(Path.Combine(mftDir, sourceNorm));
            var ext = Path.GetExtension(filePath);
            if (!ext.Equals(".cf", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".dt", StringComparison.OrdinalIgnoreCase))
                continue;

            // Catalog / Catalog_ru / Catalog_en — иерархия в стартере
            var catalog = FirstProp(props, "Catalog", "Catalog_ru", "Catalog_ru_RU", "Catalog_en", "Catalog_en_US");
            if (string.IsNullOrWhiteSpace(catalog))
            {
                // запасной путь: Vendor/Name/Version
                catalog = string.Join("/", new[] { vendor, name, version }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            var destination = FirstProp(props, "Destination") ?? "";
            var kind = ClassifyFromCatalogAndSource(catalog, Path.GetFileNameWithoutExtension(filePath));

            string rel;
            try { rel = Path.GetRelativePath(root, filePath); }
            catch { rel = filePath; }

            var leafName = catalog.Contains('/')
                ? catalog.Substring(catalog.LastIndexOf('/') + 1).Trim()
                : catalog.Trim();
            if (string.IsNullOrWhiteSpace(leafName))
                leafName = Path.GetFileName(filePath);

            var kindSuffix = kind switch
            {
                TemplateKind.Demo => LocalizationManager.T("Tpl.SuffixDemo"),
                TemplateKind.Empty => LocalizationManager.T("Tpl.SuffixEmpty"),
                _ => ""
            };

            yield return new TemplateInfo
            {
                DisplayName = leafName + kindSuffix + $" ({ext.TrimStart('.').ToUpperInvariant()})",
                FilePath = filePath,
                RelativePath = rel,
                RootFolder = root,
                Vendor = vendor,
                ConfigurationName = string.IsNullOrWhiteSpace(name) ? leafName : name,
                Version = version,
                CatalogPath = catalog.Trim(),
                Destination = destination,
                Kind = kind
            };
        }
    }

    private static string? FirstProp(Dictionary<string, string> props, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (props.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }

    private static TemplateKind ClassifyFromCatalogAndSource(string catalog, string fileName)
    {
        var s = (catalog + " " + fileName).ToLowerInvariant();
        if (s.Contains("demo") || s.Contains("демо") || s.Contains("demonstration") || s.Contains("демонстр"))
            return TemplateKind.Demo;
        if (s.Contains("empty") || s.Contains("пуст") || s.Contains("blank") ||
            s.Contains("чистая") || s.Contains("заготовк") ||
            fileName.Equals("1cv8", StringComparison.OrdinalIgnoreCase) && s.Contains("empty"))
            return TemplateKind.Empty;
        // empty.dt / demo.dt по имени файла
        if (fileName.Contains("demo", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("демо", StringComparison.OrdinalIgnoreCase))
            return TemplateKind.Demo;
        if (fileName.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("пуст", StringComparison.OrdinalIgnoreCase))
            return TemplateKind.Empty;
        return TemplateKind.Configuration;
    }

    /// <summary>Файлы .cf/.dt без *.mft — эвристика по Vendor\Config\Version.</summary>
    private static void ScanOrphanTemplates(
        string root, string current, int depth, int maxDepth,
        List<TemplateInfo> result, HashSet<string> seenFiles)
    {
        if (depth > maxDepth) return;

        // Если в каталоге есть mft — файлы уже учтены через ParseManifest
        try
        {
            if (Directory.EnumerateFiles(current, "*.mft").Any())
            {
                // всё равно обходим подкаталоги (у некоторых поставщиков mft только в листьях)
                foreach (var dir in Directory.EnumerateDirectories(current))
                {
                    var n = Path.GetFileName(dir);
                    if (n.StartsWith(".", StringComparison.Ordinal)) continue;
                    try { ScanOrphanTemplates(root, dir, depth + 1, maxDepth, result, seenFiles); }
                    catch { }
                }
                return;
            }
        }
        catch { return; }

        try
        {
            foreach (var file in Directory.EnumerateFiles(current))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".cf", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".dt", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seenFiles.Add(file)) continue;

                string rel;
                try { rel = Path.GetRelativePath(root, file); }
                catch { rel = file; }

                var parts = rel.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);

                var vendor = parts.Length >= 1 ? parts[0] : "";
                var config = parts.Length >= 2 ? parts[1] : Path.GetFileNameWithoutExtension(file);
                var version = parts.Length >= 3 ? parts[2].Replace('_', '.') : "";
                var kind = ClassifyFromCatalogAndSource(string.Join(" ", parts), Path.GetFileNameWithoutExtension(file));

                // Catalog-подобная иерархия: Поставщик / Конфигурация / Версия / файл
                var catalogParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(vendor)) catalogParts.Add(vendor);
                if (!string.IsNullOrWhiteSpace(config)) catalogParts.Add(config);
                if (!string.IsNullOrWhiteSpace(version)) catalogParts.Add(version);
                var leaf = Path.GetFileNameWithoutExtension(file);
                if (kind == TemplateKind.Demo) leaf += LocalizationManager.T("Template.SuffixDemo");
                else if (kind == TemplateKind.Empty) leaf += LocalizationManager.T("Template.SuffixEmpty");
                else if (!leaf.Equals("1cv8", StringComparison.OrdinalIgnoreCase) &&
                         !leaf.Equals("1Cv8", StringComparison.OrdinalIgnoreCase))
                {
                    // подпапка Exchange и т.п.
                    if (parts.Length > 4)
                        leaf = parts[^2];
                }
                else
                {
                    leaf = kind == TemplateKind.Configuration ? LocalizationManager.T("Tpl.NameConfiguration") :
                           kind == TemplateKind.Demo ? LocalizationManager.T("Tpl.NameDemoBase") : LocalizationManager.T("Tpl.NameEmptyBase");
                }
                catalogParts.Add(leaf);

                var catalog = string.Join("/", catalogParts);
                result.Add(new TemplateInfo
                {
                    DisplayName = leaf + $" ({ext.TrimStart('.').ToUpperInvariant()})",
                    FilePath = file,
                    RelativePath = rel,
                    RootFolder = root,
                    Vendor = vendor,
                    ConfigurationName = config,
                    Version = version,
                    CatalogPath = catalog,
                    Kind = kind
                });
            }
        }
        catch { return; }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                try { ScanOrphanTemplates(root, dir, depth + 1, maxDepth, result, seenFiles); }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Дерево как в стартере 1С: по сегментам Catalog (через «/»).
    /// Под заголовком листа — версия, тип (CF/DT) и путь к файлу.
    /// </summary>
    public static IReadOnlyList<TemplateTreeNode> BuildTemplateTree(IEnumerable<TemplateInfo> templates)
    {
        var root = new TemplateTreeNode { Title = "" };

        foreach (var t in templates
                     .OrderBy(x => x.CatalogPath, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(x => x.Version, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var segments = t.CatalogSegments;
            if (segments.Length == 0)
            {
                segments = new[]
                {
                    string.IsNullOrWhiteSpace(t.Vendor) ? LocalizationManager.T("Tpl.VendorOther") : t.Vendor,
                    string.IsNullOrWhiteSpace(t.ConfigurationName) ? t.DisplayName : t.ConfigurationName
                };
            }

            var node = root;
            for (var i = 0; i < segments.Length; i++)
            {
                var title = segments[i];
                var isLast = i == segments.Length - 1;
                if (isLast)
                {
                    // Лист: одноимённых шаблонов разных версий — отдельные узлы
                    var subtitleParts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(t.Version))
                        subtitleParts.Add("v" + t.Version);
                    subtitleParts.Add(t.Extension);
                    if (!string.IsNullOrWhiteSpace(t.Vendor))
                        subtitleParts.Add(t.Vendor);
                    var subtitle = string.Join(" · ", subtitleParts);
                    if (!string.IsNullOrWhiteSpace(t.FilePath))
                        subtitle += "\n" + t.FilePath;

                    // Если уже есть лист с тем же Title+FilePath — пропуск
                    if (node.Children.Any(c => c.Template is not null &&
                            string.Equals(c.Template.FilePath, t.FilePath, StringComparison.OrdinalIgnoreCase)))
                        break;

                    // Несколько версий одной конфигурации — добавляем версию в заголовок, если дублируется имя
                    var leafTitle = title;
                    if (node.Children.Any(c => c.Template is not null &&
                            string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase)) &&
                        !string.IsNullOrWhiteSpace(t.Version))
                    {
                        leafTitle = $"{title} ({t.Version})";
                    }

                    node.Children.Add(new TemplateTreeNode
                    {
                        Title = leafTitle,
                        Subtitle = subtitle,
                        Template = t
                    });
                }
                else
                {
                    var child = node.Children.FirstOrDefault(c =>
                        c.Template is null &&
                        string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase));
                    if (child is null)
                    {
                        child = new TemplateTreeNode { Title = title };
                        node.Children.Add(child);
                    }
                    node = child;
                }
            }
        }

        // Сортировка детей на каждом уровне
        SortTree(root);
        return root.Children;
    }

    private static void SortTree(TemplateTreeNode node)
    {
        node.Children.Sort((a, b) =>
        {
            // группы раньше листьев? нет — как в 1С, просто по имени; демо в конце группы
            var ca = a.Template?.Kind == TemplateKind.Demo ? 1 : 0;
            var cb = b.Template?.Kind == TemplateKind.Demo ? 1 : 0;
            if (ca != cb) return ca.CompareTo(cb);
            return string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        });
        foreach (var c in node.Children)
            SortTree(c);
    }
}
