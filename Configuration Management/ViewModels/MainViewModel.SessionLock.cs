#if WINDOWS
using System.Windows.Input;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Команды функций №19 (временная блокировка приложения паролем) и №20
/// (блокировка сеансов файловой ИБ без открытия «1С:Предприятия», CTRL+ALT+L)
/// StartManager для Windows/WPF. Частичный класс <see cref="MainViewModel"/>.
/// </summary>
public partial class MainViewModel
{
    private ICommand? _showSessionLockCommand;
    private ICommand? _lockAppCommand;
    private string _hotkeySessionLock = "Ctrl+Alt+L";
    private string _hotkeyLockApp = "";
    // Пароль временной блокировки приложения (PBKDF2-хэш), функция №19.
    private string _appLockPasswordHash = "";

    /// <summary>Горячая клавиша «Блокировка сеансов ИБ» (по умолчанию Ctrl+Alt+L).</summary>
    public string HotkeySessionLock
    {
        get => _hotkeySessionLock;
        set
        {
            if (SetProperty(ref _hotkeySessionLock, NormalizeHotkey(value, "Ctrl+Alt+L")))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Горячая клавиша «Временная блокировка приложения» (по умолчанию не задана).</summary>
    public string HotkeyLockApp
    {
        get => _hotkeyLockApp;
        set
        {
            if (SetProperty(ref _hotkeyLockApp, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Команда открытия окна «Блокировка сеансов информационной базы» (Ctrl+Alt+L).</summary>
    public ICommand ShowSessionLockCommand =>
        _showSessionLockCommand ??= new RelayCommand(
            ExecuteShowSessionLock,
            () => SelectedInfobase is not null);

    /// <summary>Команда временной блокировки приложения паролем.</summary>
    public ICommand LockAppCommand =>
        _lockAppCommand ??= new RelayCommand(ExecuteLockApp);

    private void ExecuteShowSessionLock()
    {
        var infobase = SelectedInfobase;
        if (infobase is null)
            return;
        var win = new Configuration_Management.SessionLockWindow(infobase);
        win.ShowDialog();
    }

    private void ExecuteLockApp()
    {
        // Если пароль ещё не задан — сначала предложить его установить.
        // При отмене установки блокировка не запускается (issue #294).
        if (!HasAppLockPassword)
        {
            var setupWin = new Configuration_Management.AppLockWindow(this, setupMode: true);
            // Окно блокировки открывается модально относительно главного окна (issue #294):
            // без владельца повторный запуск приложения ставил главное окно поверх диалога,
            // и блокировка визуально «не срабатывала».
            setupWin.Owner = System.Windows.Application.Current.MainWindow;
            setupWin.ShowDialog();
            if (!HasAppLockPassword)
                return;
        }

        // Цикл разблокировки: интерфейс остаётся недоступным, пока не введён верный
        // пароль. Кнопка «Отмена», крестик и Esc в режиме разблокировки недоступны
        // (AppLockWindow), поэтому окно закрывается только по верному паролю (issue #294).
        while (true)
        {
            var unlockWin = new Configuration_Management.AppLockWindow(this, setupMode: false);
            unlockWin.Owner = System.Windows.Application.Current.MainWindow;
            unlockWin.ShowDialog();
            if (unlockWin.Unlocked)
                return;
        }
    }

    /// <summary>Пароль блокировки приложения (PBKDF2-хэш) из настроек.</summary>
    public bool HasAppLockPassword => !string.IsNullOrEmpty(_appLockPasswordHash);

    /// <summary>Задаёт/меняет пароль блокировки приложения.</summary>
    public void SetAppLockPassword(string? password)
    {
        _appLockPasswordHash = PasswordHasher.Hash(password ?? string.Empty);
        ScheduleSaveSettings();
    }

    /// <summary>Проверяет пароль для снятия блокировки приложения.</summary>
    public bool VerifyAppLockPassword(string password) =>
        PasswordHasher.Verify(password, _appLockPasswordHash);
}
#endif