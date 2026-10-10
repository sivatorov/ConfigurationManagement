#if LINUX
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Services;
using Configuration_Management.Themes;

namespace Configuration_Management;

/// <summary>
/// Диалог «Попробовать ещё раз?» после загрузки цепочки обновлений с ошибками
/// (issue #352.3): кнопка «Да» с обратным отсчётом 60 с; по нуле — автоматический
/// «Да» (issue #352.3, комментарий 7OH от 2026-10-10: повтор должен начаться сам,
/// пользователь мог отойти). Avalonia/Linux-версия окна <see cref="ChainRetryWindow"/>
/// (строится кодом).
/// </summary>
public sealed class ChainRetryWindow : ModalWindowBase
{
    private readonly DispatcherTimer _timer;
    private int _secondsLeft;
    private readonly string _yesBaseText;
    private readonly TextBlock _yesButtonText = new();
    private bool _retryRequested;

    public ChainRetryWindow(string message, int countdownSeconds = ChainRetryCountdown.DefaultSeconds)
    {
        Title = LocalizationManager.T("Updates.Chain.Retry.Title");
        Width = 460;
        FontSize = 13;
        CanResize = false;

        _yesBaseText = LocalizationManager.T("Updates.Chain.Retry.Yes");
        _secondsLeft = countdownSeconds;

        var root = new StackPanel { Margin = new Thickness(16) };
        var messageText = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        Themes.ThemeBrushes.Bind(messageText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        root.Children.Add(messageText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var yesButton = new Button
        {
            MinWidth = 130,
            Height = 34,
            Content = new ContentControl { Content = _yesButtonText },
        };
        yesButton.Styled(ControlThemes.DialogConfirmButton);
        yesButton.Click += (_, _) =>
        {
            _timer.Stop();
            _retryRequested = true;
            Close();
        };

        var noButton = BuildCancelActionButton(110, 34);
        noButton.Click += (_, _) =>
        {
            _timer.Stop();
            _retryRequested = false;
            Close();
        };

        buttons.Children.Add(yesButton);
        buttons.Children.Add(noButton);
        root.Children.Add(buttons);

        UpdateYesButtonText();
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            UpdateYesButtonText();
            if (ChainRetryCountdown.IsFinished(_secondsLeft))
            {
                // Время вышло — автоматический «Да» (issue #352.3): повтор начинается
                // без участия пользователя.
                _timer.Stop();
                _retryRequested = true;
                Close();
            }
        };
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Показывает диалог модально: true — пользователь выбрал повтор.</summary>
    public static bool Ask(Window? owner, string message, int countdownSeconds = ChainRetryCountdown.DefaultSeconds)
    {
        var window = new ChainRetryWindow(message, countdownSeconds);
        window.ShowDialogSync(owner);
        return window._retryRequested;
    }

    private void UpdateYesButtonText() =>
        _yesButtonText.Text = ChainRetryCountdown.ButtonText(_yesBaseText, _secondsLeft);
}
#endif
