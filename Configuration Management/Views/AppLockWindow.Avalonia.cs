#if LINUX
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management;

/// <summary>
/// Окно временной блокировки приложения паролем (Avalonia/Linux), функция №19 StartManager.
/// Режимы: «установить пароль» (два поля) и «разблокировать» (одно поле).
/// Пароль сохраняется в виде PBKDF2-хэша в настройках.
/// </summary>
public sealed class AppLockWindow : ModalWindowBase
{
    private readonly MainViewModel _vm;
    private readonly bool _setupMode;
    // Поля стилизуются как остальные поля ввода приложения (ModernPasswordBox):
    // карточный фон темы, скругление 8 и акцентный контур при фокусе (issue #294).
    private readonly Controls.PasswordBox _pwd1 = new Controls.PasswordBox().Styled(ControlThemes.ModernPasswordBox);
    private readonly Controls.PasswordBox _pwd2 = new Controls.PasswordBox().Styled(ControlThemes.ModernPasswordBox);
    private readonly TextBlock _pwd1Label = new();
    private readonly TextBlock _pwd2Label = new();

    /// <summary>true, если блокировка снята (введён верный пароль).</summary>
    public bool Unlocked { get; private set; }

    public AppLockWindow(MainViewModel vm, bool setupMode = false)
    {
        _vm = vm;
        _setupMode = setupMode;

        Width = 460;
        Height = 340;
        MinWidth = 440;
        MinHeight = 320;
        FontSize = 13;
        Content = BuildRoot();

        if (setupMode)
        {
            Title = T("AppLock.SetupTitle");
            TitleLabel.Text = T("AppLock.SetupTitle");
            PromptLabel.Text = T("AppLock.SetupPrompt");
            OkText.Text = T("Common.Save");
            CancelText.Text = T("Common.Cancel");
        }
        else
        {
            Title = T("AppLock.LockTitle");
            TitleLabel.Text = T("AppLock.LockTitle");
            PromptLabel.Text = T("AppLock.UnlockPrompt");
            OkText.Text = T("AppLock.Unlock");
            // Режим разблокировки: одно видимое поле с подписью «Пароль».
            _pwd1Label.Text = T("AppLock.Password");
            _pwd1Label.IsVisible = true;
            _pwd1.IsVisible = true;
            _pwd2Label.IsVisible = false;
            _pwd2.IsVisible = false;
            // «Отмена» недоступна: снять блокировку можно только верным паролем (issue #294).
            _cancelButton.IsVisible = false;
            UnlockField = _pwd1;
        }

        // В режиме разблокировки окно нельзя закрыть ни крестиком, ни системным меню:
        // пока блокировка активна, интерфейс недоступен (issue #294).
        Closing += (_, e) =>
        {
            if (!_setupMode && !Unlocked)
                e.Cancel = true;
        };

        // Фокус на первое (видимое) поле пароля при открытии окна (issue #294):
        // в режиме установки — «Новый пароль», при разблокировке — единственное поле.
        Opened += (_, _) => _pwd1.Focus();
    }

    private static string T(string key) => LocalizationManager.T(key);

    private TextBlock TitleLabel { get; } = new()
    {
        FontSize = 15,
        FontWeight = Avalonia.Media.FontWeight.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 6),
        TextWrapping = Avalonia.Media.TextWrapping.Wrap
    };

    private TextBlock PromptLabel { get; } = new()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 12)
    };

    private TextBlock OkText { get; } = new() { Text = T("Common.Ok") };
    private TextBlock CancelText { get; } = new() { Text = T("Common.Cancel") };

    private Controls.PasswordBox? UnlockField;

    // Кнопка «Отмена» — поле класса, чтобы скрывать её в режиме разблокировки (issue #294).
    private readonly Button _cancelButton = new();

    private Control BuildRoot()
    {
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(TitleLabel);
        stack.Children.Add(PromptLabel);

        _pwd1Label.Text = T("AppLock.NewPassword");
        _pwd1Label.Margin = new Thickness(0, 0, 0, 4);
        stack.Children.Add(_pwd1Label);
        _pwd1.Margin = new Thickness(0, 0, 0, 8);
        _pwd1.KeyDown += OnPassword_KeyDown;
        stack.Children.Add(_pwd1);

        _pwd2Label.Text = T("AppLock.ConfirmPassword");
        _pwd2Label.Margin = new Thickness(0, 0, 0, 4);
        stack.Children.Add(_pwd2Label);
        _pwd2.Margin = new Thickness(0, 0, 0, 4);
        _pwd2.KeyDown += OnPassword_KeyDown;
        stack.Children.Add(_pwd2);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _cancelButton.Content = CancelText;
        _cancelButton.Width = 110;
        _cancelButton.Margin = new Thickness(0, 0, 8, 0);
        _cancelButton.Click += OnCancel_Click;
        var ok = new Button { Content = OkText, Width = 160, MinHeight = 36 };
        ok.Click += OnOk_Click;
        buttons.Children.Add(_cancelButton);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        return stack;
    }

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        if (_setupMode)
        {
            var pwd = _pwd1.Password ?? string.Empty;
            var confirm = _pwd2.Password ?? string.Empty;
            if (string.IsNullOrEmpty(pwd))
            {
                _vm.ShowWarning(T("AppLock.PasswordEmpty"));
                return;
            }
            if (!string.Equals(pwd, confirm, StringComparison.Ordinal))
            {
                _vm.ShowWarning(T("AppLock.PasswordMismatch"));
                return;
            }
            _vm.SetAppLockPassword(pwd);
            Close();
            return;
        }

        var field = UnlockField ?? _pwd1;
        var entered = field.Password ?? string.Empty;
        if (_vm.VerifyAppLockPassword(entered))
        {
            Unlocked = true;
            Close();
        }
        else
        {
            _vm.ShowWarning(T("AppLock.WrongPassword"));
            field.Clear();
            field.Focus();
        }
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void OnPassword_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            OnOk_Click(sender, e);
            e.Handled = true;
        }
    }
}
#endif