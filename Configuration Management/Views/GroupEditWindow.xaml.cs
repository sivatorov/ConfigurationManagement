#if WINDOWS
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог создания/редактирования группы.
    /// Поддерживает выбор родительской группы, иконки и цвета.
    /// </summary>
    public partial class GroupEditWindow : Window
    {
        private readonly ObservableCollection<Group> _groups;
        private readonly Group? _editingGroup;
        private readonly bool _noGroupMode;
        private string _color = "#2D6CDF";
        private string _iconColor = "#FFFFFF";
        private string _icon = string.Empty;
        private string _parentId = string.Empty;
        private bool _colorTabInitialized;
        private bool _iconTabInitialized;

        private static readonly string[] AvailableIconKeys =
        {
            "",
            "IconFolder", "IconDatabase", "IconServices", "IconStar", "IconTag",
            "IconPin", "IconInfo", "IconPlay", "IconSettings", "IconSearch",
            "IconAdd", "IconUsers", "IconHistory", "IconSync", "IconBackup",
            "IconConfiguration", "IconPublish", "IconMonitoring", "IconScheduler",
            "IconLogs", "IconRights", "IconExtension", "IconImport", "IconExport",
            "IconFilter", "IconCopy", "IconEdit", "IconSave", "IconRefresh",
            "IconOpen", "IconWarning", "IconOk", "IconError", "IconAutostart",
            "IconTheme", "IconSun", "IconMoon", "IconCompare", "IconMerge",
        };

        // Ключ перевода подписи иконки в ToolTip.
        private static string IconLabelKey(string iconKey) =>
            "GroupEdit.Icon." + (string.IsNullOrEmpty(iconKey) ? "Default" : iconKey.Substring("Icon".Length));

        public GroupEditWindow(IEnumerable<Group> groups, Group? parent = null)
            : this(groups, parent?.Id ?? string.Empty, editingGroup: null)
        {
        }

        public GroupEditWindow(IEnumerable<Group> groups, string parentId, Group? editingGroup)
            : this(groups, parentId, editingGroup, noGroupMode: false,
                  noGroupColor: null, noGroupIconColor: null, noGroupIcon: null)
        {
        }

        /// <summary>
        /// <summary>
        /// Редактирование служебного узла «Без группы» / «Закреплённые» (у него нет модели Group).
        /// Открывается то же окно, но только с вкладками «Цвет» и «Иконка»,
        /// как для обычной группы (по аналогии с настройками группы).
        /// </summary>
        public GroupEditWindow(
            IEnumerable<Group> groups,
            string noGroupColor,
            string noGroupIconColor,
            string noGroupIcon)
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
            InitializeComponent();
            _groups = new ObservableCollection<Group>(groups);
            _editingGroup = editingGroup;
            _parentId = parentId ?? string.Empty;
            _noGroupMode = noGroupMode;

            if (noGroupMode)
            {
                // Служебный узел «Без группы»/«Закреплённые»: наименование, родительскую
                // группу и описание менять нельзя (при сохранении они не применяются).
                // Вкладку «Основные» скрываем полностью — остаются только «Цвет» и «Иконка»
                // (замечание к issue #240).
                MainTabItem.Visibility = Visibility.Collapsed;
                // «Основные» скрыта — активной становится «Цвет» (первая видимая вкладка).
                ColorTabItem.IsSelected = true;

                _color = !string.IsNullOrWhiteSpace(noGroupColor) ? noGroupColor : "#2D6CDF";
                _iconColor = !string.IsNullOrWhiteSpace(noGroupIconColor) ? noGroupIconColor : "#FFFFFF";
                _icon = noGroupIcon ?? string.Empty;
                return;
            }

            if (editingGroup is not null)
            {
                Result.Id = editingGroup.Id;
                NameBox.Text = editingGroup.Name;
                DescriptionBox.Text = editingGroup.Description;
                _color = string.IsNullOrWhiteSpace(editingGroup.Color) ? "#2D6CDF" : editingGroup.Color;
                _iconColor = string.IsNullOrWhiteSpace(editingGroup.IconColor) ? "#FFFFFF" : editingGroup.IconColor;
                _icon = editingGroup.Icon ?? string.Empty;
            }
            else
            {
                Result.Id = Guid.NewGuid().ToString();
            }

            UpdateParentPathDisplay();
            // Содержимое вкладок «Цвет» и «Иконка» инициализируется лениво —
            // при первой загрузке соответствующей вкладки (OnColorTab_Loaded / OnIconTab_Loaded),
            // т.к. WPF создаёт элементы только активной вкладки TabControl.

            // Активная вкладка — «Основные» с именем (issue #297): для группы имя важнее
            // цвета ярлычка. Первая вкладка активна в WPF по умолчанию, но указываем явно,
            // чтобы поведение не зависело от порядка вкладок. Фокус сразу на поле имени:
            // при добавлении группы удобно печатать имя без лишнего щелчка.
            MainTabItem.IsSelected = true;
            // Фокус ставим отложенно (issue #297): синхронная установка в Loaded «съедается» —
            // TabControl при инициализации может перехватить фокус, и он попадает в поле
            // «не всегда». ApplicationIdle выполняется после полного показа и активации окна;
            // Keyboard.Focus и FocusManager страхуют от перехвата фокуса элементом вкладок.
            Loaded += (_, _) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    if (!NameBox.Focus())
                        System.Windows.Input.Keyboard.Focus(NameBox);
                    System.Windows.Input.FocusManager.SetFocusedElement(this, NameBox);
                    NameBox.SelectAll();
                }));
            };
        }

        public Group Result { get; private set; } = new();

        private void UpdateParentPathDisplay()
        {
            if (string.IsNullOrEmpty(_parentId))
            {
                ParentPathBox.Text = LocalizationManager.T("GroupEdit.RootGroup");
                return;
            }

            var parent = _groups.FirstOrDefault(g =>
                string.Equals(g.Id, _parentId, StringComparison.OrdinalIgnoreCase));
            ParentPathBox.Text = parent is null
                ? LocalizationManager.T("GroupEdit.RootGroup")
                : GroupHierarchyHelper.GetFullPath(parent, _groups);
        }

        private void OnSelectParent_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new GroupPickerWindow(
                _groups,
                currentGroupId: _parentId,
                excludeGroupId: _editingGroup?.Id,
                allowNone: true,
                noneLabel: LocalizationManager.T("GroupEdit.RootGroup"),
                kind: GroupPickerObjectKind.Group)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                _parentId = dialog.ResultGroupId;
                UpdateParentPathDisplay();
            }
        }

        private void BuildIconPicker()
        {
            IconPickerPanel.Children.Clear();
            var iconBrush = new SolidColorBrush(ParseColor(_iconColor));
            foreach (var key in AvailableIconKeys)
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("IconPickButton"),
                    Tag = key,
                    ToolTip = LocalizationManager.T(IconLabelKey(key))
                };

                if (string.IsNullOrEmpty(key))
                {
                    btn.Content = new TextBlock
                    {
                        Text = "∅",
                        FontSize = 14,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                else
                {
                    try
                    {
                        var geom = TryFindResource(key) as Geometry;
                        if (geom != null)
                        {
                            btn.Content = new Path
                            {
                                Data = geom,
                                Fill = iconBrush,
                                Width = 18,
                                Height = 18,
                                Stretch = Stretch.Uniform
                            };
                        }
                        else
                        {
                            btn.Content = new TextBlock { Text = "?", FontSize = 12 };
                        }
                    }
                    catch
                    {
                        btn.Content = new TextBlock { Text = "?", FontSize = 12 };
                    }
                }

                btn.Click += OnIcon_Click;
                IconPickerPanel.Children.Add(btn);
            }

            HighlightSelectedIcon();
        }

        /// <summary>Перекрашивает иконки в панели выбора в цвет иконки.</summary>
        private void ApplyIconPickerColors()
        {
            var brush = new SolidColorBrush(ParseColor(_iconColor));
            foreach (var child in IconPickerPanel.Children)
            {
                if (child is Button { Content: Path path })
                    path.Fill = brush;
            }
        }

        private void OnIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string key)
            {
                _icon = key;
                HighlightSelectedIcon();
            }
        }

        private static readonly Brush IconPickBaseBackground =
            new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)); // #374151

        private void HighlightSelectedIcon()
        {
            var c = ParseColor(_iconColor);
            // Светлая иконка — подсветка выбора делаем яркой обводкой, фон остаётся тёмным.
            var isLightIcon = (c.R + c.G + c.B) / 3.0 > 200;
            foreach (var child in IconPickerPanel.Children)
            {
                if (child is Button button && button.Tag is string key)
                {
                    var isSelected = string.Equals(key, _icon, StringComparison.Ordinal);
                    button.BorderBrush = isSelected
                        ? (isLightIcon
                            ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)) // янтарь
                            : new SolidColorBrush(c))
                        : new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
                    button.BorderThickness = new Thickness(isSelected ? 2.5 : 1);
                    button.Background = IconPickBaseBackground;
                }
            }
        }

        /// <summary>
        /// Инициализирует вкладку «Цвет» при её первой загрузке:
        /// загружает текущий цвет во встроенный пикер и подписывается на его изменение.
        /// </summary>
        private void OnColorTab_Loaded(object sender, RoutedEventArgs e)
        {
            if (_colorTabInitialized)
                return;
            _colorTabInitialized = true;

            HeaderColorPicker.SelectedColor = _color;
            HeaderColorPicker.SelectedColorChanged += (_, _) =>
            {
                _color = HeaderColorPicker.SelectedColor ?? "#2D6CDF";
            };
        }

        /// <summary>
        /// Инициализирует вкладку «Иконка» при её первой загрузке:
        /// строит сетку иконок, загружает цвет иконки во встроенный пикер
        /// и подписывается на его изменение (для перекраски иконок).
        /// </summary>
        private void OnIconTab_Loaded(object sender, RoutedEventArgs e)
        {
            if (_iconTabInitialized)
                return;
            _iconTabInitialized = true;

            BuildIconPicker();
            IconColorPicker.SelectedColor = _iconColor;
            IconColorPicker.SelectedColorChanged += (_, _) =>
            {
                _iconColor = IconColorPicker.SelectedColor ?? "#FFFFFF";
                ApplyIconPickerColors();
                HighlightSelectedIcon();
            };
        }

        private void OnSave_Click(object sender, RoutedEventArgs e)
        {
            // Разрешено сохранять группу без наименования — в файле ibases.v8i
            // группы могут не иметь имени, поэтому редактирование должно работать.
            if (!_noGroupMode)
            {
                Result.Name = NameBox.Text.Trim();
                Result.Description = DescriptionBox.Text.Trim();
            }
            else
            {
                // Служебный узел «Без группы»: имя зафиксировано, цвет/иконка берутся из вкладок.
                Result.Name = LocalizationManager.T("GroupEdit.NoGroup");
            }

            // Цвет и цвет иконки берём из встроенных пикеров — источник того же значения,
            // что показывается в предпросмотре (issue #241). Поля _color/_iconColor остаются
            // запасным вариантом, пока соответствующая вкладка не открывалась: WPF создаёт
            // содержимое вкладки лениво, и пикер ещё не проинициализирован текущим цветом.
            // Раньше цвет сохранялся только через поле _color, обновляемое по событию пикера,
            // и мог расходиться с тем, что пользователь видел при выборе.
            Result.Color = _colorTabInitialized
                ? (HeaderColorPicker.SelectedColor ?? "#2D6CDF")
                : _color;
            Result.IconColor = _iconTabInitialized
                ? (IconColorPicker.SelectedColor ?? "#FFFFFF")
                : _iconColor;
            Result.Icon = _icon;
            Result.ParentId = _parentId ?? string.Empty;
            DialogResult = true;
        }

        private static Color ParseColor(string? hex)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex ?? "#2D6CDF");
            }
            catch
            {
                return (Color)ColorConverter.ConvertFromString("#2D6CDF");
            }
        }
    }
}
#endif
