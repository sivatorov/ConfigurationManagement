#if LINUX
using System.Windows.Input;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Отвечает за запуск информационных баз в различных режимах (Avalonia/Linux).
/// </summary>
public sealed class LaunchViewModel : ViewModelBase
{
    private readonly Func<Infobase?> _getSelected;
    private readonly IOneCLauncher _launcher;
    private readonly IAppLogger _logger;
    private readonly Action _onLaunched;

    public LaunchViewModel(
        Func<Infobase?> getSelected,
        IOneCLauncher launcher,
        IAppLogger logger,
        Action onLaunched)
    {
        _getSelected = getSelected;
        _launcher = launcher;
        _logger = logger;
        _onLaunched = onLaunched;

        LaunchCommand = new RelayCommand(Launch, _ => _getSelected() is not null);
    }

    public ICommand LaunchCommand { get; }

    /// <summary>
    /// Переопределения запуска Предприятия из блока «Текущая сессия».
    /// Возвращает null, когда переопределять нечего, и тогда запуск идёт
    /// по настройкам самой базы.
    /// </summary>
    public Func<Infobase, LaunchOverrides?>? EnterpriseOverrides { get; set; }

    /// <summary>Запуск Предприятия с учётом переопределений текущей сессии.</summary>
    private bool LaunchEnterprise(Infobase infobase)
    {
        var overrides = EnterpriseOverrides?.Invoke(infobase);
        return overrides is null
            ? _launcher.Launch(infobase, OneCLaunchMode.Enterprise)
            : _launcher.Launch(infobase, OneCLaunchMode.Enterprise,
                overrides.Client, overrides.RunMode, overrides.Architecture);
    }

    public void Launch(object? parameter)
    {
        var selected = _getSelected();
        if (selected is null)
            return;

        var kind = parameter switch
        {
            LaunchKind k => k,
            string s when Enum.TryParse<LaunchKind>(s, true, out var parsed) => parsed,
            _ => LaunchKind.Enterprise
        };

        bool ok = kind switch
        {
            LaunchKind.Configurator =>
                _launcher.Launch(selected, OneCLaunchMode.Configurator),
            LaunchKind.Thin32 =>
                _launcher.Launch(selected, OneCLaunchMode.Enterprise, OneCClientType.Thin, OneCArchitecture.x86),
            LaunchKind.Thick32 =>
                _launcher.Launch(selected, OneCLaunchMode.Enterprise, OneCClientType.Thick, OneCArchitecture.x86),
            LaunchKind.Thin64 =>
                _launcher.Launch(selected, OneCLaunchMode.Enterprise, OneCClientType.Thin, OneCArchitecture.x64),
            LaunchKind.Thick64 =>
                _launcher.Launch(selected, OneCLaunchMode.Enterprise, OneCClientType.Thick, OneCArchitecture.x64),
            _ => LaunchEnterprise(selected)
        };

        if (ok)
        {
            _logger.Info($"Запущена база «{selected.Name}» ({kind})");
            _onLaunched();
        }
        else
        {
            _logger.Warn($"Не удалось запустить базу «{selected.Name}» ({kind})");
        }
    }
}

/// <summary>
/// Переопределения очередного запуска Предприятия: тип клиента, режим форм
/// и разрядность. Пустое значение поля означает «как у базы».
/// </summary>
public sealed record LaunchOverrides(OneCClientType? Client, OneCRunMode? RunMode, OneCArchitecture Architecture);
#endif
