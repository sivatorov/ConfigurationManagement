#if LINUX
using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Themes;

namespace Configuration_Management.Services;

/// <summary>
/// Модальный диалог перехода на новую программу Workspace 1C (Linux/Avalonia).
/// Аналог Windows-версии <c>Workspace1CMigrationWindow</c>: предложение скачать
/// новую программу (с прогрессом в этом же окне), открыть страницу проекта или
/// отложить переход. Все ошибки обрабатываются внутри окна и не роняют приложение.
/// </summary>
internal sealed class Workspace1CMigrationWindowAvalonia : Window
{
    private readonly Workspace1CMigrationService _service;

    private readonly StackPanel _offerPanel;
    private readonly StackPanel _progressPanel;
    private readonly StackPanel _donePanel;
    private readonly StackPanel _errorPanel;

    private readonly ProgressBar _bar;
    private readonly TextBlock _progressText;
    private readonly TextBlock _doneText;
    private readonly TextBlock _errorText;

    /// <summary>Путь к скачанному файлу; заполняется после успешной загрузки.</summary>
    private string? _downloadedFile;

    public Workspace1CMigrationWindowAvalonia(Workspace1CMigrationService service)
    {
        _service = service;

        Title = LocalizationManager.T("Workspace1C.Title");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.Full;

        ThemeBrushes.Bind(this, TemplatedControl.BackgroundProperty, "ContentBackgroundColorBrush");

        // Иконка перехода (общая для всех этапов диалога).
        var icon = Configuration_Management.IconHelper.MakeIcon("IconRocketLaunch", 24);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;

        // ===== Этап 1: предложение перейти на Workspace 1C =====
        var heading = new TextBlock
        {
            Text = LocalizationManager.T("Workspace1C.Title"),
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        ThemeBrushes.Bind(heading, TextBlock.ForegroundProperty, "TextPrimaryColorBrush");

        var dialogText = new TextBlock
        {
            Text = LocalizationManager.T("Workspace1C.DialogText"),
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        ThemeBrushes.Bind(dialogText, TextBlock.ForegroundProperty, "TextPrimaryColorBrush");

        var downloadButton = BuildPrimaryButton(
            LocalizationManager.T("Workspace1C.Download"), OnDownloadClick);
        var openPageButton = BuildSecondaryButton(
            LocalizationManager.T("Workspace1C.OpenPage"), OnOpenPageClick);
        var laterButton = BuildSecondaryButton(
            LocalizationManager.T("Workspace1C.Later"), (_, _) => Close());

        var offerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0)
        };
        offerButtons.Children.Add(downloadButton);
        offerButtons.Children.Add(openPageButton);
        offerButtons.Children.Add(laterButton);

        _offerPanel = new StackPanel { Spacing = 0 };
        _offerPanel.Children.Add(heading);
        _offerPanel.Children.Add(dialogText);
        _offerPanel.Children.Add(offerButtons);

        // ===== Этап 2: скачивание с прогрессом в этом же окне =====
        var progressHeading = new TextBlock
        {
            Text = LocalizationManager.T("Workspace1C.Downloading"),
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        ThemeBrushes.Bind(progressHeading, TextBlock.ForegroundProperty, "TextPrimaryColorBrush");

        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            IsIndeterminate = false,
            Height = 6,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _progressText = new TextBlock
        {
            Text = LocalizationManager.T("Workspace1C.Downloading"),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        };
        ThemeBrushes.Bind(_progressText, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");

        _progressPanel = new StackPanel { Spacing = 0, IsVisible = false };
        _progressPanel.Children.Add(progressHeading);
        _progressPanel.Children.Add(_bar);
        _progressPanel.Children.Add(_progressText);

        // ===== Этап 3: завершение — путь к файлу и кнопка «Открыть папку» =====
        _doneText = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        ThemeBrushes.Bind(_doneText, TextBlock.ForegroundProperty, "TextPrimaryColorBrush");

        var openFolderButton = BuildPrimaryButton(
            LocalizationManager.T("Workspace1C.OpenFolder"), OnOpenFolderClick);
        var doneCloseButton = BuildSecondaryButton(
            LocalizationManager.T("Common.Close"), (_, _) => Close());

        var doneButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0)
        };
        doneButtons.Children.Add(openFolderButton);
        doneButtons.Children.Add(doneCloseButton);

        _donePanel = new StackPanel { Spacing = 0, IsVisible = false };
        _donePanel.Children.Add(_doneText);
        _donePanel.Children.Add(doneButtons);

        // ===== Этап 4: ошибка — встроена в это же окно =====
        var errorHeading = new TextBlock
        {
            Text = LocalizationManager.T("Workspace1C.ErrorTitle"),
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#DC2626"))
        };

        _errorText = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        ThemeBrushes.Bind(_errorText, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");

        var errorCloseButton = BuildSecondaryButton(
            LocalizationManager.T("Common.Close"), (_, _) => Close());
        errorCloseButton.Margin = new Thickness(0, 20, 0, 0);
        errorCloseButton.HorizontalAlignment = HorizontalAlignment.Right;

        _errorPanel = new StackPanel { Spacing = 0, IsVisible = false };
        _errorPanel.Children.Add(errorHeading);
        _errorPanel.Children.Add(_errorText);
        _errorPanel.Children.Add(errorCloseButton);

        // Общий контейнер: иконка слева, этапы справа.
        var body = new Grid { Margin = new Thickness(4, 8, 4, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        body.Children.Add(icon);
        var stages = new StackPanel { Spacing = 0 };
        stages.Children.Add(_offerPanel);
        stages.Children.Add(_progressPanel);
        stages.Children.Add(_donePanel);
        stages.Children.Add(_errorPanel);
        Grid.SetColumn(stages, 1);
        body.Children.Add(stages);

        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { body }
        };
        ThemeBrushes.Bind(content, Panel.BackgroundProperty, "CardBackgroundColorBrush");
        Content = content;
    }

    /// <summary>
    /// «Скачать»: скрываем предложение, показываем прогресс и запускаем загрузку.
    /// По завершении загрузки остаёмся в этом же окне и показываем путь к файлу
    /// с кнопкой «Открыть папку».
    /// </summary>
    private async void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        _offerPanel.IsVisible = false;
        _progressPanel.IsVisible = true;
        _bar.Value = 0;
        _bar.IsIndeterminate = false;
        _progressText.Text = LocalizationManager.T("Workspace1C.Downloading");

        // Подписка на прогресс скачивания (событие приходит из фонового потока).
        _service.DownloadProgressChanged += OnDownloadProgress;
        try
        {
            var release = await _service.GetLatestReleaseAsync();
            if (release is null || string.IsNullOrWhiteSpace(release.DownloadUrl))
            {
                ShowError(LocalizationManager.T("Workspace1C.NoDownloadUrl"));
                return;
            }

            _downloadedFile = await _service.DownloadAsync(release);
            if (_downloadedFile is null)
            {
                ShowError(LocalizationManager.T("Workspace1C.DownloadFailed"));
                return;
            }

            // Скачивание завершено — показываем путь к файлу и кнопку «Открыть папку».
            _progressPanel.IsVisible = false;
            _donePanel.IsVisible = true;
            _doneText.Text = string.Format(
                LocalizationManager.T("Workspace1C.Downloaded"), _downloadedFile);
        }
        finally
        {
            _service.DownloadProgressChanged -= OnDownloadProgress;
        }
    }

    /// <summary>Обновляет индикатор прогресса внутри окна (вызывается из фонового потока).</summary>
    private void OnDownloadProgress(double percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (percent < 0)
            {
                // Длина файла заранее неизвестна — индикатор переводим в неопределённый режим.
                _bar.IsIndeterminate = true;
                _progressText.Text = LocalizationManager.T("Workspace1C.Downloading");
                return;
            }

            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(percent, 0, 100);
            _progressText.Text = string.Format(
                LocalizationManager.T("Workspace1C.DownloadProgressFormat"), (int)percent);
        });
    }

    /// <summary>«Открыть страницу»: открывает страницу проекта Workspace 1C в браузере.</summary>
    private void OnOpenPageClick(object? sender, RoutedEventArgs e)
    {
        if (!OneCLauncher.OpenUrl(Workspace1CMigrationService.ProjectUrl))
        {
            // Браузер не открылся — копируем адрес в буфер обмена, чтобы
            // пользователь всё равно смог перейти на страницу проекта.
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    _ = clipboard.SetTextAsync(Workspace1CMigrationService.ProjectUrl);
            }
            catch { /* копирование не должно ронять окно */ }
        }
    }

    /// <summary>«Открыть папку»: открывает в файловом менеджере папку со скачанным файлом.</summary>
    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_downloadedFile is null)
            return;

        try
        {
            var dir = Path.GetDirectoryName(_downloadedFile);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                OneCLauncher.OpenUrl(dir);
        }
        catch
        {
            // Не удалось открыть папку — путь уже показан в диалоге.
        }
    }

    /// <summary>Показывает ошибку прямо в этом окне (вместо отдельного диалога ошибки).</summary>
    private void ShowError(string message)
    {
        _offerPanel.IsVisible = false;
        _progressPanel.IsVisible = false;
        _donePanel.IsVisible = false;
        _errorPanel.IsVisible = true;
        _errorText.Text = message;
    }

    /// <summary>Основная (зелёная) кнопка диалога.</summary>
    private static Button BuildPrimaryButton(string text, EventHandler<RoutedEventArgs> onClick)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            },
            MinWidth = 120,
            IsDefault = true,
            Background = new SolidColorBrush(Color.Parse("#16A34A")),
            Foreground = Brushes.White
        };
        button.Click += onClick;
        return button;
    }

    /// <summary>Вторичная (прозрачная) кнопка диалога.</summary>
    private static Button BuildSecondaryButton(string text, EventHandler<RoutedEventArgs> onClick)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            },
            MinWidth = 100
        };
        button.Styled(ControlThemes.SecondaryButton);
        button.Click += onClick;
        return button;
    }
}
#endif