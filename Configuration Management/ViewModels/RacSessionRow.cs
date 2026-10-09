using System.Globalization;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Строка вкладки «Сеансы» окна «Серверы 1С» (0.3.9.124): форматирование
/// <see cref="RacSessionInfo"/> для отображения — время старта/активности локально,
/// длительности «HH:mm:ss», память в МБ, состояние и блокировки с цветами.
/// Чистый .NET — без платформенных зависимостей.
/// </summary>
public sealed class RacSessionRow
{
    private readonly RacSessionInfo _info;

    public RacSessionRow(RacSessionInfo info, string infobaseName = "")
    {
        _info = info ?? throw new System.ArgumentNullException(nameof(info));
        InfobaseName = infobaseName;
    }

    /// <summary>Идентификатор сеанса.</summary>
    public System.Guid Id => _info.Id;

    /// <summary>
    /// Имя информационной базы сеанса (из маппинга VM по «infobase summary list»,
    /// issue #324 C2: колонка «Информационная база» на вкладке «Сеансы»);
    /// «—» для сеансов без базы / неизвестных идентификаторов.
    /// </summary>
    public string InfobaseName { get; }

    /// <summary>Идентификатор информационной базы сеанса (пусто — не указана).</summary>
    public string InfobaseIdText => _info.InfobaseId?.ToString() ?? string.Empty;

    /// <summary>Имя пользователя сеанса.</summary>
    public string User => _info.User;

    /// <summary>Имя компьютера клиента.</summary>
    public string Host => _info.Host;

    /// <summary>Идентификатор клиентского приложения (например «1CV8»).</summary>
    public string AppId => _info.AppId;

    /// <summary>Время старта сеанса (локальное), «—» если не задано.</summary>
    public string StartedAtText => _info.StartedAt == default
        ? "—"
        : _info.StartedAt.ToString("dd.MM.yyyy HH:mm:ss");

    /// <summary>Время последней активности (локальное), «—» если не задано.</summary>
    public string LastActiveAtText => _info.LastActiveAt == default
        ? "—"
        : _info.LastActiveAt.ToString("dd.MM.yyyy HH:mm:ss");

    /// <summary>Заблокирован служебными заданиями.</summary>
    public bool BlockedByLs => _info.BlockedByLs;

    /// <summary>Заблокирован взаимоблокировкой.</summary>
    public bool BlockedByDeadlock => _info.BlockedByDeadlock;

    /// <summary>Признак блокировки: «●» при любой блокировке, иначе пусто.</summary>
    public string BlockedSymbol => _info.BlockedByLs || _info.BlockedByDeadlock ? "●" : string.Empty;

    /// <summary>Состояние сеанса из rac (например «active», «sleep», «dead»).</summary>
    public string State => _info.State;

    /// <summary>Переведённое состояние сеанса.</summary>
    public string StateText => LocalizeState(_info.State);

    /// <summary>Объём памяти сеанса, МБ.</summary>
    public string MemoryText => (MemoryMb(_info.Memory)).ToString("0.0") + " " + LocalizationManager.T("ServerMonitor.Mb");

    /// <summary>Общая длительность сеанса (HH:mm:ss).</summary>
    public string DurationAllText => FormatDurationMs(_info.DurationAll);

    /// <summary>Длительность текущего вызова (HH:mm:ss).</summary>
    public string DurationCurrentText => FormatDurationMs(_info.DurationCurrent);

    /// <summary>Длительность работы СУБД (HH:mm:ss).</summary>
    public string DurationDbmsText => FormatDurationMs(_info.DurationDbms);

    /// <summary>Длительность работы CPU (HH:mm:ss).</summary>
    public string DurationCpuText => FormatDurationMs(_info.DurationCpu);

    /// <summary>Длительность ожидания (HH:mm:ss).</summary>
    public string DurationWaitText => FormatDurationMs(_info.DurationWait);

    /// <summary>Идентификатор соединения сеанса.</summary>
    public System.Guid ConnectionId => _info.ConnectionId;

    /// <summary>Признак спящего (hibernate) сеанса.</summary>
    public bool Hibernate => _info.Hibernate;

    /// <summary>«Да»/«Нет» для колонки «Hibernate».</summary>
    public string HibernateText => _info.Hibernate
        ? LocalizationManager.T("Common.Yes")
        : LocalizationManager.T("Common.No");

    /// <summary>
    /// Цвет состояния: зелёный — активен, красный — завершён/мёртв,
    /// жёлтый — ожидание/вызов, нейтральный — остальное.
    /// </summary>
    public string StateColorHex => _info.State switch
    {
        "active" => "#16A34A",
        "dead" or "deleted" => "#DC2626",
        "sleep" or "wait" or "call" or "ls" => "#D97706",
        _ => "#64748B"
    };

    private static double MemoryMb(long bytes) => bytes / 1024.0 / 1024.0;

    private static string FormatDurationMs(long milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string LocalizeState(string state) => state.ToLowerInvariant() switch
    {
        "active" => LocalizationManager.T("ServerMonitor.Session.State.Active"),
        "sleep" => LocalizationManager.T("ServerMonitor.Session.State.Sleep"),
        "call" => LocalizationManager.T("ServerMonitor.Session.State.Call"),
        "wait" => LocalizationManager.T("ServerMonitor.Session.State.Wait"),
        "ls" => LocalizationManager.T("ServerMonitor.Session.State.Ls"),
        "dead" => LocalizationManager.T("ServerMonitor.Session.State.Dead"),
        _ => string.IsNullOrWhiteSpace(state) ? "—" : state
    };
}