#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management;

/// <summary>
/// Окно «Обновление платформы 1С»: единый список установленных и доступных версий
/// технологической платформы (Версия/Размер/Статус/Совместимые базы), проверка
/// каталога портала releases.1c.ru, загрузка дистрибутива и установка на Windows.
/// Сервисы берутся из <see cref="AppServices"/> (паттерн
/// <see cref="ActualReleasesWindow"/>); вся логика — в чистой
/// <see cref="PlatformUpdateViewModel"/>. Открытие по хоткею привязывается
/// на этапе 0.3.9.214; окно готово к открытию из конструктора.
/// </summary>
public partial class PlatformUpdateWindow : Window
{
    private readonly PlatformUpdateViewModel _viewModel;

    /// <summary>Открывает окно «Обновление платформы 1С».</summary>
    public PlatformUpdateWindow()
    {
        InitializeComponent();

        var service = AppServices.GetRequiredService<IPlatformUpdateService>();
        var repository = AppServices.GetRequiredService<IInfobaseRepository>();
        var updates = AppServices.GetRequiredService<IOneCUpdatesService>();
        var dialogs = AppServices.GetRequiredService<IDialogService>();
        var running = AppServices.GetRequiredService<IRunningInfobasesService>();
        var notifier = AppServices.GetRequiredService<INotificationService>();
        var logger = AppServices.GetRequiredService<IAppLogger>();

        _viewModel = new PlatformUpdateViewModel(
            service,
            repository,
            LoadInstalledVersions,
            (url, targetPath, progress, ct) =>
                updates.DownloadDistributionAsync(url, targetPath, progress, ct),
            InstallFromZipAsync,
            defaultName => dialogs.SaveFileDialog(
                LocalizationManager.T("PlatformUpdate.DownloadOnly"),
                defaultName ?? "platform.zip",
                "Архивы (*.zip)|*.zip|Все файлы (*.*)|*.*",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            _ => dialogs.OpenFileDialog(
                LocalizationManager.T("PlatformUpdate.ChooseInstaller"),
                "Исполняемые файлы (*.exe)|*.exe|Пакеты Linux (*.deb;*.rpm)|*.deb;*.rpm|Все файлы (*.*)|*.*",
                null),
            // Проверка готовности к установке (этап 0.3.9.214): процессы 1С, права,
            // свободное место, подпись; диалог подтверждения и уведомление о результате.
            loadRunningProcesses: () => running.GetRunning()
                .Select(p => p.ProcessName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            isAdministrator: PlatformInstaller.IsAdministrator,
            getFreeBytes: path => DiskFreeSpaceHelper.TryGetInfo(path, DiskFreeSpaceHelper.DefaultDriveResolver)?.FreeBytes,
            hasValidSignature: PlatformInstaller.HasValidSignature,
            confirmDialog: (title, message) => dialogs.Confirm(message, title),
            notify: (title, message, kind, evt) => notifier.Show(title, message, kind, evt),
            appLogger: logger,
            // Удаление старых версий (этап 0.3.9.215): инфо установленных версий с путями,
            // пути бинарников запущенных процессов и удаление каталога через PlatformInstaller.
            loadInstalledVersionInfos: () => PlatformVersionService.FindInstalledVersionInfos(),
            loadRunningBinPaths: () => running.GetRunning()
                .Select(p => OldVersionCleaner.ExtractExecutablePath(p.CommandLine))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => p!)
                .ToList(),
            deleteVersionDirectory: (version, log, ct) =>
                PlatformInstaller.DeleteVersionDirectoryAsync(version, log, ct),
            // issue #334: обновление списка версий и связанных свойств — в UI-потоке
            // (WPF CollectionView запрещает изменения из фонового потока NotSupportedException).
            dispatchToUi: action => Dispatcher.InvokeAsync(action),
            // issue #334: диалог выбора варианта дистрибутива после «Скачать и установить».
            chooseDistribution: ShowDistributionPicker);

        DataContext = _viewModel;
        RowsGrid.ItemsSource = _viewModel.Rows;

        // Автопрокрутка журнала к последней строке при добавлении сообщений.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Закрытие окна по Esc (паттерн ActualReleasesWindow, issue #264).
        PreviewKeyDown += OnWindow_PreviewKeyDown;
    }

    /// <summary>Закрывает окно по Esc без модификаторов (issue #264).</summary>
    private void OnWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>Передаёт выделенную строку списка в ViewModel (доступность команд
    /// «Скачать и установить»/«Только скачать» зависит от выделения).</summary>
    private void OnRowsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SelectedRow = RowsGrid.SelectedItem as PlatformUpdateRowViewModel;
    }

    /// <summary>Таймер отложенного скрытия панели статуса (анти-мигание, issue #334).</summary>
    private DispatcherTimer? _hideStatusTimer;

    /// <summary>Задержка скрытия панели статуса после завершения операции: панель не
    /// «мелькает» при мгновенном сбое проверки обновлений (issue #334).</summary>
    private static readonly TimeSpan StatusHideDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Автопрокрутка журнала в конец и анти-мигание панели статуса:
    /// показ при старте операции, скрытие с задержкой <see cref="StatusHideDelay"/>
    /// после её завершения. PropertyChanged от AppendLog может прийти с ФОНОВОГО
    /// потока (CheckUpdatesAsync использует ConfigureAwait(false)), а прямой вызов
    /// ScrollToEnd в WPF бросает InvalidOperationException «Вызывающий поток не может
    /// получить доступ к данному объекту» (issue #334). Прокрутка перекидывается в
    /// UI-поток; защита ?. покрывает закрытие окна до исполнения отложенного вызова.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlatformUpdateViewModel.LogText))
        {
            Dispatcher.BeginInvoke(new Action(() => LogBox?.ScrollToEnd()));
            return;
        }

        if (e.PropertyName != nameof(PlatformUpdateViewModel.IsBusy))
            return;

        _hideStatusTimer?.Stop();
        if (_viewModel.IsBusy)
        {
            StatusPanel.Visibility = Visibility.Visible;
            return;
        }

        _hideStatusTimer = new DispatcherTimer { Interval = StatusHideDelay };
        _hideStatusTimer.Tick += (_, _) =>
        {
            _hideStatusTimer!.Stop();
            StatusPanel.Visibility = Visibility.Collapsed;
        };
        _hideStatusTimer.Start();
    }

