#if WINDOWS
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;

namespace Configuration_Management
{
    public partial class App : Application
    {
        private static Mutex? _instanceMutex;
        private static EventWaitHandle? _activateEvent;
        private static CancellationTokenSource? _activateCts;
        private const string MutexName = "Global\\ConfigurationManagement_1C_SingleInstance";
        private const string ActivateEventName = "Global\\ConfigurationManagement_1C_Activate";

        protected override void OnStartup(StartupEventArgs e)
        {
            // Единое «стеклянное» оформление всех диалоговых окон: общий хелпер применяет
            // WindowChrome, собственные кнопки окна и полупрозрачную подложку к каждому
            // окну приложения (главное, оформленное самостоятельно, пропускается).
            WindowChromeHelper.RegisterGlobalWindowStyling();

            // Режим COM-агента перехватывается раньше, в Program.Main: агенту не нужны
            // ни WPF, ни ресурсные словари тем. См. ComReadHost.

            // Показываем любые необработанные ошибки — иначе окно просто не появляется.
            DispatcherUnhandledException += (_, args) =>
            {
                LogFatal(LocalizationManager.T("App.Fatal.Interface"), args.Exception);
                ShowFatalError(LocalizationManager.T("App.Fatal.Interface"), args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    LogFatal(LocalizationManager.T("App.Fatal.Critical"), ex);
                    ShowFatalError(LocalizationManager.T("App.Fatal.Critical"), ex);
                }
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                ShowFatalError(LocalizationManager.T("App.Fatal.BackgroundTask"), args.Exception);
                args.SetObserved();
            };

            try
            {
                // Загружаем настройки до показа окна, чтобы проверить запрет второго экземпляра.
                AppServices.Configure();

                // Инициализируем учётные записи (профили): загружаем реестр, при первом
                // запуске мигрируем легаси-данные в профиль по умолчанию. Репозиторий
                // читает/пишет файлы данных в каталог активного профиля.
                var profileService = AppServices.GetRequiredService<IProfileService>();
                profileService.EnsureInitialized();

                // Если в приложении несколько учётных записей — показываем окно авторизации
                // по аналогии со списком пользователей 1С. При одной записи входим без запроса.
                if (profileService.Profiles.Count > 1)
                {
                    var selectedId = LoginWindow.ShowLogin(profileService);
                    if (selectedId == null)
                    {
                        // Вход отменён — завершаем приложение.
                        Shutdown();
                        return;
                    }
                    profileService.SetCurrentProfile(selectedId);
                }

                ProfileBackupService.DataDirectoryResolver = () => profileService.CurrentProfileDataDirectory;

                var repository = AppServices.GetRequiredService<IInfobaseRepository>();
                AppSettings settings;
                try
                {
                    settings = repository.LoadSettings();
                }
                catch
                {
                    settings = new AppSettings();
                }

                // Восстановление профиля из указанного каталога резервной копии
                // (например, после переустановки системы): настройки, список баз
                // (с пользователями и паролями запуска), группы и ibases.v8i.
                // Файлы копируются до загрузки данных главным окном, поэтому приложение
                // сразу открывается с привычным состоянием. Настройки перечитываются,
                // чтобы последующие этапы запуска использовали восстановленные значения.
                if (settings.ProfileRestoreOnStartup
                    && !string.IsNullOrWhiteSpace(settings.ProfileBackupDirectory)
                    && ProfileBackupService.HasBackup(settings.ProfileBackupDirectory))
                {
                    try
                    {
                        ProfileBackupService.Restore(settings.ProfileBackupDirectory, settings.IbasesSyncFilePath);
                        try { settings = repository.LoadSettings(); }
                        catch { /* оставляем уже прочитанные настройки */ }
                    }
                    catch (Exception ex)
                    {
                        // Сбой восстановления не должен блокировать запуск.
                        System.Diagnostics.Debug.WriteLine("[profile] Ошибка восстановления профиля: " + ex.Message);
                    }
                }

                // Инициализируем локализацию: выбираем сохранённый язык, иначе язык
                // системы. Внешние языки (.json) подгружаются из папки Languages.
                try
                {
                    LocalizationManager.Instance.Initialize(settings.Language);
                }
                catch
                {
                    // Локализация не должна блокировать запуск приложения.
                }

                if (!settings.AllowMultipleInstances)
                {
                    _instanceMutex = new Mutex(true, MutexName, out var createdNew);
                    if (!createdNew)
                    {
                        // Уже запущен другой экземпляр — просим его показать окно (в т.ч. из трея) и выходим.
                        SignalExistingInstance();
                        Shutdown();
                        return;
                    }

                    // Слушаем сигнал от повторных запусков, чтобы поднять окно (в том числе из трея).
                    StartActivationListener();
                }

                base.OnStartup(e);

                // Применяем сохранённую цветовую схему (тему оформления). Предпочитаем раздельную
                // схему для активной базовой темы (Light/Dark), иначе — старый одиночный
                // ActiveColorScheme (миграция) или встроенные цвета.
                var themeName = string.IsNullOrWhiteSpace(settings.Theme)
                    ? ThemeManager.LightThemeName
                    : settings.Theme;
                var isDark = string.Equals(themeName, ThemeManager.DarkThemeName, StringComparison.OrdinalIgnoreCase);
                Configuration_Management.Models.ColorScheme? scheme = isDark
                    ? settings.DarkColorScheme
                    : settings.LightColorScheme;
                if (scheme is not { Colors.Count: > 0 }
                    && settings.ActiveColorScheme is { Colors.Count: > 0 }
                    && settings.ActiveColorScheme.IsDark == isDark)
                {
                    scheme = settings.ActiveColorScheme;
                }
                ThemeManager.ApplyScheme(scheme ?? (isDark
                    ? Configuration_Management.Models.ColorScheme.CreateDark()
                    : Configuration_Management.Models.ColorScheme.CreateLight()));
#if DEBUG
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm_theme_debug.log"),
                        $"[startup] theme='{themeName}' dark={isDark} applied='{ThemeManager.CurrentScheme.Name}' " +
                        $"darkSlot='{settings.DarkColorScheme?.Name}' lightSlot='{settings.LightColorScheme?.Name}' " +
                        $"active='{settings.ActiveColorScheme?.Name}'{System.Environment.NewLine}");
                }
                catch { /* не критично */ }
#endif

