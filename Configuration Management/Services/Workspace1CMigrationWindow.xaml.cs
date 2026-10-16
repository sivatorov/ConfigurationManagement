#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Configuration_Management.Localization;

namespace Configuration_Management.Services;

/// <summary>
/// Модальный диалог перехода на новую программу Workspace 1C (Windows/WPF).
/// Показывается один раз при запуске (флаг <c>Workspace1CMigrationShown</c>)
/// и из вкладки «О программе». Предлагает скачать новую программу (с прогрессом
/// в этом же окне), открыть страницу проекта или отложить переход. Все ошибки
/// обрабатываются внутри окна и не роняют приложение.
/// </summary>
public partial class Workspace1CMigrationWindow : Window
{
    private readonly Workspace1CMigrationService _service;

    /// <summary>Путь к скачанному файлу; заполняется после успешной загрузки.</summary>
    private string? _downloadedFile;

    public Workspace1CMigrationWindow(Workspace1CMigrationService service)
    {
        InitializeComponent();
        _service = service;

        Title = LocalizationManager.T("Workspace1C.Title");
        HeadingText.Text = LocalizationManager.T("Workspace1C.Title");
        DialogText.Text = LocalizationManager.T("Workspace1C.DialogText");

        // На старте главного окна ещё нет, и первым MainWindow становится само это
        // окно: присваивание Owner самому себе бросает ArgumentException. Без
        // владельца окно просто открывается по центру экрана.
        var owner = Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Loaded += (_, _) => DownloadButton.Focus();

        // Закрытие окна по Esc, единообразно с остальными окнами приложения.
        // Во время скачивания окно не закрывается: случайный Esc не должен
        // прерывать загрузку и оставлять фоновую задачу без окна.
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnWindow_PreviewKeyDown), true);
    }

    /// <summary>Закрывает окно по Esc без модификаторов. Во время скачивания окно не закрывается.</summary>
    private void OnWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape
            || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (ProgressPanel.Visibility == Visibility.Visible)
            return;
        e.Handled = true;
        Close();
    }

    /// <summary>
    /// «Скачать»: скрываем предложение, показываем прогресс и запускаем загрузку.
    /// По завершении загрузки остаёмся в этом же окне и показываем путь к файлу
    /// с кнопкой «Открыть папку».
    /// </summary>
    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false;
        OpenPageButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        OfferPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        ProgressBar.IsIndeterminate = false;
        ProgressText.Text = LocalizationManager.T("Workspace1C.Downloading");
        RefreshWindowSize();

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
            ProgressPanel.Visibility = Visibility.Collapsed;
            DonePanel.Visibility = Visibility.Visible;
            DoneText.Text = string.Format(
                LocalizationManager.T("Workspace1C.Downloaded"), _downloadedFile);
            OpenFolderButton.Focus();
            RefreshWindowSize();
        }
        finally
        {
            _service.DownloadProgressChanged -= OnDownloadProgress;
        }
    }

    /// <summary>Обновляет индикатор прогресса внутри окна (вызывается из фонового потока).</summary>
    private void OnDownloadProgress(double percent)
    {
        Dispatcher.Invoke(() =>
        {
            if (percent < 0)
            {
                // Длина файла заранее неизвестна — индикатор переводим в неопределённый режим.
                ProgressBar.IsIndeterminate = true;
                ProgressText.Text = LocalizationManager.T("Workspace1C.Downloading");
                return;
            }

            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = percent;
            ProgressText.Text = string.Format(
                LocalizationManager.T("Workspace1C.DownloadProgressFormat"), (int)percent);
        });
    }

    /// <summary>«Открыть страницу»: открывает страницу проекта Workspace 1C в браузере.</summary>
    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Workspace1CMigrationService.ProjectUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Браузер не открылся — диалог продолжает работать.
        }
    }

    /// <summary>«Позже»: закрывает диалог без скачивания.</summary>
    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>«Открыть папку»: открывает в проводнике папку со скачанным файлом.</summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_downloadedFile is null)
            return;

        try
        {
            var dir = Path.GetDirectoryName(_downloadedFile);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Не удалось открыть папку — путь уже показан в диалоге.
        }
    }

    /// <summary>Закрывает окно по кнопке финального этапа.</summary>
    private void OnDoneCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Показывает ошибку прямо в этом окне (вместо закрытия окна и открытия
    /// отдельного диалога ошибки), чтобы при сбое не появлялось несколько окон.
    /// </summary>
    private void ShowError(string message)
    {
        OfferPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
        ErrorCloseButton.Focus();
        RefreshWindowSize();
    }

    /// <summary>
    /// Принудительно пересчитывает размер окна по содержимому текущего этапа.
    /// WPF не всегда пересобирает окно с <c>SizeToContent="Height"</c> после
    /// изменения <c>Visibility</c> блоков, поэтому сбрасываем авто-подбор
    /// в Manual, заново меряем содержимое и возвращаем обратно.
    /// </summary>
    private void RefreshWindowSize()
    {
        try
        {
            if (!IsLoaded)
                return;

            SizeToContent = SizeToContent.Manual;
            InvalidateMeasure();
            UpdateLayout();

            SizeToContent = SizeToContent.Height;
            InvalidateMeasure();
            UpdateLayout();
        }
        catch
        {
            // Сбой пересчёта размера не должен ломать диалог.
        }
    }
}
#endif