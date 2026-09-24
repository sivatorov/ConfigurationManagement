#if LINUX
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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

        /// <summary>
        /// Хронометр запуска (issue #153): по нему пишутся метки этапов старта Linux/Avalonia,
        /// чтобы на машине пользователя (VirtualBox/KDE NEON X11) было видно, до какого этапа
        /// дошёл запуск и где именно он останавливается или начинает «молотить» CPU.
        /// </summary>
        private static readonly System.Diagnostics.Stopwatch _startupWatch =
            System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Работает ли режим единственного экземпляра: блокировка взята и сигнал
        /// от повторного запуска слушается. Значение относится к текущему
        /// процессу и после старта не меняется, даже если настройку переключат:
        /// блокировка берётся один раз, и снятый в окне настроек флажок не
        /// делает окно возвращаемым повторным запуском.
        /// </summary>
        internal static bool SingleInstanceActive { get; private set; }

        private const string LockFileName = "configuration-management.lock";
        private const string ActivateFileName = "activate";

        /// <summary>
        /// Каталог данных приложения (например ~/.config/ConfigurationManagement).
        /// Считается тем же механизмом, что и у репозитория настроек: расчёт
        /// напрямую через SpecialFolder.ApplicationData на Linux даёт пустую
        /// строку, когда каталога из XDG_CONFIG_HOME нет, путь становится
        /// относительным, и приложение молча не запускается.
        /// </summary>
        private static string DataDirectory =>
            _dataDir ??= Services.PlatformPaths.AppDataDirectory;

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
                    ShowFatalError(TOr("App.Fatal.Critical", "Критическая ошибка"), ex);
            };

            // Необработанные исключения на UI-потоке (команды, построение и показ модальных
            // окон) Avalonia маршрутизирует через Dispatcher. Без обработчика они поднимаются
            // наверх и завершают процесс аварийно (Signal 6 / SIGABRT), как в issue #168:
            // при правке свойств базы и при открытии настроек приложение падало без отчёта.
            // Перехватываем, логируем в errors.log и не даём abort-у убить процесс. Окно,
            // которое строится в момент сбоя, не откроется, но приложение продолжит работу.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                ShowFatalError(TOr("App.Fatal.Interface", "Ошибка интерфейса"), args.Exception);
                args.Handled = true;
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                ShowFatalError(TOr("App.Fatal.BackgroundTask", "Ошибка фоновой задачи"), args.Exception);
                args.SetObserved();
            };

            // Освобождаем файловый lock при завершении процесса и фиксируем факт выхода.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                // Диагностика issue #153: запись о штатном (managed) выходе с кодом возврата.
                // Если окно закрылось «само», а этой строки в логе нет — процесс завершился
                // нативным сбоем (SIGSEGV/SIGABRT в рендере/вводе) до какого-либо управляемого
                // обработчика, и причина не в логике приложения, а в окружении (vmwgfx/GL).
                try { LogProcessExit(); } catch { /* ignore */ }
                try { _activateCts?.Cancel(); _activateCts?.Dispose(); } catch { /* ignore */ }
                try { _instanceLock?.Dispose(); } catch { /* ignore */ }
                // Останавливаем планировщик заданий по расписанию (issue #286).
                try { AppServices.GetRequiredService<SchedulerService>().Stop(); } catch { /* ignore */ }
            };

            try
            {
                // Загружаем настройки до показа окна, чтобы проверить запрет второго экземпляра.
                AppServices.Configure();

                // Логгер доступен только после настройки контейнера. Метки этапов запуска
                // (issue #153) пишутся в журнал приложения и в консоль, чтобы на виртуальной
                // машине пользователя (VirtualBox/KDE NEON X11) было видно, до какого этапа
                // старт дошёл и где именно он останавливается или начинает «молотить» CPU.
                var startupLogger = AppServices.GetRequiredService<Services.IAppLogger>();
                LogStartupStage(startupLogger, "Контейнер настроен");

                // Диагностика окружения рендеринга (issue #153): флаги непрозрачности/анимаций
                // и переменные сессии пишутся в файловый лог один раз при старте, чтобы на
                // машине пользователя (VirtualBox/KDE NEON X11) было видно, какой режим выбран
                // и почему окно «висит» или рисуется чёрным (связано с #177).
                try
                {
                    Services.LinuxRendering.LogStartupDiagnostics(startupLogger);
                    LogStartupStage(startupLogger, "Диагностика окружения рендеринга выполнена");
                }
                catch
                {
                    // Диагностика не должна блокировать запуск.
                    LogStartupStage(startupLogger, "Диагностика окружения рендеринга пропущена");
                }

                // Портативный режим (функция №1 StartManager, Этап 10): если приложение
                // запущено со сменного носителя (portable.dat рядом с exe или env-флаг),
                // при первом запуске переносим данные из системного каталога в каталог
                // рядом с приложением. Выполняется ДО инициализации профилей, чтобы
                // репозиторий сразу писал в портативный каталог.
                try { Services.PortablePaths.EnsurePortableData(); }
                catch { /* портативный режим — вспомогательная возможность */ }

                // Инициализируем учётные записи (профили): загружаем реестр, при первом
                // запуске мигрируем легаси-данные в профиль по умолчанию. Репозиторий
                // читает/пишет файлы данных в каталог активного профиля.
                var profileService = AppServices.GetRequiredService<IProfileService>();
                profileService.EnsureInitialized();
                LogStartupStage(startupLogger, "Профили инициализированы");

                // Любое окно, закрытое до создания главного, гасит приложение: режим
                // завершения по умолчанию OnLastWindowClose считает его последним,
                // и запуск падает с «Dispatcher shut down». Поэтому до показа
                // главного окна завершение только явное, а прежний режим
                // возвращается после. Условие здесь не на число профилей: на этом
                // отрезке модальным может оказаться и другое окно.
                var shutdownModeBeforeStartup = ShutdownMode.OnLastWindowClose;
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime startupLifetime)
                {
                    shutdownModeBeforeStartup = startupLifetime.ShutdownMode;
                    startupLifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }

                // Если в приложении несколько учётных записей — показываем окно авторизации
                // по аналогии со списком пользователей 1С. При одной записи входим без запроса.
                if (profileService.Profiles.Count > 1)
                {
                    LogStartupStage(startupLogger, "Окно авторизации: профилей более одного");

                    // Локализацию поднимаем до показа окна: настройки выбранного профиля
                    // читаются ниже, а без словаря окно входа показывает ключи
                    // (Auth.Title, Auth.Login) вместо подписей. Язык берётся из профиля,
                    // активного с прошлого запуска, и уточняется после выбора.
                    try
                    {
                        var startupSettings = AppServices.GetRequiredService<IInfobaseRepository>().LoadSettings();
                        LocalizationManager.Instance.Initialize(startupSettings.Language, DataDirectory);
                    }
                    catch
                    {
                        LocalizationManager.Instance.Initialize(null, DataDirectory);
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
                else
                {
                    LogStartupStage(startupLogger, "Окно авторизации не требуется");
                }

                ProfileBackupService.DataDirectoryResolver = () => profileService.CurrentProfileDataDirectory;

                var repository = AppServices.GetRequiredService<IInfobaseRepository>();
                AppSettings settings;
                try { settings = repository.LoadSettings(); }
                catch { settings = new AppSettings(); }
                LogStartupStage(startupLogger, "Настройки загружены");

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
                        Console.Error.WriteLine("[profile] Ошибка восстановления профиля: " + ex.Message);
                    }
                }

                // Инициализируем локализацию: выбранный или системный язык, а также
                // загружаем внешние языки (.json) из папок Languages (рядом с приложением
                // и в каталоге данных).
                try
                {
                    LocalizationManager.Instance.Initialize(settings.Language, DataDirectory);
                    // Если словарь уже поднят ради окна входа, Initialize выходит сразу,
                    // поэтому язык выбранного профиля применяется отдельно и по тем же
                    // правилам: пустое значение означает язык системы.
                    LocalizationManager.Instance.ApplyPreferredLanguage(settings.Language);
                }
                catch
                {
                    // Локализация не должна блокировать запуск приложения.
                }
                LogStartupStage(startupLogger, "Локализация инициализирована");

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
                    SingleInstanceActive = true;
                }
                LogStartupStage(startupLogger, "Проверка единственного экземпляра завершена");

                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    _desktopLifetime = desktop;

                    // Применяем сохранённую цветовую схему (две палитры) и вариант темы.
                    // Старые раздельные схемы (активная + слоты светлой/тёмной) мигрируются
                    // в единую схему.
                    var mergedScheme = Configuration_Management.Models.ColorScheme.FromLegacy(
                        settings.ActiveColorScheme, settings.LightColorScheme, settings.DarkColorScheme);
                    var activeTheme = string.IsNullOrWhiteSpace(settings.Theme)
                        ? ThemeManager.LightThemeName
                        : settings.Theme;
                    ThemeManager.ApplyScheme(mergedScheme);
                    ThemeManager.ApplyTheme(activeTheme == ThemeManager.DarkThemeName);

                    // Компактный режим интерфейса (влияет на метрики отступов/иконок,
                    // должен быть установлен до построения главного окна).
                    UiMetrics.Compact = settings.CompactMode;
                    LogStartupStage(startupLogger, "Тема применена");

                    var mainWindow = AppServices.GetRequiredService<MainWindow>();
                    LogStartupStage(startupLogger, "Главное окно построено");

                    // Версия в заголовке (информационная версия, напр. «0.3.1.1»).
                    // Из InformationalVersion отбрасываем возможный суффикс «+<sha>».
                    var infoVersion = VersionInfo.Display();
                    var versionText = string.IsNullOrWhiteSpace(infoVersion) ? "" : $" v{infoVersion}";
                    mainWindow.Title = $"{LocalizationManager.T("App.Title")}{versionText}";

                    // Применяем сохранённые настройки шрифта интерфейса и отдельных областей
                    // (дерево групп, кнопки, поля ввода, правая панель, статус-бар).
                    ThemeManager.ApplyFont(mainWindow,
                        settings.FontFamily, settings.FontSize, settings.FontWeight, settings.FontStyle);
                    // Меню, подсказки и выпадающие списки живут в отдельных корнях
                    // и шрифт окна не наследуют, поэтому им он ставится стилем.
                    ThemeManager.ApplyFontToPopups(
                        settings.FontFamily, settings.FontSize, settings.FontWeight, settings.FontStyle);
                    ThemeManager.ApplyElementFonts(mainWindow, settings.ElementFonts);

                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    LogStartupStage(startupLogger, "Главное окно показано");

                    // Фоновая проверка обновлений (Linux/Avalonia): запускаем после показа
                    // главного окна, чтобы не задерживать старт. Если пользователь отключил
                    // проверку в настройках — пропускаем. При обнаружении новой версии всегда
                    // показывается единый диалог с вопросом о применении обновления.
                    var updateService = AppServices.GetRequiredService<UpdateService>();
                    // Флаг ставится всегда, а не только вместе с фоновой проверкой: тот же
                    // экземпляр службы обслуживает кнопку «Проверить обновления» во вкладке
                    // «О программе». Пока присваивание стояло внутри условия, у выключивших
                    // проверку при запуске оставалось значение по умолчанию (true), и ручная
                    // проверка молча скачивала и ставила новую версию с перезапуском, минуя
                    // вопрос о согласии и вопрос о моменте перезапуска.
                    updateService.ApplyAutoUpdatePolicy(settings.AutoUpdateEnabled);

                    if (settings.CheckForUpdatesOnStartup)
                        CheckForUpdatesInBackground(updateService);

                    // Задания по расписанию (issue #286): запускаем планировщик после
                    // входа в профиль и показа главного окна. Он работает, пока приложение
                    // запущено; остановка выполняется при завершении приложения.
                    try
                    {
                        AppServices.GetRequiredService<SchedulerService>().Start();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[schedule] Ошибка запуска планировщика: " + ex.Message);
                    }

                    // Прежний режим завершения возвращается: на время старта он
                    // переключался на явный, иначе закрытие окна входа гасило
                    // приложение до появления главного.
                    desktop.ShutdownMode = shutdownModeBeforeStartup;
                    LogStartupStage(startupLogger, "Запуск завершён");
                }
            }
            catch (Exception ex)
            {
                // issue #213: при раннем сбое локализация может быть ещё не загружена,
                // тогда T вернёт сам ключ — подставляем встроенный читаемый текст.
                ShowFatalError(TOr("App.Fatal.StartupFailed", "Не удалось запустить приложение"), ex);
                Shutdown(1);
            }

            base.OnFrameworkInitializationCompleted();
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
        /// Завершает приложение на этапе запуска. У Avalonia Application нет метода Shutdown,
        /// а desktop.Shutdown здесь не годится: оба вызова происходят внутри
        /// OnFrameworkInitializationCompleted, то есть до входа в цикл сообщений, и гасят
        /// Dispatcher раньше времени. Тогда MainLoop падает с InvalidOperationException
        /// «Cannot perform requested operation because the Dispatcher shut down».
        /// </summary>
        private static void Shutdown(int exitCode = 0) => Environment.Exit(exitCode);

        /// <summary>
        /// Пишет метку этапа запуска в журнал приложения и в консоль (issue #153).
        /// Диагностическая сборка Linux: по времени нарастающим итогом видно, до какого
        /// этапа дошёл старт и где он остановился или начал циклически «молотить» CPU.
        /// Безопасен на любом этапе — при недоступном логгере просто молча пропускается.
        /// </summary>
        private static void LogStartupStage(IAppLogger? logger, string stage)
        {
            try
            {
                var elapsed = _startupWatch.ElapsedMilliseconds;
                var message = $"[startup {elapsed} мс] {stage}";
                if (logger is not null)
                    logger.Info(message);
                else
                    Console.WriteLine(message);
            }
            catch { /* диагностика не должна блокировать запуск */ }
        }

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
        /// Записывает фатальную ошибку в errors.log и на stderr, а также в журнал
        /// приложения через логгер, если контейнер уже настроен.
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

                // Дублируем в штатный журнал приложения (app.log), если логгер уже доступен:
                // в нём фатальная ошибка видна рядом с обычными событиями и облегчает разбор.
                try
                {
                    var logger = AppServices.Services?.GetService<IAppLogger>();
                    logger?.Error($"{title}: {ex.Message}", ex);
                }
                catch { /* ignore */ }
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Фиксирует факт штатного (managed) завершения процесса с кодом возврата
        /// в консоль и errors.log. Диагностика issue #153: если окно закрылось «само»,
        /// а этой записи в логе нет, значит процесс упал нативно (SIGSEGV/SIGABRT)
        /// до какого-либо управляемого обработчика — причина не в логике приложения,
        /// а в рендере/вводе окружения (например, GL на vmwgfx).
        /// </summary>
        private static void LogProcessExit()
        {
            try
            {
                var text =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Процесс завершается (managed exit), код возврата {Environment.ExitCode}.";
                Console.WriteLine(text);
                try
                {
                    Directory.CreateDirectory(DataDirectory);
                    File.AppendAllText(Path.Combine(DataDirectory, "errors.log"), text + Environment.NewLine);
                }
                catch { /* ignore */ }
            }
            catch { /* ignore */ }
        }
    }
}
#endif