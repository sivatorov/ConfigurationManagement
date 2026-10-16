using Configuration_Management.Services;
using Configuration_Management.Services.EventLog;
using Configuration_Management.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Configuration_Management;

/// <summary>
/// Корневой контейнер зависимостей приложения.
/// </summary>
public static class AppServices
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static void Configure()
    {
        var services = new ServiceCollection();

        // Общие регистрации для обеих платформ (Windows/WPF и Linux/Avalonia):
        // реализации сервисов платформы 1С не зависят от UI-фреймворка.
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<IAppLogger, FileAppLogger>();
        services.AddSingleton<IInfobaseRepository, InfobaseRepository>();
        services.AddSingleton<IOneCLauncher, OneCLauncherService>();
        services.AddSingleton<IOneCComConnector, OneCComConnector>();
        services.AddSingleton<IPlatformVersionService, PlatformVersionServiceAdapter>();
        services.AddSingleton<IIbasesSyncService, IbasesSyncService>();
        // Пути удалённых пользователем пустых групп (issue #327): персистентный список
        // deleted_groups.json рядом с настройками, чтобы импорт из ibases.v8i не возвращал
        // удалённые пустые группы обратно. Чистый сервис — без UI-зависимостей.
        services.AddSingleton<IDeletedGroupPathsStore, DeletedGroupPathsStore>();
        services.AddSingleton<ICreateInfobaseService, CreateInfobaseService>();
        // Проверка обновлений конфигураций 1С по web-ресурсу обновлений (функции №21/№22).
        services.AddSingleton<IOneCUpdatesService, OneCUpdatesService>();
        // Автообновление платформы 1С (функция 9, цикл 0.3.9.208–0.3.9.216): каталог доступных
        // версий с портала, ленивая подгрузка файлов релиза и выбор дистрибутива под ОС.
        // Чистый сервис — без UI-зависимостей.
        services.AddSingleton<IPlatformUpdateService, PlatformUpdateService>();
        // Пользовательские типовые конфигурации 1С (issue #321): отдельный читаемый JSON-файл
        // custom_config_types.json рядом с настройками; миграция из AppSettings.CustomConfigTypes.
        services.AddSingleton<ICustomConfigTypesStore, CustomConfigTypesStore>();
        // Учётные записи ИТС 1С (issue #333): отдельный читаемый JSON-файл its_accounts.json
        // рядом с настройками; миграция логина/пароля из AppSettings.UpdatesLogin/UpdatesPassword
        // в запись «Основная» при первом обращении. Чистый сервис — без UI-зависимостей.
        services.AddSingleton<IItsAccountsStore, ItsAccountsStore>();
        // Сценарии резервирования и восстановление (функции №16/№18): хранилище сценариев,
        // архивация ZIP/RAR и оркестратор выполнения. Чистые сервисы — без UI-зависимостей.
        services.AddSingleton<IBackupScenarioStore, BackupScenarioStore>();
        // Сценарии запуска скриптов (issue #308): JSON-хранилище и чистая подстановка
        // параметров. Чистые сервисы — без UI-зависимостей.
        services.AddSingleton<IScriptScenarioStore, ScriptScenarioStore>();
        // Пользовательские действия контекстного меню (функция 7, цикл 0.3.9.193–0.3.9.199):
        // единый читаемый JSON-файл custom_actions.json рядом с настройками. Чистый сервис —
        // без UI-зависимостей.
        services.AddSingleton<ICustomActionsStore, CustomActionsStore>();
        // Проверка резервных копий (функция 8, цикл 0.3.9.200–0.3.9.207): кэш результатов
        // проверки — единый читаемый JSON-файл backup_validation_cache.json рядом с настройками.
        // Чистый сервис — без UI-зависимостей.
        services.AddSingleton<IBackupValidationCacheStore, BackupValidationCacheStore>();
        services.AddSingleton<IArchiveService, ArchiveService>();
        services.AddSingleton<IBackupService, BackupService>();
        // Задания по расписанию (issue #286): хранилище заданий, обновление конфигурации ИБ
        // из .cf (/LoadCfg + /UpdateDBCfg) и фоновый планировщик. Чистые сервисы.
        services.AddSingleton<IScheduledTaskStore, ScheduledTaskStore>();
        services.AddSingleton<IConfigUpdateService, ConfigUpdateService>();
        services.AddSingleton<SchedulerService>();
        // Просмотр журнала регистрации ИБ (цикл 0.3.9.161–0.3.9.166): ридер
        // SQLite-формата журнала (.lgd). Ридер последовательного формата (.lgf/.lgp)
        // и фасад LgdReadSession добавляются следующими этапами; выбор ридера по
        // формату журнала будет выполнять LgdReadSession.
        services.AddSingleton<SqliteLgdReader>();
        // Блокировка сеансов файловой ИБ (функция №20): пакетный запуск конфигуратора
        // (/LockIB) без открытия «1С:Предприятия». Чистый сервис — без UI-зависимостей.
        services.AddSingleton<ISessionLockService, SessionLockService>();
        // Интеграция с проводником Windows (функция №12): ассоциация .1CD и команды
        // контекстного меню. На Windows/WPF — реализация на реестре HKCU; на Linux/Avalonia —
        // заглушка (IExplorerIntegrationService.IsAvailable == false). Тип один, реализация
        // выбирается символами условной компиляции (#if WINDOWS / #if LINUX).
        services.AddSingleton<IExplorerIntegrationService, ExplorerIntegrationService>();
        // Автозапуск при старте ОС (функция №31 StartManager): на Windows/WPF — ключ реестра
        // HKCU\...\Run; на Linux/Avalonia — автозапуск десктоп-окружения (~/.config/autostart).
        services.AddSingleton<IAutoStartService, AutoStartService>();
        // Сохранение копии экрана по хоткею (функция №30 StartManager): на Windows — захват
        // всего виртуального рабочего стола (CopyFromScreen); на Linux/Avalonia — снимок окна.
        services.AddSingleton<IScreenshotService, ScreenshotService>();
        // Администрирование ИБ (Этап 6, функция №29 + консоль серверов): запуск chdbfl
        // для проверки целостности файловой ИБ и консоли администрирования серверов 1С
        // для клиент-серверных баз. Чистый сервис — без UI-зависимостей.
        services.AddSingleton<IInfobaseAdminService, InfobaseAdminService>();
        // Встроенный монитор серверов 1С (цикл 0.3.9.123–0.3.9.126): клиент rac
        // (Remote Administration Client) — чистый сервис без UI-зависимостей.
        services.AddSingleton<IRacClient, RacClient>();
        // Диагностика сети до сервера 1С (функция 12, цикл 0.3.9.229–0.3.9.233):
        // DNS-резолв, ICMP-пинг и TCP-проверка портов с RTT. Чистый сервис без
        // UI-зависимостей (низкоуровневые операции инжектируются делегатами).
        services.AddSingleton<INetworkDiagnosticsService, NetworkDiagnosticsService>();
        // Память портов серверов 1С для окна «Диагностика подключения» (issue #335):
        // server_ports.json рядом с настройками — порт на каждый сервис (кластер,
        // rac/монитор, хранилище) в разрезе сервера. Чистый сервис — без UI-зависимостей.
        services.AddSingleton<IServerPortsStore, ServerPortsStore>();
        // Компоновка меню «Утилиты» (issue #374): пользовательская структура меню —
        // utilities_menu_layout.json рядом с настройками, по образцу CustomActionsStore.
        // Чистый сервис — без UI-зависимостей.
        services.AddSingleton<IUtilitiesMenuLayoutStore, UtilitiesMenuLayoutStore>();
        // Обозреватель хранилища конфигурации (цикл 0.3.9.127–0.3.9.130): пакетные операции
        // хранилища через DESIGNER (выгрузка версии .cf, отчёт по истории, захват/отмена
        // захвата) + состав версии. Чистый сервис — без UI-зависимостей.
        services.AddSingleton<IRepositoryStorageService, RepositoryStorageService>();
        // Обозреватель метаданных конфигурации (цикл 0.3.9.132–0.3.9.136): выгрузка
        // /DumpConfigToFiles из базы или .cf (переиспользование инфраструктуры сравнения)
        // без удаления каталога до закрытия окна. Чистый сервис — без UI-зависимостей.
        services.AddSingleton<IMetadataExplorerService, MetadataExplorerService>();
        // Индикатор «база сейчас запущена» и инспектор процессов: список процессов 1С
        // с командными строками и подробностями (PID, время старта, владелец).
        // Windows — WMI (Win32_Process), Linux — обход /proc; тип один, реализация
        // выбирается символами условной компиляции.
        services.AddSingleton<IRunningInfobasesService, RunningInfobasesService>();
        // Уведомления (функции №4/№5): системный канал ОС — balloon-tip трея на
        // Windows/WPF, notify-send на Linux/Avalonia (тип один, реализация выбирается
        // символами условной компиляции #if WINDOWS / #if LINUX); внешние каналы
        // Telegram/email добавляются этапами 0.3.9.183–0.3.9.184. Диспетчер
        // NotificationDispatcher рассылает сообщение всем включённым каналам.
        services.AddSingleton<INotificationChannel, SystemNotificationChannel>();
        // Внешние каналы (выпуск 0.3.12.69): Telegram-бот и email (SMTP). Настройки —
        // отдельный файл notifications.json профиля (NotificationSettingsStore).
        services.AddSingleton<INotificationSettingsStore>(sp => new NotificationSettingsStore(
            sp.GetService<IProfileService>(), null, sp.GetService<IAppLogger>()));
        services.AddSingleton<TelegramNotificationChannel>(sp => new TelegramNotificationChannel(
            sp.GetRequiredService<INotificationSettingsStore>(), sp.GetService<IAppLogger>()));
        services.AddSingleton<EmailNotificationChannel>(sp => new EmailNotificationChannel(
            sp.GetRequiredService<INotificationSettingsStore>(), sp.GetService<IAppLogger>()));
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<TelegramNotificationChannel>());
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<EmailNotificationChannel>());
        services.AddSingleton<INotificationService, NotificationDispatcher>();
        // Сообщение в строке состояния главного окна из фоновых сервисов (issue #366):
        // канал уведомления задания «Проверить наличие обновления платформы» —
        // «В главном окне». Платформенная реализация (WPF/Avalonia) выбирается
        // символами условной компиляции.
        services.AddSingleton<IMainWindowNotifier, MainWindowNotifier>();
        // Завершение процесса 1С по PID (инспектор процессов): Windows — Process.Kill
        // вместе с деревом потомков, Linux — kill через /proc со сверкой времени старта.
        services.AddSingleton<IOneCProcessKiller, OneCProcessKiller>();
        // Переход на новую программу Workspace 1C (0.3.12.70): получение последнего
        // выпуска из GitHub API и скачивание с прогрессом. Кроссплатформенный сервис —
        // используется и Windows/WPF, и Linux/Avalonia.
        services.AddSingleton<Workspace1CMigrationService>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();

