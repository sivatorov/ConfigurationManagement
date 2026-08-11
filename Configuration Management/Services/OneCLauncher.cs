using System.Diagnostics;
using System.IO;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Режим запуска платформы 1С.
/// </summary>
public enum OneCLaunchMode
{
    /// <summary>Режим «1С:Предприятие» (клиент).</summary>
    Enterprise,

    /// <summary>Режим «Конфигуратор» (разработка).</summary>
    Configurator
}

/// <summary>
/// Тип клиента 1С:Предприятие.
/// </summary>
public enum OneCClientType
{
    /// <summary>Тонкий клиент (управляемое приложение).</summary>
    Thin,

    /// <summary>Толстый клиент (обычное приложение).</summary>
    Thick
}

/// <summary>
/// Разрядность исполняемого файла платформы 1С.
/// </summary>
public enum OneCArchitecture
{
    /// <summary>32-битная версия.</summary>
    x86,

    /// <summary>64-битная версия.</summary>
    x64
}

/// <summary>
/// Сервис запуска платформы 1С:Предприятие.
/// </summary>
public static class OneCLauncher
{
    /// <summary>
    /// Запускает платформу 1С для указанной информационной базы в заданном режиме.
    /// Тип клиента определяется из режима запуска базы (LaunchMode):
    /// «Автоматический», «Тонкий клиент», «Толстый клиент» или «Веб-клиент».
    /// Разрядность берётся из настройки базы (Architecture), версия — из PlatformVersion.
    /// </summary>
    /// <param name="infobase">Информационная база.</param>
    /// <param name="mode">Режим запуска (Предприятие или Конфигуратор).</param>
    /// <returns>true, если запуск успешно инициирован.</returns>
    public static bool Launch(Infobase infobase, OneCLaunchMode mode)
    {
        // В режиме «Конфигуратор» тип клиента не применяется.
        if (mode == OneCLaunchMode.Configurator)
            return Launch(infobase, mode, OneCClientType.Thin, GetArchitecture(infobase));

        // Веб-клиент запускается через браузер.
        if (string.Equals(infobase.LaunchMode, "Веб-клиент", StringComparison.OrdinalIgnoreCase))
            return LaunchWebClient(infobase);

        // Автоматический режим — платформа сама выбирает клиент (без /RunMode).
        if (string.Equals(infobase.LaunchMode, "Автоматический", StringComparison.OrdinalIgnoreCase))
            return Launch(infobase, mode, null, GetArchitecture(infobase));

        // Толстый клиент.
        if (string.Equals(infobase.LaunchMode, "Толстый клиент", StringComparison.OrdinalIgnoreCase))
            return Launch(infobase, mode, OneCClientType.Thick, GetArchitecture(infobase));

        // По умолчанию — тонкий клиент.
        return Launch(infobase, mode, OneCClientType.Thin, GetArchitecture(infobase));
    }

    /// <summary>
    /// Возвращает разрядность запуска из настройки базы. «64» — 64-битная платформа,
    /// любое другое значение (в т.ч. пустое) — 32-битная.
    /// </summary>
    private static OneCArchitecture GetArchitecture(Infobase infobase)
        => string.Equals(infobase.Architecture, "64", StringComparison.OrdinalIgnoreCase)
            ? OneCArchitecture.x64
            : OneCArchitecture.x86;

