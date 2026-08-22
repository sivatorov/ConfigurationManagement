#if LINUX
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог удаления ИБ: сведения о базе и опция физического удаления каталога (файловые базы).
    /// Avalonia/Linux-версия WPF-окна <see cref="DeleteInfobaseWindow"/>.
    /// </summary>
    public class DeleteInfobaseWindow : ModalWindowBase
    {
        private readonly Infobase _infobase;
        private readonly IDialogService _dialogs;
        private readonly CheckBox _physicalDeleteCheck = new() { Content = LocalizationManager.T("DeleteInfobase.PhysicalDelete") };
        private readonly StackPanel _physicalPanel = new() { Spacing = 6 };
        private readonly TextBlock _existsText = new();
        private readonly TextBlock _physicalHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        /// <summary>Пользователь подтвердил удаление.</summary>
        public bool Confirmed { get; private set; }

        /// <summary>Нужно физически удалить каталог файловой базы.</summary>
        public bool DeletePhysically { get; private set; }

        public DeleteInfobaseWindow(Infobase infobase)
        {
            Title = LocalizationManager.T("DeleteInfobase.Title");
            Width = 520;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            SystemDecorations = SystemDecorations.Full;

            _infobase = infobase;
            _dialogs = AppServices.GetRequiredService<IDialogService>();

            Content = BuildRoot();
        }

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var title = new TextBlock
            {
                Text = LocalizationManager.T("DeleteInfobase.Title"),
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var details = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
            details.Children.Add(DetailRow(LocalizationManager.T("DeleteInfobase.DetailName"), string.IsNullOrWhiteSpace(_infobase.Name) ? "—" : _infobase.Name));
            details.Children.Add(DetailRow(LocalizationManager.T("Main.Type"), _infobase.ConnectionTypeDisplay));
            details.Children.Add(DetailRow(LocalizationManager.T("Main.ServerPath"), string.IsNullOrWhiteSpace(_infobase.ServerDatabaseDisplay)
                ? (_infobase.ConnectionStringDisplay ?? "—")
                : _infobase.ServerDatabaseDisplay));
            details.Children.Add(DetailRow(LocalizationManager.T("Main.GroupLabel"), string.IsNullOrWhiteSpace(_infobase.Group) ? LocalizationManager.T("Connection.NoGroup") : _infobase.Group));
            details.Children.Add(DetailRow(LocalizationManager.T("Main.Platform"), string.IsNullOrWhiteSpace(_infobase.PlatformVersion) ? "—" : _infobase.PlatformVersion));
            Grid.SetRow(details, 1);
            grid.Children.Add(details);

            var isFile = _infobase.Connection.Type == ConnectionType.File;

            // Панель физического удаления
            _physicalPanel.Margin = new Thickness(0, 0, 0, 12);
            _physicalPanel.Children.Add(new TextBlock { Text = LocalizationManager.T("DeleteInfobase.PhysicalHeader"), FontWeight = FontWeight.SemiBold });
            var existsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            existsRow.Children.Add(new TextBlock { Text = LocalizationManager.T("DeleteInfobase.DirLabel") });
            existsRow.Children.Add(_existsText);
            _physicalPanel.Children.Add(existsRow);

            if (isFile)
            {
                var dir = InfobaseMaintenanceService.GetFileBaseDirectory(_infobase);
                var exists = InfobaseMaintenanceService.FileBaseExists(_infobase);
                if (exists && !string.IsNullOrEmpty(dir))
                {
                    _existsText.Text = string.Format(LocalizationManager.T("DeleteInfobase.ExistsYes"), dir);
                    _existsText.Foreground = new SolidColorBrush(Color.Parse("#2E8B57"));
                    _physicalDeleteCheck.IsEnabled = true;
                    _physicalHint.Text = string.Format(LocalizationManager.T("DeleteInfobase.PhysicalHintDynamic"), dir);
                }
                else
                {
                    _existsText.Text = string.IsNullOrEmpty(dir)
                        ? LocalizationManager.T("DeleteInfobase.DirNotSpecified")
                        : string.Format(LocalizationManager.T("DeleteInfobase.DirNotFound"), dir);
                    // Серый статус «каталог не найден» — вторичный текст из темы.
                    ThemeBrushes.Bind(_existsText, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
                    _physicalDeleteCheck.IsEnabled = false;
                    _physicalDeleteCheck.IsChecked = false;
                    _physicalHint.Text = LocalizationManager.T("DeleteInfobase.PhysicalUnavailable");
                }
                _physicalPanel.Children.Add(_physicalDeleteCheck);
                _physicalPanel.Children.Add(_physicalHint);
            }
            else
            {
                _existsText.Text = LocalizationManager.T("DeleteInfobase.NonFileOnlyFromList");
                ThemeBrushes.Bind(_existsText, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            }

            Grid.SetRow(_physicalPanel, 2);
            grid.Children.Add(_physicalPanel);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var cancel = new Button { Content = LocalizationManager.T("Common.Cancel"), MinWidth = 100, IsCancel = true };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
            var delete = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        IconHelper.MakeIcon("IconDelete", 16, "TextOnAccentColorBrush"),
                        new TextBlock { Text = LocalizationManager.T("DeleteInfobase.Delete"), VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                MinWidth = 120,
                IsDefault = true,
                Background = new SolidColorBrush(Color.Parse("#DC2626")),
                Foreground = Brushes.White
            };
            delete.Click += (_, _) => OnDelete_Click();
            buttons.Children.Add(delete);
            Grid.SetRow(buttons, 5);
            grid.Children.Add(buttons);

            return grid;
        }

        private static Grid DetailRow(string label, string value)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(140)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            var labelBlock = new TextBlock { Text = label, FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);

            var valueBlock = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(valueBlock, 1);
            grid.Children.Add(valueBlock);
            return grid;
        }

        private void OnDelete_Click()
        {
            DeletePhysically = _physicalDeleteCheck.IsChecked == true
                               && _physicalPanel.IsVisible
                               && _physicalDeleteCheck.IsEnabled;

            if (DeletePhysically)
            {
                var dir = InfobaseMaintenanceService.GetFileBaseDirectory(_infobase) ?? "";
                var confirm = _dialogs.Confirm(
                    string.Format(LocalizationManager.T("DeleteInfobase.PhysicalConfirm"), dir),
                    LocalizationManager.T("DeleteInfobase.PhysicalDeleteTitle"));
                if (!confirm)
                    return;
            }

            Confirmed = true;
            DialogResult = true;
            Close();
        }
    }
}
#endif