                var mainWindow = AppServices.GetRequiredService<MainWindow>();
                MainWindow = mainWindow;

                // Версия в заголовке (используем информационную версию, чтобы показать
                // точное значение «0.2.7.15», которое не помещается в 4-частный AssemblyVersion).
                // Из InformationalVersion отбрасываем возможный суффикс «+<sha>».
                var infoVersion = VersionInfo.Display();
                var versionText = string.IsNullOrWhiteSpace(infoVersion) ? "" : $" v{infoVersion}";
                mainWindow.Title = $"{LocalizationManager.T("App.Title")}{versionText}";

                // Значок в заголовке главного окна — тот же app.ico (основной значок приложения).
                mainWindow.Icon = LoadAppIconImageSource() ?? mainWindow.Icon;

                // Применяем сохранённые настройки шрифта интерфейса.
                ThemeManager.ApplyFont(mainWindow,
                    settings.FontFamily, settings.FontSize, settings.FontWeight, settings.FontStyle);

                // Применяем индивидуальные настройки шрифта отдельных областей.
                ThemeManager.ApplyElementFonts(mainWindow, settings.ElementFonts);

                // Компактный режим применяется в MainWindow.OnWindowLoaded, когда
                // визуальное дерево уже построено. Здесь его вызывать нельзя:
                // ApplyCompact обходит дерево через VisualTreeHelper, а до показа
                // окна оно ещё пустое, поэтому масштабирование не сработало бы.

                mainWindow.Show();

