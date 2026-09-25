using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Configuration_Management.Localization;

namespace Configuration_Management.Models;

/// <summary>
/// Представляет информационную базу (аналог информационной базы 1С).
/// </summary>
public class Infobase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    /// <summary>
    /// Идентификатор базы 1С (GUID из файла ibases.v8i, ключ ID).
    /// Используется для точной очистки кеша 1С.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Наименование информационной базы.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Группа, к которой относится база.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Порядок отображения базы внутри группы (меньше — выше в списке).
    /// Используется при перетаскивании между базами.
    /// </summary>
    public int SortOrder { get; set; }

    private bool _isFavorite;

    /// <summary>Признак избранной базы.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
    }

    private int _favoriteHotkeyNumber;

    /// <summary>
    /// Номер горячей клавиши избранного (1–9 → Alt+N), 0 — не назначен.
    /// Отображается рядом со звездой в списке.
    /// </summary>
    public int FavoriteHotkeyNumber
    {
        get => _favoriteHotkeyNumber;
        set
        {
            if (SetProperty(ref _favoriteHotkeyNumber, value))
                OnPropertyChanged(nameof(FavoriteHotkeyDisplay));
        }
    }

    /// <summary>Текст номера для UI («1»…«9» или пусто).</summary>
    public string FavoriteHotkeyDisplay =>
        _favoriteHotkeyNumber >= 1 && _favoriteHotkeyNumber <= 9
            ? _favoriteHotkeyNumber.ToString()
            : string.Empty;

    private bool _isPinned;

    /// <summary>Признак закреплённой базы (отображается вверху списка без группы).</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    private bool _isSelected;

    /// <summary>Признак выбранной базы в дереве (для синхронизации с TreeViewItem.IsSelected).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private DateTime? _lastLaunchDate;

    /// <summary>Дата и время последнего запуска базы.</summary>
    public DateTime? LastLaunchDate
    {
        get => _lastLaunchDate;
        set
        {
            if (SetProperty(ref _lastLaunchDate, value))
                OnPropertyChanged(nameof(LastLaunchDisplay));
        }
    }

    private ConnectionSettings _connection = new();

    /// <summary>
    /// Настройки подключения к базе. Никогда не равен null:
    /// при десериализации (в том числе, когда в JSON свойство задано как null)
    /// значение гарантированно подменяется пустыми настройками, чтобы вычисляемые
    /// свойства (<see cref="ConnectionStringDisplay"/>, <see cref="ServerDatabaseDisplay"/>)
    /// не вызывали NullReferenceException при выборе базы.
    /// </summary>
    public ConnectionSettings Connection
    {
        get => _connection;
        set => _connection = value ?? new ConnectionSettings();
    }

    private RepositorySettings _repository = new();

    /// <summary>
    /// Настройки подключения к хранилищу конфигурации (адрес, имя хранилища,
    /// логин и пароль). Используются при работе в Конфигураторе для входа «под собой».
    /// Никогда не равен null: при десериализации подменяется пустыми настройками.
    /// </summary>
    public RepositorySettings Repository
    {
        get => _repository;
        set => _repository = value ?? new RepositorySettings();
    }

    /// <summary>
    /// Настройки авторизации для запуска «1С:Предприятие» (отдельно от Конфигуратора).
    /// Если значение равно null — Предприятие использует авторизацию информационной базы
    /// (<see cref="Connection"/>), что обеспечивает обратную совместимость.
    /// При задании отдельной авторизации она применяется при запуске «1С:Предприятие».
    /// </summary>
    public InfobaseAuthSettings? EnterpriseAuth { get; set; }

    /// <summary>
    /// Настройки авторизации для запуска Конфигуратора (отдельно от «1С:Предприятие»).
    /// Если значение равно null — Конфигуратор использует авторизацию информационной базы
    /// (<see cref="Connection"/>), что обеспечивает обратную совместимость.
    /// При задании отдельной авторизации она применяется при запуске Конфигуратора.
    /// </summary>
    public InfobaseAuthSettings? ConfiguratorAuth { get; set; }

    /// <summary>
    /// Признак «Авторизация как для 1С:Предприятия» для Конфигуратора (issue #201).
    /// При включении авторизация Конфигуратора копирует учётные данные «1С:Предприятия»
    /// (логин/пароль/режим входа). Хранится отдельно, чтобы флаг переживал повторное
    /// редактирование базы без эвристики сравнения значений.
    /// </summary>
    public bool ConfiguratorUseEnterpriseAuth { get; set; }

    /// <summary>Версия платформы 1С.</summary>
    private string _platformVersion = string.Empty;
    /// <summary>Версия платформы 1С (например 8.3.27.1644).</summary>
    public string PlatformVersion
    {
        get => _platformVersion;
        set
        {
            if (SetProperty(ref _platformVersion, value ?? string.Empty))
                OnPropertyChanged(nameof(PlatformVersionDisplay));
        }
    }

    private string _configurationName = string.Empty;
    private string _configurationVersion = string.Empty;

    /// <summary>Наименование конфигурации 1С (например «Бухгалтерия предприятия»).</summary>
    public string ConfigurationName
    {
        get => _configurationName;
        set
        {
            if (SetProperty(ref _configurationName, value ?? string.Empty))
                OnPropertyChanged(nameof(ConfigurationDisplay));
        }
    }

    /// <summary>Версия конфигурации 1С (например «3.0.142.32»).</summary>
    public string ConfigurationVersion
    {
        get => _configurationVersion;
        set
        {
            if (SetProperty(ref _configurationVersion, value ?? string.Empty))
                OnPropertyChanged(nameof(ConfigurationDisplay));
        }
    }

    private string _updateConfigCode = string.Empty;

    /// <summary>
    /// Код типовой конфигурации 1С для проверки обновлений (связь ИБ ↔ конфигурация).
    /// Совпадает с <see cref="OneCConfigType.Code"/>. Пусто — связь не задана.
    /// </summary>
    public string UpdateConfigCode
    {
        get => _updateConfigCode;
        set => SetProperty(ref _updateConfigCode, value ?? string.Empty);
    }

    private string _updateUrlOverride = string.Empty;

    /// <summary>
    /// Ручная ссылка на каталог релизов для проверки обновлений. Если пуста — адрес
    /// формируется автоматически по правилу 1С из кода конфигурации и редакции.
    /// </summary>
    public string UpdateUrlOverride
    {
        get => _updateUrlOverride;
        set => SetProperty(ref _updateUrlOverride, value ?? string.Empty);
    }

    /// <summary>Включена ли временная индикация «(обновление информации)» (issue #244).</summary>
    private bool _configInfoRefreshing;
    /// <summary>Колонка, в которой показывается временная надпись (issue #244).</summary>
    private string _configInfoIndicatorColumn = string.Empty;

    /// <summary>Показывать ли временную надпись «(обновление информации)» в строке базы (issue #244).</summary>
    public bool IsConfigInfoRefreshing
    {
        get => _configInfoRefreshing;
        private set => SetProperty(ref _configInfoRefreshing, value);
    }

    /// <summary>Колонка, в которой отображается временная надпись (issue #244).</summary>
    public string ConfigInfoIndicatorColumn
    {
        get => _configInfoIndicatorColumn;
        private set => SetProperty(ref _configInfoIndicatorColumn, value ?? string.Empty);
    }

    /// <summary>
    /// Включает/выключает временную индикацию обновления информации о конфигурации
    /// (issue #244): при включении конкретная колонка показывает «(обновление информации)».
    /// </summary>
    public void SetConfigInfoIndicator(bool refreshing, string column)
    {
        var target = column ?? string.Empty;
        if (_configInfoRefreshing == refreshing && _configInfoIndicatorColumn == target)
            return;
        _configInfoRefreshing = refreshing;
        _configInfoIndicatorColumn = target;
        OnPropertyChanged(nameof(IsConfigInfoRefreshing));
        OnPropertyChanged(nameof(ConfigInfoIndicatorColumn));
        OnPropertyChanged(nameof(NameDisplay));
        OnPropertyChanged(nameof(ConfigurationNameDisplay));
        OnPropertyChanged(nameof(ConfigurationVersionDisplay));
    }

    /// <summary>Отображение колонки «Название» с учётом временной индикации обновления (issue #244).</summary>
    public string NameDisplay =>
        IsConfigInfoRefreshing && _configInfoIndicatorColumn == "Name"
            ? LocalizationManager.T("Main.ConfigInfoUpdating")
            : Name;

    /// <summary>Отображение колонки «Конфигурация» с учётом временной индикации обновления (issue #244).</summary>
    public string ConfigurationNameDisplay =>
        IsConfigInfoRefreshing && _configInfoIndicatorColumn == "Configuration"
            ? LocalizationManager.T("Main.ConfigInfoUpdating")
            : ConfigurationName;

    /// <summary>Отображение колонки «№ релиза» с учётом временной индикации обновления (issue #244).</summary>
    public string ConfigurationVersionDisplay =>
        IsConfigInfoRefreshing && _configInfoIndicatorColumn == "ConfigurationVersion"
            ? LocalizationManager.T("Main.ConfigInfoUpdating")
            : ConfigurationVersion;

    /// <summary>Отображение: «Название (версия)» или одно из полей.</summary>
    public string ConfigurationDisplay
    {
        get
        {
            var n = (_configurationName ?? string.Empty).Trim();
            var v = (_configurationVersion ?? string.Empty).Trim();
            if (n.Length == 0 && v.Length == 0) return string.Empty;
            if (n.Length == 0) return v;
            if (v.Length == 0) return n;
            return $"{n} ({v})";
        }
    }

    /// <summary>Режим запуска (Автоматический, Тонкий клиент, Толстый клиент, Веб-клиент).</summary>
    private string _launchMode = "Автоматический";

    /// <summary>Режим запуска (Автоматический, Тонкий клиент, Толстый клиент, Веб-клиент).</summary>
    public string LaunchMode
    {
        get => _launchMode;
        set
        {
            if (SetProperty(ref _launchMode, value ?? "Автоматический"))
                OnPropertyChanged(nameof(ParsedLaunchMode));
        }
    }

    /// <summary>Дополнительные параметры запуска платформы 1С (например, /UC, /DisableStartupMessages и др.).</summary>
    public string LaunchParameters { get; set; } = string.Empty;

    /// <summary>
    /// Режим запуска базы по умолчанию (при двойном клике на базе): пусто — автоматически
    /// (1С:Предприятие), "Enterprise" — 1С:Предприятие, "Configurator" — Конфигуратор.
    /// Каноническое строковое значение, не локализуется.
    /// </summary>
    public string DefaultLaunchMode { get; set; } = string.Empty;

    /// <summary>
    /// Действие по двойному щелчку на базе (функция №28 StartManager).
    /// Каноническое строковое значение: пусто — использовать глобальную настройку,
    /// "Enterprise" — запустить «1С:Предприятие», "Configurator" — запустить «Конфигуратор»,
    /// "None" — ничего не делать. См. <see cref="DoubleClickAction"/>.
    /// </summary>
    public string DoubleClickAction { get; set; } = Configuration_Management.Models.DoubleClickAction.Default;

    /// <summary>
    /// Путь к внешней обработке (.epf/.erf), запускаемой при открытии базы в режиме
    /// «1С:Предприятие» (функция №25 StartManager). Передаётся ключом /Execute "путь".
    /// Пустая строка — обработка не запускается.
    /// </summary>
    public string ExternalProcessingPath { get; set; } = string.Empty;

    /// <summary>
    /// Данные внешней обработки, передаваемые ключом /C "данные" при запуске через
    /// <see cref="ExternalProcessingPath"/> (функция №25 StartManager). Необязательны.
    /// </summary>
    public string ExternalProcessingData { get; set; } = string.Empty;

    private string _architecture = "32-priority";

    /// <summary>Разрядность платформы при запуске базы («32» или «64» бита).</summary>
    public string Architecture
    {
        get => _architecture;
        set
        {
            if (SetProperty(ref _architecture, value ?? "32-priority"))
                OnPropertyChanged(nameof(PlatformVersionDisplay));
        }
    }

    /// <summary>Человекочитаемая разрядность для UI (как в лаунчере 1С).</summary>
    public string ArchitectureDisplay => (Architecture ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "64" or "x64" => LocalizationManager.T("Infobase.ArchX64"),
        "32" or "x86" => LocalizationManager.T("Infobase.ArchX86"),
        "64-priority" => LocalizationManager.T("Infobase.ArchPriority64"),
        "32-priority" => LocalizationManager.T("Infobase.ArchPriority32"),
        _ => string.IsNullOrWhiteSpace(Architecture) ? LocalizationManager.T("Infobase.ArchPriority32") : Architecture
    };

    /// <summary>
    /// Версия платформы с разрядностью для списка баз (issue #101).
    /// Если разрядность выбрана явно (32/x86 или 64/x64), рядом с версией
    /// показывается суффикс «[x86]»/«[x64]»; при автоопределении или приоритетном
    /// режиме — только чистая версия. Примеры: «8.3.19 [x86]», «8.3 [x64]», «8.3.27.2325».
    /// </summary>
    public string PlatformVersionDisplay
    {
        get
        {
            var version = (PlatformVersion ?? string.Empty).Trim();
            if (version.Length == 0)
                return string.Empty;

            var arch = (Architecture ?? string.Empty).Trim().ToLowerInvariant();
            var suffix = arch switch
            {
                "32" or "x86" => " [x86]",
                "64" or "x64" => " [x64]",
                _ => string.Empty
            };
            return version + suffix;
        }
    }

    /// <summary>
    /// Тип клиента (тонкий или толстый). Каноническое строковое значение-идентификатор:
    /// хранится в модели и сравнивается (StringComparison.OrdinalIgnoreCase), поэтому
    /// НЕ локализуется напрямую.
    /// </summary>
    private string _clientType = "Тонкий";

    /// <summary>
    /// Тип клиента (тонкий или толстый). Каноническое значение, персистится на диск
    /// и используется для сравнения — НЕ переводится.
    /// </summary>
    public string ClientType
    {
        get => _clientType;
        set
        {
            if (SetProperty(ref _clientType, value ?? "Тонкий"))
                OnPropertyChanged(nameof(ClientTypeDisplay));
        }
    }

    /// <summary>
    /// Тип клиента для отображения (локализованный). Используется в карточке базы
    /// и в строке состояния. Каноническая строка (<see cref="ClientType"/>) не изменяется.
    /// Неизвестные значения выводятся как есть (fallback).
    /// </summary>
    public string ClientTypeDisplay
    {
        get
        {
            var type = (_clientType ?? string.Empty).Trim();
            return type.ToLowerInvariant() switch
            {
                "тонкий" => LocalizationManager.T("Main.SessionClientThin"),
                "толстый" => LocalizationManager.T("Main.SessionClientThickManaged"),
                _ => string.IsNullOrWhiteSpace(type) ? string.Empty : _clientType ?? string.Empty
            };
        }
    }

    /// <summary>Описание базы.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Теги базы данных.</summary>
    private List<string> _tags = new();

    public List<string> Tags
    {
        get => _tags;
        set
        {
            _tags = value ?? new List<string>();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Сообщает UI об изменении набора тегов.
    /// Подменяет список новым экземпляром — иначе ItemsControl не обновляется.
    /// </summary>
    public void NotifyTagsChanged()
    {
        _tags = new List<string>(_tags);
        OnPropertyChanged(nameof(Tags));
    }

    /// <summary>Дерево метаданных конфигурации.</summary>
    public MetadataNode? MetadataRoot { get; set; }

    /// <summary>
    /// Возвращает строку соединения для отображения.
    /// </summary>
    public string ConnectionStringDisplay => Connection.ToConnectionString();

    /// <summary>
    /// Название группы для отображения. Базы без группы отображаются в группе «Без группы».
    /// </summary>
    public string GroupDisplay => string.IsNullOrWhiteSpace(Group) ? LocalizationManager.T("Group.NoGroup") : Group;

    /// <summary>
    /// Группа, в которой отображается база в общем списке. Закреплённые базы
    /// выводятся в отдельной группе «Закреплённые» вверху таблицы, независимо от их группы.
    /// </summary>
    public string DisplayGroup => IsPinned ? LocalizationManager.T("Main.Pinned") : GroupDisplay;

    /// <summary>
    /// Порядок группы для сортировки: закреплённые базы всегда идут первыми.
    /// </summary>
    public int GroupSortOrder => IsPinned ? 0 : 1;

    /// <summary>
    /// Возвращает путь к файловой базе в кавычках (без префикса File=).
    /// Для клиент-серверного режима возвращает строку соединения.
    /// </summary>
    public string ConnectionPathDisplay => Connection.Type switch
    {
        ConnectionType.File => $"\"{Connection.FilePath}\"",
        _ => Connection.ToConnectionString()
    };

    /// <summary>
    /// Тип подключения для отображения (файловая или клиент-серверная).
    /// </summary>
    public string ConnectionTypeDisplay => Connection.Type switch
    {
        ConnectionType.File => LocalizationManager.T("Infobase.Type.File"),
        ConnectionType.WebServer => LocalizationManager.T("Infobase.Type.WebServer"),
        _ => LocalizationManager.T("Infobase.Type.ClientServer")
    };

    private bool? _checkedAvailability;

    private bool _isChecking;

    /// <summary>
    /// Идёт ли проверка доступности этой базы командой «Проверить доступность всех баз».
    /// Пока проверка идёт, значок базы в списке показывается серым с индикатором ожидания
    /// (часики), а в подсказке — текст «Проверка доступности…».
    /// </summary>
    public bool IsChecking => _isChecking;

    /// <summary>
    /// Помечает базу как «проверяется» (true) или возвращает обычное отображение (false).
    /// Вызывается перед стартом проверки для всех баз; после завершения проверки конкретной
    /// базы результат применяется через <see cref="SetCheckedAvailability"/>, который сам
    /// сбрасывает признак проверки.
    /// </summary>
    public void SetChecking(bool checking)
    {
        if (_isChecking == checking)
            return;
        _isChecking = checking;
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(StatusIconKey));
        OnPropertyChanged(nameof(StatusColorHex));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    /// <summary>
    /// Доступность базы. По умолчанию — расчёт по параметрам подключения: для файловых
    /// баз — наличие каталога/файла на диске, для клиент-серверных и веб-баз реальная
    /// доступность удалённо не проверяется (чтобы не блокировать интерфейс сетевыми
    /// запросами). Если команда «Проверить доступность» задала результат проверки
    /// через <see cref="SetCheckedAvailability"/>, используется именно он.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (_checkedAvailability.HasValue)
                return _checkedAvailability.Value;
            return Connection.Type switch
            {
                ConnectionType.File => !string.IsNullOrWhiteSpace(Connection.FilePath)
                    && (Directory.Exists(Connection.FilePath) || File.Exists(Connection.FilePath)),
                ConnectionType.WebServer => !string.IsNullOrWhiteSpace(Connection.WebUrl),
                _ => !string.IsNullOrWhiteSpace(Connection.Server)
                    || !string.IsNullOrWhiteSpace(Connection.DatabaseName)
            };
        }
    }

    /// <summary>
    /// Задаёт результат фактической проверки доступности базы (или null — вернуть
    /// расчётное значение по параметрам подключения). Сбрасывает признак «проверяется»
    /// и обновляет статусные свойства, влияющие на иконку базы в списке (ключ, цвет, подсказка).
    /// </summary>
    public void SetCheckedAvailability(bool? available)
    {
        var wasChecking = _isChecking;
        if (_checkedAvailability == available && !wasChecking)
            return;
        _checkedAvailability = available;
        _isChecking = false;
        if (wasChecking)
            OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(StatusIconKey));
        OnPropertyChanged(nameof(StatusColorHex));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    /// <summary>
    /// Ключ иконки статуса базы для списка баз (геометрия из Icons.xaml / Icons.axaml):
    /// файловая — база данных, веб-сервер — глобус, клиент-серверная — сеть,
    /// во время проверки доступности — серые часики.
    /// Передаётся в IconKeyToGeometryConverter для отрисовки Path.
    /// <para>
    /// Недоступная база (issue #289) показывается ОРИГИНАЛЬНОЙ иконкой типа подключения,
    /// а не красным крестиком: цвет иконки задаётся отдельно свойством
    /// <see cref="StatusColorHex"/> (для недоступной — красный), поэтому форма значка
    /// всегда соответствует типу базы — файловая это или серверная, даже после
    /// команды «Проверить доступность всех баз».
    /// </para>
    /// <para>
    /// Issue #161: файловая база раньше рисовалась той же иконкой «папка» (IconFolder), что
    /// и группы, и отличалась от неё только цветом. Чтобы база отличалась от папки по форме,
    /// файловые базы теперь используют отдельную геометрию базы данных (IconDatabase),
    /// которая по форме не совпадает с IconFolder.
    /// </para>
    /// </summary>
    public string StatusIconKey => _isChecking
        ? "IconInProgress"
        : Connection.Type switch
        {
            ConnectionType.File => "IconDatabase",
            ConnectionType.WebServer => "IconWeb",
            _ => "IconNetwork"
        };

    /// <summary>
    /// Подпись статуса базы для подсказки к иконке в списке.
    /// Во время проверки доступности — «Проверка доступности…».
    /// </summary>
    public string StatusDisplay => _isChecking
        ? LocalizationManager.T("Infobase.Checking")
        : !IsAvailable
            ? Connection.Type switch
            {
                ConnectionType.File => LocalizationManager.T("Infobase.Unavailable.File"),
                ConnectionType.WebServer => LocalizationManager.T("Infobase.Unavailable.Web"),
                _ => LocalizationManager.T("Infobase.Unavailable.ClientServer")
            }
            : Connection.Type switch
            {
                ConnectionType.File => LocalizationManager.T("Infobase.Status.File"),
                ConnectionType.WebServer => LocalizationManager.T("Infobase.Status.Web"),
                _ => LocalizationManager.T("Infobase.Status.ClientServer")
            };

    /// <summary>
    /// Цвет иконки статуса базы (ARGB-строка) в зависимости от типа подключения
    /// и доступности: файловая — янтарный, веб-сервер — синий, клиент-серверная —
    /// фиолетовый, недоступная — красный, во время проверки — серый.
    /// </summary>
    public string StatusColorHex => _isChecking
        ? "#9E9E9E"
        : !IsAvailable
            ? "#E53935"
            : Connection.Type switch
            {
                ConnectionType.File => "#E8A33D",
                ConnectionType.WebServer => "#3B82F6",
                _ => "#8B5CF6"
            };

    /// <summary>
    /// Режим запуска для отображения (локализованный). Используется в колонке
    /// «Режим запуска», строке состояния и в карточке базы. Каноническое строковое
    /// значение (<see cref="LaunchMode"/>) при этом не изменяется — оно остаётся для
    /// хранения и сравнения. Неизвестные значения выводятся как есть (fallback).
    /// </summary>
    public string ParsedLaunchMode
    {
        get
        {
            // Каноническое значение не меняется; сравнение регистронезависимое
            // (ToLowerInvariant покрывает «Автоматический»→«автоматический» и т.д.).
            var mode = (LaunchMode ?? string.Empty).Trim().ToLowerInvariant();
            return mode switch
            {
                "" or "автоматический" => LocalizationManager.T("Connection.LaunchAuto"),
                "тонкий клиент" or "тонкий клиент (управляемое приложение)" => LocalizationManager.T("Connection.LaunchThin"),
                "толстый клиент" or "толстый клиент (управляемое приложение)" => LocalizationManager.T("Connection.LaunchThickManaged"),
                "толстый клиент (обычные формы)" => LocalizationManager.T("Connection.LaunchThickOrdinary"),
                "веб-клиент" => LocalizationManager.T("Connection.LaunchWeb"),
                _ => LaunchMode ?? string.Empty
            };
        }
    }

    /// <summary>
    /// Сервер или база для отображения. Для файлового режима — путь к базе,
    /// для клиент-серверного — сервер и имя базы. Используется в колонке «Сервер/База».
    /// </summary>
    public string ServerDatabaseDisplay => Connection.Type switch
    {
        ConnectionType.File => string.IsNullOrWhiteSpace(Connection.FilePath)
            ? "—"
            : Connection.FilePath,
        _ => string.IsNullOrWhiteSpace(Connection.Server)
            ? Connection.DatabaseName
            : $"{Connection.Server}\\{Connection.DatabaseName}"
    };

    /// <summary>
    /// Дата последнего запуска для отображения.
    /// </summary>
    public string LastLaunchDisplay =>
        LastLaunchDate.HasValue
            ? LastLaunchDate.Value.ToString("dd.MM.yyyy HH:mm")
            : LocalizationManager.T("Infobase.LastLaunch.Never");

    /// <summary>История запусков (глубина — настройка <c>MaxLaunchHistoryPerBase</c>, по умолчанию 30).</summary>
    private List<LaunchHistoryEntry> _launchHistory = new();

    public List<LaunchHistoryEntry> LaunchHistory
    {
        get => _launchHistory;
        set
        {
            _launchHistory = value ?? new List<LaunchHistoryEntry>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LaunchHistoryDisplay));
        }
    }

    /// <summary>Краткий текст для UI: число записей / последний.</summary>
    public string LaunchHistoryDisplay =>
        _launchHistory.Count == 0
            ? LocalizationManager.T("Infobase.History.Empty")
            : string.Format(
                LocalizationManager.T("Infobase.History.Summary"),
                _launchHistory.Count,
                _launchHistory[0].Timestamp.ToString("dd.MM HH:mm"));

    /// <summary>
    /// Добавить запись в историю (новые сверху). Глубина истории (issue #246) берётся
    /// из глобальной настройки <see cref="AppSettings.MaxLaunchHistoryPerBase"/>; при
    /// превышении лимита самые старые записи удаляются. Явно переданный
    /// <paramref name="maxHistory"/> имеет приоритет над настройкой (используется редко,
    /// например из тестов), но существующие вызовы без него продолжают работать как раньше.
    /// </summary>
    public void AddLaunchHistory(string mode, string details = "", int? maxHistory = null)
    {
        _launchHistory.Insert(0, new LaunchHistoryEntry
        {
            Timestamp = DateTime.Now,
            Mode = mode,
            Details = details ?? ""
        });
        var max = maxHistory > 0 ? maxHistory.Value : ResolveMaxLaunchHistory();
        while (_launchHistory.Count > max)
            _launchHistory.RemoveAt(_launchHistory.Count - 1);
        LastLaunchDate = DateTime.Now;
        OnPropertyChanged(nameof(LaunchHistory));
        OnPropertyChanged(nameof(LaunchHistoryDisplay));
        OnPropertyChanged(nameof(LastLaunchDisplay));
    }

    /// <summary>
    /// Возвращает глубину истории из глобальной настройки (issue #246). Считывается из
    /// <c>settings.json</c> так же, как это делает <see cref="Services.ConfigurationInfoService"/>
    /// для таймаута COM: значения небольшие, чтение файла происходит редко (только при
    /// фактическом добавлении записи истории). Если настройка недоступна или некорректна —
    /// используется значение по умолчанию 30.
    /// </summary>
    private static int ResolveMaxLaunchHistory()
    {
        try
        {
            var settings = Configuration_Management.AppServices
                .GetRequiredService<Configuration_Management.Services.IInfobaseRepository>()
                .LoadSettings();
            return settings != null && settings.MaxLaunchHistoryPerBase > 0
                ? settings.MaxLaunchHistoryPerBase
                : 30;
        }
        catch
        {
            return 30;
        }
    }

    private long? _fileSizeBytes;
    private bool _fileSizeResolved;

    /// <summary>Размер файловой ИБ в байтах (null — не файловая / не посчитан).</summary>
    public long? FileSizeBytes
    {
        get => _fileSizeBytes;
        set
        {
            if (SetProperty(ref _fileSizeBytes, value))
            {
                _fileSizeResolved = true;
                OnPropertyChanged(nameof(FileSizeDisplay));
            }
        }
    }

    private long? _manualSizeBytes;

    /// <summary>
    /// Размер базы, заданный пользователем вручную в байтах (issue #243).
    /// Позволяет хранить размеры клиент-серверных баз (например, полученные
    /// запросом в СУБД), не держа их в комментариях. Если задан — используется
    /// при отображении вместо автоматического значения; для файловых баз
    /// автоматический расчёт продолжает работать, пока ручное не задано.
    /// null — ручной размер не задан.
    /// </summary>
    public long? ManualSizeBytes
    {
        get => _manualSizeBytes;
        set
        {
            if (SetProperty(ref _manualSizeBytes, value))
                OnPropertyChanged(nameof(FileSizeDisplay));
        }
    }

    /// <summary>Размер для колонки списка.</summary>
    public string FileSizeDisplay
    {
        get
        {
            if (_manualSizeBytes.HasValue)
                return FormatSize(_manualSizeBytes.Value);
            if (Connection.Type != ConnectionType.File)
                return "—";
            if (!_fileSizeResolved || !_fileSizeBytes.HasValue)
                return "…";
            return FormatSize(_fileSizeBytes.Value);
        }
    }

    private DateTime? _lastWriteTimeUtc;
    private bool _lastWriteResolved;

    /// <summary>
    /// Время последнего изменения файла информационной базы (UTC), null — не файловая
    /// или ещё не считано. Заполняется при расчёте метаданных файловой базы
    /// (<see cref="FileSizeBytes"/>) и используется колонкой «Дата изменений» (Этап 13).
    /// </summary>
    public DateTime? FileLastWriteTimeUtc
    {
        get => _lastWriteTimeUtc;
        set
        {
            if (SetProperty(ref _lastWriteTimeUtc, value))
            {
                _lastWriteResolved = true;
                OnPropertyChanged(nameof(LastModifiedDisplay));
            }
        }
    }

    /// <summary>Дата последнего изменения файла ИБ для колонки «Дата изменений» списка баз.</summary>
    public string LastModifiedDisplay
    {
        get
        {
            if (Connection.Type != ConnectionType.File)
                return "—";
            if (!_lastWriteResolved || !_lastWriteTimeUtc.HasValue)
                return "…";
            return _lastWriteTimeUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        }
    }

    public static string FormatSize(long bytes)
    {
        // Локализованные единицы размера берём из общего механизма (CacheClean.SizeUnits):
        // «Б,КБ,МБ,ГБ,ТБ» для русского и «B,KB,MB,GB,TB» для английского.
        var units = LocalizationManager.T("CacheClean.SizeUnits")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        var number = index == 0 ? value.ToString("0") : value.ToString("0.0");
        return $"{number} {units[index]}";
    }

    /// <summary>
    /// Инициалы базы для отображения в аватаре списка.
    /// </summary>
    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
                return LocalizationManager.T("Model.DefaultBaseName");

            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return LocalizationManager.T("Model.DefaultBaseName");

            if (parts.Length == 1)
                return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();

            return (parts[0][0].ToString() + parts[1][0].ToString()).ToUpperInvariant();
        }
    }

    private long _cacheSizeBytes = -1;
    private int _cacheSizeGeneration;

    /// <summary>
    /// Размер локального кеша 1С базы в байтах (суммарно программный + пользовательский).
    /// Значение -1 означает, что размер ещё не вычислен — в правой панели показывается «…».
    /// </summary>
    public long CacheSizeBytes
    {
        get => _cacheSizeBytes;
        private set
        {
            if (SetProperty(ref _cacheSizeBytes, value))
                OnPropertyChanged(nameof(CacheSizeDisplay));
        }
    }

    /// <summary>Человекочитаемый размер кеша для правой панели («…», пока размер не вычислен).</summary>
    public string CacheSizeDisplay =>
        _cacheSizeBytes < 0 ? "…" : FormatSize(_cacheSizeBytes);

    /// <summary>
    /// Асинхронно вычисляет размер кеша базы (программный + пользовательский) в фоновом
    /// потоке и обновляет <see cref="CacheSizeDisplay"/>. Устаревший результат игнорируется,
    /// если за время вычисления было запрошено новое вычисление (например, перевыбрана база).
    /// </summary>
    public void RefreshCacheSizeAsync()
    {
        var generation = ++_cacheSizeGeneration;
        CacheSizeBytes = -1;

        _ = Task.Run(() => Services.OneCCacheCleaner.GetSize(this, Services.OneCCacheKind.All))
            .ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled || generation != _cacheSizeGeneration)
                    return;
                CacheSizeBytes = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}