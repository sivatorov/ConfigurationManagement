#if WINDOWS
using System;
using System.Windows;
using System.Windows.Input;
using Configuration_Management.Localization;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Окно временной блокировки приложения паролем (функция №19 StartManager).
    /// Работает в двух режимах:
    ///  — «установить пароль» (когда пароль ещё не задан): два поля ввода пароля;
    ///  — «разблокировать» (когда пароль уже задан): одно поле для снятия блокировки.
    /// Пароль сохраняется в виде PBKDF2-хэша в настройках.
    /// </summary>
    public partial class AppLockWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly bool _setupMode;

        /// <summary>true, если блокировка снята (введён верный пароль).</summary>
        public bool Unlocked { get; private set; }

        public AppLockWindow(MainViewModel vm, bool setupMode = false)
        {
            InitializeComponent();
            _vm = vm;
            _setupMode = setupMode;

            if (setupMode)
            {
                Title = LocalizationManager.T("AppLock.SetupTitle");
                TitleLabel.Text = LocalizationManager.T("AppLock.SetupTitle");
                PromptLabel.Text = LocalizationManager.T("AppLock.SetupPrompt");
                OkTextBlock.Text = LocalizationManager.T("Common.Save");
                CancelTextBlock.Text = LocalizationManager.T("Common.Cancel");
                NewPasswordLabel.Text = LocalizationManager.T("AppLock.NewPassword");
                ConfirmLabel.Text = LocalizationManager.T("AppLock.ConfirmPassword");
                PasswordBox2.Visibility = Visibility.Visible;
                ConfirmLabel.Visibility = Visibility.Visible;
            }
            else
            {
                Title = LocalizationManager.T("AppLock.LockTitle");
                TitleLabel.Text = LocalizationManager.T("AppLock.LockTitle");
                PromptLabel.Text = LocalizationManager.T("AppLock.UnlockPrompt");
                OkTextBlock.Text = LocalizationManager.T("AppLock.Unlock");
                // Режим разблокировки: одно видимое поле с подписью «Пароль».
                NewPasswordLabel.Text = LocalizationManager.T("AppLock.Password");
                NewPasswordLabel.Visibility = Visibility.Visible;
                PasswordBox1.Visibility = Visibility.Visible;
                ConfirmLabel.Visibility = Visibility.Collapsed;
                PasswordBox2.Visibility = Visibility.Collapsed;
                // В режиме разблокировки «Отмена» недоступна: снять блокировку
                // можно только верным паролем (issue #294).
                CancelButton.Visibility = Visibility.Collapsed;
            }

            // В режиме разблокировки окно нельзя закрыть ни крестиком, ни Alt+F4,
            // ни системным меню: пока блокировка активна, интерфейс недоступен (issue #294).
            Closing += (_, e) =>
            {
                if (!_setupMode && !Unlocked)
                    e.Cancel = true;
            };

            Loaded += (_, _) =>
            {
                // Фокус на первое (видимое) поле пароля: при установке — «Новый пароль»,
                // при разблокировке — единственное поле ввода.
                PasswordBox1.Focus();
            };
        }

        private void OnOk_Click(object sender, RoutedEventArgs e)
        {
            if (_setupMode)
            {
                var pwd = PasswordBox1.Password ?? string.Empty;
                var confirm = PasswordBox2.Password ?? string.Empty;
                if (string.IsNullOrEmpty(pwd))
                {
                    MessageBox.Show(LocalizationManager.T("AppLock.PasswordEmpty"),
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!string.Equals(pwd, confirm, StringComparison.Ordinal))
                {
                    MessageBox.Show(LocalizationManager.T("AppLock.PasswordMismatch"),
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _vm.SetAppLockPassword(pwd);
                DialogResult = true;
                return;
            }

            var entered = PasswordBox1.Password ?? string.Empty;
            if (_vm.VerifyAppLockPassword(entered))
            {
                Unlocked = true;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show(LocalizationManager.T("AppLock.WrongPassword"),
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
                PasswordBox1.Clear();
                PasswordBox1.Focus();
            }
        }

        private void OnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private void OnPassword_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnOk_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}
#endif