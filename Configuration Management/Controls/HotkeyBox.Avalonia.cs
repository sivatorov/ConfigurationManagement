#if LINUX
using System.Collections.Generic;
using Avalonia;
using System;
using Avalonia.Controls;
using Avalonia.Input;
using Configuration_Management.Localization;

namespace Configuration_Management.Controls
{
    /// <summary>
    /// Avalonia-версия текстового поля ввода горячей клавиши в стиле конфигуратора 1С:
    /// достаточно установить фокус и нажать нужную комбинацию (Ctrl+Shift+F и т.п.).
    /// Backspace/Delete — сбросить, Esc — отменить ввод. Свойство <see cref="Value"/> —
    /// каноническое представление жеста (например «Ctrl+Shift+F») или пустая строка.
    /// </summary>
    public class HotkeyBox : TextBox
    {
        /// <summary>
        /// Тема ищется по ключу стиля, а для наследника её в Fluent нет:
        /// без этого поле не отрисуется, как это было у дерева и PasswordBox.
        /// </summary>
        protected override Type StyleKeyOverride => typeof(TextBox);

        /// <summary>Каноническое представление горячей клавиши (например «Ctrl+Shift+F») или пустая строка.</summary>
        public static readonly StyledProperty<string> ValueProperty =
            AvaloniaProperty.Register<HotkeyBox, string>(nameof(Value), string.Empty);

        public string Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public HotkeyBox()
        {
            IsReadOnly = true;
            Text = FormatValue(Value);
            ToolTip.SetTip(this, LocalizationManager.T("Hotkey.Tooltip"));
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ValueProperty)
                Text = FormatValue((string?)change.NewValue);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            var key = e.Key;

            // Только модификатор — показываем «промежуточное» состояние, не фиксируем.
            if (IsModifierKey(key))
            {
                e.Handled = true;
                Text = BuildPendingText(key);
                return;
            }

            if (key == Key.Escape)
            {
                e.Handled = true;
                Text = FormatValue(Value); // отмена ввода
                return;
            }

            if (key == Key.Back || key == Key.Delete)
            {
                e.Handled = true;
                Value = string.Empty; // сброс назначения
                Text = LocalizationManager.T("Common.None");
                return;
            }

            // Клавиши навигации/ввода не должны записываться как горячая клавиша.
            if (key == Key.Tab || key == Key.Enter || IsNavigationKey(key))
                return;

            // Зафиксирована полноценная комбинация.
            e.Handled = true;
            Value = FormatCombo(e.KeyModifiers, key);
            Text = FormatValue(Value);
        }

        private static bool IsModifierKey(Key key) =>
            key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin;

        private static bool IsNavigationKey(Key key) =>
            key is Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Home or Key.End or Key.PageUp or Key.PageDown
                or Key.CapsLock or Key.NumLock or Key.Scroll;

        private static string BuildPendingText(Key key) =>
            key switch
            {
                Key.LeftCtrl or Key.RightCtrl => "Ctrl+…",
                Key.LeftShift or Key.RightShift => "Shift+…",
                Key.LeftAlt or Key.RightAlt => "Alt+…",
                Key.LWin or Key.RWin => "Win+…",
                _ => "…"
            };

        private static string FormatCombo(KeyModifiers mods, Key key)
        {
            var parts = new List<string>();
            if ((mods & KeyModifiers.Control) != 0) parts.Add("Ctrl");
            if ((mods & KeyModifiers.Shift) != 0) parts.Add("Shift");
            if ((mods & KeyModifiers.Alt) != 0) parts.Add("Alt");
            if ((mods & KeyModifiers.Meta) != 0) parts.Add("Win");
            parts.Add(KeyToDisplay(key));
            return string.Join("+", parts);
        }

        private static string KeyToDisplay(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
                return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return "NumPad" + (key - Key.NumPad0);
            if (key >= Key.F1 && key <= Key.F12)
                return "F" + (key - Key.F1 + 1);

            return key switch
            {
                Key.OemComma => ",",
                Key.OemPeriod => ".",
                Key.OemQuestion => "?",
                Key.OemPlus => "+",
                Key.OemMinus => "-",
                Key.OemOpenBrackets => "[",
                Key.OemCloseBrackets => "]",
                Key.OemQuotes => "\"",
                Key.OemSemicolon => ";",
                Key.OemBackslash => "\\",
                Key.OemPipe => "|",
                Key.OemTilde => "~",
                Key.Add => "NumPad+",
                Key.Subtract => "NumPad-",
                Key.Multiply => "NumPad*",
                Key.Divide => "NumPad/",
                Key.Decimal => "NumPad.",
                Key.Space => "Space",
                _ => key.ToString()
            };
        }

        private static string FormatValue(string? value)
            => string.IsNullOrWhiteSpace(value) ? LocalizationManager.T("Common.None") : value.Trim();
    }
}
#endif