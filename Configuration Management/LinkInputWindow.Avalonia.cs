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
    /// Диалог ввода ссылки на информационную базу (аналог «Перейти по ссылке»
    /// в стандартном загрузчике 1С). Avalonia/Linux-версия WPF-окна <see cref="LinkInputWindow"/>.
    /// </summary>
    public class LinkInputWindow : ModalWindowBase
    {
        private readonly TextBox _linkBox;
        private readonly Button _okButton;

        /// <summary>
        /// Создаёт диалог ввода ссылки на информационную базу.
        /// </summary>
        public LinkInputWindow()
        {
            Title = LocalizationManager.T("LinkInput.Title");
            Width = 460;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            SystemDecorations = SystemDecorations.Full;

            _linkBox = new TextBox { Padding = new Thickness(8, 6) };
            _linkBox.TextChanged += (_, _) => UpdateOkEnabled();
            _linkBox.KeyDown += OnLinkBox_KeyDown;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var prompt = new TextBlock
            {
                Text = LocalizationManager.T("LinkInput.Label"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(prompt, 0);

            Grid.SetRow(_linkBox, 1);

            // Кнопки строим вручную — нужен доступ к ОК для управления IsEnabled.
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var cancel = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        IconHelper.MakeIcon("IconClose", 14),
                        new TextBlock { Text = LocalizationManager.T("Common.Cancel"), VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                MinWidth = 110,
                IsCancel = true
            };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);

            _okButton = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        IconHelper.MakeIcon("IconOk", 14),
                        new TextBlock { Text = LocalizationManager.T("Common.Ok"), VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                MinWidth = 130,
                IsDefault = true
            };
            _okButton.Click += (_, _) => OnOk_Click();
            buttons.Children.Add(_okButton);

            Grid.SetRow(buttons, 2);

            grid.Children.Add(prompt);
            grid.Children.Add(_linkBox);
            grid.Children.Add(buttons);

            Content = grid;

            UpdateOkEnabled();
            Opened += (_, _) => _linkBox.Focus();
        }

        /// <summary>
        /// Введённая ссылка на информационную базу (null, если пользователь отменил ввод).
        /// </summary>
        public string? Result { get; private set; }

        private void UpdateOkEnabled()
        {
            _okButton.IsEnabled = !string.IsNullOrWhiteSpace(_linkBox.Text);
        }

        private void OnOk_Click()
        {
            Result = _linkBox.Text?.Trim();
            DialogResult = true;
            Close();
        }

        private void OnLinkBox_KeyDown(object? sender, KeyEventArgs e)
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