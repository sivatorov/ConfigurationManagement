#if LINUX
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;

namespace Configuration_Management
{
    /// <summary>
    /// Avalonia-версия точки входа приложения (Linux). Заменяет WPF App.xaml / App.xaml.cs.
    /// Собирается только под #if LINUX; Windows использует WPF-версию App.
    /// </summary>
    public partial class App : Application
    {
        private static FileStream? _instanceLock;
        private static string? _dataDir;
        private static CancellationTokenSource? _activateCts;
        private static IClassicDesktopStyleApplicationLifetime? _desktopLifetime;

        private const string LockFileName = "configuration-management.lock";
        private const string ActivateFileName = "activate";

        /// <summary>Каталог данных приложения (например ~/.config/ConfigurationManagement).</summary>
        private static string DataDirectory =>
            _dataDir ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ConfigurationManagement");

        public override void Initialize()
        {
            base.Initialize();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Показываем любые необработанные ошибки — иначе окно просто не появляется.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    ShowFatalError(LocalizationManager.T("App.Fatal.Critical"), ex);
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                ShowFatalError(LocalizationManager.T("App.Fatal.BackgroundTask"), args.Exception);
                args.SetObserved();
            };

            // Освобождаем файловый lock при завершении процесса.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _activateCts?.Cancel(); _activateCts?.Dispose(); } catch { /* ignore */ }
                try { _instanceLock?.Dispose(); } catch { /* ignore */ }
            };

            try
            {
                // Загружаем настройки до показа окна, чтобы проверить запрет второго экземпляра.
                AppServices.Configure();

                var repository = AppServices.GetRequiredService<IInfobaseRepository>();
                AppSettings settings;
                try { settings = repository.LoadSettings(); }
                catch { settings = new AppSettings(); }

                // Инициализируем локализацию: выбранный или системный язык, а также
                // загружаем внешние языки (.json) из папок Languages (рядом с приложением
                // и в каталоге данных).
                try
                {
                    LocalizationManager.Instance.Initialize(settings.Language, DataDirectory);
                }
                catch
                {
                    // Локализация не должна блокировать запуск приложения.
                }

                if (!settings.AllowMultipleInstances)
                {
                    if (!TryAcquireSingleInstanceLock())
                    {
                        // Уже запущен другой экземпляр — просим его показать окно и выходим.
                        SignalExistingInstance();
                        Shutdown();
                        return;
                    }
                    // Слушаем сигнал от повторных запусков, чтобы поднять окно.
                    StartActivationListener();
                }

                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    _desktopLifetime = desktop;

                    // Применяем сохранённую цветовую схему (тему оформления).
                    if (settings.ActiveColorScheme is { Colors.Count: > 0 })
                        ThemeManager.ApplyScheme(settings.ActiveColorScheme);
                    else
                        ThemeManager.ApplyTheme(
                            string.IsNullOrWhiteSpace(settings.Theme)
                                ? ThemeManager.LightThemeName
                                : settings.Theme);

                    // Компактный режим интерфейса (влияет на метрики отступов/иконок,
                    // должен быть установлен до построения главного окна).
                    UiMetrics.Compact = settings.CompactMode;

                    var mainWindow = AppServices.GetRequiredService<MainWindow>();

                    // Версия в заголовке (информационная версия, напр. «0.3.1.1»).
                    var infoVersion = Assembly.GetExecutingAssembly()
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    var versionText = string.IsNullOrWhiteSpace(infoVersion) ? "" : $" v{infoVersion}";
                    mainWindow.Title = $"{LocalizationManager.T("App.Title")}{versionText}";

                    // Применяем сохранённые настройки шрифта интерфейса и отдельных областей
                    // (дерево групп, кнопки, поля ввода, правая панель, статус-бар).
                    ThemeManager.ApplyFont(mainWindow,
                        settings.FontFamily, settings.FontSize, settings.FontWeight, settings.FontStyle);
                    ThemeManager.ApplyElementFonts(mainWindow, settings.ElementFonts);

                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                }
            }
            catch (Exception ex)
            {
                ShowFatalError(LocalizationManager.T("App.Fatal.StartupFailed"), ex);
                Shutdown(1);
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// Завершает приложение на этапе запуска. У Avalonia Application нет метода Shutdown,
        /// а desktop.Shutdown здесь не годится: оба вызова происходят внутри
        /// OnFrameworkInitializationCompleted, то есть до входа в цикл сообщений, и гасят
        /// Dispatcher раньше времени. Тогда MainLoop падает с InvalidOperationException
        /// «Cannot perform requested operation because the Dispatcher shut down».
        /// </summary>
        private static void Shutdown(int exitCode = 0) => Environment.Exit(exitCode);

        /// <summary>
        /// Захватывает исключительный файловый lock (один экземпляр на Linux).
        /// Файл-блокировка в каталоге данных с FileShare.None.
        /// </summary>
        private static bool TryAcquireSingleInstanceLock()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                _instanceLock = new FileStream(
                    Path.Combine(DataDirectory, LockFileName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) { return false; }                 // другой экземпляр держит lock
            catch (UnauthorizedAccessException) { return false; } // нет прав — считаем занятым
        }

        /// <summary>
        /// Второй экземпляр: создаёт файл-сигнал, чтобы первый показал главное окно.
        /// </summary>
        private static void SignalExistingInstance()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(
                    Path.Combine(DataDirectory, ActivateFileName),
                    DateTime.UtcNow.Ticks.ToString());
            }
            catch { /* ignore — повторный запуск просто завершится */ }
        }

        /// <summary>
        /// Основной экземпляр: следит за файлом-сигналом и активирует главное окно.
        /// </summary>
        private static void StartActivationListener()
        {
            try
            {
                _activateCts = new CancellationTokenSource();
                var token = _activateCts.Token;
                var activatePath = Path.Combine(DataDirectory, ActivateFileName);

                Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (File.Exists(activatePath))
                            {
                                try { File.Delete(activatePath); } catch { /* ignore */ }
                                Dispatcher.UIThread.Post(ActivateMainWindow);
                            }
                        }
                        catch { /* ignore transient errors */ }
                        Thread.Sleep(300);
                    }
                }, token);
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Показывает и активирует главное окно (в том числе если оно было свёрнуто).
        /// </summary>
        private static void ActivateMainWindow()
        {
            try
            {
                if (_desktopLifetime?.MainWindow is not Window win)
                    return;
                if (!win.IsVisible)
                    win.Show();
                if (win.WindowState == WindowState.Minimized)
                    win.WindowState = WindowState.Normal;
                win.Activate();
                win.Topmost = true;
                win.Topmost = false;
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Записывает фатальную ошибку в errors.log и на stderr.
        /// (Полноценный диалог ошибок появится вместе с портом окон — Этап 3.)
        /// </summary>
        private static void ShowFatalError(string title, Exception ex)
        {
            try
            {
                var text = $"{title}{Environment.NewLine}{ex}{Environment.NewLine}";
                Console.Error.WriteLine(text);
                try
                {
                    Directory.CreateDirectory(DataDirectory);
                    File.AppendAllText(
                        Path.Combine(DataDirectory, "errors.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}");
                }
                catch { /* ignore */ }
            }
            catch { /* ignore */ }
        }
    }
}
#endif