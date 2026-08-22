#if LINUX
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Configuration_Management.Controls
{
    /// <summary>
    /// Avalonia-замена WPF-контрола PasswordBox: поле ввода со скрытыми символами,
    /// свойство <see cref="Password"/> и событие <see cref="PasswordChanged"/>.
    /// В Avalonia 11 отдельного PasswordBox нет, роль играет TextBox с PasswordChar.
    /// </summary>
    public class PasswordBox : TextBox
    {
        /// <summary>
        /// Тема оформления ищется по ключу стиля, а для наследника её в Fluent нет:
        /// без этого поле ввода остаётся без шаблона и не отрисовывается.
        /// </summary>
        protected override Type StyleKeyOverride => typeof(TextBox);

        public PasswordBox()
        {
            PasswordChar = '•';
        }

        /// <summary>Введённый пароль. Пустая строка вместо null, как в WPF.</summary>
        public string Password
        {
            get => Text ?? string.Empty;
            set
            {
                if ((Text ?? string.Empty) != (value ?? string.Empty))
                    Text = value;
            }
        }

        /// <summary>Возникает при изменении содержимого поля.</summary>
        public event EventHandler<RoutedEventArgs>? PasswordChanged;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == TextProperty)
                PasswordChanged?.Invoke(this, new RoutedEventArgs());
        }
    }
}
#endif
