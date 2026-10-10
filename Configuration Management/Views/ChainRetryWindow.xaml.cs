#if WINDOWS
using System;
using System.Windows;
using System.Windows.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Services;

namespace Configuration_Management;

/// <summary>
/// Диалог «Попробовать ещё раз?» после загрузки цепочки обновлений с ошибками
/// (issue #352.3): кнопка «Да» с обратным отсчётом 60 с; по нуле — автоматический
/// «Нет». «Да» вызывает повторную загрузку цепочки (планировщик пропустит уже
/// скачанные файлы и покажет корректный «Скачивается X из Y»).
/// </summary>
public partial class ChainRetryWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _secondsLeft = ChainRetryCountdown.DefaultSeconds;
    private readonly string _yesBaseText;

    /// <summary>Итог диалога: true — «Да» (повторить), false/null — «Нет».</summary>
    public bool RetryRequested { get; private set; }

    public ChainRetryWindow(string message, int countdownSeconds = ChainRetryCountdown.DefaultSeconds)
    {
        InitializeComponent();

        Title = LocalizationManager.T("Updates.Chain.Retry.Title");
        MessageText.Text = message;
        _yesBaseText = LocalizationManager.T("Updates.Chain.Retry.Yes");
        _secondsLeft = countdownSeconds;
        UpdateYesButtonText();

        var owner = Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        YesButton.Focus();
    }

    /// <summary>Показывает диалог модально: true — пользователь (или таймер «Да») выбрал повтор.</summary>
    public static bool Ask(Window? owner, string message, int countdownSeconds = ChainRetryCountdown.DefaultSeconds)
    {
        var window = new ChainRetryWindow(message, countdownSeconds);
        if (owner is not null && !ReferenceEquals(owner, window))
            window.Owner = owner;
        window.ShowDialog();
        return window.RetryRequested;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsLeft--;
        UpdateYesButtonText();
        if (ChainRetryCountdown.IsFinished(_secondsLeft))
        {
            // Время вышло — автоматический «Нет».
            _timer.Stop();
            Close();
        }
    }

    private void UpdateYesButtonText() =>
        YesButtonText.Text = ChainRetryCountdown.ButtonText(_yesBaseText, _secondsLeft);

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        RetryRequested = true;
        DialogResult = true;
    }

    private void OnNoClick(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        RetryRequested = false;
    }
}
#endif
