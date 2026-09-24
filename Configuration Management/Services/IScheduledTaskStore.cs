using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Хранилище заданий по расписанию (issue #286): чтение/запись/удаление JSON-файлов
/// заданий в каталоге профиля. По одному файлу на задание — как у сценариев резервирования.
/// </summary>
public interface IScheduledTaskStore
{
    /// <summary>Каталог файлов заданий.</summary>
    string TasksDirectory { get; }

    /// <summary>Загружает все задания из JSON-файлов (отсортированы по имени).</summary>
    IReadOnlyList<ScheduledTask> LoadAll();

    /// <summary>Сохраняет задание в отдельный JSON-файл (создаёт или перезаписывает).</summary>
    void Save(ScheduledTask task);

    /// <summary>Удаляет задание по идентификатору.</summary>
    void Delete(string id);

    /// <summary>Возвращает задание по идентификатору или <c>null</c>.</summary>
    ScheduledTask? Get(string id);
}