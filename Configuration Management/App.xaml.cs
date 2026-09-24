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
        private static bool _ownsInstanceMutex;
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
                var title = TOr("App.Fatal.Interface", "Ошибка интерфейса");
                LogFatal(title, args.Exception);
                ShowFatalError(title, args.Exception);
                // Убираем «окна-зомби» после сбоя конструктора (issue #270): окно, упавшее во
                // время создания, уже успело попасть в Application.Windows, но никогда не было
                // показано; при ShutdownMode.OnLastWindowClose такое окно держит процесс живым
                // после «Выход» — приложение не завершается.
                RemoveBrokenWindows();
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    var title = TOr("App.Fatal.Critical", "Критическая ошибка");
                    LogFatal(title, ex);
                    ShowFatalError(title, ex);
                }
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                ShowFatalError(TOr("App.Fatal.BackgroundTask", "Ошибка фоновой задачи"), args.Exception);
                args.SetObserved();
            };

            try
            {
                // Загружаем настройки до показа окна, чтобы проверить запрет второго экземпляра.
                AppServices.Configure();

                // Портативный режим (функция №1 StartManager, Этап 10): если приложение
                // запущено со сменного носителя (portable.dat рядом с exe или env-флаг),
                // при первом запуске переносим данные из системного каталога в каталог
                // рядом с приложением, чтобы настройки хранились и переносились вместе.
                // Выполняется ДО инициализации профилей, чтобы репозиторий сразу писал
                // в портативный каталог (PlatformPaths.AppDataDirectory учитывает режим).
                try { Services.PortablePaths.EnsurePortableData(); }
                catch { /* портативный режим — вспомогательная возможность */ }

                // Инициализируем учётные записи (профили): загружаем реестр, при первом
                // запуске мигрируем легаси-данные в профиль по умолчанию. Репозиторий
                // читает/пишет файлы данных в каталог активного профиля.
                var profileService = AppServices.GetRequiredService<IProfileService>();
                profileService.EnsureInitialized();

                // Окно входа создаётся первым и становится главным окном приложения
                // (Application.MainWindow). При ShutdownMode=OnLastWindowClose его закрытие
                // после успешного входа молча гасило бы приложение раньше, чем появится
                // главное окно (issue #193). Поэтому на время старта завершение только
                // явное, а прежний режим возвращается после показа главного окна.
                var shutdownModeBeforeStartup = ShutdownMode;
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // Если в приложении несколько учётных записей — показываем окно авторизации
                // по аналогии со списком пользователей 1С. При одной записи входим без запроса.
                if (profileService.Profiles.Count > 1)
                {
                    // Локализацию поднимаем до показа окна: настройки профиля читаются
                    // ниже, а без словаря окно входа показывает ключи (Auth.Title,
                    // Auth.SelectAccountHint, Auth.Login, Common.Cancel) вместо подписей
                    // (issue #189). Язык берётся из профиля, активного с прошлого запуска,
                    // и уточняется после выбора.
                    try
                    {
                        var startupRepository = AppServices.GetRequiredService<IInfobaseRepository>();
                        var startupSettings = startupRepository.LoadSettings();
                        LocalizationManager.Instance.Initialize(startupSettings.Language);
                    }
                    catch
                    {
                        LocalizationManager.Instance.Initialize(null);
                    }

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

                // Команды контекстного меню проводника (функция №12): --register / --launch / --designer.
                // Выполняются после инициализации активного профиля (репозиторий уже указывает на его
                // каталог данных) и до проверки одиночного экземпляра: даже если приложение уже запущено,
                // эта команда обрабатывается здесь, а затем запускается ещё один полноценный процесс.
                ExplorerCommandLine.TryHandle(e.Args);

                // Инициализируем локализацию: выбираем сохранённый язык, иначе язык
                // системы. Внешние языки (.json) подгружаются из папки Languages.
                try
                {
                    LocalizationManager.Instance.Initialize(settings.Language);
                    // Если словарь уже поднят ради окна входа, Initialize выходит сразу,
                    // поэтому язык выбранного профиля применяется отдельно и по тем же
                    // правилам: пустое значение означает язык системы.
                    LocalizationManager.Instance.ApplyPreferredLanguage(settings.Language);
                }
                catch
                {
                    // Локализация не должна блокировать запуск приложения.
                }

                if (!settings.AllowMultipleInstances)
                {
                    _instanceMutex = new Mutex(true, MutexName, out var createdNew);
                    // Владение мутексом получает только экземпляр, создавший его (createdNew == true).
                    // Повторный запуск владение не получает, поэтому ReleaseMutex() в OnExit допустим
                    // только при нашем владении: иначе он бросает ApplicationException
                    // «Object synchronization method was called from an unsynchronized block of code»,
                    // и под отладчиком с остановкой на исключениях это выглядит как падение.
                    _ownsInstanceMutex = createdNew;
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

                // Применяем сохранённую цветовую схему (две палитры) и вариант темы.
                // Старые раздельные схемы (активная + слоты светлой/тёмной) мигрируются
                // в единую схему.
                var mergedScheme = Configuration_Management.Models.ColorScheme.FromLegacy(
                    settings.ActiveColorScheme, settings.LightColorScheme, settings.DarkColorScheme);
                var themeName = string.IsNullOrWhiteSpace(settings.Theme)
                    ? ThemeManager.LightThemeName
                    : settings.Theme;
                ThemeManager.ApplyScheme(mergedScheme);
                ThemeManager.ApplyTheme(themeName == ThemeManager.DarkThemeName);
#if DEBUG
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm_theme_debug.log"),
                        $"[startup] theme='{themeName}' dark={themeName == ThemeManager.DarkThemeName} applied='{ThemeManager.CurrentScheme.Name}' " +
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

                // Прежний режим завершения возвращается: на время старта он переключался
                // на явный, иначе закрытие окна входа гасило приложение до появления главного.
                ShutdownMode = shutdownModeBeforeStartup;

                // Фоновая проверка обновлений (Windows/WPF): запускаем после показа
                // главного окна, чтобы не задерживать старт. Если пользователь отключил
                // проверку в настройках — пропускаем. Работа выполняется асинхронно,
                // UI при этом не блокируется.
                if (settings.CheckForUpdatesOnStartup)
                {
                    var updateService = AppServices.GetRequiredService<UpdateService>();
                    // Передаём флаг автообновления из настроек. При обнаружении новой версии
                    // фоновая проверка ВСЕГДА покажет единый диалог с вопросом «Перезапустить
                    // сейчас / Обновить после закрытия», независимо от этого флага.
                    updateService.AutoUpdateEnabled = settings.AutoUpdateEnabled;
                    CheckForUpdatesInBackground(updateService);
                }

                // Задания по расписанию (issue #286): запускаем планировщик после входа
                // в профиль и показа главного окна. Он работает, пока приложение запущено.
                try
                {
                    AppServices.GetRequiredService<SchedulerService>().Start();
                }
                catch (Exception ex)
                {
                    // Сбой планировщика не должен блокировать запуск приложения.
                    System.Diagnostics.Debug.WriteLine("[schedule] Ошибка запуска планировщика: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                // issue #213: при раннем сбое локализация может быть ещё не загружена,
                // тогда T вернёт сам ключ — подставляем встроенный читаемый текст.
                var fatalTitle = TOr("App.Fatal.StartupFailed", "Не удалось запустить приложение");
                LogFatal(fatalTitle, ex);
                ShowFatalError(fatalTitle, ex);
                Shutdown(1);
            }
        }

        /// <summary>
        /// Возвращает перевод ключа, а если ключ не найден (словари ещё пусты из-за
        /// сбоя до инициализации локализации), — встроенный запасной текст. Так
        /// фатальное сообщение остаётся читаемым при любом состоянии приложения (issue #213).
        /// </summary>
        private static string TOr(string key, string fallback)
        {
            var text = LocalizationManager.T(key);
            return string.Equals(text, key, StringComparison.Ordinal) ? fallback : text;
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
                    sb.AppendLine(TOr("App.Fatal.InternalError", "Внутренняя ошибка:"));
                    sb.AppendLine(ex.InnerException.Message);
                }
                sb.AppendLine();
                sb.AppendLine(ex.GetType().FullName);
                // Не перегружаем пользователя огромным стеком, но даём начало.
                var stack = ex.StackTrace ?? "";
                if (stack.Length > 1200)
                    stack = stack[..1200] + "…";
                sb.AppendLine(stack);

                AppServices.GetRequiredService<IDialogService>()
                    .ShowError(sb.ToString(), TOr("App.Fatal.Title", "Управление конфигурациями 1С — ошибка"));
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

            // Страховка от «зависшего» процесса при выходе (issue #270): закрываем окна,
            // оставшиеся открытыми к моменту завершения (например, «зомби» после сбоя
            // конструктора диалога). В штатном сценарии ShutdownMode.OnLastWindowClose
            // запускает завершение по закрытию последнего окна, и к OnExit все окна уже
            // закрыты; здесь это дополнительная гарантия для нештатных путей.
            CloseRemainingWindows();

            try
            {
                var logger = AppServices.GetRequiredService<IAppLogger>();
                logger.Info("Приложение завершает работу");
            }
            catch
            {
                // ignore
            }

            // Останавливаем планировщик заданий по расписанию (issue #286).
            try
            {
                AppServices.GetRequiredService<SchedulerService>().Stop();
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
                // ReleaseMutex корректен только для экземпляра, владеющего мутексом:
                // повторный запуск (createdNew == false) владение не получает, и вызов
                // на чужом мутексе бросает ApplicationException. При закрытии процесса
                // ОС освобождает мутекс автоматически, поэтому пропуск здесь безопасен.
                if (_ownsInstanceMutex)
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
        /// Закрывает окна, созданные, но так и не показанные (сбой конструктора, issue #270).
        /// Такое окно числится в <see cref="Application.Windows"/>, и при
        /// <see cref="ShutdownMode.OnLastWindowClose"/> приложение после «Выход» не завершается:
        /// режим считает последнее окно открытым. Показанные окна (в т.ч. скрытые в трей) имеют
        /// <c>IsLoaded == true</c> и не затрагиваются, поэтому сценарий «закрытие в трей» не
        /// ломается.
        /// </summary>
        private static void RemoveBrokenWindows()
        {
            try
            {
                var app = Current;
                if (app is null || app.Windows.Count == 0)
                    return;

                for (var i = app.Windows.Count - 1; i >= 0; i--)
                {
                    var window = app.Windows[i];
                    if (window is null || window.IsLoaded)
                        continue;

                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                        // Окно в полуразобранном состоянии: Close может отказать, но попытка
                        // снять его из списка окон не должна маскировать исходную ошибку.
                    }
                }
            }
            catch
            {
                // Очистка не должна маскировать исходную ошибку.
            }
        }

        /// <summary>
        /// Закрывает все оставшиеся окна приложения (issue #270). Используется в
        /// <see cref="OnExit"/> как страховка от «зависшего» процесса: закрытое окно удаляется
        /// из <see cref="Application.Windows"/>, и ничего не держит процесс после завершения.
        /// </summary>
        private static void CloseRemainingWindows()
        {
            try
            {
                var app = Current;
                if (app is null || app.Windows.Count == 0)
                    return;

                for (var i = app.Windows.Count - 1; i >= 0; i--)
                {
                    var window = app.Windows[i];
                    if (window is null)
                        continue;

                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                        // ignore — окно могло быть уже закрыто или находиться в неопределённом
                        // состоянии; это страховочный путь.
                    }
                }
            }
            catch
            {
                // ignore
            }
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
