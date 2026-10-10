using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Configuration_Management.Services;

/// <summary>Состояние фоновой загрузки.</summary>
public enum BackgroundDownloadState
{
    /// <summary>Загрузка выполняется.</summary>
    Active,

    /// <summary>Загрузка успешно завершена.</summary>
    Completed,

    /// <summary>Загрузка завершилась ошибкой.</summary>
    Failed,

    /// <summary>Загрузка отменена пользователем.</summary>
    Cancelled,
}

/// <summary>
/// Менеджер фоновых загрузок (issue #334 п.1): реестр активных скачиваний, живущий
/// в сервисном слое и НЕ привязанный к жизни окон. Окно скачивания платформы и
/// скачивание цепочек обновлений регистрируют загрузку при старте; после закрытия
/// окна загрузка продолжается, а главное окно показывает индикатор (число активных
/// загрузок, агрегированный прогресс) и позволяет отменить загрузку.
/// Класс потокобезопасен: события могут приходить из фоновых потоков; подписчики
/// (главное окно) сами маршалируют обновление UI через <see cref="UiDispatch"/>.
/// </summary>
public sealed class BackgroundDownloadManager
{
    /// <summary>Описатель одной фоновой загрузки.</summary>
    public sealed class ActiveDownload
    {
        /// <summary>Стабильный идентификатор (например, «platform:8.3.27.2214:file.zip»).</summary>
        public string Id { get; }

        /// <summary>Человекочитаемый заголовок для индикатора (имя файла/цепочки).</summary>
        public string Title { get; }

        /// <summary>Прогресс 0..1 (обновляется через <see cref="BackgroundDownloadManager.ReportProgress"/>).</summary>
        public double Progress { get; internal set; }

        /// <summary>Состояние загрузки.</summary>
        public BackgroundDownloadState State { get; internal set; }

        /// <summary>Ключ локализации ошибки (для состояния Failed) или null.</summary>
        public string? ErrorKey { get; internal set; }

        /// <summary>Источник отмены: отмена из индикатора главного окна пробрасывается
        /// в выполняющуюся загрузку через этот токен.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>True — загрузка ещё выполняется.</summary>
        public bool IsActive => State == BackgroundDownloadState.Active;

        internal ActiveDownload(string id, string title)
        {
            Id = id;
            Title = title;
            State = BackgroundDownloadState.Active;
            Progress = 0;
        }

        /// <summary>Отображаемый текст (отладка/журналы).</summary>
        public override string ToString() => $"{Title} ({Progress:P0})";
    }

    /// <summary>Событие любого изменения реестра (старт/прогресс/завершение/отмена) —
    /// источник для агрегированного индикатора главного окна.</summary>
    public event Action? Changed;

    /// <summary>Загрузка успешно завершена.</summary>
    public event Action<ActiveDownload>? Completed;

    /// <summary>Загрузка завершилась ошибкой или отменена.</summary>
    public event Action<ActiveDownload>? Failed;

    private readonly object _lock = new();
    private readonly Dictionary<string, ActiveDownload> _downloads = new(StringComparer.Ordinal);

    /// <summary>Общий экземпляр приложения: окна регистрируют загрузки в нём,
    /// главное окно подписывается на его события.</summary>
    public static BackgroundDownloadManager Default { get; } = new();

    /// <summary>Число активных (выполняющихся) загрузок.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_lock)
                return _downloads.Values.Count(d => d.IsActive);
        }
    }

    /// <summary>Снимок реестра загрузок (потокобезопасная копия).</summary>
    public IReadOnlyList<ActiveDownload> Snapshot()
    {
        lock (_lock)
            return _downloads.Values.ToList();
    }

    /// <summary>Регистрирует старт фоновой загрузки. Повторный старт с тем же
    /// <paramref name="id"/> при активной загрузке возвращает существующую запись
    /// (повторный вызов из окна не создаёт дубликат); завершённая запись заменяется
    /// новой (повторное скачивание того же файла).</summary>
    public ActiveDownload Start(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Идентификатор загрузки обязателен", nameof(id));

        ActiveDownload entry;
        lock (_lock)
        {
            if (_downloads.TryGetValue(id, out var existing) && existing.IsActive)
                return existing;
            entry = new ActiveDownload(id, title ?? string.Empty);
            _downloads[id] = entry;
        }

        RaiseChanged();
        return entry;
    }

    /// <summary>Обновляет прогресс активной загрузки (0..1, клампится). Неактивные
    /// записи игнорируются — поздние отчёты прогресса после завершения не «оживляют»
    /// запись в индикаторе.</summary>
    public void ReportProgress(string id, double progress)
    {
        lock (_lock)
        {
            if (!_downloads.TryGetValue(id, out var entry) || !entry.IsActive)
                return;
            entry.Progress = Math.Clamp(progress, 0, 1);
        }

        RaiseChanged();
    }

    /// <summary>Помечает загрузку успешно завершённой.</summary>
    public void Complete(string id)
    {
        ActiveDownload? entry;
        lock (_lock)
        {
            if (!_downloads.TryGetValue(id, out entry))
                return;
            entry.State = BackgroundDownloadState.Completed;
            entry.Progress = 1;
        }

        RaiseChanged();
        Completed?.Invoke(entry);
    }

    /// <summary>Помечает загрузку завершившейся ошибкой.</summary>
    public void Fail(string id, string? errorKey = null)
    {
        ActiveDownload? entry;
        lock (_lock)
        {
            if (!_downloads.TryGetValue(id, out entry))
                return;
            entry.State = BackgroundDownloadState.Failed;
            entry.ErrorKey = errorKey;
        }

        RaiseChanged();
        Failed?.Invoke(entry);
    }

    /// <summary>Отменяет загрузку: выставляет состояние Cancelled и пробрасывает отмену
    /// в выполняющийся запрос через токен. True — запись найдена и была активной.</summary>
    public bool Cancel(string id)
    {
        ActiveDownload? entry;
        lock (_lock)
        {
            if (!_downloads.TryGetValue(id, out entry) || !entry.IsActive)
                return false;
            entry.State = BackgroundDownloadState.Cancelled;
        }

        try
        {
            entry.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Токен уже освобождён — состояние всё равно зафиксировано.
        }

        RaiseChanged();
        Failed?.Invoke(entry);
        return true;
    }

    /// <summary>Отменяет все активные загрузки (кнопка отмены в индикаторе).</summary>
    public void CancelAll()
    {
        List<ActiveDownload> toCancel;
        lock (_lock)
        {
            toCancel = _downloads.Values.Where(d => d.IsActive).ToList();
            foreach (var entry in toCancel)
                entry.State = BackgroundDownloadState.Cancelled;
        }

        foreach (var entry in toCancel)
        {
            try
            {
                entry.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Токен уже освобождён — состояние всё равно зафиксировано.
            }

            Failed?.Invoke(entry);
        }

        if (toCancel.Count > 0)
            RaiseChanged();
    }

    /// <summary>Удаляет завершённые записи из реестра (очистка индикатора).</summary>
    public void ClearFinished()
    {
        lock (_lock)
        {
            var finished = _downloads.Values.Where(d => !d.IsActive).ToList();
            foreach (var entry in finished)
                _downloads.Remove(entry.Id);
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        handler?.Invoke();
    }
}