    /// <summary>Читает установленные версии платформы через Windows-сканер
    /// <see cref="PlatformVersionService.FindInstalledVersionInfos"/> и приводит их
    /// к чистым номерам версий (<see cref="PlatformVersionService.ParseVariant"/> —
    /// суффикс разрядности «(64)» отбрасывается) для сопоставления с каталогом.</summary>
    private static IReadOnlyList<string> LoadInstalledVersions()
    {
        return PlatformVersionService.FindInstalledVersionInfos()
            .Select(info =>
            {
                PlatformVersionService.ParseVariant(info.Display, out var clean, out _);
                return clean;
            })
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
    }

    /// <summary>Адаптер инжектируемого установщика: результат <see cref="PlatformInstaller"/>
    /// приводится к кортежу, понятному чистой ViewModel (тип результата существует
    /// только в Windows-сборке под <c>#if WINDOWS</c>).</summary>
    private static async Task<(bool Success, string? ErrorKey, int ExitCode)> InstallFromZipAsync(
        string zipPath, string version, string? installDirectory,
        IProgress<string>? log, CancellationToken ct)
    {
        var result = await PlatformInstaller.InstallFromZipAsync(zipPath, version, installDirectory, log, ct)
            .ConfigureAwait(false);
        return (result.Success, result.ErrorKey, result.ExitCode);
    }

    /// <summary>Открывает login.1c.ru в браузере (issue #323/#330/#334): пользователь выполняет
    /// вход вручную, после чего возвращается в окно и повторяет проверку.</summary>
    private void OnOpenLoginClick(object sender, RoutedEventArgs e)
    {
        if (!OneCLauncher.OpenUrl("https://login.1c.ru/login"))
        {
            _viewModel.AppendLog(LocalizationManager.T("Settings.About.LinkOpenFailed"));
        }
    }

    /// <summary>Открывает справочник учётных записей ИТС (issue #323/#330/#334): после правки
    /// данных повторный вход использует обновлённую запись.</summary>
    private void OnItsAccountsClick(object sender, RoutedEventArgs e)
    {
        var win = new ItsAccountsWindow { Owner = this };
        win.ShowDialog();
    }

    private void OnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Диалог выбора варианта дистрибутива (issue #334): список файлов для текущей ОС
    /// (x86/x64, полный/тонкий клиент), предвыбран рекомендуемый; null — отмена, остаётся
    /// рекомендуемый вариант. Может вызываться из фонового потока — показ переводится
    /// в UI-поток через <see cref="Dispatcher"/>.
    /// </summary>
    private PlatformDistributionOption? ShowDistributionPicker(
        IReadOnlyList<PlatformDistributionOption> options)
    {
        PlatformDistributionOption? result = null;
        void Show()
        {
            var picker = new PlatformDistributionPickerWindow(options) { Owner = this };
            if (picker.ShowDialog() == true)
                result = picker.Result;
        }

        if (Dispatcher.CheckAccess())
            Show();
        else
            Dispatcher.Invoke(Show);
        return result;
    }
}
#endif