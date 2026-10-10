namespace Configuration_Management.Models;

/// <summary>
/// Файл дистрибутива конкретной версии технологической платформы 1С из каталога
/// <c>releases.1c.ru</c>. Заполняется парсером ответа <c>version_files?nick=…&ver=…</c>.
/// </summary>
public class PlatformReleaseFile
{
    /// <summary>Имя файла, например «8.3.27.2214_x64.zip».</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Прямая ссылка на файл (с учётом возможной query-части).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Размер файла в байтах (0 — неизвестен).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Разрядность дистрибутива: «x64»/«x86» или null, если не определена.</summary>
    public string? Architecture { get; set; }

    /// <summary>Тип дистрибутива по расширению файла.</summary>
    public PlatformDistributionKind Kind { get; set; }

    /// <summary>Заголовок группы со страницы файлов релиза (issue #330 п.2/#334 п.2):
    /// жирные заголовки страницы — «Технологическая платформа», «Тонкий клиент
    /// 1С:Предприятия», «Клиент 1С:Предприятия», «Сервер 1С:Предприятия» и т.п.
    /// null/пусто — группы на странице не обнаружены (старые релизы, JSON-ответ):
    /// слой отображения подставляет локализованную фолбэк-группу
    /// «Файлы релиза» (ключ <c>PlatformDownload.Group.Default</c>).</summary>
    public string? Group { get; set; }
}