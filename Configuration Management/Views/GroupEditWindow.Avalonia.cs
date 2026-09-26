#if LINUX
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Themes;
using Configuration_Management.Models;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог создания/редактирования группы. Поддерживает выбор родительской группы,
    /// иконки и цвета. Avalonia/Linux-версия WPF-окна <see cref="GroupEditWindow"/>.
    /// </summary>
    public class GroupEditWindow : ModalWindowBase
    {
        private readonly IReadOnlyList<Group> _groups;
        private readonly Group? _editingGroup;
        private readonly bool _noGroupMode;
        private string _color = "#2D6CDF";
        private string _iconColor = "#FFFFFF";
        private string _icon = string.Empty;
        private string _parentId = string.Empty;

        /// <summary>Вкладка «Основные» (с именем). null для служебных узлов «Без группы»/«Закреплённые».</summary>
        private TabItem? _mainTabItem;

        private readonly TextBox _nameBox =
            new TextBox { Padding = new Thickness(4, 3) }.Styled(ControlThemes.ModernTextBox);

        private readonly TextBox _descriptionBox =
            new TextBox { Padding = new Thickness(4, 3) }.Styled(ControlThemes.ModernTextBox);
        private readonly TextBox _parentPathBox =
            new TextBox { Padding = new Thickness(4, 3), IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center }
                .Styled(ControlThemes.ModernTextBox);
        private readonly ColorPickerControl _colorControl = new();
        private readonly ColorPickerControl _iconColorControl = new();
        private readonly WrapPanel _iconPickerPanel = new();

        private static readonly (string Key, string Label)[] AvailableIcons =
        {
            ("", LocalizationManager.T("GroupEdit.Icon.Default")), ("IconFolder", LocalizationManager.T("GroupEdit.Icon.Folder")), ("IconDatabase", LocalizationManager.T("GroupEdit.Icon.Database")),
            ("IconServices", LocalizationManager.T("GroupEdit.Icon.Services")), ("IconStar", LocalizationManager.T("GroupEdit.Icon.Star")), ("IconTag", LocalizationManager.T("GroupEdit.Icon.Tag")),
            ("IconPin", LocalizationManager.T("GroupEdit.Icon.Pin")), ("IconInfo", LocalizationManager.T("GroupEdit.Icon.Info")), ("IconPlay", LocalizationManager.T("GroupEdit.Icon.Play")),
            ("IconSettings", LocalizationManager.T("GroupEdit.Icon.Settings")), ("IconSearch", LocalizationManager.T("GroupEdit.Icon.Search")), ("IconAdd", LocalizationManager.T("GroupEdit.Icon.Add")),
            ("IconUsers", LocalizationManager.T("GroupEdit.Icon.Users")), ("IconHistory", LocalizationManager.T("GroupEdit.Icon.History")), ("IconSync", LocalizationManager.T("GroupEdit.Icon.Sync")),
            ("IconBackup", LocalizationManager.T("GroupEdit.Icon.Backup")), ("IconConfiguration", LocalizationManager.T("GroupEdit.Icon.Configuration")), ("IconPublish", LocalizationManager.T("GroupEdit.Icon.Publish")),
            ("IconMonitoring", LocalizationManager.T("GroupEdit.Icon.Monitoring")), ("IconScheduler", LocalizationManager.T("GroupEdit.Icon.Scheduler")), ("IconLogs", LocalizationManager.T("GroupEdit.Icon.Logs")),
            ("IconRights", LocalizationManager.T("GroupEdit.Icon.Rights")), ("IconExtension", LocalizationManager.T("GroupEdit.Icon.Extension")), ("IconImport", LocalizationManager.T("GroupEdit.Icon.Import")),
            ("IconExport", LocalizationManager.T("GroupEdit.Icon.Export")), ("IconFilter", LocalizationManager.T("GroupEdit.Icon.Filter")), ("IconCopy", LocalizationManager.T("GroupEdit.Icon.Copy")),
            ("IconEdit", LocalizationManager.T("GroupEdit.Icon.Edit")), ("IconSave", LocalizationManager.T("GroupEdit.Icon.Save")), ("IconRefresh", LocalizationManager.T("GroupEdit.Icon.Refresh")),
            ("IconOpen", LocalizationManager.T("GroupEdit.Icon.Open")), ("IconWarning", LocalizationManager.T("GroupEdit.Icon.Warning")), ("IconOk", LocalizationManager.T("GroupEdit.Icon.Ok")),
            ("IconError", LocalizationManager.T("GroupEdit.Icon.Error")), ("IconAutostart", LocalizationManager.T("GroupEdit.Icon.Autostart")), ("IconTheme", LocalizationManager.T("GroupEdit.Icon.Theme")),
            ("IconSun", LocalizationManager.T("GroupEdit.Icon.Sun")), ("IconMoon", LocalizationManager.T("GroupEdit.Icon.Moon")), ("IconCompare", LocalizationManager.T("GroupEdit.Icon.Compare")),
            ("IconMerge", LocalizationManager.T("GroupEdit.Icon.Merge"))
        };


        public GroupEditWindow(IEnumerable<Group> groups, Group? parent = null)
            : this(groups, parent?.Id ?? string.Empty, editingGroup: null)
        {
        }

        public GroupEditWindow(IEnumerable<Group> groups, string parentId, Group? editingGroup)
            : this(groups, parentId, editingGroup, noGroupMode: false,
                  noGroupColor: null, noGroupIconColor: null, noGroupIcon: null)
        {
        }

        /// <summary>Редактирование служебного узла «Без группы» / «Закреплённые» (только цвет и иконка).</summary>
        public GroupEditWindow(IEnumerable<Group> groups, string noGroupColor, string noGroupIconColor, string noGroupIcon)
            : this(groups, parentId: string.Empty, editingGroup: null, noGroupMode: true,
                  noGroupColor, noGroupIconColor, noGroupIcon)
        {
        }

        private GroupEditWindow(
            IEnumerable<Group> groups,
            string parentId,
            Group? editingGroup,
            bool noGroupMode,
            string? noGroupColor,
            string? noGroupIconColor,
            string? noGroupIcon)
        {
            Title = LocalizationManager.T("GroupEdit.Title");
            Width = 540;
            MinWidth = 460;
            MinHeight = 520;
            MaxHeight = 880;
            // Высота задана числом, а не по содержимому: с SizeToContent окно
            // меряет только первую вкладку и после переключения не пересчитывается,
            // а прокрутка внутри при бесконечной высоте не включается вовсе.
            // В итоге на вкладке цвета не было видно ни зелёного и синего
            // бегунков, ни поля HEX, и добраться до них было нечем.
            Height = 700;
            CanResize = true;

            _groups = groups.ToList();
            _editingGroup = editingGroup;
            _parentId = parentId ?? string.Empty;
            _noGroupMode = noGroupMode;

            if (noGroupMode)
            {
                _nameBox.Text = LocalizationManager.T("GroupEdit.NoGroup");
                _nameBox.IsEnabled = false;
                _descriptionBox.IsEnabled = false;
                _parentPathBox.Text = LocalizationManager.T("GroupEdit.RootGroup");
                _color = !string.IsNullOrWhiteSpace(noGroupColor) ? noGroupColor : "#2D6CDF";
                _iconColor = !string.IsNullOrWhiteSpace(noGroupIconColor) ? noGroupIconColor : "#FFFFFF";
                _icon = noGroupIcon ?? string.Empty;
            }
            else if (editingGroup is not null)
            {
                Result.Id = editingGroup.Id;
                _nameBox.Text = editingGroup.Name;
                _descriptionBox.Text = editingGroup.Description;
                _color = string.IsNullOrWhiteSpace(editingGroup.Color) ? "#2D6CDF" : editingGroup.Color;
                _iconColor = string.IsNullOrWhiteSpace(editingGroup.IconColor) ? "#FFFFFF" : editingGroup.IconColor;
                _icon = editingGroup.Icon ?? string.Empty;
            }
            else
            {
                Result.Id = Guid.NewGuid().ToString();
            }

            UpdateParentPathDisplay();

            // Встроенный выбор цвета и цвета иконки — как в окне выбора цвета.
            _colorControl.SelectedColor = _color;
            _iconColorControl.SelectedColor = _iconColor;
            _iconColorControl.PropertyChanged += OnIconColorControl_PropertyChanged;

            Content = BuildRoot();

            if (!_noGroupMode)
            {
                // Фокус сразу на поле имени (issue #297): при добавлении группы удобно
                // печатать имя без лишнего щелчка (как фокус пароля в AppLockWindow, #294).
                // Ставим отложенно — после полного показа и активации окна, иначе Focus()
                // может вернуть false (окно ещё не активировано/не прошла компоновка);
                // при неудаче повторяем до 3 попыток.
                Opened += (_, _) => FocusNameBoxAttempt(0);
            }

            ApplyIconPickerColors();
            HighlightSelectedIcon();
        }

        /// <summary>
        /// Отложенная установка фокуса в поле «Наименование» с повторами (issue #297):
        /// синхронный вызов в Opened может вернуть false, если окно ещё не активировано
        /// или не завершилась компоновка — в этом случае пробуем снова.
        /// </summary>
        private void FocusNameBoxAttempt(int attempt)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_nameBox.Focus())
                {
                    _nameBox.SelectAll();
                    return;
                }

                // Окно ещё не готово — повторяем (всего до 3 попыток).
                if (attempt < 2)
                    FocusNameBoxAttempt(attempt + 1);
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        public Group Result { get; private set; } = new();

        private void UpdateParentPathDisplay()
        {
            if (string.IsNullOrEmpty(_parentId))
            {
                _parentPathBox.Text = LocalizationManager.T("GroupEdit.RootGroup");
                return;
            }

            var parent = _groups.FirstOrDefault(g =>
                string.Equals(g.Id, _parentId, StringComparison.OrdinalIgnoreCase));
            _parentPathBox.Text = parent is null
                ? LocalizationManager.T("GroupEdit.RootGroup")
                : GroupHierarchyHelper.GetFullPath(parent, _groups);
        }

        private Control BuildRoot()
        {
            // Раскладка по разметке (GroupEditWindow.xaml:154): вкладки тянутся
            // на всю высоту, кнопки прижаты к низу отдельной полосой с отбивкой.
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var tabs = new TabControl { Margin = new Thickness(16, 16, 16, 0) };
            tabs.Styled(ControlThemes.SettingsSubTabControl);

            // ===== Вкладка «Основные» =====
            // Раскладка по разметке (GroupEditWindow.xaml:175): колонка подписей 140,
            // поля строками, всё внутри рамки с заголовком.
            var general = new Grid();
            general.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(140)));
            general.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            for (var i = 0; i < 3; i++)
                general.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            void PlaceRow(int row, string labelKey, Control control)
            {
                var label = new TextBlock
                {
                    Text = LocalizationManager.T(labelKey),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4)
                };
                Grid.SetRow(label, row);
                Grid.SetColumn(label, 0);
                general.Children.Add(label);

                control.Margin = new Thickness(0, 4);
                Grid.SetRow(control, row);
                Grid.SetColumn(control, 1);
                general.Children.Add(control);
            }

            PlaceRow(0, "GroupEdit.NameLabel", _nameBox);

            var parentRow = new Grid();
            parentRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            parentRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            parentRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            ToolTip.SetTip(_parentPathBox, LocalizationManager.T("GroupEdit.ParentTooltip"));
            Grid.SetColumn(_parentPathBox, 0);
            parentRow.Children.Add(_parentPathBox);

            var selectParent = new Button
            {
                Content = IconHelper.IconAndText("IconFolder", LocalizationManager.T("GroupEdit.SelectParent"), 14,
                    "SecondaryButtonTextBrush"),
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            selectParent.Styled(ControlThemes.SecondaryButton);
            selectParent.Click += (_, _) => OnSelectParent_Click();
            ToolTip.SetTip(selectParent, LocalizationManager.T("GroupEdit.SelectParentTooltip"));
            Grid.SetColumn(selectParent, 1);
            parentRow.Children.Add(selectParent);

            var parentHelp = new Controls.HelpLink
            {
                HelpText = LocalizationManager.T("GroupEdit.ParentHelp"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(parentHelp, 2);
            parentRow.Children.Add(parentHelp);

            PlaceRow(1, "GroupEdit.ParentGroupLabel", parentRow);
            PlaceRow(2, "GroupEdit.DescriptionLabel", _descriptionBox);

            var generalBox = Controls.GroupBoxPanel.Build("GroupEdit.BasicParams", general,
                margin: new Thickness(0, 0, 0, 12), padding: new Thickness(10));

            // Для служебных узлов «Без группы»/«Закреплённые» вкладку «Основные»
            // не показываем: наименование, родитель и описание при сохранении
            // не применяются (замечание к issue #240). Остаются «Цвет» и «Иконка».
            if (!_noGroupMode)
            {
                _mainTabItem = SubTab("IconFileDocument", "GroupEdit.TabMain", generalBox);
                tabs.Items.Add(_mainTabItem);
            }

            // ===== Вкладка «Цвет» =====
            var colorTab = new StackPanel();
            colorTab.Children.Add(SectionHint("GroupEdit.ColorHint"));
            colorTab.Children.Add(_colorControl);
            var colorBox = Controls.GroupBoxPanel.Build("GroupEdit.TitleColor", colorTab,
                margin: new Thickness(0, 0, 0, 12), padding: new Thickness(10));
            var colorTabItem = SubTab("IconPalette", "GroupEdit.TabColor", colorBox);
            tabs.Items.Add(colorTabItem);

            // ===== Вкладка «Иконка» =====
            // Значок вкладки: в разметке это Kind="Shape" из пакета MaterialDesign,
            // в словаре автора такого контура нет, поэтому взят IconApplication.
            var iconTab = new StackPanel();
            iconTab.Children.Add(SectionHint("GroupEdit.IconColorHint"));
            _iconColorControl.Margin = new Thickness(0, 0, 0, 12);
            iconTab.Children.Add(_iconColorControl);
            iconTab.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("GroupEdit.IconLabel"),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 6, 0, 0)
            });
            BuildIconPicker();
            // Прокрутка одна, её даёт вкладка: в разметке набор значков лежит
            // в WrapPanel без своей прокрутки (GroupEditWindow.xaml:284).
            iconTab.Children.Add(_iconPickerPanel);
            var iconBox = Controls.GroupBoxPanel.Build("GroupEdit.IconAndColor", iconTab,
                margin: new Thickness(0, 0, 0, 12), padding: new Thickness(10));
            tabs.Items.Add(SubTab("IconApplication", "GroupEdit.TabIcon", iconBox));

            // Активная вкладка: для обычных групп — «Основные» с именем (issue #297:
            // при добавлении группы имя важнее цвета ярлычка); для служебных узлов
            // «Без группы»/«Закреплённые» — «Цвет» (вкладка «Основные» скрыта).
            tabs.SelectedItem = _mainTabItem ?? colorTabItem;

            Grid.SetRow(tabs, 0);
            grid.Children.Add(tabs);

            // Нижняя панель по разметке (GroupEditWindow.xaml:293): зелёное
            // сохранение шириной 130 слева, красная отмена шириной 120 справа.
            // Значок отмены в разметке взят из ключа IconClear, у нас тот же
            // контур лежит под именем IconClose.
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Children =
                {
                    BuildConfirmActionButton("Common.Save", "IconSave", 130, OnSave_Click, iconGap: 8),
                    BuildCancelActionButton(120, iconSize: 14, iconGap: 8)
                }
            };
            var buttonsBar = new Border
            {
                Padding = new Thickness(16, 10, 16, 16),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = buttons
            };
            Themes.ThemeBrushes.Bind(buttonsBar, Border.BorderBrushProperty, "BorderBrushColor");
            Themes.ThemeBrushes.Bind(buttonsBar, Border.BackgroundProperty, "CardBackgroundBrush");
            Grid.SetRow(buttonsBar, 1);
            grid.Children.Add(buttonsBar);

            return grid;
        }

        /// <summary>
        /// Вкладка раздела: значок и подпись по центру, содержимое в прокрутке.
        /// </summary>
        private static TabItem SubTab(string iconKey, string titleKey, Control content)
        {
            var tab = new TabItem
            {
                Content = new ScrollViewer
                {
                    Content = content,
                    Padding = new Thickness(2, 8, 2, 4),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            };
            tab.Styled(ControlThemes.SettingsSubTabItem);

            // Значок красится подписью вкладки: в разметке он берёт Foreground
            // у самой вкладки (GroupEditWindow.xaml:168).
            var icon = IconHelper.MakeIcon(iconKey, 16, out var path);
            path.Bind(Avalonia.Controls.Shapes.Shape.FillProperty,
                new Avalonia.Data.Binding(nameof(TabItem.Foreground)) { Source = tab });
            icon.Margin = new Thickness(0, 0, 6, 0);

            tab.Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    icon,
                    new TextBlock { Text = LocalizationManager.T(titleKey), VerticalAlignment = VerticalAlignment.Center }
                }
            };
            return tab;
        }

        /// <summary>Серое пояснение под подписью поля, как в разметке WPF.</summary>
        private static TextBlock SectionHint(string key)
        {
            var block = new TextBlock
            {
                Text = LocalizationManager.T(key),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            Themes.ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return block;
        }

        private void BuildIconPicker()
        {
            _iconPickerPanel.Children.Clear();
            var iconBrush = new SolidColorBrush(ParseColor(_iconColor));
            foreach (var (key, label) in AvailableIcons)
            {
                var btn = new Button { Tag = key };
                btn.Styled(ControlThemes.IconPickButton);
                ToolTip.SetTip(btn, label);

                if (string.IsNullOrEmpty(key))
                {
                    btn.Content = IconHelper.MakeIcon("IconClose", 16, "TextOnAccentColorBrush");
                }
                else
                {
                    var geom = ResolveIcon(key);
                    btn.Content = new Path
                    {
                        Data = geom,
                        Fill = iconBrush,
                        Width = 18,
                        Height = 18,
                        Stretch = Stretch.Uniform
                    };
                }

                btn.Click += (_, _) => { _icon = key; HighlightSelectedIcon(); };
                _iconPickerPanel.Children.Add(btn);
            }

            HighlightSelectedIcon();
        }

        private static Geometry ResolveIcon(string key)
        {
            if (Application.Current is { } app &&
                app.TryGetResource(key, null, out var res) && res is Geometry g)
                return g;
            return StreamGeometry.Parse("M0,0H1V1H0Z");
        }

        private void ApplyIconPickerColors()
        {
            var brush = new SolidColorBrush(ParseColor(_iconColor));
            foreach (var child in _iconPickerPanel.Children)
            {
                if (child is Button { Content: Path path })
                    path.Fill = brush;
            }
        }

        private void HighlightSelectedIcon()
        {
            var c = ParseColor(_iconColor);
            var isLightIcon = (c.R + c.G + c.B) / 3.0 > 200;
            foreach (var child in _iconPickerPanel.Children)
            {
                if (child is Button { Tag: string key } button)
                {
                    var isSelected = string.Equals(key, _icon, StringComparison.Ordinal);
                    button.BorderBrush = isSelected
                        ? (isLightIcon
                            ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
                            : new SolidColorBrush(c))
                        : new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
                    button.BorderThickness = new Thickness(isSelected ? 2.5 : 1);
                    button.Background = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
                }
            }
        }

        private void OnSelectParent_Click()
        {
            var dialog = new GroupPickerWindow(
                _groups,
                currentGroupId: _parentId,
                excludeGroupId: _editingGroup?.Id,
                allowNone: true,
                noneLabel: LocalizationManager.T("GroupEdit.RootGroup"),
                kind: GroupPickerObjectKind.Group);
            if (dialog.ShowDialogSync(this))
            {
                _parentId = dialog.ResultGroupId;
                UpdateParentPathDisplay();
            }
        }

        private void OnIconColorControl_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != ColorPickerControl.SelectedColorProperty)
                return;

            _iconColor = _iconColorControl.SelectedColor;
            ApplyIconPickerColors();
            HighlightSelectedIcon();
        }

        private void OnSave_Click()
        {
            if (!_noGroupMode)
            {
                Result.Name = _nameBox.Text?.Trim() ?? string.Empty;
                Result.Description = _descriptionBox.Text?.Trim() ?? string.Empty;
            }
            else
            {
                Result.Name = LocalizationManager.T("GroupEdit.NoGroup");
            }

            Result.Color = _colorControl.SelectedColor ?? "#2D6CDF";
            Result.IconColor = _iconColorControl.SelectedColor ?? "#FFFFFF";
            Result.Icon = _icon;
            Result.ParentId = _parentId ?? string.Empty;
        }

        private static Color ParseColor(string? hex)
        {
            try
            {
                return Color.Parse(string.IsNullOrWhiteSpace(hex) ? "#2D6CDF" : hex);
            }
            catch
            {
                return Color.Parse("#2D6CDF");
            }
        }
    }
}
#endif