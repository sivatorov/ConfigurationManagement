namespace Configuration_Management.Services;

/// <summary>
/// Вариант выбора файла релиза при одиночном скачивании (issue #352.1): со страницы
/// файлов версии (<c>version_files?…</c>) пользователь выбирает «Дистрибутив обновления»
/// (.cf-обновление, приоритет) или «Полный дистрибутив». Подпись задаётся ключом
/// локализации, чтобы окна WPF и Avalonia показывали её одинаково.
/// </summary>
/// <param name="CaptionKey">Ключ локализации подписи («Updates.FileChoice.UpdateDistribution» /
/// «Updates.FileChoice.FullDistribution»).</param>
/// <param name="Url">Ссылка скачивания (резолвленная до файлового эндпоинта/бинарника).</param>
/// <param name="FileName">Имя файла дистрибутива (для имени сохранения и подписи).</param>
public sealed record UpdateFileChoice(string CaptionKey, string Url, string FileName)
{
    /// <summary>Ключ подписи «Дистрибутив обновления» (приоритетный вариант, .cf).</summary>
    public const string UpdateDistributionCaptionKey = "Updates.FileChoice.UpdateDistribution";

    /// <summary>Ключ подписи «Полный дистрибутив».</summary>
    public const string FullDistributionCaptionKey = "Updates.FileChoice.FullDistribution";
}
