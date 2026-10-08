#if LINUX
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Services;
using Configuration_Management.Themes;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог выбора варианта дистрибутива платформы 1С (issue #334, Linux/Avalonia):
    /// после нажатия «Скачать и установить» показывает список файлов, доступных для
    /// текущей ОС (deb/rpm/tar.gz, x64/x86), с пометкой рекомендуемого. Результат —
    /// <see cref="PlatformDistributionOption"/> или null при отмене.
    /// </summary>
    public sealed class PlatformDistributionPickerWindow : ModalWindowBase
    {
        private readonly ListBox _optionsList;

        /// <param name="options">Варианты дистрибутива для текущей ОС.</param>
        public PlatformDistributionPickerWindow(IReadOnlyList<PlatformDistributionOption> options)
        {
            Title = LocalizationManager.T("PlatformUpdate.DistributionPicker.Title");
            Width = 640;
            Height = 420;
            MinWidth = 540;
            MinHeight = 320;
            FontSize = 13;
            CanResize = true;

            var title = new TextBlock
            {
                Text = LocalizationManager.T("PlatformUpdate.DistributionPicker.Title"),
                FontSize = 14,
                FontWeight = FontWeight.SemiBold
            };
            var description = new TextBlock
            {
                Text = LocalizationManager.T("PlatformUpdate.DistributionPicker.Description"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            ThemeBrushes.Bind(description, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var header = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(title);
            header.Children.Add(description);

            _optionsList = new ListBox
            {
                ItemsSource = options
            };
            _optionsList.ItemTemplate = new FuncDataTemplate<PlatformDistributionOption>((option, _) =>
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                panel.Children.Add(new TextBlock { Text = option.DisplayName, VerticalAlignment = VerticalAlignment.Center });
                if (option.IsRecommended)
                {
                    var rec = new TextBlock
                    {
                        Text = LocalizationManager.T("PlatformUpdate.DistributionPicker.Recommended"),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    rec.Foreground = new SolidColorBrush(Color.Parse("#16A34A"));
                    panel.Children.Add(rec);
                }
                return panel;
            });
            _optionsList.DoubleTapped += OnOptionsList_DoubleTapped;
            if (options is not null)
            {
                for (var i = 0; i < options.Count; i++)
                {
                    if (options[i].IsRecommended)
                    {
                        _optionsList.SelectedIndex = i;
                        break;
                    }
                }
            }

            var listBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Child = _optionsList
            };
            ThemeBrushes.Bind(listBorder, Border.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(listBorder, Border.BorderBrushProperty, "BorderColorBrush");

            var cancel = new Button
            {
                Content = IconHelper.IconAndText("IconClose", LocalizationManager.T("Common.Close"), 14, "TextPrimaryBrush"),
                IsCancel = true,
                Padding = new Thickness(14, 6)
            };
            cancel.Styled(ControlThemes.ModernButton);
            ThemeBrushes.Bind(cancel, Button.BackgroundProperty, "ItemHoverBrush");
            ThemeBrushes.Bind(cancel, Button.ForegroundProperty, "TextPrimaryBrush");
            cancel.Click += (_, _) => Close();

            var choose = new Button
            {
                Content = LocalizationManager.T("PlatformUpdate.DistributionPicker.Choose"),
                Padding = new Thickness(14, 6),
                Margin = new Thickness(8, 0, 0, 0)
            };
            choose.Styled(ControlThemes.DialogConfirmButton);
            choose.Click += (_, _) =>
            {
                if (_optionsList.SelectedItem is not null)
                {
                    DialogResult = true;
                    Close();
                }
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(choose);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(header, 0);
            Grid.SetRow(listBorder, 1);
            Grid.SetRow(buttons, 2);
            root.Children.Add(header);
            root.Children.Add(listBorder);
            root.Children.Add(buttons);

            Content = root;
        }

        /// <summary>Выбранный пользователем вариант или null при отмене.</summary>
        public PlatformDistributionOption? Result => _optionsList.SelectedItem as PlatformDistributionOption;

        private void OnOptionsList_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (_optionsList.SelectedItem is not null)
            {
                DialogResult = true;
                Close();
            }
        }
    }
}
#endif