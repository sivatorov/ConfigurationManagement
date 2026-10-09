using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Параметры подключения к серверу 1С:Предприятие для команд rac (Remote Administration Client).
/// Адрес/порт/логин сохраняются в настройках приложения; <see cref="Password"/> живёт ТОЛЬКО
/// в памяти (вводится при каждом открытии окна монитора) и не пишется в журнал (см.
/// <see cref="SensitiveDataMasker.MaskRacPassword"/>).
/// </summary>
public sealed record RacConnectionParams
{
    /// <summary>Адрес сервера 1С (host или IP).</summary>
    public string Address { get; init; } = "localhost";

    /// <summary>
    /// Порт агента сервера 1С (ragent) — по умолчанию 1540; для сервера администрирования
    /// RAS — 1545.
    /// </summary>
    public int Port { get; init; } = IRacClient.DefaultPort;

    /// <summary>Логин администратора кластера (пустая строка — без аутентификации).</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>Пароль администратора кластера. Только в памяти, не сериализуется.</summary>
    public string Password { get; init; } = string.Empty;
}

/// <summary>
/// Ошибка выполнения команды rac: не найден исполняемый файл, ненулевой код выхода,
/// превышен таймаут ожидания или ошибка запуска процесса.
/// </summary>
public sealed class RacClientException : Exception
{
    public RacClientException(string message) : base(message)
    {
    }

    public RacClientException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Клиент утилиты rac (Remote Administration Client) — сервисный слой встроенного монитора
/// серверов 1С (цикл 0.3.9.123–0.3.9.126). Чистый сервис — без UI-зависимостей.
/// Исполняемый файл rac ищется в каталоге bin установленной платформы через
/// <see cref="OneCPlatformLocator.FindRacExecutable"/>; команды выполняются прямым запуском
/// процесса без shell (ArgumentList) с захватом stdout/stderr и таймаутом.
/// </summary>
public interface IRacClient
{
    /// <summary>Порт агента сервера 1С (ragent) по умолчанию.</summary>
    public const int DefaultPort = 1540;

    /// <summary>Список кластеров сервера (команда «cluster list»).</summary>
    Task<IReadOnlyList<RacCluster>> GetClustersAsync(
        RacConnectionParams parameters, CancellationToken cancellationToken = default);

    /// <summary>Информация о кластере (команда «cluster info»); null, если вывод пуст/не распознан.</summary>
    Task<RacClusterInfo?> GetClusterInfoAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default);

    /// <summary>Рабочие процессы кластера (команда «process list --cluster=...»).</summary>
    Task<IReadOnlyList<RacProcessInfo>> GetProcessesAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default);

    /// <summary>Сеансы кластера (команда «session list --cluster=...»).</summary>
    Task<IReadOnlyList<RacSessionInfo>> GetSessionsAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default);

    /// <summary>Соединения кластера (команда «connection list --cluster=...»).</summary>
    Task<IReadOnlyList<RacConnectionInfo>> GetConnectionsAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default);

    /// <summary>Блокировки объектов данных кластера (команда «lock list --cluster=...»).</summary>
    Task<IReadOnlyList<RacLockInfo>> GetLocksAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Информационные базы кластера (команда «infobase summary list --cluster=...»).
    /// Используется функцией импорта баз из кластера 1С (цикл 0.3.9.172–0.3.9.175).
    /// </summary>
    Task<IReadOnlyList<RacInfobaseSummary>> GetInfobasesAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Регламентные задания кластера (команда «job list --cluster=...»).
    /// Вкладка «Регламентные задания» монитора серверов (цикл 0.3.9.176–0.3.9.179).
    /// </summary>
    Task<IReadOnlyList<RacJobInfo>> GetJobsAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Изменение параметров кластера (команда «cluster update --cluster=...», issue #324,
    /// C3: редактируемая «Информация о кластере»). Передаются ТОЛЬКО изменённые свойства
    /// (см. <see cref="RacClusterUpdate.IsEmpty"/>). Возвращает true при ExitCode 0;
    /// при неудаче — false и текст ошибки (включая stderr rac) в <see cref="LastActionError"/>.
    /// </summary>
    Task<bool> UpdateClusterAsync(
        RacConnectionParams parameters, Guid clusterId, RacClusterUpdate changes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Изменение состояния регламентного задания (команды «job pause/resume/disable/enable
    /// --cluster=... --job=...»). Возвращает true при ExitCode 0; при неудаче — false
    /// и текст ошибки (включая stderr rac) в <see cref="LastActionError"/>.
    /// </summary>
    Task<bool> SetJobStateAsync(
        RacConnectionParams parameters, Guid clusterId, Guid jobId, RacJobAction action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Завершение сеанса пользователя (команда «session terminate --cluster=... --session=...»).
    /// Реализация — этап 3 (0.3.9.125): возвращает true при ExitCode 0; при неудаче — false
    /// и текст ошибки (включая stderr rac) в <see cref="LastActionError"/>.
    /// </summary>
    Task<bool> TerminateSessionAsync(
        RacConnectionParams parameters, Guid clusterId, Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Разрыв соединения клиента (команда «connection disconnect --cluster=... --connection=...»).
    /// Реализация — этап 3 (0.3.9.125): возвращает true при ExitCode 0; при неудаче — false
    /// и текст ошибки (включая stderr rac) в <see cref="LastActionError"/>.
    /// </summary>
    Task<bool> DisconnectConnectionAsync(
        RacConnectionParams parameters, Guid clusterId, Guid connectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Текст последней ошибки действия (<see cref="TerminateSessionAsync"/> /
    /// <see cref="DisconnectConnectionAsync"/>): сообщение rac со stderr при неудаче,
    /// пустая строка при успехе или пока действие не выполнялось.
    /// </summary>
    string LastActionError { get; }
}