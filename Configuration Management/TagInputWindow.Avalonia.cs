#if LINUX
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог ввода названия тега. Avalonia/Linux-версия WPF-окна <see cref="TagInputWindow"/>.
    /// </summary>
    public class TagInputWindow : ModalWindowBase
    {
        private readonly TextBox _tagBox;

        /// <summary>
        /// Создаёт диалог ввода тега.
        /// </summary>
        public TagInputWindow()
        {
            Title = LocalizationManager.T("TagInput.Title");
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            SystemDecorations = SystemDecorations.Full;

            _tagBox = new TextBox { Padding = new Thickness(8, 6) };
            _tagBox.KeyDown += OnTagBox_KeyDown;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var prompt = new TextBlock { Text = LocalizationManager.T("TagInput.Prompt"), Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(prompt, 0);

            Grid.SetRow(_tagBox, 1);

            var buttons = BuildButtons(null, 130, OnOk_Click);
            Grid.SetRow(buttons, 2);

            grid.Children.Add(prompt);
            grid.Children.Add(_tagBox);
            grid.Children.Add(buttons);

            Content = grid;

            Opened += (_, _) =>
            {
                _tagBox.Focus();
                _tagBox.SelectAll();
            };
        }

        /// <summary>
        /// Введённое название тега (null, если пользователь отменил ввод).
        /// </summary>
        public string? Result { get; private set; }

        private void OnOk_Click()
        {
            Result = _tagBox.Text?.Trim();
        }

        private void OnTagBox_KeyDown(object? sender, KeyEventArgs e)
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