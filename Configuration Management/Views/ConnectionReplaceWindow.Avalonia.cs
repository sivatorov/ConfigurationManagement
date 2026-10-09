#if LINUX
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Окно «Заменить в строках подключения…» (0.3.9.190, Avalonia/Linux): массовая замена
    /// в строке подключения баз по правилу «найти → заменить на». Вся логика — в чистой
    /// <see cref="ConnectionReplaceViewModel"/>; окно — тонкая обёртка (как
    /// <see cref="ServerMonitorWindow"/> / <see cref="ClusterImportWindow"/>): конструктор
    /// принимает готовый VM, поля и ComboBox'ы привязаны к нему (списки
    /// <c>Fields</c>/<c>Scopes</c>/<c>Modes</c> с локализованными текстами), предпросмотр
    /// «База | Поле | Было | Станет» (ListBox + Grid строк по образцу BuildSessionRow) с
    /// подсветкой изменённых строк (колонка «Станет» — зелёный Foreground при Changed).
    /// Подтверждение перед применением — через <see cref="IDialogService.Confirm"/> с ключом
    /// <c>ConnectionReplace.Confirm.ApplyFormat</c>: при отказе базы не мутируются. После
    /// успешного применения окно НЕ закрывается — показывает результат и активную кнопку
    /// «Отменить последнюю замену»; закрытие (Esc/«Закрыть») ничего не меняет.
    /// </summary>
    public sealed class ConnectionReplaceWindow : ModalWindowBase
    {
        private readonly ConnectionReplaceViewModel _vm;

        /// <param name="vm">Готовый ViewModel окна (кандидаты области и колбэки формирует MainViewModel).</param>
        public ConnectionReplaceWindow(ConnectionReplaceViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));

            Title = LocalizationManager.T("ConnectionReplace.Title");
            Width = 1060;
            Height = 620;
            MinWidth = 860;
            MinHeight = 440;
            FontSize = 13;
            CanResize = true;

            DataContext = _vm;

            // ---- Параметры: Найти / Заменить на. ----
            var findBox = Tb("FindText", 170);
            var replaceBox = Tb("ReplaceText", 170);

            // ---- ComboBox'ы: Поле / Область / Режим (списки из VM, выбор пишется в VM). ----
            var fieldCombo = BuildCombo(_vm.Fields, item => item.Value, value => _vm.SelectedField = value);
            var scopeCombo = BuildCombo(_vm.Scopes, item => item.Value, value => _vm.SelectedScope = value);
            var modeCombo = BuildCombo(_vm.Modes, item => item.Value, value => _vm.SelectedMatchMode = value);

            var ignoreCaseCheck = new CheckBox
            {
                Content = LocalizationManager.T("ConnectionReplace.IgnoreCase"),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = _vm.IgnoreCase
            };
            ignoreCaseCheck.IsCheckedChanged += (_, _) => _vm.IgnoreCase = ignoreCaseCheck.IsChecked == true;

            var findButton = BuildActionButton(LocalizationManager.T("ConnectionReplace.FindButton"), "🔍",
                () => _vm.RefreshPreviewCommand.Execute(null));

            // Панель параметров — ДВЕ СТРОКИ (issue #357): на первой только «Найти» и
            // «Заменить на», на второй — Поле / Область / Режим / регистр + «Найти».
            var paramsRow1 = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 0, 0, 6),
                Children =
                {
                    Labeled("ConnectionReplace.FindLabel", findBox),
                    Labeled("ConnectionReplace.ReplaceLabel", replaceBox)
                }
            };
            var paramsRow2 = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 0, 0, 8),
                Children =
                {
                    Labeled("ConnectionReplace.FieldLabel", fieldCombo),
                    Labeled("ConnectionReplace.ScopeLabel", scopeCombo),
                    Labeled("ConnectionReplace.ModeLabel", modeCombo),
                    ignoreCaseCheck,
                    findButton
                }
            };
            var paramsPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { paramsRow1, paramsRow2 }
            };

            // ---- Предпросмотр «База | Поле | Было | Станет». ----
            var previewList = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<object>((item, _) => BuildPreviewRow(item))
            };
            previewList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(ListBoxItem.MinHeightProperty, 38d),
                    new Setter(ListBoxItem.VerticalContentAlignmentProperty, VerticalAlignment.Center)
                }
            });
            previewList.Bind(ListBox.ItemsSourceProperty, new Binding("PreviewRows"));

            // ---- Сводка предпросмотра, результат применения/отката, ошибка. ----
            var summary = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            summary.Bind(TextBlock.TextProperty, new Binding("SummaryText"));
            var result = new TextBlock { FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            result.Bind(TextBlock.TextProperty, new Binding("ResultText"));
            var error = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#DC2626")),
                TextWrapping = TextWrapping.Wrap
            };
            error.Bind(TextBlock.TextProperty, new Binding("ErrorMessage"));

            // ---- Кнопки: Заменить (с подтверждением), Отменить, Закрыть. ----
            var applyButton = BuildActionButton(LocalizationManager.T("ConnectionReplace.ApplyButton"), "✓", OnApplyClick);
            applyButton.Background = new SolidColorBrush(Color.Parse("#06B6D4"));
            applyButton.Foreground = Brushes.White;
            applyButton.Bind(Button.IsEnabledProperty, new Binding("CanApply"));

            var undoButton = BuildActionButton(LocalizationManager.T("ConnectionReplace.UndoButton"), "↩",
                () => _vm.UndoLastCommand.Execute(null));
            undoButton.Bind(Button.IsEnabledProperty, new Binding("CanUndo"));

            var closeButton = BuildCloseButton();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 10, 0, 0),
                Children = { applyButton, undoButton, closeButton }
            };

            var root = new Grid
            {
                Margin = new Thickness(16),
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                }
            };
            Place(root, paramsPanel, 0);
            Place(root, previewList, 1);
            Place(root, new StackPanel { Children = { summary, result, error }, Spacing = 2 }, 2);
            Place(root, buttons, 3);
            Content = root;
        }

        /// <summary>
        /// «Заменить»: подтверждение с числом затронутых баз через IDialogService; при отказе
        /// применение не выполняется. После применения окно остаётся открытым (VM выводит
        /// ResultText и разблокирует «Отменить последнюю замену»).
        /// </summary>
        private void OnApplyClick()
        {
            if (!_vm.CanApply || _vm.IsPreviewDirty)
                return;

            var message = string.Format(
                LocalizationManager.T("ConnectionReplace.Confirm.ApplyFormat"),
                _vm.AffectedCount);
            if (!AppServices.GetRequiredService<IDialogService>().Confirm(message, Title))
                return;

            _vm.ApplyCommand.Execute(null);
        }

        // ===================== Построители =====================

        private static void Place(Grid grid, Control control, int row)
        {
            Grid.SetRow(control, row);
            grid.Children.Add(control);
        }

        private static Control Labeled(string textKey, Control input)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = LocalizationManager.T(textKey), VerticalAlignment = VerticalAlignment.Center },
                    input
                }
            };
            return panel;
        }

        private static TextBox Tb(string property, double width)
        {
            var tb = new TextBox
            {
                Width = width,
                Padding = new Thickness(6, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            tb.Styled(ControlThemes.ModernTextBox);
            tb.Bind(TextBox.TextProperty, new Binding(property, BindingMode.TwoWay));
            return tb;
        }

        /// <summary>
        /// ComboBox по списку <see cref="DisplayItem{T}"/> из VM: текущее значение выбирается
        /// при открытии, изменение выбора записывается в VM-свойство (логика — в VM, окно
        /// только синхронизирует, как у остальных окон).
        /// </summary>
        private static ComboBox BuildCombo<T>(
            System.Collections.Generic.IReadOnlyList<DisplayItem<T>> items,
            Func<DisplayItem<T>, T> getValue,
            Action<T> writeToVm)
        {
            var combo = new ComboBox { Width = 180, VerticalContentAlignment = VerticalAlignment.Center };
            combo.Styled(ControlThemes.ModernComboBox);
            combo.ItemsSource = items;
            combo.ItemTemplate = new FuncDataTemplate<DisplayItem<T>>((item, _) =>
                new TextBlock { Text = item.DisplayText });
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is DisplayItem<T> item)
                    writeToVm(getValue(item));
            };
            return combo;
        }

        private static Button BuildActionButton(string text, string icon, Action onClick)
        {
            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = icon, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                Padding = new Thickness(12, 5)
            };
            button.Styled(ControlThemes.ModernButton);
            ThemeBrushes.Bind(button, Button.BackgroundProperty, "ItemHoverBrush");
            button.Click += (_, _) => onClick();
            return button;
        }

        private Button BuildCloseButton()
        {
            var button = new Button
            {
                Content = IconHelper.IconAndText("IconClose", LocalizationManager.T("Common.Close"), 14, "TextPrimaryBrush"),
                IsCancel = true
            };
            button.Styled(ControlThemes.ModernButton);
            ThemeBrushes.Bind(button, Button.BackgroundProperty, "ItemHoverBrush");
            ThemeBrushes.Bind(button, Button.ForegroundProperty, "TextPrimaryBrush");
            button.Click += (_, _) => Close();
            return button;
        }

        // ===================== Строка предпросмотра =====================

        private Control BuildPreviewRow(object item)
        {
            var row = (ConnectionReplacePreviewRow)item;
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(1.3, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1.6, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1.6, GridUnitType.Star))
                }
            };
            AddCell(grid, CellText("Infobase.Name"), 0);
            AddCell(grid, new TextBlock
            {
                Text = ConnectionReplaceViewModel.FieldDisplayText(row.Field),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            }, 1);
            AddCell(grid, CellText("BeforeText"), 2);
            // «Станет»: зелёный Foreground при реальном изменении (подсветка, как фон ячейки WPF).
            AddCell(grid, CellText("AfterText", colorHex: row.Changed ? "#16A34A" : null), 3);
            return grid;
        }

        private static Control CellText(string property, bool bold = false, string? colorHex = null)
        {
            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = colorHex is null ? null : new SolidColorBrush(Color.Parse(colorHex))
            };
            text.Bind(TextBlock.TextProperty, new Binding(property));
            return text;
        }

        private static void AddCell(Grid grid, Control control, int column)
        {
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }
    }
}
#endif