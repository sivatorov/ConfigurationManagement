using System.Collections.Generic;

namespace Configuration_Management.Models;

/// <summary>Тип варианта цепочки обновлений (issue #352).</summary>
public enum UpdateChainKind
{
    /// <summary>Вариант 1 «снизу вверх»: каждый шаг — максимальная версия,
    /// на которую можно прыгнуть с текущей (жадный поиск).</summary>
    BottomUp,

    /// <summary>Вариант 2 «оптимальный»: минимальное число прыжков (поиск по графу).</summary>
    Optimal,
}

/// <summary>
/// Один вариант цепочки обновлений конфигурации: номер (1..n) и последовательность
/// версий от текущей до последней (в порядке установки — от текущей к целевой).
/// Шаги — релизы каталога <c>releases.1c.ru</c> (версия + ссылка version_files +
/// список версий, из которых можно обновиться напрямую).
/// </summary>
public sealed class UpdateChainVariant
{
    /// <summary>Порядковый номер варианта в таблице окна (1, 2, …).</summary>
    public int Number { get; init; }

    /// <summary>Тип варианта: снизу вверх или оптимальный.</summary>
    public UpdateChainKind Kind { get; init; }

    /// <summary>Версии цепочки от текущей к последней (включая последнюю; без текущей).</summary>
    public IReadOnlyList<PlatformRelease> Steps { get; init; } = new List<PlatformRelease>();
}

/// <summary>
/// Результат построения цепочек обновлений для пары «текущая версия → последняя версия»
/// (issue #352): возможно ли прямое обновление, какие варианты цепочки построены.
/// Чистый результат алгоритма <see cref="Services.UpdateChainBuilder"/> — без сети и UI.
/// </summary>
public sealed class UpdateChainSet
{
    /// <summary>True — хотя бы у одного релиза каталога есть данные «Список версий»
    /// (иначе цепочка не строится: совместимость версий неизвестна).</summary>
    public bool HasSourceData { get; init; }

    /// <summary>True — текущую версию можно обновить напрямую до последней
    /// (последняя входит в «Список версий»… точнее, текущая входит в её список).</summary>
    public bool IsDirectUpdate { get; init; }

    /// <summary>Варианты цепочки (0..2): №1 — снизу вверх (если построен),
    /// №2 — оптимальный (если построен и отличается от №1).</summary>
    public IReadOnlyList<UpdateChainVariant> Variants { get; init; } = new List<UpdateChainVariant>();
}