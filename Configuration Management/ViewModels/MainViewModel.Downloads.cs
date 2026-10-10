using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Configuration_Management.Localization;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Индикатор фоновых загрузок в главном окне (issue #334 п.1): подписка на
/// <see cref="Services.BackgroundDownloadManager"/> (общий для приложения), агрегированный
/// текст статуса («Скачивается: файл (45%)…» / «Скачивается файлов: N…») и команда отмены.
/// Свойства обновляются из любого потока: WPF и Avalonia поддерживают PropertyChanged
/// для скалярных свойств из фонового потока, коллекции здесь не используются.
/// </summary>
public partial class MainViewModel
{
    private bool _hasActiveDownloads;
    private string _downloadsStatusText = string.Empty;
    private double _activeDownloadsProgress;
    private RelayCommand? _cancelDownloadsCommand;

    /// <summary>True — есть активные фоновые загрузки (индикатор в статусной строке).</summary>
    public bool HasActiveDownloads
    {
        get => _hasActiveDownloads;
        private set => SetProperty(ref _hasActiveDownloads, value);
    }

    /// <summary>Агрегированный текст активных загрузок (пусто — ничего не скачивается).</summary>
    public string DownloadsStatusText
    {
        get => _downloadsStatusText;
        private set => SetProperty(ref _downloadsStatusText, value);
    }

    /// <summary>Агрегированный прогресс активных загрузок 0..1 (среднее по активным).</summary>
    public double ActiveDownloadsProgress
    {
        get => _activeDownloadsProgress;
        private set => SetProperty(ref _activeDownloadsProgress, value);
    }

    /// <summary>Команда «Отменить загрузки»: отменяет все активные фоновые загрузки.</summary>
    public ICommand CancelDownloadsCommand =>
        _cancelDownloadsCommand ??= new RelayCommand(
            () => Services.BackgroundDownloadManager.Default.CancelAll(),
            () => HasActiveDownloads);

    /// <summary>Подписка на менеджер фоновых загрузок (вызывается из конструктора).</summary>
    private void InitializeBackgroundDownloadsIndicator()
    {
        Services.BackgroundDownloadManager.Default.Changed += OnBackgroundDownloadsChanged;
        RefreshDownloadsIndicator();
    }

    private void OnBackgroundDownloadsChanged()
        => RefreshDownloadsIndicator();

    /// <summary>Пересчитывает индикатор из текущего снимка менеджера.</summary>
    private void RefreshDownloadsIndicator()
    {
        var active = Services.BackgroundDownloadManager.Default.Snapshot()
            .Where(d => d.IsActive)
            .ToList();

        HasActiveDownloads = active.Count > 0;
        DownloadsStatusText = active.Count switch
        {
            0 => string.Empty,
            1 => string.Format(
                LocalizationManager.T("Main.Downloads.ActiveSingle"),
                active[0].Title,
                active[0].Progress),
            _ => string.Format(LocalizationManager.T("Main.Downloads.ActiveMany"), active.Count),
        };
        ActiveDownloadsProgress = active.Count == 0
            ? 0
            : active.Average(d => d.Progress);

        _cancelDownloadsCommand?.RaiseCanExecuteChanged();
    }
}
