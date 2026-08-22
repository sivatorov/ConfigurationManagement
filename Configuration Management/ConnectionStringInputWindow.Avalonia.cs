#if LINUX
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Services;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог ввода строки подключения к информационной базе 1С. Позволяет вставить
    /// строку из буфера обмена или ввести её вручную, после чего применить.
    /// Avalonia/Linux-версия WPF-окна <see cref="ConnectionStringInputWindow"/>.
    /// </summary>
    public class ConnectionStringInputWindow : ModalWindowBase
    {
        private readonly TextBox _inputBox;
        private readonly Button _okButton;
        private readonly IDialogService _dialogs;

        /// <summary>
        /// Создаёт диалог ввода строки подключения.
        /// </summary>
        /// <param name="initialValue">Текущее значение строки подключения базы (может быть пустым).</param>
        public ConnectionStringInputWindow(string? initialValue = null)
        {
            Title = LocalizationManager.T("ConnectionStringInput.Title");
            Width = 520;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            SystemDecorations = SystemDecorations.Full;

            _dialogs = AppServices.GetRequiredService<IDialogService>();

            _inputBox = new TextBox { Padding = new Thickness(8, 6), AcceptsReturn = true, MinHeight = 80 };
            _inputBox.TextChanged += (_, _) => UpdateOkEnabled();
            _inputBox.KeyDown += OnInputBox_KeyDown;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var title = new TextBlock
            {
                Text = LocalizationManager.T("ConnectionStringInput.Header"),
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var hint = new TextBlock
            {
                Text = LocalizationManager.T("ConnectionStringInput.HintText"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 1);
            grid.Children.Add(hint);

            var inputArea = new Grid();
            inputArea.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            inputArea.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(_inputBox, 0);
            _inputBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            inputArea.Children.Add(_inputBox);

            var pasteButton = new Button { Content = LocalizationManager.T("ConnectionStringInput.Paste"), MinWidth = 130, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            pasteButton.Click += (_, _) => OnPasteClipboard_Click();
            Grid.SetColumn(pasteButton, 1);
            inputArea.Children.Add(pasteButton);

            Grid.SetRow(inputArea, 2);
            grid.Children.Add(inputArea);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var cancel = new Button { Content = LocalizationManager.T("Common.Cancel"), MinWidth = 100, IsCancel = true };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
            _okButton = new Button { Content = LocalizationManager.T("Common.Apply"), MinWidth = 120, IsDefault = true };
            _okButton.Click += (_, _) => OnOk_Click();
            buttons.Children.Add(_okButton);
            Grid.SetRow(buttons, 3);
            grid.Children.Add(buttons);

            Content = grid;
            UpdateOkEnabled();

            Opened += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(initialValue))
                {
                    _inputBox.Text = initialValue!.Trim();
                }
                else
                {
                    TryPrefillFromClipboard();
                }
                _inputBox.Focus();
                _inputBox.SelectAll();
            };
        }

        /// <summary>
        /// Введённая строка подключения (null, если пользователь отменил ввод).
        /// </summary>
        public string? Result { get; private set; }

        /// <summary>Определяет, является ли текст ссылкой на информационную базу 1С.</summary>
        private static bool LooksLikeLink(string? text)
        {
            var value = (text ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.StartsWith("e1c:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return true;

            string[] keys = { "srvr=", "ref=", "file=", "ws=", "usr=", "pwd=" };
            foreach (var key in keys)
            {
                if (value.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (value.IndexOf('\\') >= 0 || value.IndexOf('/') >= 0)
                return true;

            return false;
        }

        /// <summary>Пытается подставить строку из буфера обмена, если она удовлетворяет критериям ссылки.</summary>
        private async void TryPrefillFromClipboard()
        {
            var clipboard = Clipboard;
            if (clipboard is null)
                return;

            string text;
            try
            {
                text = await clipboard.GetTextAsync() ?? string.Empty;
            }
            catch
            {
                return;
            }

            var trimmed = text.Trim();
            if (LooksLikeLink(trimmed))
                _inputBox.Text = trimmed;
        }

        /// <summary>Вставляет текст из буфера обмена в поле ввода (без проверки критериев).</summary>
        private async void OnPasteClipboard_Click()
        {
            var clipboard = Clipboard;
            if (clipboard is null)
            {
                _dialogs.ShowWarning(LocalizationManager.T("ConnectionStringInput.ClipboardAccessError"), LocalizationManager.T("ConnectionStringInput.PasteTitle"));
                return;
            }

            string text;
            try
            {
                text = await clipboard.GetTextAsync() ?? string.Empty;
            }
            catch
            {
                _dialogs.ShowWarning(LocalizationManager.T("ConnectionStringInput.ClipboardReadError"), LocalizationManager.T("ConnectionStringInput.PasteTitle"));
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _dialogs.ShowInfo(LocalizationManager.T("ConnectionStringInput.ClipboardEmpty"), LocalizationManager.T("ConnectionStringInput.PasteTitle"));
                return;
            }

            _inputBox.Text = text.Trim();
            _inputBox.Focus();
            _inputBox.CaretIndex = _inputBox.Text.Length;
        }

        private void UpdateOkEnabled()
        {
            _okButton.IsEnabled = !string.IsNullOrWhiteSpace(_inputBox.Text);
        }

        private void OnOk_Click()
        {
            Result = _inputBox.Text?.Trim();
            DialogResult = true;
            Close();
        }

        private void OnInputBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _okButton.IsEnabled)
            {
                OnOk_Click();
                e.Handled = true;
            }
        }
    }
}
#endif