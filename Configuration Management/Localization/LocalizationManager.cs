using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Configuration_Management.Localization;

/// <summary>
/// Описание доступного языка (код, отображаемое имя, источник).
/// </summary>
public sealed class LanguageInfo
{
    /// <summary>Код языка, например "ru", "en", "de".</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Отображаемое имя языка на его собственном языке, например "Русский".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Является ли язык встроенным (поставляется вместе с приложением).</summary>
    public bool IsBuiltIn { get; init; }

    public override string ToString() => string.IsNullOrEmpty(Name) ? Code : $"{Name} ({Code})";
}

/// <summary>
/// Структура JSON-файла языка.
/// </summary>
internal sealed class LanguageFile
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Strings { get; set; } = new();
}

/// <summary>
/// Центральный менеджер локализации приложения.
///
/// Поддерживает несколько языков одновременно:
///  * Встроенные языки (ru, en) загружаются из ресурсов сборки.
///  * Дополнительные языки загружаются из файлов <c>*.json</c>, расположенных в
///    папке <c>Languages</c> рядом с исполняемым файлом и/или в каталоге данных
///    приложения. Это позволяет добавлять новые языки без пересборки.
///
/// Формат JSON-файла языка:
/// <code>
/// {
///   "code": "de",
///   "name": "Deutsch",
///   "strings": {
///     "MainWindow.Title": "Verwaltung von 1C-Konfigurationen",
///     ...
///   }
/// }
/// </code>
///
/// Если для текущего языка нет перевода какого-то ключа, выполняется откат
/// (fallback): сначала английский, затем русский, затем сам ключ. Поэтому
/// приложение продолжает работать, даже если какой-то ключ не переведён.
/// </summary>
public sealed class LocalizationManager
{
    /// <summary>Единственный экземпляр менеджера (приложение использует один язык).</summary>
    public static LocalizationManager Instance { get; } = new();

    /// <summary>Источник локализации для привязок XAML (bindings) и INotifyPropertyChanged.</summary>
    public LocalizationSource Source { get; }

    /// <summary>Событие возникает при смене языка.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>Код текущего языка, например "ru".</summary>
    public string CurrentLanguage { get; private set; } = "ru";

    /// <summary>Отображаемое имя текущего языка.</summary>
    public string CurrentLanguageName =>
        AvailableLanguages.FirstOrDefault(l => l.Code == CurrentLanguage)?.Name ?? CurrentLanguage;