    /// <summary>
    /// Запускает платформу 1С для указанной информационной базы с заданным
    /// типом клиента и разрядностью.
    /// </summary>
    /// <param name="infobase">Информационная база.</param>
    /// <param name="mode">Режим запуска (Предприятие или Конфигуратор).</param>
    /// <param name="clientType">Тип клиента (тонкий или толстый). null — автоматический выбор платформой.</param>
    /// <param name="architecture">Разрядность (32 или 64 бита).</param>
    /// <returns>true, если запуск успешно инициирован.</returns>
    public static bool Launch(Infobase infobase, OneCLaunchMode mode, OneCClientType? clientType, OneCArchitecture architecture)
    {
        var exePath = FindExecutable(infobase.PlatformVersion, architecture);
        if (string.IsNullOrEmpty(exePath))
        {
            System.Windows.MessageBox.Show(
                "Не удалось найти платформу 1С.\n" +
                "Убедитесь, что платформа 1С установлена.",
                "Платформа 1С не найдена",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        var arguments = BuildArguments(infobase, mode, clientType);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false
            };
            Process.Start(psi);

            // Обновляем дату последнего запуска базы.
            infobase.LastLaunchDate = DateTime.Now;

            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Не удалось запустить платформу 1С.\n{ex.Message}",
                "Ошибка запуска",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Формирует аргументы командной строки для запуска 1С.
    /// </summary>
    private static string BuildArguments(Infobase infobase, OneCLaunchMode mode, OneCClientType? clientType)
    {
        var modeArg = mode switch
        {
            OneCLaunchMode.Enterprise => "ENTERPRISE",
            _ => "DESIGNER"
        };

        // Параметр типа клиента применяется только в режиме «Предприятие».
        // null — автоматический режим: параметр /RunMode не передаётся,
        // платформа сама выбирает подходящий клиент.
        var clientArg = mode == OneCLaunchMode.Enterprise && clientType.HasValue
            ? clientType.Value switch
            {
                OneCClientType.Thin => " /RunModeManagedApplication",
                _ => " /RunModeOrdinaryApplication"
            }
            : "";

        var conn = infobase.Connection;
        string connectionArg = conn.Type switch
        {
            ConnectionType.File => $" /F \"{conn.FilePath}\"",
            _ => $" /S \"{conn.Server}\\{conn.DatabaseName}\""
        };

        // Если указан логин — запускаем с параметрами аутентификации,
        // иначе — автоматически (аутентификация ОС).
        var authArg = string.IsNullOrWhiteSpace(conn.User)
            ? ""
            : $" /Usr:\"{conn.User}\" /Pwd:\"{conn.Password}\"";

        // Дополнительные параметры запуска, заданные пользователем
        // (например, /UC, /DisableStartupMessages и др.).
        var extraArg = string.IsNullOrWhiteSpace(infobase.LaunchParameters)
            ? ""
            : " " + infobase.LaunchParameters.Trim();

        return $"{modeArg}{clientArg}{connectionArg}{authArg}{extraArg}";
    }

    /// <summary>
    /// Запускает веб-клиент 1С в браузере по умолчанию.
    /// Для клиент-серверной базы формируется адрес http://сервер/имя_базы,
    /// для файловой базы веб-клиент недоступен — выводится предупреждение.
    /// </summary>
    private static bool LaunchWebClient(Infobase infobase)
    {
        var conn = infobase.Connection;
        if (conn.Type != ConnectionType.ClientServer)
        {
            System.Windows.MessageBox.Show(
                "Веб-клиент доступен только для клиент-серверных информационных баз.",
                "Веб-клиент недоступен",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        var url = $"http://{conn.Server}/{conn.DatabaseName}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            infobase.LastLaunchDate = DateTime.Now;
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Не удалось открыть веб-клиент.\n{ex.Message}",
                "Ошибка запуска",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Ищет исполняемый файл платформы 1С нужной разрядности.
    /// Если задана конкретная версия (PlatformVersion) — ищет 1cv8.exe / 1cv8x64.exe
    /// в каталоге именно этой версии. Если версия не задана — ищет сначала стандартный
    /// лаунчер 1CEStart.exe, а затем любой подходящий exe нужной разрядности.
    /// </summary>
    private static string? FindExecutable(string version, OneCArchitecture architecture)
    {
        // Разрядность определяет каталог установки: 64-битные платформы ставятся
        // в Program Files, 32-битные — в Program Files (x86).
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var searchRoots = new List<string>();
        if (architecture == OneCArchitecture.x64)
        {
            if (!string.IsNullOrEmpty(programFiles))
                searchRoots.Add(programFiles);
        }
        else
        {
            if (!string.IsNullOrEmpty(programFilesX86))
                searchRoots.Add(programFilesX86);
            // Если x86-каталога нет (одноразрядная система) — используем Program Files.
            if (searchRoots.Count == 0 && !string.IsNullOrEmpty(programFiles))
                searchRoots.Add(programFiles);
        }

        // В современных версиях (8.3.22+ и 8.5.x) исполняемый файл единый «1cv8.exe»
        // для обеих разрядностей. В более старых 64-битных версиях используется
        // «1cv8x64.exe». Проверяем оба имени.
        var exeNames = architecture == OneCArchitecture.x64
            ? new[] { "1cv8x64.exe", "1cv8.exe" }
            : new[] { "1cv8.exe" };

        // 1. Если задана конкретная версия — ищем exe в её каталоге нужной разрядности.
        if (!string.IsNullOrWhiteSpace(version))
        {
            foreach (var root in searchRoots)
            {
                foreach (var exeName in exeNames)
                {
                    var candidate = Path.Combine(root, "1cv8", version, "bin", exeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        // 2. Иначе пробуем стандартный лаунчер 1CEStart.exe (общий для всех версий).
        //    Лаунчер подбирает версию автоматически, но не позволяет выбрать разрядность.
        foreach (var root in searchRoots)
        {
            var launcherPath = Path.Combine(root, "1cv8", "common", "1CEStart.exe");
            if (File.Exists(launcherPath))
                return launcherPath;
        }

        // 3. Если лаунчер не найден — ищем любой exe нужной разрядности.
        var candidates = new List<string>();
        foreach (var root in searchRoots)
        {
            var baseDir = Path.Combine(root, "1cv8");
            if (!Directory.Exists(baseDir))
                continue;

            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                foreach (var exeName in exeNames)
                {
                    candidates.Add(Path.Combine(dir, "bin", exeName));
                }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}