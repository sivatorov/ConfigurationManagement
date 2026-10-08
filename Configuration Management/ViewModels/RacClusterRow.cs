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
    /// Текст для выпадающего списка: «Имя (порт)». Правила формирования — единые с
    /// <see cref="RacCluster.ToString()"/> (0.3.9.330, issue #324): пустое или служебное
    /// значение <c>name</c> (текст ключа «name», GUID-первичный ключ) заменяется хостом
    /// кластера, при пустом хосте — нейтральным плейсхолдером с портом: пользователь
    /// видит значение, а не «ключ вместо значения».
    /// </summary>
    public string DisplayText => _cluster.ToString();

    /// <summary>
    /// <c>ToString()</c> возвращает <see cref="DisplayText"/> (0.3.9.330, issue #324):
    /// WPF-ComboBox с пользовательским шаблоном ModernComboBox в некоторых случаях
    /// показывает выбранный элемент через ToString — вместо «Configuration_Management.
    /// ViewModels.RacClusterRow» в поле должно быть читаемое имя кластера.
    /// </summary>
    public override string ToString() => DisplayText;

    /// <summary>Оригинальная модель кластера (для передачи в команды rac).</summary>
    public RacCluster Cluster => _cluster;
}