    /// <summary>Список всех доступных языков (встроенные + внешние).</summary>
    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; private set; } =
        new List<LanguageInfo>();

    // code -> переводы
    private readonly Dictionary<string, LanguageFile> _languages = new();
    private Dictionary<string, string> _current = new();
    private bool _initialized;

    private const string BuiltInRussian = "ru";
    private const string BuiltInEnglish = "en";

    // Префикс логического имени встроенных ресурсов (задаётся в .csproj).
    private const string ResourcePrefix = "cm_lang_";

    private LocalizationManager()
    {
        Source = new LocalizationSource(this);
    }

    /// <summary>
    /// Инициализирует менеджер: загружает встроенные и внешние языки,
    /// затем выбирает предпочтительный язык.
    /// </summary>
    /// <param name="preferredLanguage">
    /// Код желаемого языка. Если null/пусто/не найден — выбирается язык системы
    /// (если он есть среди доступных) или по умолчанию русский.
    /// </param>
    /// <param name="dataDirectory">Каталог данных приложения для поиска внешних языков.</param>
    public void Initialize(string? preferredLanguage = null, string? dataDirectory = null)
    {
        if (_initialized)
            return;

        _languages.Clear();

        // 1) Встроенные языки.
        LoadBuiltInLanguages();

        // 2) Внешние языки из папок Languages/.
        LoadExternalLanguages(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
            LoadExternalLanguages(dataDirectory);

        RebuildAvailableLanguages();

        // 3) Выбор языка.
        string selected = ResolveLanguage(preferredLanguage);
        SetLanguage(selected);

#if DEBUG
        // [DEBUG] Диагностика локализации: какие языки загружены и какой выбран.
        Console.Error.WriteLine(
            "[l10n-debug] Initialize: preferred=" + (preferredLanguage ?? "null") +
            ", dataDir=" + (dataDirectory ?? "null") +
            ", selected=" + selected +
            ", loaded=[" + string.Join(",", _languages.Keys) + "]");
#endif

        _initialized = true;
    }

    /// <summary>
    /// Сбрасывает состояние (используется в основном в тестах). Повторная
    /// инициализация становится возможной.
    /// </summary>
    public void Reset()
    {
        _initialized = false;
        _languages.Clear();
        _current = new Dictionary<string, string>();
        RebuildAvailableLanguages();
    }

    /// <summary>Устанавливает активный язык и уведомляет подписчиков.</summary>
    public void SetLanguage(string? code)
    {
#if DEBUG
        Console.Error.WriteLine("[l10n-debug] SetLanguage(requested=" + (code ?? "null") + ", current=" + CurrentLanguage + ")");
#endif

        if (code is null || !_languages.TryGetValue(code, out var lang))
            lang = _languages.TryGetValue(BuiltInRussian, out var ru) ? ru : _languages.Values.FirstOrDefault();

        if (lang is null)
            return;

        CurrentLanguage = lang.Code;
        _current = lang.Strings;

        ApplyCulture(lang.Code);

        LanguageChanged?.Invoke(this, EventArgs.Empty);
        Source.NotifyAll();
    }

    /// <summary>
    /// Возвращает перевод ключа для текущего языка.
    /// Откат: текущий язык → встроенный английский → встроенный русский → сам ключ.
    /// Фолбэк идёт по ВСТРОЕННЫМ словарям (issue #357): внешний файл языка (Languages/
    /// рядом с exe или в каталоге данных) с кодом «ru»/«en» ПОЛНОСТЬЮ переопределяет
    /// встроенный словарь — в устаревшем внешнем файле нет ключей новых окон, и без
    /// встроенного фолбэка интерфейс показывал ключи локализации вместо текстов.
    /// </summary>
    public string Translate(string key)
        => TranslateFromDictionaries(_current, _builtInEnglishStrings, _builtInRussianStrings, key);

    /// <summary>
    /// Чистое ядро перевода (для юнит-тестов, issue #357): текущий словарь →
    /// встроенный английский → встроенный русский → сам ключ.
    /// </summary>
    internal static string TranslateFromDictionaries(
        IReadOnlyDictionary<string, string>? current,
        IReadOnlyDictionary<string, string>? builtInEnglish,
        IReadOnlyDictionary<string, string>? builtInRussian,
        string key)
    {
        if (string.IsNullOrEmpty(key))
            return key;

        if (current is not null && current.TryGetValue(key, out var value))
            return value;

        if (builtInEnglish is not null &&
            builtInEnglish.TryGetValue(key, out var enValue))
            return enValue;

        if (builtInRussian is not null &&
            builtInRussian.TryGetValue(key, out var ruValue))
            return ruValue;

        return key;
    }

    /// <summary>
    /// Удобный статический доступ: <c>LocalizationManager.T("Key")</c>.
    /// Используется в коде (код-бихайнды, окна, построенные в коде) и в
    /// сообщениях об ошибках.
    /// </summary>
    public static string T(string key) => Instance.Translate(key);

    // ------------------------------------------------------------------
    //  Загрузка
    // ------------------------------------------------------------------

    /// Встроенные словари для фолбэка перевода (issue #357): внешние файлы языков с
    /// тем же кодом переопределяют текущий словарь целиком — без отдельной ссылки на
    /// встроенные ru/en ключи новых версий в устаревшем внешнем файле показывались бы
    /// «как есть». Заполняется в <see cref="LoadBuiltInLanguages"/>; null — встроенный
    /// язык не найден в ресурсах сборки.
    /// </summary>
    private IReadOnlyDictionary<string, string>? _builtInEnglishStrings;
    private IReadOnlyDictionary<string, string>? _builtInRussianStrings;

    private void LoadBuiltInLanguages()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                continue;

            TryAddFromStream(stream, name);
            if (_languages.TryGetValue(BuiltInEnglish, out var en))
                _builtInEnglishStrings = en.Strings;
            if (_languages.TryGetValue(BuiltInRussian, out var ru))
                _builtInRussianStrings = ru.Strings;
        }
    }

    private void LoadExternalLanguages(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return;

        var languagesDir = Path.Combine(rootDirectory, "Languages");
        if (!Directory.Exists(languagesDir))
            return;

        foreach (var file in Directory.EnumerateFiles(languagesDir, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                TryAddFromStream(stream, Path.GetFileName(file), isBuiltIn: false);
            }
            catch (IOException) { /* файл занят другим процессом */ }
            catch (UnauthorizedAccessException) { /* нет прав на чтение */ }
            catch (JsonException) { /* некорректный JSON */ }
        }
    }

    private void TryAddFromStream(Stream stream, string sourceName, bool isBuiltIn = true)
    {
        LanguageFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LanguageFile>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return;
        }

        if (file is null || string.IsNullOrWhiteSpace(file.Code))
            return;

        file.Name = string.IsNullOrWhiteSpace(file.Name) ? file.Code : file.Name;

        // Внешний язык с тем же кодом переопределяет встроенный.
        if (_languages.TryGetValue(file.Code, out var existing))
        {
            if (!isBuiltIn)
                _languages[file.Code] = file;
            return;
        }

        _languages[file.Code] = file;
    }

    private void RebuildAvailableLanguages()
    {
        AvailableLanguages = _languages
            .OrderByDescending(kv => kv.Key == BuiltInRussian)
            .ThenByDescending(kv => kv.Key == BuiltInEnglish)
            .ThenBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new LanguageInfo
            {
                Code = kv.Key,
                Name = kv.Value.Name,
                IsBuiltIn = kv.Key is BuiltInRussian or BuiltInEnglish,
            })
            .ToList();
    }

    /// <summary>
    /// Применяет предпочтительный язык по тем же правилам, что и первичная
    /// инициализация: пустое значение означает язык системы, а не «оставить как
    /// есть». Нужно там, где словарь уже поднят раньше, чем прочитаны настройки.
    /// </summary>
    /// <param name="preferred">Сохранённый язык или пусто для автоматического выбора.</param>
    public void ApplyPreferredLanguage(string? preferred) => SetLanguage(ResolveLanguage(preferred));

    /// <summary>
    /// Язык интерфейса системы, снятый до первой смены культуры. Нужен именно
    /// снимок: <see cref="SetLanguage"/> перезаписывает CurrentUICulture, и после
    /// первого переключения автоматический выбор указывал бы на уже выбранный
    /// язык, а не на системный.
    /// </summary>
    private static readonly string OsUiLanguage = ReadOsUiLanguage();

    private static string ReadOsUiLanguage()
    {
        try { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName; }
        catch (CultureNotFoundException) { return string.Empty; }
    }

    private string ResolveLanguage(string? preferred)
    {
        // 1) Предпочтительный (сохранённый пользователем) язык.
        if (!string.IsNullOrWhiteSpace(preferred) && _languages.ContainsKey(preferred!))
            return preferred!;

        // 2) Язык операционной системы, если он есть среди доступных.
        if (!string.IsNullOrEmpty(OsUiLanguage) && _languages.ContainsKey(OsUiLanguage))
            return OsUiLanguage;

        // 3) По умолчанию русский (основной язык приложения).
        return _languages.ContainsKey(BuiltInRussian) ? BuiltInRussian : _languages.Keys.First();
    }

    private static void ApplyCulture(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Код не является валидной культурой — пропускаем, приложение продолжит работу.
        }
    }
}