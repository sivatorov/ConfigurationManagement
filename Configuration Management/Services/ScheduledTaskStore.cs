using System.IO;
using System.Linq;
using System.Text.Json;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Реализация <see cref="IScheduledTaskStore"/>: JSON-файлы заданий в каталоге
/// <c><DataDir>/schedules</c>. Имя файла — санитизированный <see cref="ScheduledTask.Id"/>
/// с суффиксом <c>.task.json</c>, что исключает коллизии имён и проблемы с кириллицей в пути.
/// Сериализация — как в <see cref="InfobaseRepository"/> (отступы, включены свойства).
/// </summary>
public class ScheduledTaskStore : IScheduledTaskStore
{
    private readonly IProfileService? _profileService;
    private readonly JsonSerializerOptions _jsonOptions;

    private const string TasksSubDir = "schedules";

    public ScheduledTaskStore(IProfileService? profileService = null)
    {
        _profileService = profileService;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    /// <inheritdoc />
    public string TasksDirectory
    {
        get
        {
            var dataDir = !string.IsNullOrWhiteSpace(_profileService?.CurrentProfileDataDirectory)
                ? _profileService!.CurrentProfileDataDirectory
                : PlatformPaths.AppDataDirectory;
            return Path.Combine(dataDir, TasksSubDir);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTask> LoadAll()
    {
        try
        {
            if (!Directory.Exists(TasksDirectory))
                return Array.Empty<ScheduledTask>();

            var result = new List<ScheduledTask>();
            foreach (var file in Directory.EnumerateFiles(TasksDirectory, "*.task.json"))
            {
                try
                {
                    var task = JsonSerializer.Deserialize<ScheduledTask>(File.ReadAllText(file), _jsonOptions);
                    if (task is not null && !string.IsNullOrWhiteSpace(task.Name))
                        result.Add(task);
                }
                catch
                {
                    // Битый/нечитаемый файл пропускаем, не роняя загрузку остальных.
                }
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }
        catch
        {
            return Array.Empty<ScheduledTask>();
        }
    }

    /// <inheritdoc />
    public void Save(ScheduledTask task)
    {
        if (task is null)
            return;
        if (string.IsNullOrWhiteSpace(task.Id))
            task.Id = Guid.NewGuid().ToString("N");

        Directory.CreateDirectory(TasksDirectory);
        var json = JsonSerializer.Serialize(task, _jsonOptions);
        File.WriteAllText(FilePathFor(task.Id), json);
    }

    /// <inheritdoc />
    public void Delete(string id)
    {
        try
        {
            var path = FilePathFor(id);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Удаление — не критично: файл мог быть уже удалён или занят.
        }
    }

    /// <inheritdoc />
    public ScheduledTask? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        try
        {
            var path = FilePathFor(id);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<ScheduledTask>(File.ReadAllText(path), _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private string FilePathFor(string id)
        => Path.Combine(TasksDirectory, SanitizeId(id) + ".task.json");

    private static string SanitizeId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.NewGuid().ToString("N");
        var invalid = Path.GetInvalidFileNameChars();
        var chars = id.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}