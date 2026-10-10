namespace Configuration_Management.Services;

/// <summary>
/// Чистая логика диалога повтора цепочки обновлений (issue #352.3): обратный отсчёт
/// кнопки «Да» (60 с) и формат её подписи. Вынесена без UI, чтобы покрываться
/// юнит-тестами (<see cref="ChainRetryCountdown"/> не зависит от WPF/Avalonia).
/// </summary>
public static class ChainRetryCountdown
{
    /// <summary>Длительность обратного отсчёта кнопки «Да», секунд.</summary>
    public const int DefaultSeconds = 60;

    /// <summary>Подпись кнопки с обратным отсчётом: «Да (57)». Нулевой/отрицательный
    /// остаток даёт подпись без числа (кнопка уже недоступна).</summary>
    public static string ButtonText(string baseText, int secondsLeft)
        => secondsLeft > 0 ? $"{baseText} ({secondsLeft})" : baseText;

    /// <summary>True — отсчёт завершён (пора автоматически отвечать «Да» —
    /// повтор начинается без участия пользователя, issue #352.3).</summary>
    public static bool IsFinished(int secondsLeft) => secondsLeft <= 0;

    /// <summary>Текст вопроса диалога: «Цепочка скачалась с ошибками: N из M»
    /// (подпись ошибки) — строится здесь, чтобы WPF и Avalonia совпадали.</summary>
    public static string BuildMessage(string localizedTemplate, int failed, int total)
        => string.Format(localizedTemplate, failed, total);
}
