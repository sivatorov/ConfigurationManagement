using System.Collections.ObjectModel;
using System.ComponentModel;
using Configuration_Management.Models;

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
    private string _platformVersion = string.Empty;
    private string _architecture = "32";
    private string _launchMode = "Автоматический";
    private string _launchParameters = string.Empty;
    private ConnectionType _connectionType = ConnectionType.ClientServer;
    private string _server = string.Empty;
    private string _databaseName = string.Empty;
    private string _filePath = string.Empty;
    private string _user = string.Empty;
    private string _password = string.Empty;
    private bool _useOsAuthentication = true;
    private int _port = 1541;
    private Group? _selectedGroup;

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
            }
        }
    }

    /// <summary>Группа базы (полный путь в иерархии).</summary>
    public string Group
    {
        get => _group;
        set => SetProperty(ref _group, value);
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

    /// <summary>Разрядность платформы при запуске («32» или «64»).</summary>
    public string Architecture
    {
        get => _architecture;
        set => SetProperty(ref _architecture, value);
    }

    /// <summary>Использовать 32-битную платформу.</summary>
    public bool IsArchitecture32
    {
        get => Architecture == "32";
        set { if (value) Architecture = "32"; }
    }

    /// <summary>Использовать 64-битную платформу.</summary>
    public bool IsArchitecture64
    {
        get => Architecture == "64";
        set { if (value) Architecture = "64"; }
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
                OnPropertyChanged(nameof(IsWebClient));
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

    /// <summary>Толстый клиент.</summary>
    public bool IsThickClient
    {
        get => LaunchMode == "Толстый клиент";
        set { if (value) LaunchMode = "Толстый клиент"; }
    }

    /// <summary>Веб-клиент.</summary>
    public bool IsWebClient
    {
        get => LaunchMode == "Веб-клиент";
        set { if (value) LaunchMode = "Веб-клиент"; }
    }

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

    /// <summary>Использовать аутентификацию ОС.</summary>
    public bool UseOsAuthentication
    {
        get => _useOsAuthentication;
        set
        {
            if (SetProperty(ref _useOsAuthentication, value))
            {
                OnPropertyChanged(nameof(IsCredentialsVisible));
            }
        }
    }

    /// <summary>Видимость полей логина/пароля.</summary>
    public bool IsCredentialsVisible => !UseOsAuthentication;

    /// <summary>Порт сервера.</summary>
    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
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
            Architecture = infobase.Architecture;
            LaunchMode = infobase.LaunchMode;
            LaunchParameters = infobase.LaunchParameters;

            var conn = infobase.Connection;
            ConnectionType = conn.Type;
            Server = conn.Server;
            DatabaseName = conn.DatabaseName;
            FilePath = conn.FilePath;
            User = conn.User;
            Password = conn.Password;
            UseOsAuthentication = conn.UseOsAuthentication;
            Port = conn.Port;
        }
        finally
        {
            _isLoading = false;
        }

        _hasChanges = false;
        OnPropertyChanged(nameof(HasChanges));
    }

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
        infobase.Architecture = Architecture;
        infobase.LaunchMode = LaunchMode;
        infobase.LaunchParameters = LaunchParameters;

        var conn = infobase.Connection;
        conn.Type = ConnectionType;
        conn.Server = Server;
        conn.DatabaseName = DatabaseName;
        conn.FilePath = FilePath;
        conn.User = User;
        conn.Password = Password;
        // Если указан логин — используем аутентификацию по логину/паролю,
        // иначе — аутентификацию ОС.
        conn.UseOsAuthentication = string.IsNullOrWhiteSpace(User);
        conn.Port = Port;
    }
}