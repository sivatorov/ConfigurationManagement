using System.IO;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Тип локального кеша платформы 1С.
/// </summary>
[Flags]
public enum OneCCacheKind
{
    /// <summary>Не очищать кеш.</summary>
    None = 0,

    /// <summary>
    /// Программный кеш: %LOCALAPPDATA%\1C\1cv8…
    /// </summary>
    Program = 1,

    /// <summary>
    /// Пользовательский кеш: %APPDATA%\1C\1cv8…
    /// </summary>
    User = 2,

    /// <summary>Программный и пользовательский кеш одновременно.</summary>
    All = Program | User
}

/// <summary>
/// Сервис очистки локального кеша платформы 1С для одной или нескольких информационных баз.
/// </summary>
public static class OneCCacheCleaner
{
    /// <summary>
    /// Очищает программный и пользовательский кеш 1С для указанной информационной базы.
    /// </summary>
    /// <param name="infobase">Информационная база, кеш которой нужно очистить.</param>
    /// <returns>Количество удалённых каталогов кеша.</returns>
    public static int Clear(Infobase infobase)
    {
        return Clear(infobase, OneCCacheKind.All);
    }

    /// <summary>
    /// Очищает кеш указанного типа для одной информационной базы.
    /// </summary>
    /// <param name="infobase">Информационная база, кеш которой нужно очистить.</param>
    /// <param name="kind">Тип очищаемого кеша (программный и/или пользовательский).</param>
    /// <returns>Количество удалённых каталогов кеша.</returns>
    public static int Clear(Infobase infobase, OneCCacheKind kind)
    {
        return Clear(new[] { infobase }, kind);
    }

    /// <summary>
    /// Очищает кеш указанного типа для набора информационных баз.
    /// </summary>
    /// <param name="infobases">Набор информационных баз, кеш которых нужно очистить.</param>
    /// <param name="kind">Тип очищаемого кеша (программный и/или пользовательский).</param>
    /// <returns>Количество удалённых каталогов кеша.</returns>
    public static int Clear(IEnumerable<Infobase> infobases, OneCCacheKind kind)
    {
        if (infobases is null || kind == OneCCacheKind.None)
            return 0;

        var removed = 0;
        foreach (var infobase in infobases)
        {
            if (infobase is null)
                continue;
            removed += ClearSingle(infobase, kind);
        }

        return removed;
    }

