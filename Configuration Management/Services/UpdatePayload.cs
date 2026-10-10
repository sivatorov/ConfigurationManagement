using System.IO;
using System.IO.Compression;
using System.Text;

namespace Configuration_Management.Services;

/// <summary>
/// Защита загрузчика автообновления от «не того» файла (issue #358). Релиз может
/// отдать ZIP-архив с exe внутри (пользователь выложил архив вместо/вместе с bare-exe)
/// или вовсе HTML-страницу (редирект на страницу скачивания, ошибка сервиса). Прежний
/// помощник делал Move-Item любого скачанного файла на место exe — «не запускается».
/// Теперь скачанный файл проходит проверку сигнатуры:
///  - <c>MZ</c> (PE) — штатный путь, файл возвращается как есть;
///  - <c>PK\x03\x04</c> (ZIP) — из архива извлекается <c>ConfigurationManagement.exe</c>
///    и устанавливается он;
///  - иное (HTML и пр.) — файл удаляется, возвращается null (ошибка «не удалось
///    скачать обновление» вместо установки битого файла).
/// Логика кроссплатформенная и тестируется без сети (см. UpdatePayloadTests).
/// </summary>
public static class UpdatePayload
{
    /// <summary>Сигнатура начала скачанного файла.</summary>
    public enum PayloadSignature
    {
        /// <summary>PE-образ (самораспаковывающийся single-file exe, «MZ»).</summary>
        PortableExecutable,
        /// <summary>ZIP-архив («PK\x03\x04») — вероятно, exe упакован внутрь.</summary>
        ZipArchive,
        /// <summary>Ни PE, ни ZIP — HTML/обрезок/что-то ещё: устанавливать нельзя.</summary>
        Unknown,
    }

    /// <summary>Имя исполняемого файла, который ищется внутри ZIP-архива.</summary>
    internal const string PayloadEntryName = "ConfigurationManagement.exe";

    /// <summary>Суффикс распакованного из архива exe (рядом со скачанным файлом).</summary>
    private const string ExtractedSuffix = ".extracted.exe";

    /// <summary>Имя журнала проверки сигнатуры (рядом с журналами обновления).</summary>
    private const string LogFileName = "update-payload.log";

    /// <summary>
    /// Определяет сигнатуру существующего файла по первым четырём байтам.
    /// При любой ошибке чтения возвращает <see cref="PayloadSignature.Unknown"/>.
    /// </summary>
    public static PayloadSignature DetectSignature(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> buffer = stackalloc byte[4];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer[read..]);
                if (n == 0)
                    break;
                read += n;
            }

            return Classify(buffer[..read]);
        }
        catch
        {
            return PayloadSignature.Unknown;
        }
    }

    /// <summary>
    /// Классифицирует первые байты файла: «MZ» — PE, «PK\x03\x04» — ZIP, иное — Unknown.
    /// </summary>
    public static PayloadSignature Classify(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
            return PayloadSignature.PortableExecutable;

        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
            return PayloadSignature.ZipArchive;

        return PayloadSignature.Unknown;
    }

    /// <summary>
    /// Гарантирует, что по пути <paramref name="downloadedFile"/> лежит устанавливаемый
    /// exe. Возвращает путь к exe: исходный файл (PE) либо распакованный из ZIP
    /// <c>ConfigurationManagement.exe</c> (сохраняется рядом с исходным файлом с суффиксом
    /// <c>.extracted.exe</c>). Возвращает null и удаляет исходный файл, если это ни PE,
    /// ни ZIP (HTML-страница и т.п.) либо ZIP без exe внутри. Каждое решение пишется
    /// в журнал <paramref name="logDir"/>/update-payload.log (если каталог задан).
    /// </summary>
    public static string? EnsureExecutablePayload(string downloadedFile, string? logDir = null)
    {
        var signature = DetectSignature(downloadedFile);
        switch (signature)
        {
            case PayloadSignature.PortableExecutable:
                LogLine(logDir, $"Сигнатура MZ (PE): {Path.GetFileName(downloadedFile)} — штатный путь.");
                return downloadedFile;

            case PayloadSignature.ZipArchive:
                return TryExtractExecutable(downloadedFile, logDir);

            default:
                LogLine(logDir, $"Сигнатура не распознана (не exe/zip): {Path.GetFileName(downloadedFile)} — файл удалён, обновление отклонено.");
                TryDelete(downloadedFile);
                return null;
        }
    }

    /// <summary>
    /// Ищет в ZIP-архиве запись <c>ConfigurationManagement.exe</c> (точное имя файла,
    /// возможно в подкаталоге) и извлекает её рядом с архивом. Если точной записи нет,
    /// но архив содержит ровно один exe — берётся он. При отсутствии подходящей записи
    /// архив удаляется и возвращается null.
    /// </summary>
    private static string? TryExtractExecutable(string zipPath, string? logDir)
    {
        try
        {
            string? entryName = null;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                // Точный приоритет: запись с именем ConfigurationManagement.exe.
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.EndsWith("/" + PayloadEntryName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, PayloadEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        entryName = entry.FullName;
                        break;
                    }
                }

                // Запасной вариант: ровно один exe в архиве.
                if (entryName is null)
                {
                    var exeEntries = archive.Entries
                        .Where(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (exeEntries.Count == 1)
                        entryName = exeEntries[0].FullName;
                }
            }

            if (entryName is null)
            {
                LogLine(logDir, $"ZIP {Path.GetFileName(zipPath)} не содержит {PayloadEntryName} — файл удалён, обновление отклонено.");
                TryDelete(zipPath);
                return null;
            }

            var dir = Path.GetDirectoryName(zipPath);
            if (string.IsNullOrEmpty(dir))
                dir = ".";
            var destPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(zipPath) + ExtractedSuffix);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.GetEntry(entryName.Replace('\\', '/'))
                    ?? archive.Entries.First(e => string.Equals(
                        e.FullName.Replace('\\', '/'), entryName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(destPath, overwrite: true);
            }

            LogLine(logDir, $"ZIP {Path.GetFileName(zipPath)}: извлечён {entryName} → {Path.GetFileName(destPath)} ({new FileInfo(destPath).Length} байт).");
            return destPath;
        }
        catch (Exception ex)
        {
            LogLine(logDir, $"Ошибка распаковки ZIP {Path.GetFileName(zipPath)}: {ex.Message} — файл удалён, обновление отклонено.");
            TryDelete(zipPath);
            return null;
        }
    }

    /// <summary>Пишет строку в журнал проверки сигнатуры (каталог рядом с логами обновления).</summary>
    private static void LogLine(string? logDir, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logDir))
                return;
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, LogFileName),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // Журнал не критичен: решение уже принято.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Не критично — временный файл останется в %TEMP%.
        }
    }
}
