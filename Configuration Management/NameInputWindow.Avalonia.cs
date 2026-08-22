#if LINUX
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог ввода произвольного названия (например, названия темы оформления).
    /// Avalonia/Linux-версия WPF-окна <see cref="NameInputWindow"/>.
    /// </summary>
    public class NameInputWindow : ModalWindowBase
    {
        private readonly TextBox _nameBox;

        /// <summary>
        /// Создаёт диалог ввода названия.
        /// </summary>
        /// <param name="title">Заголовок окна.</param>
        /// <param name="label">Подпись над полем ввода.</param>
        /// <param name="okText">Текст на кнопке подтверждения.</param>
        /// <param name="initialText">Начальное значение поля ввода.</param>
        public NameInputWindow(string title, string label, string okText, string initialText = "")
        {
            Title = title;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            SystemDecorations = SystemDecorations.Full;

            _nameBox = new TextBox
            {
                Text = initialText,
                Padding = new Thickness(8, 6)
            };
            _nameBox.KeyDown += OnNameBox_KeyDown;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var prompt = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(prompt, 0);

            Grid.SetRow(_nameBox, 1);

            var buttons = BuildButtons(okText, 130, OnOk_Click);
            Grid.SetRow(buttons, 2);

            grid.Children.Add(prompt);
            grid.Children.Add(_nameBox);
            grid.Children.Add(buttons);

            Content = grid;

            Opened += (_, _) =>
            {
                _nameBox.Focus();
                _nameBox.SelectAll();
            };
        }

        /// <summary>
        /// Введённое название (null, если пользователь отменил ввод).
        /// </summary>
        public string? Result { get; private set; }

        private void OnOk_Click()
        {
            Result = _nameBox.Text?.Trim();
        }

        private void OnNameBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnOk_Click();
                DialogResult = true;
                Close();
                e.Handled = true;
            }
        }
    }
}
#endif