#if WINDOWS
        // Windows — приоритетная платформа (WPF). Здесь дополнительно регистрируются
        // WPF-диалоги, регистратор COM-коннектора и Windows-only модели представления окон.
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<IOneCComConnectorRegistrar, OneCComConnectorRegistrar>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ProfilesViewModel>();
        // Проверка обновлений из GitHub Releases (Windows-only, автообновление).
        services.AddSingleton<GitHubReleaseService>();
        // Подсистема автообновления: фоновая проверка и диалог «Доступна новая версия».
        services.AddSingleton<UpdateService>();
#else
        // Linux (Avalonia): диалоги Avalonia. Регистратор COM-коннектора не подключается —
        // на Linux COM отсутствует (чтение конфигурации выполняется без COM: 1Cv8.1CD / DESIGNER).
        services.AddSingleton<IDialogService, AvaloniaDialogService>();
        // Проверка обновлений из GitHub Releases + подсистема автообновления (Linux/Avalonia).
        services.AddSingleton<GitHubReleaseService>();
        services.AddSingleton<UpdateService>();
#endif

        Services = services.BuildServiceProvider();
    }

    public static T GetRequiredService<T>() where T : notnull =>
        Services.GetRequiredService<T>();

    /// <summary>
    /// Служба, если контейнер уже настроен и она зарегистрирована, иначе null.
    /// Для вспомогательных фоновых задач (мониторы), которые обязаны тихо
    /// пропускать работу до инициализации контейнера.
    /// </summary>
    public static T? TryGetService<T>() where T : notnull
    {
        try
        {
            return Services is null ? default : Services.GetService<T>();
        }
        catch
        {
            return default;
        }
    }
}
