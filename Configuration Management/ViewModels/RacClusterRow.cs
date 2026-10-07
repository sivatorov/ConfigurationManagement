using Configuration_Management.Models;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Строка выбора кластера в окне «Серверы 1С» (0.3.9.124): обёртка над
/// <see cref="RacCluster"/> с текстом для ComboBox/ListBox. Чистый .NET —
/// без платформенных зависимостей (WPF и Avalonia).
/// </summary>
public sealed class RacClusterRow
{
    private readonly RacCluster _cluster;

    public RacClusterRow(RacCluster cluster)
    {
        _cluster = cluster ?? throw new System.ArgumentNullException(nameof(cluster));
    }

    /// <summary>Идентификатор кластера (для команд rac --cluster=...).</summary>
    public System.Guid Id => _cluster.Id;

    /// <summary>Имя кластера.</summary>
    public string Name => _cluster.Name;

    /// <summary>Порт кластера.</summary>
    public int Port => _cluster.Port;

    /// <summary>
    /// Текст для выпадающего списка: «Имя (порт)», порт 0 опускается. Пустое имя
    /// (issue #324: rac не заполнил ключ «name» в выводе) не должно отображаться как
    /// «ключ вместо значения» — показываем нейтральный плейсхолдер с портом.
    /// </summary>
    public string DisplayText =>
        string.IsNullOrWhiteSpace(Name)
            ? (Port > 0 ? $"({Port})" : "—")
            : (Port > 0 ? $"{Name} ({Port})" : Name);

    /// <summary>Оригинальная модель кластера (для передачи в команды rac).</summary>
    public RacCluster Cluster => _cluster;
}