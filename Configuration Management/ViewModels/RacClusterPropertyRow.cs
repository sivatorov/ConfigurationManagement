using System;
using Configuration_Management.Localization;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Строка вкладки «Информация о кластере» окна «Серверы 1С» в виде «свойство — значение»
/// (issue #324, C3: свойства кластера показываются в виде, доступном для правки, а не
/// просто текстом). Редактируется ОГРАНИЧЕННЫЙ безопасный набор параметров
/// (см. <see cref="RacClusterUpdate"/>): имя, таймауты, лимиты памяти, уровень
/// безопасности, пинг, параметры аутентификации. Остальные свойства — только чтение.
/// Порт кластера rac не меняет — он не входит в набор правки.
/// </summary>
public sealed class RacClusterPropertyRow : ViewModelBase
{
    /// <summary>Исходный ключ свойства из вывода rac (например «expiration-timeout»).</summary>
    public string RawKey { get; }

    /// <summary>Локализованное имя свойства для показа пользователю.</summary>
    public string DisplayName { get; }

    /// <summary>Можно ли править значение (безопасный набор параметров).</summary>
    public bool IsEditable { get; }

    /// <summary>Значение из вывода rac до правки (эталон для поиска изменений).</summary>
    public string OriginalValue { get; }

    private string _editValue;

    public RacClusterPropertyRow(string rawKey, string value, bool editable)
    {
        RawKey = rawKey ?? string.Empty;
        DisplayName = LocalizeKey(RawKey);
        IsEditable = editable;
        OriginalValue = value ?? string.Empty;
        _editValue = OriginalValue;
    }

    /// <summary>Текущее значение в поле правки.</summary>
    public string EditValue
    {
        get => _editValue;
        set
        {
            if (SetProperty(ref _editValue, value ?? string.Empty))
                OnPropertyChanged(nameof(IsChanged));
        }
    }

    /// <summary>Значение изменено относительно вывода rac (и строка вообще правится).</summary>
    public bool IsChanged =>
        IsEditable && !string.Equals(_editValue, OriginalValue, StringComparison.Ordinal);

    /// <summary>
    /// Локализованное имя свойства по ключу rac (дефисы/подчёркивания нормализуются:
    /// «expiration-timeout» → «ServerMonitor.ClusterProperty.ExpirationTimeout»);
    /// для неизвестных ключей показывается исходный ключ rac.
    /// </summary>
    private static string LocalizeKey(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return rawKey ?? string.Empty;

        var normalized = new System.Text.StringBuilder(rawKey.Length);
        var upperNext = true;
        foreach (var ch in rawKey)
        {
            if (ch is '-' or '_' or '.')
            {
                upperNext = true;
                continue;
            }
            normalized.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
            upperNext = false;
        }

        var key = "ServerMonitor.ClusterProperty." + normalized;
        var localized = LocalizationManager.T(key);
        // LocalizationManager.T возвращает сам ключ при отсутствии перевода —
        // в этом случае показываем исходный ключ rac (свойство «как есть»).
        return string.Equals(localized, key, StringComparison.Ordinal) ? rawKey : localized;
    }
}
