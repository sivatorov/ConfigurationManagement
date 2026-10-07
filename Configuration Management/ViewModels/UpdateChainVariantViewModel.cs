using System;
using System.Collections.Generic;
using System.Linq;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Строка таблицы вариантов цепочки обновлений окна «Проверка обновлений» (issue #352):
/// номер варианта, тип (снизу вверх / оптимальный) и список версий цепочки.
/// Чистый класс без платформенных зависимостей — используется и Windows/WPF, и Linux/Avalonia.
/// </summary>
public sealed class UpdateChainVariantViewModel : ViewModelBase
{
    private readonly UpdateChainVariant _variant;

    /// <param name="variant">Вариант цепочки (результат алгоритма построения).</param>
    public UpdateChainVariantViewModel(UpdateChainVariant variant)
    {
        _variant = variant ?? throw new ArgumentNullException(nameof(variant));
    }

    /// <summary>Порядковый номер варианта (1, 2, …).</summary>
    public int Number => _variant.Number;

    /// <summary>Тип варианта: снизу вверх или оптимальный.</summary>
    public UpdateChainKind Kind => _variant.Kind;

    /// <summary>Шаги цепочки: версии от текущей к последней (включая последнюю).</summary>
    public IReadOnlyList<PlatformRelease> Steps => _variant.Steps;

    /// <summary>Список версий цепочки через «→», например «3.0.150.5 → 3.0.158.71 → 3.0.160.12».</summary>
    public string VersionsText => string.Join(" → ", Steps.Select(s => s.Version));

    /// <summary>Локализованный заголовок варианта: «Вариант N: снизу вверх (максимальный шаг)»
    /// либо «Вариант N: оптимальный (минимум шагов)».</summary>
    public string KindText
    {
        get
        {
            var key = Kind == UpdateChainKind.BottomUp
                ? "Updates.Chain.VariantBottomUp"
                : "Updates.Chain.VariantOptimal";
            return string.Format(LocalizationManager.T(key), Number);
        }
    }
}