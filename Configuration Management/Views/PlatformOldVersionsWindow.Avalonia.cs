#if LINUX
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Themes;

namespace Configuration_Management
{
    /// <summary>Строка списка диалога удаления (issue #334): версия, признаки риска
    /// («новейшая», «используется базами», «используется запущенными процессами 1С»)
    /// и состояние флажка. По умолчанию отмечены только версии без признаков риска —
    /// решение, что считать старым, остаётся за пользователем.</summary>
    public sealed class PlatformOldVersionChoice
    {
        /// <param name="entry">Версия с признаками риска
        /// (<see cref="Services.OldVersionCleanupEntry"/>).</param>
        public PlatformOldVersionChoice(Services.OldVersionCleanupEntry entry)
        {
            Version = entry.Version;
            IsNewest = entry.IsNewest;
            IsUsedByBases = entry.IsUsedByBases;
            IsUsedByProcesses = entry.IsUsedByProcesses;
            IsChecked = entry.IsCheckedByDefault;

            var badges = new List<string>();
            if (IsNewest)
                badges.Add(LocalizationManager.T("PlatformUpdate.OldVersions.Badge.Newest"));
            if (IsUsedByBases)
                badges.Add(LocalizationManager.T("PlatformUpdate.OldVersions.Badge.UsedByBases"));
            if (IsUsedByProcesses)
                badges.Add(LocalizationManager.T("PlatformUpdate.OldVersions.Badge.UsedByProcesses"));
            Badges = badges.Count == 0 ? string.Empty : " — " + string.Join(", ", badges);
        }

        /// <summary>Информация об установленной версии (Display + путь каталога).</summary>
        public PlatformVersionInfo Version { get; }

        /// <summary>True — новейшая установленная версия.</summary>
        public bool IsNewest { get; }

        /// <summary>True — на версию ссылается хотя бы одна база.</summary>
        public bool IsUsedByBases { get; }

        /// <summary>True — из каталога версии запущен процесс 1С.</summary>
        public bool IsUsedByProcesses { get; }

        /// <summary>Отображаемый текст (номер версии с разрядностью).</summary>
        public string Display => Version.Display;

        /// <summary>Текстовые пометки риска (пусто — признаков нет).</summary>
        public string Badges { get; }

        /// <summary>True — версия отмечена к удалению.</summary>
        public bool IsChecked { get; set; }
    }

    /// <summary>
    /// Диалог выбора удаляемых старых версий платформы 1С (issue #334, доработка по
    /// комментарию автора; Linux/Avalonia): в списке ПОКАЗЫВАЮТСЯ ВСЕ установленные
    /// версии — включая новейшую и используемые базами/процессами (с текстовыми
    /// пометками); ничего не фильтруется, пользователь сам решает, что считать старым.
    /// По умолчанию отмечены только версии без признаков риска; попытка удалить
    /// спорную версию дополнительно подтверждается в
    /// <see cref="ViewModels.PlatformUpdateViewModel"/>. Кнопка удаления активна,
    /// пока отмечена хотя бы одна версия; <see cref="Result"/> возвращает выбранные
    /// версии, отмена — null. Avalonia/Linux-версия WPF-окна
    /// <see cref="PlatformOldVersionsWindow"/>.
    /// </summary>
    public sealed class PlatformOldVersionsWindow : ModalWindowBase
    {
        private readonly ObservableCollection<PlatformOldVersionChoice> _choices = new();

        // Присваивается в конструкторе до первого использования в обработчиках;
        // null! — чтобы nullable-анализ не ругался на чтение поля в замыкании.
        private readonly Button _deleteButton = null!;

        /// <param name="entries">ВСЕ установленные версии с признаками риска (см.
        /// <see cref="Services.OldVersionCleaner.SelectDeletionEntries"/>).</param>
        public PlatformOldVersionsWindow(IEnumerable<Services.OldVersionCleanupEntry> entries)
        {
            Title = LocalizationManager.T("PlatformUpdate.OldVersions.Title");
            Width = 520;
            Height = 420;
            MinWidth = 440;
            MinHeight = 300;
            FontSize = 13;
            CanResize = true;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("PlatformUpdate.OldVersions.Title"),
                FontSize = 14,
                FontWeight = FontWeight.SemiBold
            });
            var description = new TextBlock
            {
                Text = LocalizationManager.T("PlatformUpdate.OldVersions.Description"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            ThemeBrushes.Bind(description, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            header.Children.Add(description);

            var listBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4)
            };
            ThemeBrushes.Bind(listBorder, Border.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(listBorder, Border.BorderBrushProperty, "BorderColorBrush");

            var panel = new StackPanel { Spacing = 4 };
            foreach (var entry in entries ?? Enumerable.Empty<Services.OldVersionCleanupEntry>())
            {
                var choice = new PlatformOldVersionChoice(entry);
                _choices.Add(choice);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
                var checkBox = new CheckBox
                {
                    IsChecked = choice.IsChecked,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                checkBox.IsCheckedChanged += (_, _) =>
                {
                    choice.IsChecked = checkBox.IsChecked == true;
                    _deleteButton.IsEnabled = _choices.Any(c => c.IsChecked);
                };
                row.Children.Add(checkBox);

                row.Children.Add(new TextBlock
                {
                    Text = choice.Display,
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (!string.IsNullOrEmpty(choice.Badges))
                {
                    var badges = new TextBlock
                    {
                        Text = choice.Badges,
                        FontStyle = FontStyle.Italic,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0)
                    };
                    ThemeBrushes.Bind(badges, TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    row.Children.Add(badges);
                }

                panel.Children.Add(row);
            }

            listBorder.Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var cancel = new Button
            {
                Content = LocalizationManager.T("Common.Cancel"),
                IsCancel = true,
                Padding = new Thickness(14, 6)
            };
            cancel.Styled(ControlThemes.ModernButton);
            ThemeBrushes.Bind(cancel, Button.BackgroundProperty, "ItemHoverBrush");
            ThemeBrushes.Bind(cancel, Button.ForegroundProperty, "TextPrimaryBrush");
            cancel.Click += (_, _) => Close();

            _deleteButton = new Button
            {
                Content = LocalizationManager.T("PlatformUpdate.OldVersions.Delete"),
                Padding = new Thickness(14, 6),
                Margin = new Thickness(8, 0, 0, 0)
            };
            _deleteButton.Styled(ControlThemes.DialogConfirmButton);
            _deleteButton.IsEnabled = _choices.Any(c => c.IsChecked);
            _deleteButton.Click += (_, _) =>
            {
                if (_choices.Any(c => c.IsChecked))
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
            buttons.Children.Add(_deleteButton);

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

        /// <summary>Версии, отмеченные пользователем к удалению (не null при DialogResult=true).</summary>
        public IReadOnlyList<PlatformVersionInfo>? Result => _choices
            .Where(c => c.IsChecked)
            .Select(c => c.Version)
            .ToList();
    }
}
#endif
