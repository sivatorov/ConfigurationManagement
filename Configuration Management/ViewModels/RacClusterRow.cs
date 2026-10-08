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
    /// «ключ вместо значения»: сначала пробуем хост кластера («ALF (27541)»), и только
    /// если хоста нет — нейтральный плейсхолдер с портом.
    /// «Неосмысленные» имена (сам текст ключа, GUID-идентификатор) также заменяются:
    /// пользователь видит значение, а не служебные данные парсера.
    /// </summary>
    public string DisplayText
    {
        get
        {
            var name = IsMeaningfulName(Name) ? Name.Trim() : string.Empty;
            var host = IsMeaningfulName(_cluster.Host) ? _cluster.Host.Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                // Запасная подпись: хост кластера вместо пустого имени (issue #324).
                var fallback = string.IsNullOrWhiteSpace(host) ? null : host;
                if (fallback is null)
                    return Port > 0 ? $"({Port})" : "—";
                name = fallback;
            }
            return Port > 0 ? $"{name} ({Port})" : name;
        }
    }

    /// <summary>True — строка выглядит как осмысленное имя кластера: не пустая,
    /// не равна тексту ключа «name» и не является GUID (первичным ключом кластера,
    /// который парсер мог ошибочно принять за имя; issue #324).</summary>
    private static bool IsMeaningfulName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var v = value.Trim();
        if (System.Guid.TryParse(v, out _))
            return false;
        if (string.Equals(v, "name", System.StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>Оригинальная модель кластера (для передачи в команды rac).</summary>
    public RacCluster Cluster => _cluster;
}