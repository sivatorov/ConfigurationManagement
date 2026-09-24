using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// ViewModel для диалога настройки подключения к информационной базе.
/// </summary>
public class ConnectionSettingsViewModel : ViewModelBase
{
    private bool _isLoading;
    private bool _hasChanges;

    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _group = string.Empty;
    private string _description = string.Empty;
    private string _configurationName = string.Empty;
    private string _configurationVersion = string.Empty;
    private string _platformVersion = string.Empty;
    private string _architecture = "32-priority";
    private string _launchMode = "Автоматический";
    private string _launchParameters = string.Empty;
    private ConnectionType _connectionType = ConnectionType.ClientServer;
    private string _server = string.Empty;
    private string _databaseName = string.Empty;
    private string _filePath = string.Empty;
    private string _webUrl = string.Empty;
    private string _user = string.Empty;
    private string _password = string.Empty;
    private AuthenticationMode _authenticationMode = AuthenticationMode.Prompt;
    private int _port = 1541;
    private Group? _selectedGroup;
    private string _connectionString = string.Empty;
    private string _repositoryServer = string.Empty;
    private string _repositoryName = string.Empty;
    private string _repositoryUser = string.Empty;
    private string _repositoryPassword = string.Empty;
    private AuthenticationMode _configuratorAuthenticationMode = AuthenticationMode.Prompt;
    private string _configuratorUser = string.Empty;
    private string _configuratorPassword = string.Empty;
    private bool _configuratorUseEnterpriseAuth;
    private string _defaultLaunchMode = string.Empty;
    private string _doubleClickAction = Configuration_Management.Models.DoubleClickAction.Default;
    private string _externalProcessingPath = string.Empty;
    private string _externalProcessingData = string.Empty;
    private string _tagInput = string.Empty;
    private IReadOnlyList<string> _availableTags = Array.Empty<string>();

    /// <summary>
    /// Создаёт ViewModel с указанным списком доступных групп.
    /// </summary>
    public ConnectionSettingsViewModel(IEnumerable<Group>? groups = null)
    {
        Groups = new ObservableCollection<Group>(groups ?? new List<Group>());
        InstalledPlatformVersions = new ObservableCollection<string>();
        PropertyChanged += OnPropertyChanged;
    }

    /// <summary>
    /// Признак того, что в настройки были внесены изменения.
    /// </summary>
    public bool HasChanges => _hasChanges;

