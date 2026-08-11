using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Configuration_Management.Models;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог создания/редактирования группы.
    /// Поддерживает выбор родительской группы для построения иерархии.
    /// </summary>
    public partial class GroupEditWindow : Window
    {
        private readonly ObservableCollection<Group> _groups;
        private string _color = "#2D6CDF";

        /// <summary>
        /// Создаёт диалог для новой группы (корневой или подгруппы).
        /// </summary>
        /// <param name="groups">Полный список групп (для выбора родителя).</param>
        /// <param name="parent">Родительская группа. Null — создаётся корневая группа.</param>
        public GroupEditWindow(IEnumerable<Group> groups, Group? parent = null)
            : this(groups, parent?.Id ?? string.Empty, editingGroup: null)
        {
        }

        /// <summary>
        /// Создаёт диалог для редактирования существующей группы.
        /// </summary>
        /// <param name="groups">Полный список групп (для выбора родителя).</param>
        /// <param name="parentId">Идентификатор текущего родителя группы (пустая строка — корень).</param>
        /// <param name="editingGroup">Редактируемая группа. Null — создание новой группы.</param>
        public GroupEditWindow(IEnumerable<Group> groups, string parentId, Group? editingGroup)
        {
            InitializeComponent();
            _groups = new ObservableCollection<Group>(groups);

            // Заполняем список доступных родительских групп.
            BuildParentList(editingGroup, parentId);

            if (editingGroup is not null)
            {
                Result.Id = editingGroup.Id;
                NameBox.Text = editingGroup.Name;
                DescriptionBox.Text = editingGroup.Description;
                _color = editingGroup.Color;
            }
            else
            {
                // Для новой группы генерируем уникальный идентификатор. Без него подгруппы,
                // ссылающиеся на ParentId, потеряют связь, и иерархия не сохранится.
                Result.Id = Guid.NewGuid().ToString();
            }

            ApplyPaletteColors();
            UpdateColorPreview();
        }

        /// <summary>
        /// Возвращает отредактированную группу.
        /// </summary>
        public Group Result { get; private set; } = new();

        /// <summary>
        /// Строит список возможных родительских групп.
        /// Исключает редактируемую группу и её потомков во избежание циклов.
        /// </summary>
        private void BuildParentList(Group? editingGroup, string currentParentId)
        {
            // Добавляем вариант «Корневая группа» (без родителя).
            ParentCombo.Items.Add(new ComboBoxItem { Content = "— Корневая группа —", Tag = string.Empty });

            foreach (var group in _groups)
            {
                if (editingGroup is not null && editingGroup.Id == group.Id)
                    continue;

                // Нельзя выбрать в качестве родителя потомка редактируемой группы.
                if (editingGroup is not null &&
                    GroupHierarchyHelper.IsAncestorOrSelf(group.Id, editingGroup.Id, _groups))
                {
                    continue;
                }

                var path = GroupHierarchyHelper.GetFullPath(group, _groups);
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                ParentCombo.Items.Add(new ComboBoxItem { Content = path, Tag = group.Id });
            }

            // Выбираем текущий родитель.
            SelectParent(currentParentId);
        }

        /// <summary>
        /// Выбирает родительскую группу в выпадающем списке.
        /// </summary>
        private void SelectParent(string parentId)
        {
            foreach (var item in ParentCombo.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, parentId, StringComparison.OrdinalIgnoreCase))
                {
                    ParentCombo.SelectedItem = cbi;
                    return;
                }
            }

            // Если родитель не найден — выбираем «Корневую группу».
            if (ParentCombo.Items.Count > 0)
            {
                ParentCombo.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Задаёт фон каждой кнопке палитры из её Tag (HEX-цвет).
        /// </summary>
        private void ApplyPaletteColors()
        {
            foreach (var child in PaletteGrid.Children)
            {
                if (child is Button button && button.Tag is string hex)
                {
                    button.Background = new SolidColorBrush(ParseColor(hex));
                }
            }
        }

        private void OnPaletteColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string hex)
            {
                _color = hex;
                UpdateColorPreview();
            }
        }

        private void OnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                MessageBox.Show("Укажите наименование группы.", "Внимание",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result.Name = NameBox.Text.Trim();
            Result.Description = DescriptionBox.Text.Trim();
            Result.Color = _color;
            Result.ParentId = ParentCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string parentId
                ? parentId
                : string.Empty;
            DialogResult = true;
        }

        private void UpdateColorPreview()
        {
            ColorPreview.Background = new SolidColorBrush(ParseColor(_color));
            ColorHexText.Text = _color;
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