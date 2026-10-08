using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Configuration_Management.Services;

/// <summary>
/// Персистентный кэш рабочего формата команды rac «job list» (issue #324):
/// rac 8.5.4.1878 отклоняет аргумент «--cluster=<uuid>» (код -1), rac 8.3 принимает.
/// Рабочий формат запоминается по ключу подключения (address:port|user|clusterId) в файле
/// <c>rac_joblist_formats.json</c> каталога данных приложения, чтобы после перезапуска
/// приложения не тратить ~1 с на заведомо падающую первую попытку при каждой первой
/// загрузке данных кластера («подключение долгое, локально почти 5 секунд», issue #324).
/// Ошибки чтения/записи глушатся: кэш — оптимизация, а не функциональность.
/// </summary>
public static class RacJobListFormatStore
{
    /// <summary>Имя файла кэша в каталоге данных приложения.</summary>
    public const string FileName = "rac_joblist_formats.json";

    /// <summary>
    /// Загружает карту «ключ подключения → индекс формата» (0 = "--cluster=<uuid>",
    /// 1 = "--cluster <uuid>", 2 = позиционный "<uuid>", issue #324).
    /// Битый/отсутствующий файл возвращает пустую карту.
    /// </summary>
    public static Dictionary<string, int> Load()
    {
        var path = ResolvePath();
        if (path is null || !File.Exists(path))
            return new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            using var stream = File.OpenRead(path);
            var map = JsonSerializer.Deserialize<Dictionary<string, int>>(stream);
            return map ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    /// <summary>Сохраняет карту форматов на диск (ошибки игнорируются).</summary>
    public static void Save(IReadOnlyDictionary<string, int> formats)
    {
        var path = ResolvePath();
        if (path is null)
            return;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(formats);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Кэш — вспомогательная оптимизация; при неудаче продолжаем без него.
        }
    }

    private static string? ResolvePath()
    {
        try
        {
            var directory = PlatformPaths.AppDataDirectory;
            return string.IsNullOrWhiteSpace(directory)
                ? null
                : Path.Combine(directory, FileName);
        }
        catch
        {
            return null;
        }
    }
}