                // Фоновая проверка обновлений (Windows/WPF): запускаем после показа
                // главного окна, чтобы не задерживать старт. Если пользователь отключил
                // проверку в настройках — пропускаем. Работа выполняется асинхронно,
                // UI при этом не блокируется.
                if (settings.CheckForUpdatesOnStartup)
                {
                    var updateService = AppServices.GetRequiredService<UpdateService>();
                    // Передаём флаг автоматического self-update из настроек: при включённом
                    // режиме фоновая проверка сама скачает, установит и перезапустит приложение.
                    updateService.AutoUpdateEnabled = settings.AutoUpdateEnabled;
                    CheckForUpdatesInBackground(updateService);
                }
            }
            catch (Exception ex)
            {
                LogFatal(LocalizationManager.T("App.Fatal.StartupFailed"), ex);
                ShowFatalError(LocalizationManager.T("App.Fatal.StartupFailed"), ex);
                Shutdown(1);
            }
        }

        /// <summary>
        /// Запускает фоновую проверку обновлений и не ждёт её завершения.
        /// Внутренние ошибки ловятся в <see cref="UpdateService"/>, здесь лишь
        /// дополнительно страхуемся, чтобы исключение не уронило поток.
        /// </summary>
        private static async void CheckForUpdatesInBackground(UpdateService updateService)
        {
            try
            {
                await updateService.CheckForUpdatesAsync().ConfigureAwait(false);
            }
            catch
            {
                // Фоновая проверка не должна влиять на запуск и работу приложения.
            }
        }

        /// <summary>
        /// Загружает значок приложения (app.ico) для заголовка главного окна.
        /// Использует IconBitmapDecoder — WPF-декодер именно для .ico-файлов.
        /// </summary>
        private static System.Windows.Media.ImageSource? LoadAppIconImageSource()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
                var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(
                    uri,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                return decoder.Frames[0];
            }
            catch { return null; }
        }

        /// <summary>
        /// Записывает полный стек-трейс фатальной ошибки в лог-файл (для диагностики).
        /// Сам по себе не бросает исключений, даже если логирование недоступно.
        /// </summary>
        private static void LogFatal(string title, Exception ex)
        {
            try
            {
                var logger = AppServices.GetRequiredService<IAppLogger>();
                var sb = new StringBuilder();
                sb.AppendLine(title);
                sb.AppendLine("Исключение: " + ex.GetType().FullName);
                sb.AppendLine("Сообщение: " + ex.Message);
                sb.AppendLine("StackTrace:");
                sb.AppendLine(ex.ToString());
                logger.Error(sb.ToString());
            }
            catch
            {
                // Логирование не должно маскировать исходную ошибку.
            }
        }

        private static void ShowFatalError(string title, Exception ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(title);
                sb.AppendLine();
                sb.AppendLine(ex.Message);
                if (ex.InnerException != null)
                {
                    sb.AppendLine();
                    sb.AppendLine(LocalizationManager.T("App.Fatal.InternalError"));
                    sb.AppendLine(ex.InnerException.Message);
                }
                sb.AppendLine();
                sb.AppendLine(ex.GetType().FullName);
                // Не перегружаем пользователя огромным стеком, но даём начало.
                var stack = ex.StackTrace ?? "";
                if (stack.Length > 1200)
                    stack = stack[..1200] + "…";
                sb.AppendLine(stack);

                MessageBox.Show(sb.ToString(), LocalizationManager.T("App.Fatal.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // ignore
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Процесс-агент COM закрывается сам, когда закроется его stdin, но при
            // нештатном завершении лучше не полагаться на это и убрать его явно.
            try
            {
                ComReadHost.Shutdown();
            }
            catch
            {
                // ignore
            }

            try
            {
                var logger = AppServices.GetRequiredService<IAppLogger>();
                logger.Info("Приложение завершает работу");
            }
            catch
            {
                // ignore
            }

            try
            {
                _activateCts?.Cancel();
                _activateCts?.Dispose();
                _activateEvent?.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                _instanceMutex?.ReleaseMutex();
                _instanceMutex?.Dispose();
            }
            catch
            {
                // ignore
            }

            base.OnExit(e);
        }

        /// <summary>
        /// Сообщает уже запущенному экземпляру, что нужно показать главное окно.
        /// Работает и когда окно свёрнуто в трей (MainWindowHandle == 0).
        /// </summary>
        private static void SignalExistingInstance()
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
            }
            catch
            {
                // Запасной вариант: попытка через handle главного окна.
                try
                {
                    var current = System.Diagnostics.Process.GetCurrentProcess();
                    foreach (var process in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
                    {
                        if (process.Id == current.Id)
                            continue;

                        var handle = process.MainWindowHandle;
                        if (handle == IntPtr.Zero)
                            continue;

                        ShowWindow(handle, 9); // SW_RESTORE
                        SetForegroundWindow(handle);
                        break;
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        /// <summary>
        /// В основном процессе ждёт сигнал от повторных запусков и активирует окно.
        /// </summary>
        private static void StartActivationListener()
        {
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                _activateCts = new CancellationTokenSource();
                var token = _activateCts.Token;

                Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (_activateEvent.WaitOne(500))
                            {
                                Current?.Dispatcher?.BeginInvoke(new Action(ActivateMainWindow));
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                        catch
                        {
                            // ignore transient errors
                        }
                    }
                }, token);
            }
            catch
            {
                // ignore — повторный запуск всё равно попытается через handle
            }
        }

        /// <summary>
        /// Показывает и активирует главное окно (в том числе если оно было скрыто в трей).
        /// </summary>
        private static void ActivateMainWindow()
        {
            try
            {
                if (Current?.MainWindow is MainWindow mw)
                {
                    mw.RestoreFromTrayPublic();
                    return;
                }

                var win = Current?.MainWindow;
                if (win is null)
                    return;

                if (!win.IsVisible)
                    win.Show();
                if (win.WindowState == WindowState.Minimized)
                    win.WindowState = WindowState.Normal;
                win.Activate();
                win.Topmost = true;
                win.Topmost = false;
                win.Focus();
            }
            catch
            {
                // ignore
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
#endif