    /// <summary>
    /// Очищает кеш 1С указанного типа для конкретной информационной базы.
    /// Кеш хранится в каталогах %LOCALAPPDATA%\1C\1cv8 (программный) и %APPDATA%\1C\1cv8
    /// (пользовательский) в подкаталогах, имя которых соответствует ID базы 1С.
    /// </summary>
    private static int ClearSingle(Infobase infobase, OneCCacheKind kind)
    {
        var removed = 0;

        // Кеш может находиться в нескольких корневых каталогах.
        foreach (var root in GetCacheRoots(kind))
        {
            if (!Directory.Exists(root))
                continue;

            // Если известен ID базы — каталог кеша называется по ID базы.
            if (!string.IsNullOrWhiteSpace(infobase.Id))
            {
                var idDir = Path.Combine(root, infobase.Id);
                if (Directory.Exists(idDir))
                {
                    TryDeleteDirectory(idDir);
                    removed++;
                    continue;
                }

                // ID может храниться в нижнем регистре.
                var idDirLower = Path.Combine(root, infobase.Id.ToLowerInvariant());
                if (Directory.Exists(idDirLower))
                {
                    TryDeleteDirectory(idDirLower);
                    removed++;
                    continue;
                }
            }

            // Если ID неизвестен — ищем каталог по имени базы (для баз, созданных вручную).
            var cacheName = GetCacheName(infobase);
            if (string.IsNullOrWhiteSpace(cacheName))
                continue;

            // Ищем каталоги кеша: как в подкаталогах версий (1cv8\<версия>\<имя>),
            // так и непосредственно в корне (1cv8\<имя>).
            foreach (var versionDir in Directory.GetDirectories(root))
            {
                var cacheDir = Path.Combine(versionDir, cacheName);
                if (Directory.Exists(cacheDir))
                {
                    TryDeleteDirectory(cacheDir);
                    removed++;
                }
            }

            // Прямой каталог кеша в корне (без версии).
            var directCacheDir = Path.Combine(root, cacheName);
            if (Directory.Exists(directCacheDir))
            {
                TryDeleteDirectory(directCacheDir);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Определяет имя каталога кеша для базы.
    /// Для клиент-серверной базы — имя базы на сервере,
    /// для файловой — имя файла базы без расширения.
    /// </summary>
    private static string GetCacheName(Infobase infobase)
    {
        var conn = infobase.Connection;
        return conn.Type switch
        {
            ConnectionType.File => Path.GetFileNameWithoutExtension(conn.FilePath),
            _ => conn.DatabaseName
        };
    }

    /// <summary>
    /// Собирает множество «защищённых» имён каталогов кеша — имён, соответствующих текущим
    /// информационным базам (ID базы и имени каталога кеша). Каталоги с такими именами
    /// не считаются «остатками» от удалённых баз и не подлежат автоматической очистке.
    /// </summary>
    private static HashSet<string> BuildProtectedNames(IEnumerable<Infobase> allBases)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allBases is null)
            return set;

        foreach (var ib in allBases)
        {
            if (ib is null)
                continue;

            if (!string.IsNullOrWhiteSpace(ib.Id))
                set.Add(ib.Id);

            var name = GetCacheName(ib);
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(name);
        }

        return set;
    }

    /// <summary>
    /// Определяет, является ли имя каталога именем версии платформы (например, «8.3.24.1234»).
    /// Каталоги версий не являются каталогами кеша отдельных баз — внутри них хранятся кеши.
    /// </summary>
    private static bool IsVersionDirName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        foreach (var c in name)
        {
            if (!char.IsDigit(c) && c != '.' && c != '-')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Перечисляет каталоги кеша, не принадлежащие ни одной текущей информационной базе.
    /// Это «остатки» от удалённых из списка или созданных вне приложения баз: каталоги,
    /// имя которых не совпадает ни с одним ID базы и ни с одним именем каталога кеша
    /// текущих баз. Каталоги версий платформы (например, «8.3.24.1234») не удаляются —
    /// они анализируются, и удаляются только их вложенные каталоги-кеши.
    /// </summary>
    private static IEnumerable<string> EnumerateOrphanDirectories(OneCCacheKind kind, IEnumerable<Infobase> allBases)
    {
        var protectedNames = BuildProtectedNames(allBases);

        foreach (var root in GetCacheRoots(kind, forOrphanScan: true))
        {
            if (!Directory.Exists(root))
                continue;

            string[] versionDirs;
            try { versionDirs = Directory.GetDirectories(root); }
            catch { continue; }

            foreach (var versionDir in versionDirs)
            {
                var versionName = Path.GetFileName(versionDir);

                if (IsVersionDirName(versionName))
                {
                    // Внутри каталога версии находятся каталоги кеша отдельных баз.
                    string[] cacheDirs;
                    try { cacheDirs = Directory.GetDirectories(versionDir); }
                    catch { continue; }

                    foreach (var cd in cacheDirs)
                    {
                        var n = Path.GetFileName(cd);
                        if (!protectedNames.Contains(n))
                            yield return cd;
                    }
                }
                else if (!protectedNames.Contains(versionName))
                {
                    // Прямой каталог кеша в корне (без версии).
                    yield return versionDir;
                }
            }
        }
    }

    /// <summary>
    /// Вычисляет суммарный размер «остатков» кеша от удалённых баз — каталогов кеша,
    /// не принадлежащих ни одной текущей информационной базе.
    /// </summary>
    /// <param name="kind">Тип кеша (программный и/или пользовательский).</param>
    /// <param name="allBases">Все текущие информационные базы (для определения «защищённых» имён).</param>
    /// <returns>Суммарный размер в байтах.</returns>
    public static long GetOrphanSize(OneCCacheKind kind, IEnumerable<Infobase> allBases)
    {
        long total = 0;
        foreach (var dir in EnumerateOrphanDirectories(kind, allBases))
            total += GetDirectorySize(dir);
        return total;
    }

    /// <summary>
    /// Удаляет «остатки» кеша от удалённых баз — каталоги кеша, не принадлежащие ни одной
    /// текущей информационной базе.
    /// </summary>
    /// <param name="kind">Тип кеша (программный и/или пользовательский).</param>
    /// <param name="allBases">Все текущие информационные базы (для определения «защищённых» имён).</param>
    /// <returns>Количество удалённых каталогов кеша.</returns>
    public static int ClearOrphans(OneCCacheKind kind, IEnumerable<Infobase> allBases)
    {
        if (kind == OneCCacheKind.None)
            return 0;

        var removed = 0;
        foreach (var dir in EnumerateOrphanDirectories(kind, allBases))
        {
            TryDeleteDirectory(dir);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Вычисляет суммарный размер кеша указанного типа (программного и/или пользовательского)
    /// во всех корневых каталогах. Размер вычисляется без учёта размера самой файловой системы
    /// и может быть неточным, если файлы кеша заняты запущенной 1С.
    /// </summary>
    /// <param name="kind">Тип кеша (программный и/или пользовательский).</param>
    /// <returns>Суммарный размер в байтах.</returns>
    public static long GetSize(OneCCacheKind kind)
    {
        long total = 0;
        if (kind == OneCCacheKind.None)
            return total;

        foreach (var root in GetCacheRoots(kind))
        {
            if (!Directory.Exists(root))
                continue;
            total += GetDirectorySize(root);
        }

        return total;
    }

    /// <summary>
    /// Вычисляет суммарный размер кеша указанного типа для одной информационной базы.
    /// Учитываются только реально существующие каталоги кеша этой базы.
    /// </summary>
    /// <param name="infobase">Информационная база, для которой вычисляется размер кеша.</param>
    /// <param name="kind">Тип кеша (программный и/или пользовательский).</param>
    /// <returns>Суммарный размер кеша базы в байтах.</returns>
    public static long GetSize(Infobase infobase, OneCCacheKind kind)
    {
        if (infobase is null || kind == OneCCacheKind.None)
            return 0;

        long total = 0;
        foreach (var dir in EnumerateCacheDirectories(infobase, kind))
            total += GetDirectorySize(dir);

        return total;
    }

    /// <summary>
    /// Перечисляет реально существующие каталоги кеша указанной информационной базы
    /// для заданного типа кеша. Логика поиска соответствует <see cref="ClearSingle"/>:
    /// каталог по ID базы, по ID в нижнем регистре, затем по имени базы (в подкаталогах
    /// версий и в корне каталога кеша).
    /// </summary>
    private static IEnumerable<string> EnumerateCacheDirectories(Infobase infobase, OneCCacheKind kind)
    {
        foreach (var root in GetCacheRoots(kind))
        {
            if (!Directory.Exists(root))
                continue;

            // Если известен ID базы — каталог кеша называется по ID базы.
            if (!string.IsNullOrWhiteSpace(infobase.Id))
            {
                var idDir = Path.Combine(root, infobase.Id);
                if (Directory.Exists(idDir))
                {
                    yield return idDir;
                    continue;
                }

                // ID может храниться в нижнем регистре.
                var idDirLower = Path.Combine(root, infobase.Id.ToLowerInvariant());
                if (Directory.Exists(idDirLower))
                {
                    yield return idDirLower;
                    continue;
                }
            }

            // Если ID неизвестен — ищем каталог по имени базы.
            var cacheName = GetCacheName(infobase);
            if (string.IsNullOrWhiteSpace(cacheName))
                continue;

            // Каталоги кеша в подкаталогах версий (1cv8\<версия>\<имя>).
            foreach (var versionDir in Directory.GetDirectories(root))
            {
                var cacheDir = Path.Combine(versionDir, cacheName);
                if (Directory.Exists(cacheDir))
                    yield return cacheDir;
            }

            // Прямой каталог кеша в корне (без версии).
            var directCacheDir = Path.Combine(root, cacheName);
            if (Directory.Exists(directCacheDir))
                yield return directCacheDir;
        }
    }

    /// <summary>
    /// Рекурсивно вычисляет суммарный размер всех файлов в каталоге (в байтах).
    /// Ошибки доступа к отдельным файлам игнорируются.
    /// </summary>
    private static long GetDirectorySize(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Игнорируем недоступные файлы (могут быть заняты запущенной 1С).
                }
            }
        }
        catch
        {
            // Игнорируем ошибки перечисления (каталог может исчезнуть или быть недоступен).
        }

        return total;
    }

    /// <summary>
    /// Возвращает корневые каталоги, где 1С хранит кеш, с учётом выбранного типа кеша.
    /// </summary>
    /// <param name="forOrphanScan">
    /// True, если корни запрашиваются для поиска «осиротевшего» кеша. В этом режиме
    /// удаляется всё, что не принадлежит известным базам, поэтому корни, где рядом
    /// с кешем баз лежат служебные каталоги платформы, в него не отдаются.
    /// </param>
    private static IEnumerable<string> GetCacheRoots(OneCCacheKind kind, bool forOrphanScan = false)
    {
        var roots = new List<string>();

#if LINUX
        // Linux: пользовательский кеш баз платформа держит в ~/.1cv8/1C/1cv8,
        // каталоги баз по GUID лежат прямо в нём (проверено на 8.3.27).
        // ~/.cache/1cv8 и ~/.local/share/1cv8 это данные встроенного браузера,
        // а не кеш баз; оставлены как были, вместе с прежним ~/.1cv8/1cv8
        // на случай других раскладок дистрибутива.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (kind.HasFlag(OneCCacheKind.Program))
        {
            var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (!string.IsNullOrWhiteSpace(xdgCache))
                roots.Add(Path.Combine(xdgCache, "1cv8"));
            else if (!string.IsNullOrEmpty(home))
                roots.Add(Path.Combine(home, ".cache", "1cv8"));
        }

        if (kind.HasFlag(OneCCacheKind.User))
        {
            var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgData))
                roots.Add(Path.Combine(xdgData, "1cv8"));
            else if (!string.IsNullOrEmpty(home))
                roots.Add(Path.Combine(home, ".local", "share", "1cv8"));
            // Общая для всех версий каталог кэша в профиле 1С.
            if (!string.IsNullOrEmpty(home))
            {
                // В ~/.1cv8/1C/1cv8 рядом с кешем баз платформа держит свои служебные
                // каталоги (conf, logs, ExtCompT, STT, standalone-server), а каталоги
                // кеша серверных баз называются Srvr__<сервер>__Ref__<база>__, то есть
                // не совпадают с именами из BuildProtectedNames. Поиск осиротевшего кеша
                // принял бы всё это за остатки удалённых баз, поэтому корень отдаётся
                // только для точечной очистки по конкретной базе.
                if (!forOrphanScan)
                    roots.Add(Path.Combine(home, ".1cv8", "1C", "1cv8"));
                roots.Add(Path.Combine(home, ".1cv8", "1cv8"));
            }
        }
#else
        // Программный кеш — %LOCALAPPDATA%\1C\1cv8.
        if (kind.HasFlag(OneCCacheKind.Program))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
                roots.Add(Path.Combine(localAppData, "1C", "1cv8"));
        }

        // Пользовательский кеш — %APPDATA%\1C\1cv8.
        if (kind.HasFlag(OneCCacheKind.User))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                roots.Add(Path.Combine(appData, "1C", "1cv8"));
        }
#endif

        return roots;
    }

    /// <summary>
    /// Удаляет каталог кеша. Чтобы не блокировать интерфейс при удалении
    /// большого количества файлов, каталог сначала переименовывается во временное
    /// имя (мгновенная операция), а затем удаляется асинхронно в фоновом потоке.
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            // Переименовываем каталог во временное имя — это мгновенная операция,
            // не зависящая от количества файлов внутри.
            var tempPath = path + ".deleting_" + Guid.NewGuid().ToString("N");
            Directory.Move(path, tempPath);

            // Удаляем переименованный каталог в фоновом потоке.
            Task.Run(() =>
            {
                try
                {
                    Directory.Delete(tempPath, recursive: true);
                }
                catch
                {
                    // Игнорируем ошибки удаления (файлы могут быть заняты запущенной 1С).
                }
            });
        }
        catch
        {
            // Игнорируем ошибки (каталог может быть занят запущенной 1С).
        }
    }
}