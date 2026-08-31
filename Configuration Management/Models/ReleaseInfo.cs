namespace Configuration_Management.Models;

/// <summary>
/// Информация о выпуске (release) приложения, полученная из GitHub Releases API.
/// </summary>
public class ReleaseInfo
{
    /// <summary>Имя тега выпуска, например «v0.3.5.71».</summary>
    public string TagName { get; set; } = string.Empty;

    /// <summary>Название выпуска (может отличаться от тега или быть пустым).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Описание выпуска (Markdown-текст заметок к версии).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Признак предварительного (pre-release) выпуска.</summary>
    public bool Prerelease { get; set; }

    /// <summary>Дата публикации выпуска, если известна.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// Прямая ссылка на Windows-инсталлятор (.exe) из assets выпуска.
    /// null — если подходящий asset не найден.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// URL страницы релиза на GitHub (html_url из API или href из Atom-ленты).
    /// Используется как fallback, когда прямая ссылка на asset недоступна.
    /// </summary>
    public string? HtmlUrl { get; set; }
}