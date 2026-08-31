using Configuration_Management.Services;
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
#endif

        Services = services.BuildServiceProvider();
    }

    public static T GetRequiredService<T>() where T : notnull =>
        Services.GetRequiredService<T>();
}
