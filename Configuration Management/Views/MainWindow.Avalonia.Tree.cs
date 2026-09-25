#if LINUX
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Дерево списка баз и правая панель главного окна (Avalonia/Linux): строки групп
    /// и баз, карточка сведений, действия и контекстное меню строки.
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>Строит строку дерева: заголовок группы или карточку базы.</summary>
        private Control BuildTreeRow(object? item)
        {
            if (item is GroupNodeViewModel group)
                return BuildGroupRow(group);
            if (item is Infobase ib)
                return BuildInfobaseRow(ib);
            return new TextBlock { Text = item?.ToString() ?? string.Empty };
        }

        private Control BuildGroupRow(GroupNodeViewModel group)
        {
            // Высота оформления группы и расстояние между группами берутся из метрик:
            // в компактном режиме вертикальный padding заголовка и внешний отступ
            // уменьшаются, чтобы группы занимали меньше места.
            // Отступ 8,3 в обычном режиме и 6,1 в компактном, рамка толщиной 2:
            // прозрачная в покое и акцентная у выбранной группы
            // (MainWindow.xaml:872-882). Рамки у нас не было вовсе.
            var header = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(UiMetrics.Compact ? 6 : 8, UiMetrics.GroupHeaderPadV),
                Margin = new Thickness(0, UiMetrics.GroupHeaderMarginV, 0, UiMetrics.GroupHeaderMarginV),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent
            };
            // Пустая группа сдвигается вправо на отступ своего уровня и ширину
            // кнопки разворота: этой кнопки у неё нет, и без сдвига её заголовок
            // начинался бы левее соседних (Converters/GroupOffsetConverter.cs:37).
            if (group.Items.Count == 0)
            {
                var marginV = UiMetrics.GroupHeaderMarginV;
                header[!Border.MarginProperty] = new Binding(nameof(TreeViewItem.Level))
                {
                    RelativeSource = new RelativeSource
                    {
                        Mode = RelativeSourceMode.FindAncestor,
                        AncestorType = typeof(TreeViewItem)
                    },
                    Converter = new FuncValueConverter<int, Thickness>(level =>
                        new Thickness(EmptyGroupOffsetFor(level), marginV, 0, marginV))
                };
            }

            header.Bind(Border.BackgroundProperty, new Binding("HeaderBrush") { Source = group });

            // Кисть берётся наблюдателем темы, а подписка на узел живёт ровно
            // столько, сколько строка находится в дереве: строки пересобираются
            // часто, а узел группы переживает их все.
            IBrush accent = Brushes.Transparent;
            void ApplyGroupSelection()
                => header.BorderBrush = group.IsSelected ? accent : Brushes.Transparent;
            ThemeBrushes.Observe(header, "AccentBrush", brush => { accent = brush; ApplyGroupSelection(); });

            void OnGroupChanged(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(GroupNodeViewModel.IsSelected))
                    ApplyGroupSelection();
            }
            header.AttachedToVisualTree += (_, _) => { group.PropertyChanged += OnGroupChanged; ApplyGroupSelection(); };
            header.DetachedFromVisualTree += (_, _) => group.PropertyChanged -= OnGroupChanged;
            ApplyGroupSelection();

            // Имя и счётчик привязаны к узлу, а не подставлены строкой: состав узла
            // меняется и без пересборки дерева (закрепление базы), и тогда готовый
            // текст остался бы со старым числом.
            var caption = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Имя группы наследует применяемый к интерфейсу шрифт; в компактном режиме
            // размер задаётся явно и уменьшается, чтобы строки групп были плотнее.
            // Значок группы перед именем, как в разметке (MainWindow.xaml:964).
            var groupIcon = IconHelper.MakeIcon(group.Icon, UiMetrics.Compact ? 14 : 18, out var groupIconPath);
            groupIconPath.Fill = group.IconBrush;
            groupIcon.Margin = new Thickness(0, 0, 8, 0);
            caption.Children.Add(groupIcon);

            var text = new TextBlock
            {
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = UiMetrics.ScaledFont(UiMetrics.Compact ? 12 : 15)
            };
            if (UiMetrics.Compact)
                text.FontSize = UiMetrics.GroupNameFont;
            text.Bind(TextBlock.TextProperty, new Binding("DisplayName") { Source = group });
            text.Bind(TextBlock.ForegroundProperty, new Binding("HeaderTextBrush") { Source = group });
            caption.Children.Add(text);

            var count = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = UiMetrics.ScaledFont(UiMetrics.Compact ? 11 : 13)
            };
            if (UiMetrics.Compact)
                count.FontSize = UiMetrics.GroupNameFont;
            count.Bind(TextBlock.TextProperty,
                new Binding("TotalInfobaseCount") { Source = group, StringFormat = "({0})" });
            count.Bind(TextBlock.ForegroundProperty, new Binding("HeaderTextBrush") { Source = group });
            caption.Children.Add(count);

            // Колонки те же, что у строки базы: команды группы стоят в колонке
            // «Действия» и попадают ровно под кнопки строк, а имя со счётчиком
            // занимает всё, что левее (MainWindow.xaml:961 и 1015).
            var row = new Grid { Name = GroupRowGridName };
            var actionsIndex = AddListColumns(row,
                _vm?.ShowFavoritesButton ?? true, _vm?.ShowPinnedButton ?? true);

            // Колонка «Действия» всегда есть в сетке (нулевой ширины при выключенной
            // настройке, AddListColumns), но панель кнопок, как и в строке базы
            // и в заголовке, строится только когда колонка видима: иначе в нулевую
            // колонку попадали бы невидимые кнопки с обработчиками (issue #191).
            if (_vm?.ShowActionsColumn != false)
            {
                var actions = new ActionsPanel();
                actions.Children.Add(GroupRowActionButton(group, "IconEdit", "EditGroupCommand",
                    LocalizationManager.T("Main.EditGroupTooltip"), "TextSecondaryBrush"));
                // «Удалить» у служебных узлов скрыта: у них нет модели группы.
                var deleteBtn = GroupRowActionButton(group, "IconDelete", "DeleteGroupCommand",
                    LocalizationManager.T("Main.DeleteGroupTooltip"), colorHex: "#DC2626");
                deleteBtn.IsVisible = group.Marker != GroupNodeViewModel.PinnedMarker
                                      && group.Marker != GroupNodeViewModel.NoGroupMarker;
                actions.Children.Add(deleteBtn);
                Grid.SetColumn(actions, actionsIndex);
                row.Children.Add(actions);
            }

            Grid.SetColumn(caption, 0);
            Grid.SetColumnSpan(caption, actionsIndex);
            row.Children.Add(caption);

            header.Child = row;
            return header;
        }

        /// <summary>
        /// Кнопка действия в колонке «Действия» строки группы: иконка, команда из вьюмодели,
        /// параметром служит узел группы строки.
        /// </summary>
        private Button GroupRowActionButton(GroupNodeViewModel group, string iconKey, string commandPath, string tooltip,
            string? brushKey = null, string? colorHex = null)
        {
            var button = new Button
            {
                Content = colorHex is not null
                    ? IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(15), new SolidColorBrush(Color.Parse(colorHex)))
                    : IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(15), brushKey ?? "TextSecondaryBrush"),
                Margin = new Thickness(1, 0),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CommandParameter = group
            };
            button.Styled(Themes.ControlThemes.IconButton);
            ToolTip.SetTip(button, tooltip);
            // Команда живёт во вьюмодели, а контекстом строки служит узел группы.
            button.Bind(Button.CommandProperty, new Binding(commandPath) { Source = _vm });
            return button;
        }

        private Control BuildInfobaseRow(Infobase ib)
        {
            // Карточка с фоном/границей из темы; hover и выделение отслеживает сама
            // (см. InfobaseRowCard): обычное → CardBackgroundBrush, hover → ItemHoverBrush,
            // выделено → ItemSelectedBrush + AccentBrush-граница.
            var card = new InfobaseRowCard();

            var grid = new Grid();
            // Слева направо: звезда, булавка, иконка типа подключения, имя базы,
            // дальше колонки значений. Звезда и булавка повторяют колонки заголовка
            // теми же ширинами и подчиняются тем же настройкам.
            var showFavorite = _vm?.ShowFavoritesButton ?? true;
            var showPin = _vm?.ShowPinnedButton ?? true;
            AddListColumns(grid, showFavorite, showPin);
            var columns = ListColumns();
            var actionsOffset = ActionsOffsetInColumns(columns);

            // Звезда, булавка, значок подключения и имя лежат в одной горизонтальной
            // панели, которая занимает все пять ведущих колонок (MainWindow.xaml:1152).
            // Так их собственная ширина не двигает колонки значений: те начинаются
            // после ведущих и стоят под своими заголовками. Отступ вложенности
            // ставит контейнер строки, панель его получает от него же.
            var lead = new StackPanel
            {
                Name = LeadBlockName,
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                // Обрезка по границе колонки «Название»: у автора имя тоже лежит
                // в горизонтальной панели и ширины не знает, но там оно упирается
                // в край окна, а у нас налезало бы на колонки значений.
                ClipToBounds = true
            };
            // Отступ вложенности: сдвигается только ведущий блок, сама строка
            // остаётся у левого края (MainWindow.xaml:1155).
            lead[!StackPanel.MarginProperty] = new Binding(nameof(TreeViewItem.Level))
            {
                RelativeSource = new RelativeSource
                {
                    Mode = RelativeSourceMode.FindAncestor,
                    AncestorType = typeof(TreeViewItem)
                },
                Converter = LeveledTreeViewItem.LeadIndent
            };

            if (showFavorite)
            {
                // Номер слота Alt+N идёт плашкой сразу за звездой и внутри той же
                // кнопки, как в разметке (MainWindow.xaml:1180). Прежде он был
                // наложен на угол колонки мелкой цифрой без подложки.
                var favorite = RowMarkButton(card, ib, "IconFavorite", "FavoriteBrush",
                    nameof(Infobase.IsFavorite), () => ib.IsFavorite,
                    LocalizationManager.T("Main.ToggleFavoriteTooltip"), "ToggleFavoriteForCommand",
                    FavoriteSlotBadge(card, ib));
                lead.Children.Add(favorite);
            }

            if (showPin)
            {
                var pin = RowMarkButton(card, ib, "IconPin", "AccentBrush",
                    nameof(Infobase.IsPinned), () => ib.IsPinned,
                    LocalizationManager.T("Main.TogglePinTooltip"), "TogglePinForCommand");
                lead.Children.Add(pin);
            }

            // Иконка статуса базы слева: тип подключения (папка / глобус / сеть),
            // «недоступна» или «проверяется» (серые часики). Цвет зависит от статуса:
            // янтарный — файловая, синий — веб, фиолетовый — клиент-сервер,
            // красный — недоступна, серый — идёт проверка доступности (issue #289).
            var connectionIconKey = ib.StatusIconKey;

            // Значок идёт без подложки и рамки: в разметке это голый Path 14 на 14
            // с отступом 6 справа (MainWindow.xaml:1234). Коробка вокруг него была
            // нашей отсебятиной.
            var iconBox = IconHelper.MakeIcon(connectionIconKey, UiMetrics.RowIcon, out var statusPath);
            statusPath.Fill = new SolidColorBrush(Color.Parse(ib.StatusColorHex));
            iconBox.HorizontalAlignment = HorizontalAlignment.Center;
            iconBox.VerticalAlignment = VerticalAlignment.Center;
            iconBox.Margin = new Thickness(0, 0, 6, 0);
            ToolTip.SetTip(iconBox, ib.StatusDisplay);

            // Во время «Проверить доступность всех баз» статус меняется по мере
            // готовности каждой базы: серый значок ожидания сменяется фактическим
            // результатом. Строка подписывается на статусные свойства Infobase,
            // чтобы обновлять иконку без пересборки всего дерева.
            card.AddSubscription(() =>
            {
                void OnStatusChanged(object? _, PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(Infobase.StatusIconKey))
                        statusPath.Data = IconHelper.Geometry(ib.StatusIconKey);
                    if (e.PropertyName == nameof(Infobase.StatusColorHex))
                        statusPath.Fill = new SolidColorBrush(Color.Parse(ib.StatusColorHex));
                    if (e.PropertyName == nameof(Infobase.StatusDisplay))
                        ToolTip.SetTip(iconBox, ib.StatusDisplay);
                }

                ib.PropertyChanged += OnStatusChanged;
                return new ActionDisposable(() => ib.PropertyChanged -= OnStatusChanged);
            });

            lead.Children.Add(iconBox);

            // Правая колонка: имя (крупно) + строки вторичной информации.
            // В компактном режиме уменьшаем и межстрочный промежуток, чтобы строки с
            // полным набором метаданных тоже «сжимались», а не оставались прежней высоты.
            var content = new StackPanel { Spacing = UiMetrics.Scaled(2), VerticalAlignment = VerticalAlignment.Center };

            // Имя лежит в горизонтальной панели, как в разметке (MainWindow.xaml:1244),
            // и своей ширины не знает: обрезку по краю колонки «Название» даёт
            // ClipToBounds ведущего блока. Многоточия при этом не будет, у автора
            // имя тоже не подрезается.
            var name = new TextBlock
            {
                Text = ib.NameDisplay,
                FontSize = UiMetrics.RowNameFont,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushes.Bind(name, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            content.Children.Add(name);

            // Второй подписи под именем в разметке нет: первая строка это значок
            // статуса и имя, а вторая отдана тегам (MainWindow.xaml:1230-1247).
            // Расположение живёт в своей колонке и в сведениях правой панели.
            lead.Children.Add(content);
            grid.Children.Add(lead);
            Grid.SetColumn(lead, 0);
            Grid.SetColumnSpan(lead, NameRowColumn + 1);

            var dataColumn = NameRowColumn + 1;
            for (var i = 0; i < columns.Count; i++)
            {
                if (i == actionsOffset)
                    dataColumn++;
                var value = ColumnValue(ib, columns[i].Key);
                var cell = SecondaryText(string.IsNullOrWhiteSpace(value) ? string.Empty : value, card);
                cell.VerticalAlignment = VerticalAlignment.Center;
                // Отступ ячейки значения как в разметке (MainWindow.xaml:1249):
                // без него значения стояли на 6 пикселей левее своих заголовков,
                // у которых такой отступ есть.
                cell.HorizontalAlignment = HorizontalAlignment.Left;
                cell.Margin = new Thickness(6, 0, 6, 0);
                if (columns[i].Key == "Version")
                {
                    // Двойной щелчок открывает выбор версии платформы, как
                    // в разметке WPF (MainWindow.xaml:1230). До этого окно
                    // PlatformVersionPickerWindow собиралось, но из интерфейса
                    // Linux-версии было недостижимо.
                    ToolTip.SetTip(cell, LocalizationManager.T("Main.PlatformVersionTooltip"));
                    // Issue #250: растягиваем ячейку на всю ширину колонки, чтобы
                    // двойной клик по пустой области колонки тоже открывал выбор версии,
                    // а не запускал базу (текст при этом остаётся прижатым влево).
                    cell.HorizontalAlignment = HorizontalAlignment.Stretch;
                    cell.TextTrimming = TextTrimming.CharacterEllipsis;
                    cell.DoubleTapped += (_, e) =>
                    {
                        e.Handled = true;
                        _vm?.PickPlatformVersionFor(ib);
                    };
                }
                grid.Children.Add(cell);
                Grid.SetColumn(cell, dataColumn);
                dataColumn++;
            }

            // Кнопки действий в колонке «Действия» (после колонки «Режим запуска»):
            // запуск, конфигуратор, изменить настройки, очистить кеш, удалить.
            // Колонка при этом нулевой ширины, поэтому панель не строится вовсе
            // и не остаётся невидимых обработчиков на каждую строку (issue #158).
            var actionsCol = NameRowColumn + 1 + actionsOffset;
            ActionsPanel? actions = null;
            if (_vm?.ShowActionsColumn != false)
            {
                // Три действия, как в разметке WPF (MainWindow.xaml:1497-1517):
                // запуск, конфигуратор, очистка кеша. Правка настроек и удаление
                // в строке не показываются, они остаются в контекстном меню.
                actions = new ActionsPanel { Spacing = 1 };
                actions.Children.Add(RowActionButton(ib, "IconPlay", "LaunchEnterpriseCommand", LocalizationManager.T("Main.LaunchEnterpriseTooltip")));
                actions.Children.Add(RowActionButton(ib, "IconWrench", "LaunchConfiguratorCommand", LocalizationManager.T("Main.LaunchConfiguratorSectionTooltip")));
                actions.Children.Add(RowActionButton(ib, "IconBroom", "ClearCacheCommand", LocalizationManager.T("Main.ClearCacheTooltip")));
                // Кнопки живут внутри панели, обрезанной по своей колонке: в узкой
                // колонке «Действия» лишние значки у автора пропадают, а у нас
                // рисовались поверх колонки «Сервер/База».
                grid.Children.Add(actions);
                Grid.SetColumn(actions, actionsCol);
            }

            if (_vm?.ShowTags == true)
            {
                // Теги идут второй строкой от левого края и до колонки действий,
                // а сами действия охватывают обе строки (MainWindow.xaml:1278
                // и 1318). У нас теги начинались с колонки имени и проходили под
                // действиями и остальными значениями.
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (actions is not null)
                    Grid.SetRowSpan(actions, 2);

                var tags = BuildRowTags(card, ib);
                // Тот же отступ вложенности, что и у ведущего блока
                // (MainWindow.xaml:1316): теги стоят под именем, а не левее его.
                tags[!Control.MarginProperty] = new Binding(nameof(TreeViewItem.Level))
                {
                    RelativeSource = new RelativeSource
                    {
                        Mode = RelativeSourceMode.FindAncestor,
                        AncestorType = typeof(TreeViewItem)
                    },
                    // Верхний отступ 2 сохраняется: привязка задаёт Margin целиком.
                    Converter = new FuncValueConverter<int, Thickness>(level =>
                        new Thickness(LeveledTreeViewItem.LeadIndentFor(level), 2, 0, 0))
                };
                grid.Children.Add(tags);
                Grid.SetRow(tags, 1);
                Grid.SetColumn(tags, 0);
                Grid.SetColumnSpan(tags, actionsCol);
            }

            card.Child = grid;

            // Двойной клик по строке базы запускает её в режиме по умолчанию
            // (issue #201): «1С:Предприятие» или «Конфигуратор» согласно DefaultLaunchMode.
            card.DoubleTapped += (_, _) =>
            {
                if (_vm is not { } vm)
                    return;
                vm.SelectedInfobase = ib;
                // Действие по двойному щелчку (функция №28 StartManager): «1С:Предприятие»,
                // «Конфигуратор» или «Ничего». Индивидуальное значение ИБ переопределяет
                // глобальную настройку (см. MainViewModel.ResolveDoubleClickAction).
                var dblAction = vm.ResolveDoubleClickAction(ib);
                if (dblAction == Configuration_Management.Models.DoubleClickAction.None)
                    return;
                if (dblAction == Configuration_Management.Models.DoubleClickAction.Configurator)
                    vm.LaunchConfiguratorCommand.Execute(null);
                else
                    vm.LaunchEnterpriseCommand.Execute(null);
            };

            // Ctrl+щелчок по телу строки — поставить/снять закладку (номер). Клики
            // по кнопкам строки (звезда/булавка/действия) обрабатываются самими
            // кнопками и сюда не всплывают, поэтому конфликта нет.
            card.PointerPressed += (_, e) =>
            {
                if (_vm is not { } vm)
                    return;
                if ((e.KeyModifiers & KeyModifiers.Control) == 0)
                    return;
                vm.ToggleBookmark(ib);
                e.Handled = true;
            };

            return card;
        }

        /// <summary>
        /// Номер слота Alt+N у избранной базы. Пусто, если слот не назначен:
        /// их девять, а избранных может быть больше.
        /// </summary>
        private Control FavoriteSlotBadge(InfobaseRowCard card, Infobase infobase)
        {
            var text = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(10),
                FontWeight = FontWeight.Bold,
                // Цвет подписи в разметке задан числом и одинаков в обеих темах.
                Foreground = new SolidColorBrush(Color.Parse("#1C1917")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            // Отступы и минимум плашки ужаты против разметки: там колонка звезды
            // шириной 28 и содержимое кнопки выходит за её край, а WPF ничего
            // не обрезает. Здесь оно обрезалось, поэтому звезда с плашкой
            // укладываются в 30 целиком.
            var host = new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 1),
                Margin = new Thickness(2, 0, 0, 0),
                MinWidth = UiMetrics.Scaled(13),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            ThemeBrushes.Bind(host, Border.BackgroundProperty, "FavoriteBrush");

            void Apply()
            {
                text.Text = infobase.FavoriteHotkeyDisplay;
                // Разметка WPF гасит плашку и по номеру, и по самой звезде
                // (MainWindow.xaml:1156-1183): без второго условия номер
                // остаётся висеть у базы, которую убрали из избранного.
                host.IsVisible = infobase.IsFavorite && !string.IsNullOrEmpty(text.Text);
            }

            void OnChanged(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(Infobase.FavoriteHotkeyDisplay)
                    || e.PropertyName == nameof(Infobase.FavoriteHotkeyNumber)
                    || e.PropertyName == nameof(Infobase.IsFavorite))
                    Apply();
            }

            card.AddSubscription(() =>
            {
                infobase.PropertyChanged += OnChanged;
                Apply();
                return new ActionDisposable(() => infobase.PropertyChanged -= OnChanged);
            });

            return host;
        }

        private Button RowMarkButton(InfobaseRowCard card, Infobase infobase, string iconKey,
            string activeBrushKey, string stateProperty, Func<bool> isActive,
            string tooltip, string commandPath, Control? trailing = null)
        {
            var iconHost = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(14), out var icon);
            Control content = iconHost;
            if (trailing is not null)
            {
                var pair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                pair.Children.Add(iconHost);
                pair.Children.Add(trailing);
                content = pair;
            }

            IBrush? active = null;
            IBrush? idle = null;
            void ApplyState()
            {
                var brush = isActive() ? active : idle;
                if (brush is not null)
                    icon.Fill = brush;
            }

            card.AddSubscription(() => Application.Current?.GetResourceObservable(activeBrushKey)
                .Subscribe(new BrushObserver(brush => active = brush, ApplyState)));
            card.AddSubscription(() => Application.Current?.GetResourceObservable("TextSecondaryBrush")
                .Subscribe(new BrushObserver(brush => idle = brush, ApplyState)));

            void OnInfobaseChanged(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == stateProperty)
                    ApplyState();
            }

            card.AddSubscription(() =>
            {
                infobase.PropertyChanged += OnInfobaseChanged;
                // Состояние могло измениться, пока строка была отсоединена.
                ApplyState();
                return new ActionDisposable(() => infobase.PropertyChanged -= OnInfobaseChanged);
            });

            var button = new Button
            {
                Content = content,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                // Своей ширины у кнопки нет: она лежит в ведущем блоке строки
                // и пакуется вплотную к соседям, как в разметке.
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                CommandParameter = infobase
            };
            ToolTip.SetTip(button, tooltip);
            // Команда живёт во вьюмодели, а контекстом строки служит сама база,
            // поэтому источник привязки указывается явно.
            button.Bind(Button.CommandProperty, new Binding(commandPath) { Source = _vm });
            return button;
        }

        /// <summary>
        /// Кнопка действия в колонке «Действия» строки базы: иконка, команда из вьюмодели,
        /// параметром служит сама информационная база строки.
        /// </summary>
        /// <param name="colorHex">
        /// Явный цвет значка. У автора кнопки строки вторичного цвета, кроме
        /// удаления: оно красное.
        /// </param>
        private Control RowActionButton(Infobase ib, string iconKey, string commandPath, string tooltip,
            string? colorHex = null)
        {
            Control glyph;
            if (colorHex is null)
            {
                glyph = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(15), "TextSecondaryBrush");
            }
            else
            {
                glyph = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(15),
                    new SolidColorBrush(Color.Parse(colorHex)));
            }

            // Оформление берёт тема IconButton разметки: у автора все пять команд
            // строки идут этим стилем с полем 1,0 (MainWindow.xaml:1282).
            // Своя кнопка была нужна, пока темы не было: штатная тема Fluent красит
            // не саму кнопку, а её внутренний ContentPresenter, и локальный
            // прозрачный фон её не перебивал.
            var button = new Button
            {
                Content = glyph,
                Margin = new Thickness(1, 0),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CommandParameter = ib
            };
            button.Styled(Themes.ControlThemes.IconButton);
            ToolTip.SetTip(button, tooltip);
            // Команда живёт во вьюмодели, а контекстом строки служит сама база.
            button.Bind(Button.CommandProperty, new Binding(commandPath) { Source = _vm });
            return button;
        }

        /// <summary>Объединяет непустые фрагменты в одну строку с разделителем «•».</summary>
        private static string JoinSegments(params string?[] parts)
        {
            var nonEmpty = parts
                .Select(p => (p ?? string.Empty).Trim())
                .Where(p => p.Length > 0 && p != "—")
                .ToList();
            return nonEmpty.Count == 0 ? string.Empty : string.Join("  •  ", nonEmpty);
        }

        /// <summary>Строка вторичной информации: приглушённый текст из темы с подсказкой по полному значению.</summary>
        private static TextBlock SecondaryText(string text, InfobaseRowCard? owner = null)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.RowSecondaryFont,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTip.SetTip(block, text);
            if (owner is null)
                ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            else
                ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return block;
        }

        private Control BuildRightPanel()
        {
            // Компактная правая панель: primary-запуски на всю ширину, вторичные
            // действия — списком в один столбец, секции без тяжёлых карточек.
            // Отступы держит само содержимое, а не Padding у ScrollViewer:
            // его отступ не входит в прокручиваемую высоту, и нижняя кнопка
            // становилась недостижимой, от неё была видна одна рамка.
            // Верхний отступ приведён к стандартному (12), как в WPF-версии и левой
            // колонке: прежний зазор 56 «отодвигал» блок запуска вниз и выглядел
            // «конским отступом перед кнопками справа» (issue #167). Теперь кнопка
            // запуска поднята и стоит вровень с верхними сегментами левой колонки.
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = UiMetrics.ActionGridGap,
                Margin = new Thickness(12, 12)
            };
            _rightPanelContent = panel;

            // Заголовок базы
            var nameBlock = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(16),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            nameBlock.Bind(TextBlock.TextProperty, new Binding("RightPanelTitle"));

            var groupBlock = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(12),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            ThemeBrushes.Bind(groupBlock, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            groupBlock.Bind(TextBlock.TextProperty, new Binding("RightPanelSubtitle"));

            // Заголовок сеткой, а не горизонтальной панелью: в панели подпись
            // получала бы бесконечную ширину и не переносилась бы по словам.
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Значок базы показывается только когда база выбрана: при выбранной
            // группе и при пустом выборе он висел бы один без подписи.
            // Значок 28 акцентной кистью и правым полем 10, как в разметке
            // (MainWindow.xaml:1709). Контур привязывается к самому Path внутри
            // холста: у Viewbox такого свойства нет, и привязка молча не работала.
            var headerIcon = IconHelper.MakeIcon("IconDatabase", UiMetrics.Scaled(28), out var headerIconPath);
            headerIconPath.Bind(Avalonia.Controls.Shapes.Path.DataProperty,
                new Binding("RightPanelIconKey") { Converter = IconKeyConverter });
            ThemeBrushes.Bind(headerIconPath, Avalonia.Controls.Shapes.Path.FillProperty, "AccentBrush");
            headerIcon.Margin = new Thickness(0, 0, 10, 0);
            headerIcon.Bind(Control.IsVisibleProperty, new Binding("HasRightPanelIcon"));
            header.Children.Add(headerIcon);
            var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            headerText.Children.Add(nameBlock);
            headerText.Children.Add(groupBlock);
            header.Children.Add(headerText);
            Grid.SetColumn(headerText, 1);

            // Подсказка «выберите базу» отдельной строкой под заголовком,
            // как в WPF: там она видна, пока база не выбрана.
            var hintBlock = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(11.5),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 4)
            };
            hintBlock.Bind(TextBlock.TextProperty, new Binding("RightPanelHint"));
            // Подсказка «выберите базу» скрыта и в компактном режиме правой панели
            // (issue #149): иначе при выделении группы сверху появлялась лишняя строка
            // информации. Видимость — производное от «подробности включены» и «база
            // не выбрана», поэтому вынесено в свойство модели ShowRightPanelHint.
            hintBlock.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelHint"));
            // Заголовок выбранной базы скрыт вместе с подробностями и при пустом
            // выборе, как в разметке (MainWindow.xaml:1694-1706): раньше он висел
            // в узкой панели всегда.
            header.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
            panel.Children.Add(header);
            panel.Children.Add(hintBlock);

            // Запуск 1С:Предприятие (primary) — акцентная кнопка запуска с меню
            // дополнительных вариантов.
            var launchEnterpriseBlock = BuildLaunchSplitButton(
                "IconPlay",
                LocalizationManager.T("Main.Enterprise"),
                "LaunchEnterpriseCommand",
                LocalizationManager.T("Main.LaunchEnterpriseTooltip"),
                primary: true,
                new (string, string, string?, string?)[]
                {
                    (LocalizationManager.T("Main.LaunchWithParams"), "LaunchEnterpriseWithParamsCommand", "IconTune", "#0EA5E9"),
                    (LocalizationManager.T("Main.LaunchWithAuth"), "LaunchEnterpriseWithAuthCommand", "IconAccountKey", "#8B5CF6")
                });

            // Конфигуратор — secondary full-width (без отдельной тяжёлой карточки).
            var launchConfiguratorBlock = BuildLaunchSplitButton(
                "IconWrench",
                LocalizationManager.T("Main.SectionConfigurator"),
                "LaunchConfiguratorCommand",
                LocalizationManager.T("Main.LaunchConfiguratorSectionTooltip"),
                primary: false,
                new (string, string, string?, string?)[]
                {
                    (LocalizationManager.T("Main.LaunchWithParams"), "LaunchConfiguratorWithParamsCommand", "IconTune", "#0EA5E9")
                });

            // Остальные действия («Очистить кеш», «Изменить настройки», «Удалить»,
            // «Добавить») перенесены в колонку «Действия» строк базы и верхнюю панель
            // команд. Здесь остаются вторичные действия списком.
            // Под «Действиями» у автора ровно три кнопки: Предприятие, Конфигуратор
            // и штатный стартер. «Открыть каталог» и «Ярлык на рабочем столе»
            // у него живут в контекстном меню строки, там они есть и у нас.
            var starterBlock = BuildActionList(
                CompactActionButton("IconApplication", LocalizationManager.T("Main.NativeStarter"), "OpenNativeStarterCommand", LocalizationManager.T("Main.NativeStarterTooltipLinux"), "#F59E0B", colorTextToo: false,
                    iconSize: UiMetrics.Scaled(15), widePadding: new Thickness(8, 6), narrowPadding: new Thickness(6, 8))
            );

            // Переход по ссылке идёт после карточки сессии, как в разметке.
            var byLinkBlock = BuildActionList(
                CompactActionButtonBound("IconLink", "OpenByLinkCaption", "OpenInfobaseByLinkCommand", LocalizationManager.T("Main.OpenLinkTooltip"), "#0EA5E9", "IconArrowRight", colorTextToo: false,
                    iconSize: UiMetrics.Scaled(16), widePadding: new Thickness(14, 11), narrowPadding: new Thickness(6, 8))
            );

            // Бейджи «Избранное» и «Закреплено», как в разметке WPF: цвета там
            // заданы явно и одинаковы в обеих темах, поэтому берутся числом.
            var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };

            var favoriteBadge = Badge("#FEF3C7");
            favoriteBadge.Child = BadgeContent("IconStar", "#F59E0B", textBinding:
                new MultiBinding
                {
                    StringFormat = "{0} {1}",
                    Bindings =
                    {
                        new Binding { Source = LocalizationManager.T("Main.Favorites") },
                        new Binding("SelectedInfobase.FavoriteHotkeyDisplay")
                    }
                }, themeBrushKey: "FavoriteBrush");
            favoriteBadge.Bind(Control.IsVisibleProperty, new Binding("SelectedInfobase.IsFavorite"));
            badges.Children.Add(favoriteBadge);

            var pinnedBadge = Badge("#EDE9FE");
            pinnedBadge.Child = BadgeContent("IconPin", "#8B5CF6", "#5B21B6",
                new Binding { Source = LocalizationManager.T("Main.PinnedLabel") });
            pinnedBadge.Bind(Control.IsVisibleProperty, new Binding("SelectedInfobase.IsPinned"));
            badges.Children.Add(pinnedBadge);

            var tagsHeader = ThemedIconAndText("IconTag", LocalizationManager.T("Main.Tags"),
                "TextSecondaryColorBrush", 14, centered: false);
            var tagsList = new ItemsControl
            {
                Margin = new Thickness(0, 0, 0, 4),
                ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
                ItemTemplate = new FuncDataTemplate<string>((tag, _) =>
                {
                    var chip = new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(8, 3),
                        Margin = new Thickness(0, 0, 4, 4),
                        BorderThickness = new Thickness(1),
                        Child = ThemedIconAndText("IconTag", tag ?? "", "AccentColorBrush", 10, centered: false)
                    };
                    ThemeBrushes.Bind(chip, Border.BackgroundProperty, "ItemHoverBrush");
                    ThemeBrushes.Bind(chip, Border.BorderBrushProperty, "BorderColorBrush");
                    return chip;
                })
            };
            tagsList.Bind(ItemsControl.ItemsSourceProperty, new Binding("SelectedInfobase.Tags"));
            var tagsBlock = new StackPanel { Spacing = 4 };
            tagsBlock.Children.Add(tagsHeader);
            tagsBlock.Children.Add(tagsList);

            // Информация о подключении.
            // Блок сведений подчинён переключателю подробностей правой панели,
            // как в WPF: там по нему прячется та же таблица.
            var connectionLabel = SectionLabel(LocalizationManager.T("Main.SectionConnInfo"));
            var connectionCard = PlainCard(
                DetailRow(LocalizationManager.T("Main.Type"), new Binding("SelectedInfobase.ConnectionTypeDisplay")),
                DetailRow(LocalizationManager.T("Main.ServerPath"), new Binding("SelectedInfobase.ConnectionPathDisplay")),
                DetailRow(LocalizationManager.T("Column.ServerBase"), new Binding("SelectedInfobase.ServerDatabaseDisplay")),
                DetailRow(LocalizationManager.T("Main.ConnectionString"), new Binding("SelectedInfobase.ConnectionStringDisplay")),
                DetailRow(LocalizationManager.T("Main.Platform"), new Binding("SelectedInfobase.PlatformVersion")),
                DetailRow(LocalizationManager.T("Main.LaunchMode"), new Binding("SelectedInfobase.ParsedLaunchMode")),
                DetailRow(LocalizationManager.T("Main.Client"), new Binding("SelectedInfobase.ClientTypeDisplay")),
                DetailRow(LocalizationManager.T("Main.Bitness"), new Binding("SelectedInfobase.ArchitectureDisplay")),
                DetailRow(LocalizationManager.T("Main.Parameters"), new Binding("SelectedInfobase.LaunchParameters")),
                DetailRow(LocalizationManager.T("Main.LastLaunch"), new Binding("SelectedInfobase.LastLaunchDisplay")),
                DetailRow(LocalizationManager.T("Main.CacheSize"), new Binding("SelectedInfobase.CacheSizeDisplay")),
                DetailRow(LocalizationManager.T("Column.Configuration"), new Binding("SelectedInfobase.ConfigurationDisplay")));
            connectionCard.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));

            // Блок «Текущая сессия»: значения действуют только на следующий запуск.
            var sessionCard = BuildSessionCard();

            // Описание (стиль из ConfigurationManagement): значение TextPrimaryBrush, нижний отступ.
            var desc = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = UiMetrics.ScaledFont(12),
                Margin = new Thickness(0, 0, 0, 10)
            };
            ThemeBrushes.Bind(desc, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            desc.Bind(TextBlock.TextProperty, new Binding("SelectedInfobase.Description"));
            var descriptionLabel = SectionLabel(LocalizationManager.T("Main.Description"), smallCaps: false);

            // Порядок блоков взят из разметки WPF: сведения о подключении, описание,
            // теги, затем действия и текущая сессия. Раньше действия стояли первыми.
            // Подписи секций подчинены тем же условиям, что и их содержимое:
            // иначе в компактной панели и без выбранной базы висели заголовки
            // без содержимого.
            badges.Bind(Control.IsVisibleProperty, new Binding("IsInfobaseSelected"));
            connectionLabel.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));
            descriptionLabel.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));
            desc.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));
            tagsBlock.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));

            panel.Children.Add(badges);
            panel.Children.Add(connectionLabel);
            panel.Children.Add(connectionCard);
            panel.Children.Add(descriptionLabel);
            panel.Children.Add(desc);
            panel.Children.Add(tagsBlock);

            // Линия и заголовок «Действия» перед кнопками запуска, как в разметке.
            // Обе видны только при показанных подробностях правой панели.
            var actionsSeparator = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 4, 0, 12)
            };
            ThemeBrushes.Bind(actionsSeparator, Border.BackgroundProperty, "BorderColorBrush");
            actionsSeparator.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
            panel.Children.Add(actionsSeparator);

            var actionsLabel = new TextBlock
            {
                Text = LocalizationManager.T("Main.Actions"),
                FontSize = UiMetrics.ScaledFont(12),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            ThemeBrushes.Bind(actionsLabel, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            actionsLabel.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
            panel.Children.Add(actionsLabel);

            panel.Children.Add(launchEnterpriseBlock);
            panel.Children.Add(launchConfiguratorBlock);
            panel.Children.Add(starterBlock);

            // Линия между запуском и остальными действиями (MainWindow.xaml:2097).
            var afterStarterSeparator = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 10, 0, 10),
                Opacity = 0.7
            };
            ThemeBrushes.Bind(afterStarterSeparator, Border.BackgroundProperty, "BorderColorBrush");
            panel.Children.Add(afterStarterSeparator);

            panel.Children.Add(sessionCard);

            // Линия между текущей сессией и переходом по ссылке видна вместе
            // с самой карточкой сессии (MainWindow.xaml:2191).
            var beforeByLinkSeparator = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 10) };
            ThemeBrushes.Bind(beforeByLinkSeparator, Border.BackgroundProperty, "BorderColorBrush");
            beforeByLinkSeparator.Bind(Control.IsVisibleProperty, new Binding("ShowSessionLaunchPanel"));
            panel.Children.Add(beforeByLinkSeparator);

            panel.Children.Add(byLinkBlock);

            // Линия между переходом по ссылке и выходом, как в разметке.
            var exitSeparator = new Border { Height = 1, Margin = new Thickness(0, 10) };
            ThemeBrushes.Bind(exitSeparator, Border.BackgroundProperty, "BorderColorBrush");
            panel.Children.Add(exitSeparator);

            // Выход — компактная кнопка внизу, без лишней «карточки».
            // «Выход» у автора красный, #DC2626 в обеих темах.
            var exitBtn = CompactActionButton("IconExitToApp", LocalizationManager.T("Main.Exit"), "ExitCommand",
                LocalizationManager.T("Main.ExitTooltip"), "#DC2626", "IconClose",
                iconSize: UiMetrics.Scaled(16), widePadding: new Thickness(14, 11), narrowPadding: new Thickness(6, 8));
            // Нижний отступ обязателен: без него последний элемент не попадает
            // в прокручиваемую высоту целиком и снизу остаётся видна только рамка.
            exitBtn.Margin = new Thickness(0, UiMetrics.ActionGridGap, 0, UiMetrics.ActionGridGap);
            panel.Children.Add(exitBtn);

            return panel;
        }

        /// <summary>
        /// Двухколоночная сетка компактных кнопок действий правой панели.
        /// Равномерно заполняет ширину и заметно экономит вертикальное место
        /// по сравнению со стеком полноширинных secondary-кнопок.
        /// </summary>
        private static Control BuildActionList(params Control[] buttons)
        {
            // Кнопки идут в один столбец, как в версии для Windows. Двухколоночная
            // раскладка экономила высоту, но подписи в неё не помещались ни при
            // какой ширине панели: при её пределе 340 на ячейку остаётся около 160
            // пикселей, а «Ярлык на рабочем столе» требует почти 190, и подпись
            // обрывалась на середине слова.
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = UiMetrics.ActionGridGap,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            foreach (var btn in buttons)
                stack.Children.Add(btn);

            return stack;
        }

        /// <summary>
        /// Компактная кнопка действия правой панели: иконка + текст, низкая высота,
        /// растягивается на всю ширину панели.
        /// </summary>
        /// <param name="colorHex">
        /// Явный цвет значка и подписи. У автора так покрашен «Выход»: #DC2626
        /// одинаково в обеих темах.
        /// </param>
        private static Control CompactActionButton(string iconKey, string text, string commandPath, string tooltip,
            string? colorHex = null, string? trailingIconKey = null, bool colorTextToo = true,
            double? iconSize = null, Thickness? widePadding = null, Thickness? narrowPadding = null)
        {
            var btn = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush",
                new CornerRadius(UiMetrics.RadiusMd))
            {
                Content = CompactIconAndText(iconKey, text, "ButtonTextBrush", colorHex: colorHex, trailingIconKey: trailingIconKey, colorTextToo: colorTextToo, iconSize: iconSize),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = UiMetrics.ActionButtonMinHeight,
                Padding = new Thickness(UiMetrics.ActionButtonPadH, UiMetrics.ActionButtonPadV),
                Margin = new Thickness(0)
            };
            BindActionPadding(btn, widePadding, narrowPadding);
            ToolTip.SetTip(btn, tooltip);
            btn.Bind(Button.CommandProperty, new Binding(commandPath));
            return btn;
        }

        /// <summary>
        /// Отступ кнопки действия меняется вместе с шириной правой панели, как
        /// в разметке: у перехода по ссылке и выхода 14 на 11 в полной панели
        /// и 6 на 8 в узкой (MainWindow.xaml:2205 и 2264).
        /// </summary>
        private static void BindActionPadding(Control button, Thickness? wide, Thickness? narrow)
        {
            if (wide is not { } w || narrow is not { } n)
                return;
            button.Bind(TemplatedControl.PaddingProperty, new Binding("ShowRightPanelDetails")
            {
                Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, Thickness>(v => v ? w : n)
            });
        }

        /// <summary>
        /// Вариант кнопки действия с подписью из привязки: нужен там, где текст
        /// меняется по состоянию, как короткая подпись открытия по ссылке.
        /// </summary>
        private static Control CompactActionButtonBound(string iconKey, string textPath, string commandPath, string tooltip,
            string? colorHex = null, string? trailingIconKey = null, bool colorTextToo = true,
            double? iconSize = null, Thickness? widePadding = null, Thickness? narrowPadding = null)
        {
            var btn = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush",
                new CornerRadius(UiMetrics.RadiusMd))
            {
                Content = CompactIconAndText(iconKey, "", "ButtonTextBrush", textPath, colorHex, trailingIconKey, colorTextToo, iconSize),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = UiMetrics.ActionButtonMinHeight,
                Padding = new Thickness(UiMetrics.ActionButtonPadH, UiMetrics.ActionButtonPadV),
                Margin = new Thickness(0)
            };
            BindActionPadding(btn, widePadding, narrowPadding);
            ToolTip.SetTip(btn, tooltip);
            btn.Bind(Button.CommandProperty, new Binding(commandPath));
            return btn;
        }

        private static Control CompactIconAndText(string iconKey, string text, string brushKey, string? textPath = null,
            string? colorHex = null, string? trailingIconKey = null, bool colorTextToo = true, double? iconSize = null)
        {
            // Сеткой, а не горизонтальной панелью: панель меряет подпись
            // бесконечной шириной, поэтому обрезка многоточием не срабатывает
            // и длинный текст вылезает за кнопку вместо того, чтобы сократиться.
            var sp = new Grid
            {
                ColumnSpacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                // Растягиваем на ширину кнопки, иначе хвостовой значок липнет
                // к подписи, а у автора он прижат к правому краю.
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (trailingIconKey is not null)
                sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (colorHex is null)
            {
                sp.Children.Add(IconHelper.MakeIcon(iconKey, iconSize ?? UiMetrics.ActionIconSize, brushKey));
            }
            else
            {
                sp.Children.Add(IconHelper.MakeIcon(iconKey, iconSize ?? UiMetrics.ActionIconSize,
                    new SolidColorBrush(Color.Parse(colorHex))));
            }
            var tb = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.ActionFontSize,
                FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            // У автора цвет значка и подписи совпадает не всегда: у выхода
            // красное и то и другое, у перехода по ссылке значок голубой,
            // а подпись обычная.
            if (colorHex is null || !colorTextToo)
                ThemeBrushes.Bind(tb, TextBlock.ForegroundProperty, brushKey);
            else
                tb.Foreground = new SolidColorBrush(Color.Parse(colorHex));
            if (textPath is not null)
                tb.Bind(TextBlock.TextProperty, new Binding(textPath));
            Grid.SetColumn(tb, 1);
            sp.Children.Add(tb);
            if (trailingIconKey is not null)
            {
                // Хвостовой значок справа, как в разметке: стрелка у перехода
                // по ссылке и крестик у выхода.
                // Цвет и прозрачность как в разметке, и прячется вместе
                // с подробностями правой панели, а не висит всегда.
                var trailingHost = IconHelper.MakeIcon(trailingIconKey, 12, out var trailing);
                trailingHost.Opacity = 0.75;
                if (colorTextToo && colorHex is not null)
                    trailing.Fill = new SolidColorBrush(Color.Parse(colorHex));
                else
                    ThemeBrushes.Bind(trailing, Avalonia.Controls.Shapes.Path.FillProperty, "ButtonTextBrush");
                trailingHost.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
                Grid.SetColumn(trailingHost, 2);
                sp.Children.Add(trailingHost);
            }
            return sp;
        }

        /// <summary>
        /// Блок «Текущая сессия»: режим клиента и разрядность только для
        /// следующего запуска, сохранённые настройки базы он не меняет.
        /// Видимостью управляет настройка, как и в WPF-версии.
        /// </summary>
        private Control BuildSessionCard()
        {
            var hint = new TextBlock
            {
                Text = LocalizationManager.T("Main.SessionOnceHint"),
                FontSize = UiMetrics.ScaledFont(11),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            ThemeBrushes.Bind(hint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            ToolTip.SetTip(hint, LocalizationManager.T("Main.CurrentSessionHelp"));
            // Подсказка и подписи групп скрыты в узкой панели, как в разметке
            // (MainWindow.xaml:2137 и 2170): без этого она выходила заметно выше.
            hint.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));

            // Переключатели идут вплотную, как у автора: карточка добавляет свой
            // интервал между каждым дочерним элементом, и от этого строки
            // расходились. Внутри контейнера интервала нет, работают только
            // собственные отступы переключателей.
            var options = new StackPanel { Spacing = 0 };
            // Кружки мельче штатных Fluent, как у автора. Части шаблона
            // адресуются по именам: общий OfType<Ellipse> сделал бы одинаковыми
            // все три окружности и раздул бы внутреннюю точку.
            foreach (var (part, size) in new[] { ("OuterEllipse", 14d), ("CheckOuterEllipse", 14d), ("CheckGlyph", 6d) })
            {
                options.Styles.Add(new Style(x => x.OfType<RadioButton>().Class("compactRadio")
                    .Template().Name(part))
                {
                    Setters =
                    {
                        new Setter(Layoutable.WidthProperty, size),
                        new Setter(Layoutable.HeightProperty, size)
                    }
                });
            }
            // Внутри шаблона Fluent обойма кружков задана фиксированной высотой,
            // и одного MinHeight на самой кнопке мало: строка оставалась 32
            // пикселя против плотных строк версии для Windows.
            options.Styles.Add(new Style(x => x.OfType<RadioButton>().Class("compactRadio")
                .Template().OfType<Grid>())
            {
                Setters = { new Setter(Layoutable.HeightProperty, UiMetrics.Scaled(22)) }
            });

            void AddOption(params Control[] items)
            {
                foreach (var item in items)
                    options.Children.Add(item);
            }

            var clientOptions = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 8)
            };
            clientOptions.Children.Add(
                SessionOption(LocalizationManager.T("Main.SessionClientAuto"), "SessionClient", "IsSessionClientAuto"));
            clientOptions.Children.Add(
                SessionOption(LocalizationManager.T("Main.SessionClientOrdinary"), "SessionClient", "IsSessionClientOrdinary"));
            clientOptions.Children.Add(
                SessionOption(LocalizationManager.T("Main.SessionClientThickManaged"), "SessionClient", "IsSessionClientThick",
                    LocalizationManager.T("Main.SessionThickManagedTooltip")));
            clientOptions.Children.Add(
                SessionOption(LocalizationManager.T("Main.SessionClientThin"), "SessionClient", "IsSessionClientThin"));

            var archOptions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    SessionOption(LocalizationManager.T("Main.SessionClientAuto"), "SessionArch", "IsSessionArchAuto",
                        margin: new Thickness(0, 2, 10, 2)),
                    SessionOption("32", "SessionArch", "IsSessionArch32",
                        margin: new Thickness(0, 2, 10, 2)),
                    SessionOption("64", "SessionArch", "IsSessionArch64")
                }
            };

            AddOption(
                SessionGroupLabel(LocalizationManager.T("Main.ClientMode")),
                clientOptions,
                SessionGroupLabel(LocalizationManager.T("Main.Bitness")),
                archOptions);

            var card = SectionCard(LocalizationManager.T("Main.CurrentSession"), "Main.CurrentSessionHelp",
                hint,
                options);

            card.Bind(Control.IsVisibleProperty, new Binding("ShowSessionLaunchPanel"));
            return card;
        }

        /// <summary>Подпись группы переключателей в блоке текущей сессии.</summary>
        private Control SessionGroupLabel(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.ScaledFont(11),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            block.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
            return block;
        }

        /// <summary>Переключатель в блоке текущей сессии: одна из взаимоисключающих опций.</summary>
        private static Control SessionOption(string text, string group, string propertyPath, string? tooltip = null,
            Thickness? margin = null)
        {
            var option = new RadioButton
            {
                Content = text,
                GroupName = group,
                FontSize = UiMetrics.ScaledFont(12),
                // Штатная строка Fluent высотой 32 растягивала список вдвое
                // против версии для Windows: там строки идут вплотную.
                MinHeight = UiMetrics.Scaled(22),
                Padding = new Thickness(6, 0, 0, 0),
                Margin = margin ?? new Thickness(0, 2)
            };
            // Класс нужен не для оформления, а чтобы поднять приоритет сеттеров:
            // Fluent задаёт размеры частей шаблона приоритетом Template, который
            // старше безусловного стиля. Условный селектор становится
            // StyleTrigger и Template перебивает.
            option.Classes.Add("compactRadio");
            option.Bind(RadioButton.IsCheckedProperty, new Binding(propertyPath) { Mode = BindingMode.TwoWay });
            if (tooltip is not null)
                ToolTip.SetTip(option, tooltip);
            return option;
        }

        /// <summary>Ключ значка в геометрию из Icons.axaml для привязок заголовка.</summary>
        private static readonly Avalonia.Data.Converters.FuncValueConverter<string?, Geometry?> IconKeyConverter =
            new(key => string.IsNullOrEmpty(key) ? null : IconHelper.Geometry(key));

        /// <summary>Рамка бейджа правой панели с явным фоном, как в разметке WPF.</summary>
        private static Border Badge(string backHex) =>
            new()
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3),
                Margin = new Thickness(0, 0, 6, 4),
                Background = new SolidColorBrush(Color.Parse(backHex))
            };

        /// <summary>Содержимое бейджа: значок и подпись из привязки.</summary>
        private static Control BadgeContent(string iconKey, string iconHex, string? textHex = null,
            IBinding? textBinding = null, string? themeBrushKey = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(IconHelper.MakeIcon(iconKey, 12, new SolidColorBrush(Color.Parse(iconHex))));
            var text = new TextBlock
            {
                FontSize = 11,
                // У автора подпись бейджа закрепления обычного начертания.
                FontWeight = themeBrushKey is null ? FontWeight.Normal : FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (themeBrushKey is not null)
                ThemeBrushes.Bind(text, TextBlock.ForegroundProperty, themeBrushKey);
            else
                text.Foreground = new SolidColorBrush(Color.Parse(textHex ?? "#000000"));
            if (textBinding is not null)
                text.Bind(TextBlock.TextProperty, textBinding);
            row.Children.Add(text);
            return row;
        }

        private static Control SectionLabel(string text, bool smallCaps = true)
        {
            // У автора подпись набрана малыми капителями (Typography.Capitals).
            // В Avalonia 11.3 такого свойства у TextBlock нет, поэтому приближаем:
            // прописные буквы кеглем помельче дают тот же рисунок строки.
            var block = new TextBlock
            {
                Text = smallCaps ? text.ToUpperInvariant() : text,
                FontSize = UiMetrics.ScaledFont(smallCaps ? 10.5 : 12),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, UiMetrics.ActionGridGap, 0, smallCaps ? 8 : 4)
            };
            ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            return block;
        }

        /// <summary>Рамка секции без собственного заголовка: подпись живёт снаружи.</summary>
        private static Control PlainCard(params Control[] children)
        {
            // Числа из разметки (MainWindow.xaml:1756): скругление 12,
            // отступ 12 на 10, нижнее поле 14.
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10),
                Margin = new Thickness(0, 0, 0, 14),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            // У автора панель светлее карточки, а не наоборот: панель
            // CardBackgroundBrush, карточка ContentBackgroundBrush.
            ThemeBrushes.Bind(card, Border.BackgroundProperty, "ContentBackgroundColorBrush");
            ThemeBrushes.Bind(card, Border.BorderBrushProperty, "BorderColorBrush");
            UiMetrics.AddBrushTransition(card);
            var content = new StackPanel { Spacing = UiMetrics.Gap };
            foreach (var child in children)
                content.Children.Add(child);
            card.Child = content;
            return card;
        }

        /// <summary>
        /// Карточка-секция с заголовком и значком внутри рамки. Осталась только
        /// у карточки текущей сессии: у остальных секций подпись вынесена наружу.
        /// </summary>
        private Control SectionCard(string title, string helpKey, params Control[] children)
        {
            // Числа из разметки (MainWindow.xaml:2101-2113): скругление 8, фон
            // ItemHover, отступ 10 на 8 и 6 на 6 в узкой панели, поле 8,0,8,10.
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(8, 0, 8, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _sessionCard = card;
            ThemeBrushes.Bind(card, Border.BackgroundProperty, "ItemHoverBrush");
            ThemeBrushes.Bind(card, Border.BorderBrushProperty, "BorderColorBrush");
            UiMetrics.AddBrushTransition(card);

            var content = new StackPanel { Spacing = 0 };

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6
            };
            // Значка у заголовка в разметке нет, зато есть кнопка справки
            // рядом с подписью (MainWindow.xaml:2118-2136). Подпись основным
            // цветом, а не вторичным.
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = UiMetrics.ScaledFont(12),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            _sessionTitleBlock = titleBlock;
            ThemeBrushes.Bind(titleBlock, TextBlock.ForegroundProperty, "TextPrimaryColorBrush");
            header.Children.Add(titleBlock);
            if (!string.IsNullOrEmpty(helpKey))
                header.Children.Add(new Controls.HelpLink
                {
                    Margin = new Thickness(6, 0, 0, 4),
                    HelpText = LocalizationManager.T(helpKey)
                });
            content.Children.Add(header);

            foreach (var child in children)
                content.Children.Add(child);

            card.Child = content;
            return card;
        }

        /// <summary>Крупная primary-кнопка на акцентном фоне с контрастным текстом/иконкой.</summary>
        private static Control PrimaryActionButton(string iconKey, string text, string commandPath, string tooltip)
        {
            var btn = new PanelButton("AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentBrush")
            {
                Content = ThemedIconAndText(iconKey, text, "TextOnAccentBrush", UiMetrics.ScaledFont(18), centered: true),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, UiMetrics.SectionMarginBottom),
                Padding = new Thickness(UiMetrics.ButtonPadH, UiMetrics.ButtonPadV)
            };
            // В Avalonia подсказка это присоединённое свойство, а не свойство контрола.
            ToolTip.SetTip(btn, tooltip);
            btn.Bind(Button.CommandProperty, new Binding(commandPath));
            return btn;
        }

        /// <summary>Secondary-кнопка с приглушённым фоном и hover/pressed из ресурсов темы.</summary>
        private static Control SecondaryActionButton(string iconKey, string text, string commandPath, string tooltip)
        {
            var btn = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush",
                new CornerRadius(UiMetrics.RadiusMd))
            {
                Content = ThemedIconAndText(iconKey, text, "ButtonTextBrush", UiMetrics.ActionIconSize, centered: false),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = UiMetrics.ActionButtonMinHeight,
                Padding = new Thickness(UiMetrics.ActionButtonPadH, UiMetrics.ActionButtonPadV),
                Margin = new Thickness(0, 0, 0, UiMetrics.ActionGridGap / 2)
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Bind(Button.CommandProperty, new Binding(commandPath));
            return btn;
        }

        /// <summary>
        /// Кнопка запуска со стрелкой и меню дополнительных вариантов, как
        /// в WPF-версии. Пункт «от имени администратора» не переносится:
        /// на Linux нет повышения прав через оболочку, параметр runAsAdmin
        /// в лаунчере не используется, а запуск клиента 1С от root оставил бы
        /// в домашнем каталоге пользователя файлы, которые ему не принадлежат.
        /// </summary>
        private static Control BuildLaunchSplitButton(
            string iconKey,
            string text,
            string commandPath,
            string tooltip,
            bool primary,
            IReadOnlyList<(string Header, string Command, string? IconKey, string? IconColor)> menuItems)
        {
            var radius = UiMetrics.RadiusLg;
            var mainCorner = new CornerRadius(radius, 0, 0, radius);

            // Вторичная кнопка запуска у автора прозрачная с рамкой, а не залитая:
            // залит только первичный запуск, а кремовым остаётся штатный стартер.
            var main = primary
                ? new PanelButton("AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentBrush", mainCorner)
                : new PanelButton("", "ItemHoverBrush",
                    "SecondaryButtonPressedBrush", "BorderColorBrush", mainCorner);

            // У вторичной кнопки подпись и значок берут основной цвет текста,
            // как в разметке: ButtonTextBrush чёрный в обеих темах и на прозрачной
            // кнопке тёмной темы не читается.
            var contentBrush = primary ? "TextOnAccentBrush" : "TextPrimaryColorBrush";
            // Числа из разметки (MainWindow.xaml:1904): содержимое прижато влево,
            // отступ 8,8,4,8, минимальная высота 34, значок 16, подпись 12.
            main.Content = ThemedIconAndText(iconKey, text, contentBrush, UiMetrics.Scaled(16), centered: false);
            main.HorizontalContentAlignment = HorizontalAlignment.Left;
            main.HorizontalAlignment = HorizontalAlignment.Stretch;
            main.MinHeight = UiMetrics.Scaled(34);
            main.Padding = new Thickness(UiMetrics.Scaled(8), UiMetrics.Scaled(8), UiMetrics.Scaled(4), UiMetrics.Scaled(8));
            main.Margin = new Thickness(0);
            ToolTip.SetTip(main, tooltip);
            main.Bind(Button.CommandProperty, new Binding(commandPath));

            var menu = new ContextMenu().Styled(Themes.ControlThemes.ModernContextMenu);
            foreach (var (header, command, itemIcon, itemColor) in menuItems)
            {
                if (header.Length == 0)
                {
                    menu.Items.Add(MenuSeparator());
                    continue;
                }

                // Значки пунктов заданы в разметке явным цветом
                // (MainWindow.xaml:1954 и 1962): настройка параметров голубая,
                // запуск с авторизацией фиолетовый.
                var item = new MenuItem { Header = header };
                item.Styled(Themes.ControlThemes.ModernMenuItem);
                if (itemIcon is not null)
                    item.Icon = IconHelper.MakeIcon(itemIcon, 18,
                        itemColor is not null ? new SolidColorBrush(Color.Parse(itemColor)) : Brushes.Gray);
                item.Bind(MenuItem.CommandProperty, new Binding(command));
                menu.Items.Add(item);
            }

            var arrowCorner = new CornerRadius(0, radius, radius, 0);
            var arrow = primary
                ? new PanelButton("AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentBrush", arrowCorner)
                // Стрелка вторичной кнопки прозрачная, как и её основная часть.
                : new PanelButton("", "ItemHoverBrush",
                    "SecondaryButtonPressedBrush", "BorderColorBrush", arrowCorner);
            arrow.Width = UiMetrics.Scaled(28);
            arrow.MinHeight = main.MinHeight;
            arrow.Padding = new Thickness(0);
            arrow.Margin = new Thickness(0);
            // Стрелка в разметке это контур ChevronDown 16, а не текстовый знак.
            arrow.Content = IconHelper.MakeIcon("IconChevronDown", UiMetrics.Scaled(16), contentBrush);
            ToolTip.SetTip(arrow, LocalizationManager.T("Main.MoreLaunchOptions"));
            arrow.ContextMenu = menu;
            arrow.Click += (_, _) => menu.Open(arrow);

            // Между частями в разметке стоит линия шириной 1 с полем 0,8:
            // у первичной кнопки полупрозрачная белая, у вторичной цвет рамки.
            var divider = new Border { Width = 1, Margin = new Thickness(0, 8) };
            if (primary)
                divider.Background = new SolidColorBrush(Color.Parse("#55FFFFFF"));
            else
                ThemeBrushes.Bind(divider, Border.BackgroundProperty, "BorderColorBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(main, 0);
            Grid.SetColumn(divider, 1);
            Grid.SetColumn(arrow, 2);
            grid.Children.Add(main);
            grid.Children.Add(divider);
            grid.Children.Add(arrow);
            return grid;
        }

        /// <summary>Содержимое кнопки: иконка + подпись, окрашенные кистью ресурса темы.</summary>
        private static Control ThemedIconAndText(string iconKey, string text, string brushKey, double iconSize, bool centered,
            double? fontSize = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (centered)
                sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(IconHelper.MakeIcon(iconKey, iconSize, brushKey));
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize ?? UiMetrics.ActionFontSize + (centered ? 0.5 : 0),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ThemeBrushes.Bind(tb, TextBlock.ForegroundProperty, brushKey);
            sp.Children.Add(tb);
            return sp;
        }

        private Control DetailRow(string label, Binding binding)
        {
            // Отступ между строками меньше восьми из разметки: у системного шрифта
            // строка выше, чем у Segoe UI, и при восьми карточка выходила заметно
            // разреженнее версии для Windows при том же кегле.
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Высота строки задана явно: без неё шаг строк определяют метрики
            // системного шрифта, и карточка выходила заметно разреженнее, чем
            // в версии для Windows при том же кегле и тех же отступах.
            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = UiMetrics.ScaledFont(12),
                LineHeight = UiMetrics.ScaledFont(16)
            };
            ThemeBrushes.Bind(labelBlock, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            grid.Children.Add(labelBlock);
            Grid.SetColumn(labelBlock, 0);

            // Значение полужирное, как в разметке WPF: подпись вторичная, значение основное.
            var valueBlock = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(12),
                LineHeight = UiMetrics.ScaledFont(16),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            valueBlock.Bind(TextBlock.TextProperty, binding);
            grid.Children.Add(valueBlock);
            Grid.SetColumn(valueBlock, 1);
            return grid;
        }

        /// <summary>
        /// Подменю «Утилиты» общей панели (issue #262): глобальные команды, не привязанные
        /// к конкретной базе. Раньше они жили в контекстном меню строки базы; теперь собраны
        /// в общее подменю, куда в будущем можно добавлять новые общие команды.
        /// Сюда же перенесена «Консоль администрирования серверов» из контекстного меню
        /// базы (issue #287): она не связана с конкретной базой и стоит сразу после
        /// «Списка типовых конфигураций», между разделителями.
        /// </summary>
        private ContextMenu BuildUtilitiesMenu()
        {
            var menu = new ContextMenu().Styled(Themes.ControlThemes.ModernContextMenu);
            if (_vm is null)
                return menu;

            // Общие команды, перенесённые из контекстного меню базы.
            menu.Items.Add(MenuAction("Updates.ActualReleasesTitle", _vm.ShowActualReleasesCommand, _vm.HotkeyActualReleases, "IconCloudDownload", "#14B8A6"));

            var manageItem = new MenuItem { Header = LocalizationManager.T("Updates.ManageList") };
            manageItem.Styled(Themes.ControlThemes.ModernMenuItem);
            manageItem.Click += (_, _) => _vm.OpenConfigTypesEdit();
            menu.Items.Add(manageItem);

            // Консоль администрирования серверов 1С не связана с конкретной базой, поэтому
            // перенесена из контекстного меню базы в «Утилиты», сразу после «Списка типовых
            // конфигураций» и с разделителями вокруг (issue #287).
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(MenuAction("Admin.ServerConsole", _vm.OpenServerConsoleCommand, _vm.HotkeyServerConsole, "IconServer", "#14B8A6"));
            menu.Items.Add(MenuSeparator());

            menu.Items.Add(MenuAction("AppLock.LockTitle", _vm.LockAppCommand, _vm.HotkeyLockApp, "IconExitToApp", "#8B5CF6", "AppLock.MenuTooltip"));
            menu.Items.Add(MenuAction("SessionLock.Title", _vm.ShowSessionLockCommand, _vm.HotkeySessionLock, "IconRights", "#EF4444"));

            // Обслуживание списка и приложения (issue #279): удаление отсутствующих
            // файловых баз, завершение процессов платформы и проверка обновлений самого
            // приложения. В WPF те же пункты в том же порядке.
            menu.Items.Add(MenuSeparator());

            var removeMissingItem = new MenuItem { Header = LocalizationManager.T("Settings.Bases.RemoveMissing") };
            removeMissingItem.Styled(Themes.ControlThemes.ModernMenuItem);
            removeMissingItem.Icon = MenuIcon("IconFolderRemove", "#EF4444");
            removeMissingItem.Click += (_, _) => _vm.RemoveMissingFileBases();
            menu.Items.Add(removeMissingItem);

            var killProcessesItem = new MenuItem { Header = LocalizationManager.T("Settings.Bases.KillProcesses") };
            killProcessesItem.Styled(Themes.ControlThemes.ModernMenuItem);
            killProcessesItem.Icon = MenuIcon("IconClose", "#F59E0B");
            killProcessesItem.Click += (_, _) => _vm.KillOneCProcesses();
            menu.Items.Add(killProcessesItem);

            menu.Items.Add(MenuSeparator());

            // Задания по расписанию (issue #286): резервная копия, обновление конфигурации
            // ИБ, «копия → обновление», обновление приложения. Открывается отложенно,
            // чтобы попап меню успел закрыться (та же проблема, что в issue #288).
            var scheduleItem = new MenuItem { Header = LocalizationManager.T("Schedule.Title") };
            scheduleItem.Styled(Themes.ControlThemes.ModernMenuItem);
            scheduleItem.Icon = MenuIcon("IconScheduler", "#14B8A6");
            scheduleItem.Click += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var win = new ScheduledTasksWindow();
                win.ShowSync(this);
            });
            menu.Items.Add(scheduleItem);

            // «Сценарии резервирования» и «Выполнить резервирование» не привязаны к конкретной
            // базе, поэтому перенесены из контекстного меню базы в «Утилиты» (issue #293).
            menu.Items.Add(MenuAction("Backup.ScenariosTitle", _vm.ShowBackupScenariosCommand, null, "IconSettings", "#F59E0B"));
            menu.Items.Add(MenuAction("Backup.RunTitle", _vm.RunBackupScenarioCommand, _vm.HotkeyRunBackup, "IconDatabaseExport", "#22C55E"));

            menu.Items.Add(MenuSeparator());

            var checkUpdatesItem = new MenuItem { Header = LocalizationManager.T("Settings.About.CheckForUpdates") };
            checkUpdatesItem.Styled(Themes.ControlThemes.ModernMenuItem);
            // Значок как у кнопки проверки обновлений в окне настроек (возле версии): Update/#3B82F6 (issue #282).
            checkUpdatesItem.Icon = MenuIcon("IconUpdate", "#3B82F6");
            checkUpdatesItem.Click += async (_, _) =>
            {
                try
                {
                    var updateService = AppServices.GetRequiredService<UpdateService>();
                    await updateService.CheckForUpdatesManualAsync();
                }
                catch
                {
                    // Внутренние ошибки уже показаны в UpdateService; здесь только страхуемся.
                }
            };
            menu.Items.Add(checkUpdatesItem);

            return menu;
        }

        private ContextMenu BuildRowContextMenu()
        {
            var menu = new ContextMenu().Styled(Themes.ControlThemes.ModernContextMenu);
            if (_vm is null)
                return menu;

            // Контекстное меню строки базы перестроено в подменю (issue #287): чтобы
            // уменьшить высоту меню, группы команд свернуты в подменю «Обновление и связь»,
            // «Администрирование», «Резервирование» и «Очистить кэш». Хоткеи (issue #290)
            // сохранены на прежних пунктах.
            menu.Items.Add(MenuAction("Main.LaunchEnterprise", _vm.LaunchEnterpriseCommand, _vm.HotkeyEnterprise, "IconPlay", "#22C55E"));
            menu.Items.Add(MenuAction("Main.LaunchConfigurator", _vm.LaunchConfiguratorCommand, _vm.HotkeyConfigurator, "IconSettings", "#3B82F6"));
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(MenuAction("Main.ToFavorites", _vm.ToggleFavoriteCommand, _vm.HotkeyFavorite, "IconStar", "#FBBF24"));
            menu.Items.Add(MenuAction("Main.Pin", _vm.TogglePinCommand, _vm.HotkeyPin, "IconPin", "#8B5CF6"));
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(MenuAction("Main.AddBase", _vm.AddInfobaseCommand, _vm.HotkeyAdd, "IconAdd", "#22C55E"));
            menu.Items.Add(MenuSeparator());

            // Подменю «Обновление и связь»: обновление информации о конфигурации, проверка
            // обновлений и привязка базы к конфигурации (issue #287).
            var updateMenu = new MenuItem
            {
                Header = LocalizationManager.T("Menu.UpdateAndLink"),
                Icon = MenuIcon("IconRefresh", "#14B8A6")
            };
            updateMenu.Styled(Themes.ControlThemes.ModernMenuItem);
            updateMenu.Items.Add(MenuAction("Main.RefreshConfigInfo", _vm.RefreshConfigurationInfoCommand, null, "IconCloudDownload", "#14B8A6"));
            // Проверка обновлений конфигураций 1С (функции №21/№22): F9 — для выбранной
            // ИБ, ALT+F9 — окно «Актуальные релизы». Сочетания показываются из настроек.
            updateMenu.Items.Add(MenuAction("Updates.CheckTitle", _vm.CheckUpdateCommand, _vm.HotkeyCheckUpdate, "IconCloudDownload", "#14B8A6"));
            updateMenu.Items.Add(MenuSeparator());
            // «Актуальные релизы» перенесено в общее подменю «Утилиты» верхней панели (issue #262).
            var linkItem = new MenuItem { Header = LocalizationManager.T("Updates.ConfigLink") };
            linkItem.Styled(Themes.ControlThemes.ModernMenuItem);
            linkItem.Click += (_, _) =>
            {
                if (_vm.SelectedInfobase is not null)
                    _vm.OpenConfigUpdateLink(_vm.SelectedInfobase);
            };
            updateMenu.Items.Add(linkItem);
            menu.Items.Add(updateMenu);

            // Подменю «Администрирование»: блокировки, каталог и проверка целостности (issue #287).
            // «Зарегистрировать COM-коннектор» и «История запусков» здесь нет намеренно:
            // внешнее соединение это COM, в Linux регистрировать нечего, а история запусков
            // ждёт порта сервисов запуска.
            var adminMenu = new MenuItem
            {
                Header = LocalizationManager.T("Menu.Administration"),
                Icon = MenuIcon("IconServer", "#EF4444")
            };
            adminMenu.Styled(Themes.ControlThemes.ModernMenuItem);
            // Блокировка сеансов файловой ИБ (функция №20, Ctrl+Alt+L) и временная блокировка приложения (функция №19).
            adminMenu.Items.Add(MenuAction("SessionLock.Title", _vm.ShowSessionLockCommand, _vm.HotkeySessionLock, "IconRights", "#EF4444"));
            adminMenu.Items.Add(MenuAction("AppLock.LockTitle", _vm.LockAppCommand, _vm.HotkeyLockApp, "IconExitToApp", "#8B5CF6", "AppLock.MenuTooltip"));
            adminMenu.Items.Add(MenuSeparator());
            adminMenu.Items.Add(MenuAction("Main.OpenCatalog", _vm.OpenInfobaseFolderCommand, null, "IconFolderOpen", "#0EA5E9"));
            // Администрирование ИБ (Этап 6, функция №29): проверка целостности файловой ИБ
            // (chdbfl). Консоль серверов перенесена в «Утилиты» (issue #287).
            adminMenu.Items.Add(MenuAction("Admin.CheckIntegrity", _vm.CheckIntegrityCommand, _vm.HotkeyCheckIntegrity, "IconDatabase", "#10B981"));
            menu.Items.Add(adminMenu);

            // Подменю «Резервирование»: выгрузки и список выгрузок (issue #287).
            // «Сценарии резервирования» и «Выполнить резервирование» перенесены в общее
            // меню «Утилиты» (issue #293); «Список выгрузок» (функция №18, Ctrl+Shift+F7)
            // остаётся здесь.
            var backupMenu = new MenuItem
            {
                Header = LocalizationManager.T("Menu.Backup"),
                Icon = MenuIcon("IconDatabaseExport", "#0EA5E9")
            };
            backupMenu.Styled(Themes.ControlThemes.ModernMenuItem);
            backupMenu.Items.Add(MenuAction("Main.DumpToDt", _vm.DumpInfobaseDtCommand, null, "IconDatabaseExport", "#0EA5E9"));
            backupMenu.Items.Add(MenuAction("Main.DumpConfigToCf", _vm.DumpConfigurationCfCommand, null, "IconFileExport", "#3B82F6"));
            backupMenu.Items.Add(MenuSeparator());
            backupMenu.Items.Add(MenuAction("Restore.Title", _vm.ShowExportsListCommand, _vm.HotkeyExportsList, null, null));
            menu.Items.Add(backupMenu);

            // Подменю «Очистить кэш»: три варианта очистки. Сочетание Ctrl+Shift+C (открытие
            // окна очистки обоих кешей) показано на пункте «Программный и пользовательский».
            var cacheMenu = new MenuItem
            {
                Header = LocalizationManager.T("Main.ClearCache"),
                Icon = MenuIcon("IconBroom", "#14B8A6")
            };
            cacheMenu.Styled(Themes.ControlThemes.ModernMenuItem);
            cacheMenu.Items.Add(MenuAction("Main.ClearProgramCache", _vm.ClearProgramCacheCommand));
            cacheMenu.Items.Add(MenuAction("Main.ClearUserCache", _vm.ClearUserCacheCommand));
            cacheMenu.Items.Add(MenuSeparator());
            cacheMenu.Items.Add(MenuAction("Main.ClearCacheBoth", _vm.ClearCacheBothCommand, _vm.HotkeyClearCache));
            menu.Items.Add(cacheMenu);

            // «Удалить» отделён разделителями сверху и снизу, как в issue #242 (соответствует WPF).
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(MenuAction("Main.Delete", _vm.DeleteInfobaseCommand, _vm.HotkeyDelete, "IconDelete", "#EF4444"));
            menu.Items.Add(MenuSeparator());
            // «Найти в списке»: переход к базе в общем списке «Все базы» (issue #285).
            menu.Items.Add(MenuAction("Main.FindInList", _vm.FindInListCommand, _vm.HotkeyFindInList, "IconSearch", "#3B82F6"));
            menu.Items.Add(MenuAction("Main.DesktopShortcut", _vm.CreateDesktopShortcutCommand, null, "IconDesktopClassic", "#6366F1"));
            menu.Items.Add(MenuAction("Main.CopyConnectionString", _vm.CopyConnectionStringCommand, null, "IconCopy", "#06B6D4"));
            menu.Items.Add(MenuSeparator());
            // «Изменить настройки» (аналог «Свойств» в ОС) размещён внизу меню,
            // после разделителя, как в issue #242 (соответствует WPF).
            menu.Items.Add(MenuAction("Main.EditSettings", _vm.EditInfobaseCommand, _vm.HotkeyEdit, "IconEdit", "#3B82F6"));
            return menu;
        }

        /// <summary>
        /// Значок пункта меню. Цвет задаётся явно, а не ресурсом темы: в разметке
        /// WPF у каждого пункта свой цвет, и он один и тот же в светлой и тёмной.
        /// </summary>
        private static Control MenuIcon(string iconKey, string colorHex)
            => IconHelper.MakeIcon(iconKey, 16, new SolidColorBrush(Color.Parse(colorHex)));

        /// <summary>Пункт меню с подписью из словаря, командой и подсказкой сочетания клавиш.</summary>
        private static MenuItem MenuAction(string textKey, System.Windows.Input.ICommand command, string? gesture = null,
            string? iconKey = null, string? iconColor = null, string? tooltipKey = null)
        {
            var item = new MenuItem
            {
                Header = LocalizationManager.T(textKey),
                Command = command
            };
            item.Styled(Themes.ControlThemes.ModernMenuItem);
            if (iconKey is not null && iconColor is not null)
                item.Icon = MenuIcon(iconKey, iconColor);
            if (Controls.HotkeyBox.TryParse(gesture, out var parsed) && parsed is not null)
                item.InputGesture = parsed;
            if (tooltipKey is not null)
                ToolTip.SetTip(item, LocalizationManager.T(tooltipKey));
            return item;
        }

        /// <summary>
        /// Разделитель пунктов меню: тонкая линия с минимальным вертикальным отступом.
        /// Значения задаются на самом элементе, а не стилем темы: у значения из шаблона
        /// Fluent приоритет выше, чем у именованного стиля (issue #287 — компактнее меню).
        /// </summary>
        private static Separator MenuSeparator() => new()
        {
            Height = 1,
            Margin = new Thickness(4, 2)
        };
    }
}
#endif