    /// <summary>
    /// Обработчик изменения свойств: помечает наличие изменений.
    /// </summary>
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoading || e.PropertyName == nameof(HasChanges))
            return;

        _hasChanges = true;
        OnPropertyChanged(nameof(HasChanges));
    }

    /// <summary>Наименование базы.</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>Идентификатор базы 1С (GUID из ibases.v8i).</summary>
    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    /// <summary>Список доступных групп.</summary>
    public ObservableCollection<Group> Groups { get; }

    /// <summary>Выбранная группа.</summary>
    public Group? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                // В свойстве Group храним полный путь группы в иерархии
                // (например, «Учёт / Бухгалтерия»), чтобы сохранялась структура.
                Group = value is null
                    ? string.Empty
                    : GroupHierarchyHelper.GetFullPath(value, Groups);
                OnPropertyChanged(nameof(GroupDisplayPath));
            }
        }
    }

    /// <summary>Группа базы (полный путь в иерархии).</summary>
    public string Group
    {
        get => _group;
        set
        {
            if (SetProperty(ref _group, value))
                OnPropertyChanged(nameof(GroupDisplayPath));
        }
    }

    /// <summary>Текст для поля группы: путь или «Без группы».</summary>
    public string GroupDisplayPath =>
        string.IsNullOrWhiteSpace(_group) ? LocalizationManager.T("Conn.GroupNoGroup") : _group;

    /// <summary>Теги базы (редактируются в окне свойств, issue #283).</summary>
    public ObservableCollection<string> Tags { get; } = new();

    /// <summary>Существующие теги всех баз для автодополнения при добавлении (issue #283).</summary>
    public IReadOnlyList<string> AvailableTags
    {
        get => _availableTags;
        private set => SetProperty(ref _availableTags, value ?? Array.Empty<string>());
    }

    /// <summary>Текст в поле ввода нового тега (issue #283).</summary>
    public string TagInput
    {
        get => _tagInput;
        set => SetProperty(ref _tagInput, value);
    }

    /// <summary>
    /// Задаёт список доступных тегов для автодополнения (сортировка по алфавиту,
    /// без дублей, регистронезависимо).
    /// </summary>
    public void SetAvailableTags(IEnumerable<string>? availableTags)
    {
        AvailableTags = availableTags?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
    }

    /// <summary>Добавляет тег из поля ввода (без дублей, регистронезависимо).</summary>
    public void AddTag()
    {
        var tag = (TagInput ?? string.Empty).Trim();
        if (tag.Length == 0)
        {
            TagInput = string.Empty;
            return;
        }
        if (!Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            Tags.Add(tag);
            // Новый тег сразу попадает и в список автодополнения: иначе в том же окне
            // его нельзя было выбрать из раскрытого списка (issue #283).
            AddToAvailableTags(tag);
        }
        TagInput = string.Empty;
    }

    /// <summary>
    /// Добавляет тег в список доступных для автодополнения (без дублей,
    /// регистронезависимо, с сохранением сортировки по алфавиту).
    /// </summary>
    private void AddToAvailableTags(string tag)
    {
        if (_availableTags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            return;

        var updated = _availableTags
            .Append(tag)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AvailableTags = updated;
    }

    /// <summary>Удаляет тег (регистронезависимо).</summary>
    public void RemoveTag(string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return;
        var item = Tags.FirstOrDefault(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            Tags.Remove(item);
    }

    /// <summary>
    /// Находит группу по полному пути (например, «Учёт / Бухгалтерия»).
    /// </summary>
    private Group? FindGroupByPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return null;

        return GroupHierarchyHelper.FindByFullPath(fullPath, Groups);
    }

    /// <summary>Описание базы.</summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    /// <summary>Версия платформы.</summary>
    public string PlatformVersion
    {
        get => _platformVersion;
        set => SetProperty(ref _platformVersion, value);
    }
    public string ConfigurationName
    {
        get => _configurationName;
        set => SetProperty(ref _configurationName, value ?? string.Empty);
    }

    public string ConfigurationVersion
    {
        get => _configurationVersion;
        set => SetProperty(ref _configurationVersion, value ?? string.Empty);
    }



    /// <summary>
    /// Разрядность запуска клиента (как в 1С:Предприятие):
    /// «32», «64», «32-priority» (по умолчанию в 1С), «64-priority».
    /// </summary>
    public string Architecture
    {
        get => _architecture;
        set
        {
            if (SetProperty(ref _architecture, NormalizeArchitecture(value)))
            {
                OnPropertyChanged(nameof(IsArchitecture32));
                OnPropertyChanged(nameof(IsArchitecture64));
                OnPropertyChanged(nameof(IsArchitecture32Priority));
                OnPropertyChanged(nameof(IsArchitecture64Priority));
                OnPropertyChanged(nameof(ArchitectureHint));
            }
        }
    }

    /// <summary>Всегда 32 (x86).</summary>
    public bool IsArchitecture32
    {
        get => Architecture == "32";
        set { if (value) Architecture = "32"; }
    }

    /// <summary>Всегда 64 (x86-64).</summary>
    public bool IsArchitecture64
    {
        get => Architecture == "64";
        set { if (value) Architecture = "64"; }
    }

    /// <summary>Приоритет 32 (x86) — режим по умолчанию в 1С.</summary>
    public bool IsArchitecture32Priority
    {
        get => Architecture == "32-priority";
        set { if (value) Architecture = "32-priority"; }
    }

    /// <summary>Приоритет 64 (x86-64).</summary>
    public bool IsArchitecture64Priority
    {
        get => Architecture == "64-priority";
        set { if (value) Architecture = "64-priority"; }
    }

    /// <summary>ОС 64-битная (иначе 64-клиент недоступен).</summary>
    public bool IsOs64Bit { get; } = Environment.Is64BitOperatingSystem;

    /// <summary>Краткая подсказка по выбранному режиму разрядности.</summary>
    public string ArchitectureHint => Architecture switch
    {
        "32" => LocalizationManager.T("Conn.ArchHint32"),
        "64" => LocalizationManager.T("Conn.ArchHint64"),
        "64-priority" => LocalizationManager.T("Conn.ArchHint64Priority"),
        _ => LocalizationManager.T("Conn.ArchHint32Priority")
    };

    /// <summary>Нормализация значения разрядности (совместимость со старыми «32»/«64»).</summary>
    public static string NormalizeArchitecture(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v switch
        {
            "64" or "x64" or "x86-64" or "x86_64" => "64",
            "32" or "x86" => "32",
            "64-priority" or "priority64" or "x86-64-priority" => "64-priority",
            "32-priority" or "priority32" or "x86-priority" or "" => "32-priority",
            _ => "32-priority"
        };
    }

    /// <summary>Список установленных версий платформы 1С для выбора.</summary>
    public ObservableCollection<string> InstalledPlatformVersions { get; }

    /// <summary>
    /// Устанавливает список установленных версий платформы 1С.
    /// </summary>
    public void SetInstalledPlatformVersions(IEnumerable<string> versions)
    {
        InstalledPlatformVersions.Clear();
        foreach (var version in versions)
        {
            InstalledPlatformVersions.Add(version);
        }
    }

    /// <summary>
    /// Список доступных серверов 1С (из клиент-серверных баз в списке) для выпадающего списка.
    /// </summary>
    public ObservableCollection<string> AvailableServers { get; } = new();

    /// <summary>
    /// Устанавливает список доступных серверов 1С из других баз списка.
    /// Сортируем по алфавиту и исключаем пустые значения.
    /// </summary>
    public void SetAvailableServers(IEnumerable<string>? servers)
    {
        AvailableServers.Clear();
        if (servers is null)
            return;

        foreach (var server in servers
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Select(s => s.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            AvailableServers.Add(server);
        }
    }

    /// <summary>
    /// Список доступных серверов хранилища конфигурации (из настроек хранилища других баз)
    /// для выпадающего списка поля «Сервер хранилища» (issue #140).
    /// </summary>
    public ObservableCollection<string> AvailableRepositoryServers { get; } = new();

    /// <summary>
    /// Устанавливает список доступных серверов хранилища конфигурации.
    /// Сортируем по алфавиту и исключаем пустые значения.
    /// </summary>
    public void SetAvailableRepositoryServers(IEnumerable<string>? servers)
    {
        AvailableRepositoryServers.Clear();
        if (servers is null)
            return;

        foreach (var server in servers
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Select(s => s.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            AvailableRepositoryServers.Add(server);
        }
    }

    /// <summary>
    /// Разделяет единое поле подключения к хранилищу, скопированное из 1С
    /// (например «tcp://server:1542/ИмяХранилища»), на адрес сервера и имя хранилища (issue #140).
    /// </summary>
    public void SplitRepositoryConnectionString(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
            return;

        // Убираем возможный префикс схемы «tcp://», «file://» и т.п. до «://».
        var body = text;
        var schemeIdx = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
            body = text[(schemeIdx + 3)..];

        // Разделяем на «сервер/имя» по первому слэшу.
        var slashIdx = body.IndexOf('/');
        if (slashIdx < 0)
        {
            RepositoryServer = body.Trim();
            RepositoryName = string.Empty;
        }
        else
        {
            RepositoryServer = body[..slashIdx].Trim();
            RepositoryName = body[(slashIdx + 1)..].Trim();
        }
    }

    /// <summary>
    /// Список доступных портов серверов 1С (из клиент-серверных баз в списке) для выпадающего списка.
    /// </summary>
    public ObservableCollection<string> AvailablePorts { get; } = new();

    /// <summary>
    /// Устанавливает список доступных портов серверов 1С из других баз списка.
    /// Сортируем по возрастанию и исключаем пустые/нулевые значения.
    /// </summary>
    public void SetAvailablePorts(IEnumerable<int>? ports)
    {
        AvailablePorts.Clear();
        if (ports is null)
            return;

        foreach (var port in ports
                     .Where(p => p > 0)
                     .Distinct()
                     .OrderBy(p => p))
        {
            AvailablePorts.Add(port.ToString());
        }
    }

    /// <summary>Режим запуска (строка: Автоматический, Тонкий клиент, Толстый клиент, Веб-клиент).</summary>
    public string LaunchMode
    {
        get => _launchMode;
        set
        {
            if (SetProperty(ref _launchMode, value))
            {
                OnPropertyChanged(nameof(IsAutoMode));
                OnPropertyChanged(nameof(IsThinClient));
                OnPropertyChanged(nameof(IsThickClient));
                OnPropertyChanged(nameof(IsThickOrdinaryClient));
                OnPropertyChanged(nameof(IsWebClient));
                OnPropertyChanged(nameof(LaunchModeHint));
            }
        }
    }

    /// <summary>Автоматический режим запуска.</summary>
    public bool IsAutoMode
    {
        get => LaunchMode == "Автоматический";
        set { if (value) LaunchMode = "Автоматический"; }
    }

    /// <summary>Тонкий клиент.</summary>
    public bool IsThinClient
    {
        get => LaunchMode == "Тонкий клиент";
        set { if (value) LaunchMode = "Тонкий клиент"; }
    }

    /// <summary>Толстый клиент (управляемые формы).</summary>
    public bool IsThickClient
    {
        get => LaunchMode == "Толстый клиент";
        set { if (value) LaunchMode = "Толстый клиент"; }
    }

    /// <summary>Толстый клиент (обычные формы).</summary>
    public bool IsThickOrdinaryClient
    {
        get => LaunchMode == "Толстый клиент (обычные формы)";
        set { if (value) LaunchMode = "Толстый клиент (обычные формы)"; }
    }

    /// <summary>Веб-клиент.</summary>
    public bool IsWebClient
    {
        get => LaunchMode == "Веб-клиент";
        set { if (value) LaunchMode = "Веб-клиент"; }
    }

    /// <summary>Подсказка по выбранному режиму запуска.</summary>
    public string LaunchModeHint => LaunchMode switch
    {
        "Тонкий клиент" => LocalizationManager.T("Conn.LaunchThinHint"),
        "Толстый клиент" => LocalizationManager.T("Conn.LaunchThickManagedHint"),
        "Толстый клиент (обычные формы)" => LocalizationManager.T("Conn.LaunchThickOrdinaryHint"),
        "Веб-клиент" => LocalizationManager.T("Conn.LaunchWebHint"),
        _ => LocalizationManager.T("Conn.LaunchAutoHint")
    };

    /// <summary>Дополнительные параметры запуска платформы 1С.</summary>
    public string LaunchParameters
    {
        get => _launchParameters;
        set => SetProperty(ref _launchParameters, value);
    }

    /// <summary>Тип подключения.</summary>
    public ConnectionType ConnectionType
    {
        get => _connectionType;
        set
        {
            if (SetProperty(ref _connectionType, value))
            {
                OnPropertyChanged(nameof(IsClientServer));
                OnPropertyChanged(nameof(IsFile));
                OnPropertyChanged(nameof(IsWebServer));
                OnPropertyChanged(nameof(IsWebClientAllowed));
                // Если веб-клиент выбран, а тип подключения больше не позволяет его — сбрасываем
                if (!IsWebClientAllowed && IsWebClient)
                    LaunchMode = "Автоматический";
            }
        }
    }

    /// <summary>Признак клиент-серверного подключения.</summary>
    public bool IsClientServer
    {
        get => ConnectionType == ConnectionType.ClientServer;
        set { if (value) ConnectionType = ConnectionType.ClientServer; }
    }

    /// <summary>Признак файлового подключения.</summary>
    public bool IsFile
    {
        get => ConnectionType == ConnectionType.File;
        set { if (value) ConnectionType = ConnectionType.File; }
    }

    /// <summary>Признак подключения через веб-сервер.</summary>
    public bool IsWebServer
    {
        get => ConnectionType == ConnectionType.WebServer;
        set { if (value) ConnectionType = ConnectionType.WebServer; }
    }

    /// <summary>
    /// Веб-клиент доступен только при подключении через веб-сервер
    /// или клиент-серверном подключении (с публикацией).
    /// </summary>
    public bool IsWebClientAllowed =>
        ConnectionType == ConnectionType.WebServer;

    /// <summary>Имя сервера.</summary>
    public string Server
    {
        get => _server;
        set => SetProperty(ref _server, value);
    }

    /// <summary>Имя базы на сервере.</summary>
    public string DatabaseName
    {
        get => _databaseName;
        set => SetProperty(ref _databaseName, value);
    }

    /// <summary>Путь к файловой базе.</summary>
    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    /// <summary>URL веб-публикации.</summary>
    public string WebUrl
    {
        get => _webUrl;
        set => SetProperty(ref _webUrl, value);
    }

    private long? _manualSizeBytes;

    /// <summary>
    /// Размер базы, заданный пользователем вручную в байтах (issue #243).
    /// null — не задан (для файловых баз показывается автоматический расчёт).
    /// </summary>
    public long? ManualSizeBytes
    {
        get => _manualSizeBytes;
        set
        {
            if (SetProperty(ref _manualSizeBytes, value))
            {
                OnPropertyChanged(nameof(IsManualSizeSet));
                OnPropertyChanged(nameof(ManualSizeText));
            }
        }
    }

    /// <summary>Признак «размер задан вручную» (управляет видимостью/доступностью поля).</summary>
    public bool IsManualSizeSet
    {
        get => _manualSizeBytes.HasValue;
        set
        {
            if (value == _manualSizeBytes.HasValue)
                return;
            if (value)
            {
                if (!_manualSizeBytes.HasValue)
                    ManualSizeBytes = 0;
            }
            else
            {
                ManualSizeBytes = null;
            }
        }
    }

    /// <summary>Текстовое представление ручного размера в байтах для ввода (пусто — не задан).</summary>
    public string ManualSizeText
    {
        get => _manualSizeBytes?.ToString() ?? string.Empty;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (long.TryParse(text, out var parsed) && parsed >= 0)
            {
                // Используем SetProperty: он и меняет поле, и оповещает подписчиков,
                // и помечает наличие изменений (иначе кнопка «Сохранить» не активировалась
                // и введённый вручную размер не сохранялся, issue #243).
                if (SetProperty(ref _manualSizeBytes, (long?)parsed))
                    OnPropertyChanged(nameof(IsManualSizeSet));
            }
            else if (string.IsNullOrEmpty(text) && _manualSizeBytes.HasValue)
            {
                SetProperty(ref _manualSizeBytes, (long?)null);
                OnPropertyChanged(nameof(IsManualSizeSet));
            }
            OnPropertyChanged(nameof(ManualSizeText));
        }
    }

    /// <summary>Пользователь.</summary>
    public string User
    {
        get => _user;
        set => SetProperty(ref _user, value);
    }

    /// <summary>Пароль.</summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    /// <summary>Адрес сервера хранилища конфигурации.</summary>
    public string RepositoryServer
    {
        get => _repositoryServer;
        set => SetProperty(ref _repositoryServer, value);
    }

    /// <summary>Имя хранилища конфигурации на сервере.</summary>
    public string RepositoryName
    {
        get => _repositoryName;
        set => SetProperty(ref _repositoryName, value);
    }

    /// <summary>Логин пользователя хранилища конфигурации.</summary>
    public string RepositoryUser
    {
        get => _repositoryUser;
        set => SetProperty(ref _repositoryUser, value);
    }

    /// <summary>Пароль пользователя хранилища конфигурации.</summary>
    public string RepositoryPassword
    {
        get => _repositoryPassword;
        set => SetProperty(ref _repositoryPassword, value);
    }

    /// <summary>Режим аутентификации для запуска Конфигуратора.</summary>
    public AuthenticationMode ConfiguratorAuthenticationMode
    {
        get => _configuratorAuthenticationMode;
        set
        {
            if (SetProperty(ref _configuratorAuthenticationMode, value))
            {
                OnPropertyChanged(nameof(IsConfiguratorAuthPrompt));
                OnPropertyChanged(nameof(IsConfiguratorAuthCredentials));
                OnPropertyChanged(nameof(IsConfiguratorAuthWindows));
                OnPropertyChanged(nameof(IsConfiguratorCredentialsVisible));
            }
        }
    }

    /// <summary>Запрашивать имя и пароль в Конфигураторе.</summary>
    public bool IsConfiguratorAuthPrompt
    {
        get => ConfiguratorAuthenticationMode == AuthenticationMode.Prompt;
        set { if (value) ConfiguratorAuthenticationMode = AuthenticationMode.Prompt; }
    }

    /// <summary>Выполнять вход автоматически в Конфигураторе.</summary>
    public bool IsConfiguratorAuthCredentials
    {
        get => ConfiguratorAuthenticationMode == AuthenticationMode.Credentials;
        set { if (value) ConfiguratorAuthenticationMode = AuthenticationMode.Credentials; }
    }

    /// <summary>Аутентификация ОС в Конфигураторе.</summary>
    public bool IsConfiguratorAuthWindows
    {
        get => ConfiguratorAuthenticationMode == AuthenticationMode.Windows;
        set { if (value) ConfiguratorAuthenticationMode = AuthenticationMode.Windows; }
    }

    /// <summary>Видимость полей логина/пароля Конфигуратора (только при автоматическом входе).</summary>
    public bool IsConfiguratorCredentialsVisible => ConfiguratorAuthenticationMode == AuthenticationMode.Credentials;

    /// <summary>Пользователь для запуска Конфигуратора.</summary>
    public string ConfiguratorUser
    {
        get => _configuratorUser;
        set => SetProperty(ref _configuratorUser, value);
    }

    /// <summary>Пароль для запуска Конфигуратора.</summary>
    public string ConfiguratorPassword
    {
        get => _configuratorPassword;
        set => SetProperty(ref _configuratorPassword, value);
    }

    /// <summary>
    /// Признак «Авторизация как для 1С:Предприятия» для Конфигуратора (issue #201).
    /// При включении Конфигуратор использует те же учётные данные, что и «1С:Предприятие».
    /// </summary>
    public bool ConfiguratorUseEnterpriseAuth
    {
        get => _configuratorUseEnterpriseAuth;
        set
        {
            if (SetProperty(ref _configuratorUseEnterpriseAuth, value))
            {
                OnPropertyChanged(nameof(IsConfiguratorAuthEnabled));
                // При включении сразу копируем учётные данные «1С:Предприятия»
                // в поля Конфигуратора, чтобы они отображались согласованно.
                if (value)
                {
                    ConfiguratorAuthenticationMode = AuthenticationMode;
                    ConfiguratorUser = User;
                    ConfiguratorPassword = Password;
                }
            }
        }
    }

    /// <summary>Редактируемы ли поля авторизации Конфигуратора (выключено при «как для 1С:Предприятия»).</summary>
    public bool IsConfiguratorAuthEnabled => !_configuratorUseEnterpriseAuth;

    /// <summary>
    /// Режим запуска базы по умолчанию (при двойном клике на базе): пусто — автоматически
    /// (1С:Предприятие), "Enterprise" — 1С:Предприятие, "Configurator" — Конфигуратор.
    /// </summary>
    public string DefaultLaunchMode
    {
        get => _defaultLaunchMode;
        set => SetProperty(ref _defaultLaunchMode, value ?? string.Empty);
    }

    /// <summary>
    /// Действие по двойному щелчку на базе (функция №28 StartManager). Пусто — использовать
    /// глобальную настройку; "Enterprise" / "Configurator" / "None" — индивидуальное значение.
    /// </summary>
    public string DoubleClickAction
    {
        get => _doubleClickAction;
        set => SetProperty(ref _doubleClickAction, value ?? string.Empty);
    }

    /// <summary>Путь к внешней обработке (.epf/.erf), запускаемой при открытии ИБ (функция №25 StartManager).</summary>
    public string ExternalProcessingPath
    {
        get => _externalProcessingPath;
        set => SetProperty(ref _externalProcessingPath, value ?? string.Empty);
    }

    /// <summary>Данные внешней обработки (ключ /C) (функция №25 StartManager).</summary>
    public string ExternalProcessingData
    {
        get => _externalProcessingData;
        set => SetProperty(ref _externalProcessingData, value ?? string.Empty);
    }

    /// <summary>Режим аутентификации.</summary>
    public AuthenticationMode AuthenticationMode
    {
        get => _authenticationMode;
        set
        {
            if (SetProperty(ref _authenticationMode, value))
            {
                OnPropertyChanged(nameof(IsAuthPrompt));
                OnPropertyChanged(nameof(IsAuthCredentials));
                OnPropertyChanged(nameof(IsAuthWindows));
                OnPropertyChanged(nameof(IsCredentialsVisible));
            }
        }
    }

    /// <summary>Запрашивать имя и пароль.</summary>
    public bool IsAuthPrompt
    {
        get => AuthenticationMode == AuthenticationMode.Prompt;
        set { if (value) AuthenticationMode = AuthenticationMode.Prompt; }
    }

    /// <summary>Выполнять вход автоматически.</summary>
    public bool IsAuthCredentials
    {
        get => AuthenticationMode == AuthenticationMode.Credentials;
        set { if (value) AuthenticationMode = AuthenticationMode.Credentials; }
    }

    /// <summary>Аутентификация операционной системы.</summary>
    public bool IsAuthWindows
    {
        get => AuthenticationMode == AuthenticationMode.Windows;
        set { if (value) AuthenticationMode = AuthenticationMode.Windows; }
    }

    /// <summary>Видимость полей логина/пароля (только при автоматическом входе).</summary>
    public bool IsCredentialsVisible => AuthenticationMode == AuthenticationMode.Credentials;

    /// <summary>Совместимость: признак аутентификации ОС.</summary>
    public bool UseOsAuthentication
    {
        get => AuthenticationMode == AuthenticationMode.Windows;
        set
        {
            if (value)
                AuthenticationMode = AuthenticationMode.Windows;
            else if (AuthenticationMode == AuthenticationMode.Windows)
                AuthenticationMode = AuthenticationMode.Prompt;
        }
    }

    /// <summary>Порт сервера.</summary>
    public int Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value))
                OnPropertyChanged(nameof(PortText));
        }
    }

    /// <summary>
    /// Текстовое представление порта для редактируемого выпадающего списка.
    /// Позволяет и выбрать порт из списка доступных, и ввести значение вручную.
    /// </summary>
    public string PortText
    {
        get => _port > 0 ? _port.ToString() : string.Empty;
        set
        {
            if (int.TryParse(value, out var parsed) && parsed > 0 && parsed <= 65535)
                Port = parsed;
        }
    }

    /// <summary>
    /// Строка подключения 1С для ввода/отображения в окне настроек базы.
    /// Может быть введена вручную или вставлена из буфера обмена.
    /// Всегда доступна в окне (не зависит от выбранной вкладки).
    /// </summary>
    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (SetProperty(ref _connectionString, value))
                OnPropertyChanged(nameof(HasConnectionString));
        }
    }

    /// <summary>Признак того, что строка подключения не пустая.</summary>
    public bool HasConnectionString => !string.IsNullOrWhiteSpace(_connectionString);

    /// <summary>
    /// Применяет указанную строку подключения 1С к полям ViewModel.
    /// Разбивает строку на тип подключения, сервер/порт, имя базы, путь файла или URL,
    /// пользователя и пароль. Если наименование базы не задано — подставляет имя базы (Ref)
    /// или имя каталога файловой базы.
    /// </summary>
    /// <param name="connectionString">Строка подключения 1С.</param>
    public void ApplyConnectionString(string? connectionString)
    {
        var parsed = ConnectionSettings.ParseConnectionString(connectionString);

        ConnectionType = parsed.Type;
        Server = parsed.Server;
        DatabaseName = parsed.DatabaseName;
        FilePath = parsed.FilePath;
        WebUrl = parsed.WebUrl;
        User = parsed.User;
        Password = parsed.Password;
        AuthenticationMode = parsed.AuthenticationMode;
        Port = parsed.Port;

        // Если наименование не задано — предлагаем имя базы (Ref) или имя файла.
        if (string.IsNullOrWhiteSpace(Name))
        {
            var suggestedName = parsed.Type switch
            {
                ConnectionType.File => SuggestNameFromPath(parsed.FilePath),
                ConnectionType.WebServer => parsed.WebUrl,
                _ => parsed.DatabaseName
            };
            if (!string.IsNullOrWhiteSpace(suggestedName))
            {
                Name = suggestedName;
            }
        }
    }

    /// <summary>
    /// Строит информационную базу по текущим настройкам подключения для зондирования
    /// свойств конфигурации. База не сохраняется — используется только чтением.
    /// </summary>
    private Infobase BuildProbeInfobase()
    {
        var ib = new Infobase { Connection = new ConnectionSettings() };
        var conn = ib.Connection;
        conn.Type = ConnectionType;
        conn.Server = Server;
        conn.DatabaseName = DatabaseName;
        conn.FilePath = FilePath;
        conn.WebUrl = WebUrl;
        conn.User = User;
        conn.Password = Password;
        conn.AuthenticationMode = AuthenticationMode;
        conn.Port = Port;

        // Имя базы нужно для сообщений об ошибках чтения свойств конфигурации (issue #174):
        // без него в журнале появляется «...базы «»:», хотя Ref известен. Приоритет — заданное
        // наименование, затем Ref (DatabaseName), затем имя файла файловой базы.
        ib.Name = !string.IsNullOrWhiteSpace(Name) ? Name
            : !string.IsNullOrWhiteSpace(DatabaseName) ? DatabaseName
            : SuggestNameFromPath(FilePath);

        // Версия платформы базы (issue #175): она нужна для разворота шаблона имени
        // COM-коннектора при чтении свойств конфигурации. Без неё бралась бы максимальная
        // установленная версия (часто с суффиксом разрядности), а указанная для базы версия
        // (например «8.3.27») игнорировалась бы — как и происходило при вызове «Определить»
        // из диалога свойств базы.
        ib.PlatformVersion = PlatformVersion;

        return ib;
    }

    /// <summary>
    /// Читает имя и версию конфигурации по текущим настройкам подключения, не изменяя
    /// привязанных свойств ViewModel. Безопасен для вызова из фонового потока. Возвращает
    /// прочитанные данные или null. <paramref name="onStage"/> — обратный вызов смены этапа
    /// для диалога прогресса (issue #174).
    /// </summary>
    public OneCConfigInfo? ReadConfiguration(Action<string>? onStage = null)
    {
        var ib = BuildProbeInfobase();
        // Таймаут резолвится внутри (настройка ComDetectTimeoutMs, по умолчанию 30000 мс),
        // а не жёстко 8000 мс (issue #174): первое COM-подключение часто превышает 8 секунд.
        return ConfigurationInfoService.ReadAndApply(ib, overwriteExisting: true, timeoutMs: null, onStage);
    }

    /// <summary>
    /// Применяет прочитанные данные к полям <see cref="ConfigurationName"/> /
    /// <see cref="ConfigurationVersion"/>. Вызывать только в UI-потоке. Возвращает true,
    /// если хотя бы одно поле обновлено.
    /// </summary>
    public bool ApplyConfiguration(OneCConfigInfo? info)
    {
        if (info is null)
            return false;

        var changed = false;
        if (!string.IsNullOrWhiteSpace(info.Value.Name))
        {
            ConfigurationName = info.Value.Name.Trim();
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(info.Value.Version))
        {
            ConfigurationVersion = info.Value.Version.Trim();
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Определяет имя и версию конфигурации по текущим настройкам подключения
    /// (COM-коннектор на Windows, эвристика/конфигуратор на Linux) и обновляет
    /// поля <see cref="ConfigurationName"/> / <see cref="ConfigurationVersion"/>
    /// (issue #174). Возвращает true, если данные удалось получить.
    /// </summary>
    public bool DetermineConfiguration(bool overwriteExisting = true)
    {
        var info = ReadConfiguration();
        return ApplyConfiguration(info);
    }

    /// <summary>
    /// Формирует имя базы из пути к файловой базе (имя последнего каталога).
    /// </summary>
    private static string SuggestNameFromPath(string? filePath)
    {
        var path = (filePath ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    /// <summary>
    /// Заполняет ViewModel из информационной базы.
    /// </summary>
    public void LoadFrom(Infobase infobase)
    {
        _isLoading = true;
        try
        {
            Id = infobase.Id;
            Name = infobase.Name;
            Group = infobase.Group;
            SelectedGroup = FindGroupByPath(infobase.Group);
            Description = infobase.Description;
            PlatformVersion = infobase.PlatformVersion;
            ConfigurationName = infobase.ConfigurationName;
            ConfigurationVersion = infobase.ConfigurationVersion;
            Architecture = NormalizeArchitecture(infobase.Architecture);
            LaunchMode = infobase.LaunchMode;
            LaunchParameters = infobase.LaunchParameters;
            DefaultLaunchMode = infobase.DefaultLaunchMode ?? string.Empty;
            // Действие по двойному щелчку и внешняя обработка при запуске (Этап 7).
            DoubleClickAction = infobase.DoubleClickAction ?? string.Empty;
            ExternalProcessingPath = infobase.ExternalProcessingPath ?? string.Empty;
            ExternalProcessingData = infobase.ExternalProcessingData ?? string.Empty;

            // Ручной размер базы (issue #243).
            ManualSizeBytes = infobase.ManualSizeBytes;

            var conn = infobase.Connection;
            ConnectionType = conn.Type;
            Server = conn.Server;
            DatabaseName = conn.DatabaseName;
            FilePath = conn.FilePath;
            WebUrl = conn.WebUrl;
            // Авторизация «1С:Предприятие» — отдельная настройка (EnterpriseAuth),
            // если задана; иначе берём авторизацию информационной базы (обратная совместимость).
            if (infobase.EnterpriseAuth is { } ent)
            {
                // Отдельная авторизация уже сохранена — используем её режим как есть,
                // без устаревшей миграции (иначе выбранный «Запрашивать имя и пароль»
                // с заполненным логином перезаписывался бы на «Вход автоматически»).
                User = ent.User;
                Password = ent.Password;
                if (ent.UseOsAuthentication && ent.AuthenticationMode == AuthenticationMode.Prompt
                    && string.IsNullOrWhiteSpace(ent.User))
                    AuthenticationMode = AuthenticationMode.Windows;
                else
                    AuthenticationMode = ent.AuthenticationMode;
            }
            else
            {
                // Обратная совместимость: отдельной авторизации нет — берём из базы,
                // применяя миграцию старого формата (логин без режима → автоматический вход).
                User = conn.User;
                Password = conn.Password;
                if (conn.AuthenticationMode != AuthenticationMode.Prompt
                    || !string.IsNullOrWhiteSpace(conn.User)
                    || conn.UseOsAuthentication)
                {
                    AuthenticationMode = conn.AuthenticationMode;
                    // Старые файлы: если был только флаг ОС или логин без режима.
                    if (conn.UseOsAuthentication && conn.AuthenticationMode == AuthenticationMode.Prompt
                        && string.IsNullOrWhiteSpace(conn.User))
                        AuthenticationMode = AuthenticationMode.Windows;
                    else if (!string.IsNullOrWhiteSpace(conn.User) && conn.AuthenticationMode == AuthenticationMode.Prompt
                             && !conn.UseOsAuthentication)
                        AuthenticationMode = AuthenticationMode.Credentials;
                }
                else
                {
                    AuthenticationMode = AuthenticationMode.Prompt;
                }
            }
            Port = conn.Port;
            // Заполняем поле строки подключения для отображения/редактирования.
            _connectionString = conn.ToConnectionString();

            // Данные хранилища конфигурации.
            var repo = infobase.Repository;
            RepositoryServer = repo.Server;
            RepositoryName = repo.RepositoryName;
            RepositoryUser = repo.User;
            RepositoryPassword = repo.Password;

            // Авторизация Конфигуратора — отдельная настройка, независимая от
            // авторизации «1С:Предприятие». Если она ещё не задана, поля остаются
            // в значениях по умолчанию (запрос имени и пароля, без пользователя),
            // и при сохранении не копируются из авторизации базы.
            if (infobase.ConfiguratorAuth is { } cfgAuth)
            {
                ConfiguratorAuthenticationMode = cfgAuth.AuthenticationMode;
                ConfiguratorUser = cfgAuth.User;
                ConfiguratorPassword = cfgAuth.Password;
            }
            // Признак «Авторизация как для 1С:Предприятия» читается отдельно от самих
            // учётных данных: при включении сеттер скопирует в поля Конфигуратора
            // значения «1С:Предприятия» уже из загруженных выше полей.
            ConfiguratorUseEnterpriseAuth = infobase.ConfiguratorUseEnterpriseAuth;

            // Теги базы (issue #283): загружаем текущие теги для редактирования.
            Tags.Clear();
            foreach (var t in infobase.Tags ?? new List<string>())
                Tags.Add(t);
        }
        finally
        {
            _isLoading = false;
        }

        _hasChanges = false;
        OnPropertyChanged(nameof(HasChanges));
    }

    /// <summary>
    /// Проверяет, что значения полей, которые подставляются в командную строку 1С
    /// (аргументы /F /S /WS /N /P), безопасны для этой грамматики — не содержат
    /// символа двойной кавычки и управляющих символов. Иначе они молча отбрасываются
    /// при запуске (см. <c>OneCLauncher.IsSafeCliValue</c>), и база открывается
    /// с неверными параметрами или без них (issue #205).
    /// </summary>
    /// <returns>
    /// Локализованное сообщение об ошибке с именем первого недопустимого поля,
    /// или <c>null</c>, если все проверяемые значения безопасны.
    /// </returns>
    public string? ValidateCliArgs()
    {
        // Поля подключения, попадающие в аргументы /F /S /WS.
        string? connectionField = ConnectionType switch
        {
            ConnectionType.File => IsUnsafeForCli(FilePath) ? LocalizationManager.T("Connection.FieldFilePath") : null,
            ConnectionType.WebServer => IsUnsafeForCli(WebUrl) ? LocalizationManager.T("Connection.FieldWebUrl") : null,
            _ => IsUnsafeForCli(Server) ? LocalizationManager.T("Connection.FieldServer")
               : IsUnsafeForCli(DatabaseName) ? LocalizationManager.T("Connection.FieldDatabaseName")
               : null
        };
        if (connectionField is not null)
            return BuildCliInvalidMessage(connectionField);

        // Логин/пароль «1С:Предприятие» используются при автоматическом входе (/N /P).
        if (AuthenticationMode == AuthenticationMode.Credentials)
        {
            if (IsUnsafeForCli(User))
                return BuildCliInvalidMessage(LocalizationManager.T("Connection.FieldUser"));
            if (IsUnsafeForCli(Password))
                return BuildCliInvalidMessage(LocalizationManager.T("Connection.FieldPassword"));
        }

        // Отдельная авторизация Конфигуратора. При «как для 1С:Предприятия»
        // используются те же учётные данные, уже проверенные выше.
        if (!ConfiguratorUseEnterpriseAuth && ConfiguratorAuthenticationMode == AuthenticationMode.Credentials)
        {
            if (IsUnsafeForCli(ConfiguratorUser))
                return BuildCliInvalidMessage(LocalizationManager.T("Connection.FieldConfiguratorUser"));
            if (IsUnsafeForCli(ConfiguratorPassword))
                return BuildCliInvalidMessage(LocalizationManager.T("Connection.FieldConfiguratorPassword"));
        }

        return null;
    }

    /// <summary>
    /// True, если значение непустое и содержит символ, который нельзя передать
    /// внутри кавычек ключа командной строки 1С: двойную кавычку или управляющий
    /// символ. Пустое значение безопасно само по себе — оно не порождает
    /// инъекции, хотя и приводит к отсутствию аргумента.
    /// </summary>
    private static bool IsUnsafeForCli(string? value)
        => !string.IsNullOrEmpty(value) &&
           (value!.IndexOf('"') >= 0 || value.Any(c => char.IsControl(c)));

    /// <summary>Собирает локализованное сообщение о недопустимом значении поля.</summary>
    private static string BuildCliInvalidMessage(string fieldName)
        => string.Format(LocalizationManager.T("Connection.InvalidCliCharFormat"), fieldName);

    /// <summary>
    /// Применяет значения ViewModel к информационной базе.
    /// </summary>
    public void ApplyTo(Infobase infobase)
    {
        // Сохраняем идентификатор базы, чтобы не потерять его при редактировании.
        infobase.Id = Id;
        infobase.Name = Name;
        infobase.Group = Group;
        infobase.Description = Description;
        infobase.PlatformVersion = PlatformVersion;
        infobase.ConfigurationName = ConfigurationName;
        infobase.ConfigurationVersion = ConfigurationVersion;
        infobase.Architecture = NormalizeArchitecture(Architecture);
        infobase.LaunchMode = string.IsNullOrWhiteSpace(LaunchMode) ? "Автоматический" : LaunchMode;
        infobase.LaunchParameters = LaunchParameters ?? string.Empty;
        infobase.DefaultLaunchMode = (DefaultLaunchMode ?? string.Empty).Trim();
        // Действие по двойному щелчку и внешняя обработка при запуске (Этап 7).
        infobase.DoubleClickAction = (DoubleClickAction ?? string.Empty).Trim();
        infobase.ExternalProcessingPath = (ExternalProcessingPath ?? string.Empty).Trim();
        infobase.ExternalProcessingData = (ExternalProcessingData ?? string.Empty).Trim();

        // Теги базы (issue #283): переносим отредактированные теги.
        infobase.Tags = Tags.ToList();

        // Ручной размер базы (issue #243).
        infobase.ManualSizeBytes = ManualSizeBytes;

        if (infobase.Connection is null)
            infobase.Connection = new ConnectionSettings();
        var conn = infobase.Connection;
        conn.Type = ConnectionType;
        conn.Server = Server;
        conn.DatabaseName = DatabaseName;
        conn.FilePath = FilePath;
        conn.WebUrl = WebUrl;
        conn.User = User;
        conn.Password = Password;
        conn.AuthenticationMode = AuthenticationMode;
        conn.Port = Port;

        if (infobase.Repository is null)
            infobase.Repository = new RepositorySettings();
        var repo = infobase.Repository;
        repo.Server = RepositoryServer;
        repo.RepositoryName = RepositoryName;
        repo.User = RepositoryUser;
        repo.Password = RepositoryPassword;

        // Авторизация «1С:Предприятие» сохраняется отдельно (EnterpriseAuth),
        // независимо от авторизации Конфигуратора и параметров подключения базы.
        infobase.EnterpriseAuth = new InfobaseAuthSettings
        {
            AuthenticationMode = AuthenticationMode,
            User = User,
            Password = Password
        };

        // Авторизация Конфигуратора всегда сохраняется отдельно (независимо от
        // авторизации «1С:Предприятие»), чтобы при изменении одной из них другая
        // не подстраивалась автоматически. При включённом признаке «как для
        // 1С:Предприятия» в авторизацию Конфигуратора копируются учётные данные
        // «1С:Предприятия» (логин, пароль и режим входа).
        infobase.ConfiguratorUseEnterpriseAuth = ConfiguratorUseEnterpriseAuth;
        if (ConfiguratorUseEnterpriseAuth)
        {
            infobase.ConfiguratorAuth = new InfobaseAuthSettings
            {
                AuthenticationMode = AuthenticationMode,
                User = User,
                Password = Password
            };
        }
        else
        {
            infobase.ConfiguratorAuth = new InfobaseAuthSettings
            {
                AuthenticationMode = ConfiguratorAuthenticationMode,
                User = ConfiguratorUser,
                Password = ConfiguratorPassword
            };
        }
    }
}