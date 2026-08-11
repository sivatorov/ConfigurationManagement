using System.ComponentModel;
using System.Runtime.CompilerServices;

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

    private bool _isFavorite;

    /// <summary>Признак избранной базы.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
    }

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

    /// <summary>Дата и время последнего запуска базы.</summary>
    public DateTime? LastLaunchDate { get; set; }

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

    /// <summary>Версия платформы 1С.</summary>
    public string PlatformVersion { get; set; } = string.Empty;

    /// <summary>Режим запуска (Автоматический, Тонкий клиент, Толстый клиент, Веб-клиент).</summary>
    public string LaunchMode { get; set; } = "Автоматический";

    /// <summary>Дополнительные параметры запуска платформы 1С (например, /UC, /DisableStartupMessages и др.).</summary>
    public string LaunchParameters { get; set; } = string.Empty;

    /// <summary>Разрядность платформы при запуске базы («32» или «64» бита).</summary>
    public string Architecture { get; set; } = "32";

    /// <summary>Тип клиента (тонкий или толстый).</summary>
    public string ClientType { get; set; } = "Тонкий";

    /// <summary>Описание базы.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Теги базы данных.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Дерево метаданных конфигурации.</summary>
    public MetadataNode? MetadataRoot { get; set; }

    /// <summary>
    /// Возвращает строку соединения для отображения.
    /// </summary>
    public string ConnectionStringDisplay => Connection.ToConnectionString();

    /// <summary>
    /// Название группы для отображения. Базы без группы отображаются в группе «Без группы».
    /// </summary>
    public string GroupDisplay => string.IsNullOrWhiteSpace(Group) ? "Без группы" : Group;

    /// <summary>
    /// Группа, в которой отображается база в общем списке. Закреплённые базы
    /// выводятся в отдельной группе «Закреплённые» вверху таблицы, независимо от их группы.
    /// </summary>
    public string DisplayGroup => IsPinned ? "Закреплённые" : GroupDisplay;

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
        ConnectionType.File => "Файловая",
        _ => "Клиент-серверная"
    };

    /// <summary>
    /// Режим запуска для отображения. Используется в колонке «Режим запуска».
    /// </summary>
    public string ParsedLaunchMode => string.IsNullOrWhiteSpace(LaunchMode)
        ? "Автоматический"
        : LaunchMode;

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
            : "Не запускалась";

    /// <summary>
    /// Инициалы базы для отображения в аватаре списка.
    /// </summary>
    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
                return "1С";

            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "1С";

            if (parts.Length == 1)
                return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();

            return (parts[0][0].ToString() + parts[1][0].ToString()).ToUpperInvariant();
        }
    }
}