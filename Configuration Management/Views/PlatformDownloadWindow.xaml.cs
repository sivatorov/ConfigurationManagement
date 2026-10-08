#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management;

/// <summary>
/// Окно «Скачивание версии платформы 1С» (issue #330): список версий каталога
/// releases.1c.ru, выбор разрядности (32/64) и типа дистрибутива, скачивание файла
/// в выбранную папку с прогрессом. Установка НЕ выполняется автоматически — после
/// скачивания пользователь сам открывает папку или запускает установщик (Windows:
/// распаковка zip и запуск setup.exe в интерактивном режиме). Авторизация портала —
/// через учётную запись ИТС из справочника (#333). Сервисы берутся из
/// <see cref="AppServices"/>; вся логика — в чистой <see cref="PlatformDownloadViewModel"/>.
/// </summary>
public partial class PlatformDownloadWindow : Window
{
    private readonly PlatformDownloadViewModel _viewModel;
    private readonly IInfobaseRepository _repository;
    private readonly AppSettings _settings;

    /// <summary>Открывает окно «Скачивание версии платформы 1С».</summary>
    public PlatformDownloadWindow()
    {
        InitializeComponent();

        var service = AppServices.GetRequiredService<IPlatformUpdateService>();
        var updates = AppServices.GetRequiredService<IOneCUpdatesService>();
        var accounts = AppServices.GetRequiredService<IItsAccountsStore>();
        var archive = AppServices.GetRequiredService<IArchiveService>();
        var dialogs = AppServices.GetRequiredService<IDialogService>();
        var notifier = AppServices.GetRequiredService<INotificationService>();
        var logger = AppServices.GetRequiredService<IAppLogger>();
        _repository = AppServices.GetRequiredService<IInfobaseRepository>();
        _settings = _repository.LoadSettings();
        var initialDirectory = ResolveDefaultDirectory(_settings.PlatformDownloadDirectory);

        _viewModel = new PlatformDownloadViewModel(
            service,
            // Резолв учётной записи ИТС: выбранная в настройках, иначе «Основная» (issue #333).
            () => accounts.Resolve(_settings.ItsAccountId),
            (url, targetPath, progress, ct) =>
                updates.DownloadDistributionAsync(url, targetPath, progress, ct),
            OpenDownloadedFolder,
            path => RunInstaller(path, archive),
            chooseDirectory: () => dialogs.OpenFolderDialog(
                LocalizationManager.T("PlatformDownload.ChooseDirectoryTitle"), initialDirectory),
            is64Bit: Environment.Is64BitOperatingSystem,
            defaultDirectory: initialDirectory,
            isWindows: true,
            notify: (title, message, kind, evt) => notifier.Show(title, message, kind, evt),
            appLogger: logger,
            // issue #330: заполнение списка версий и связанных свойств — в UI-потоке
            // (WPF CollectionView запрещает изменения из фонового потока NotSupportedException).
            dispatchToUi: action => Dispatcher.InvokeAsync(action));

        DataContext = _viewModel;

        // Сохранение каталога загрузок в настройки при изменении (после выбора папки/скачивания).
        _viewModel.PropertyChanged += OnViewModel_PropertyChanged;

        // Закрытие окна по Esc (паттерн ActualReleasesWindow, issue #264).
        PreviewKeyDown += OnWindow_PreviewKeyDown;

        Loaded += OnWindow_Loaded;
    }

    /// <summary>При открытии — первичная проверка каталога версий.</summary>
    private async void OnWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.VersionTree.Count == 0)
            await _viewModel.LoadCatalogAsync();
    }

    private void OnWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>Выбор листа дерева версий передаёт релиз в ViewModel (issue #330).</summary>
    private void OnVersionsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.SelectedVersionNode = e.NewValue as PlatformCatalogNode;
    }

    private void OnViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlatformDownloadViewModel.TargetDirectory))
        {
            try
            {
                _settings.PlatformDownloadDirectory = _viewModel.TargetDirectory;
                _repository.SaveSettings(_settings);
            }
            catch
            {
                // Несохранение каталога загрузок не критично.
            }
        }

        // Автопрокрутка журнала (issue #330): PropertyChanged от AppendLog может прийти
        // с ФОНОВОГО потока (LoadCatalogAsync/DownloadAsync используют ConfigureAwait(false)),
        // а прямой вызов ScrollToEnd в WPF бросает InvalidOperationException «Вызывающий поток
        // не может получить доступ к данному объекту». Перекидываем прокрутку в UI-поток;
        // защита ?. покрывает закрытие окна до исполнения отложенного вызова.
        if (e.PropertyName == nameof(PlatformDownloadViewModel.LogText))
        {
            Dispatcher.BeginInvoke(new Action(() => LogBox?.ScrollToEnd()));
        }
    }

    /// <summary>Открывает папку со скачанным файлом (проводник с выделением файла).</summary>
    private static bool OpenDownloadedFolder(string downloadedPath)
    {
        try
        {
            if (File.Exists(downloadedPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{downloadedPath}\"",
                    UseShellExecute = true
                });
                return true;
            }

            var dir = Path.GetDirectoryName(downloadedPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>Запускает установщик по желанию пользователя (НЕ автоматически): zip-архив
    /// распаковывается во временную папку, находится setup.exe и запускается интерактивно —
    /// тихая установка не выполняется (требование issue #330).</summary>
    private static bool RunInstaller(string downloadedPath, IArchiveService archive)
    {
        try
        {
            if (!File.Exists(downloadedPath))
                return false;

            var extension = Path.GetExtension(downloadedPath).ToLowerInvariant();
            if (extension == ".zip")
            {
                var extractDir = Path.Combine(Path.GetTempPath(),
                    "cm_platform_" + Guid.NewGuid().ToString("N"));
                if (!archive.ExtractArchive(downloadedPath, extractDir))
                    return false;

                var setup = PlatformInstaller.FindSetupExecutable(extractDir);
                if (string.IsNullOrEmpty(setup))
                    return false;

                // Интерактивный запуск мастера установки (без тихих ключей).
                Process.Start(new ProcessStartInfo
                {
                    FileName = setup,
                    UseShellExecute = true
                });
                return true;
            }

            // Прочие типы (например exe) запускаются напрямую.
            Process.Start(new ProcessStartInfo
            {
                FileName = downloadedPath,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Каталог загрузок по умолчанию: сохранённая настройка либо
    /// <c>Загрузки/1CPlatform</c> (создаётся при скачивании).</summary>
    private static string ResolveDefaultDirectory(string? saved)
    {
        if (!string.IsNullOrWhiteSpace(saved))
            return saved.Trim();

        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(downloads, "1CPlatform");
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
}

/// <summary>Преобразует тип дистрибутива в локализованный текст для комбобокса.</summary>
public sealed class PlatformDownloadTypeToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is Services.PlatformDownloadType type)
            return LocalizationManager.T(Services.PlatformDistributionPicker.TypeLocalizationKey(type));
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
#endif