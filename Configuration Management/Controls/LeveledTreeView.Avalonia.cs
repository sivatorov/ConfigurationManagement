#if LINUX
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Configuration_Management.Models;
using Configuration_Management.ViewModels;

namespace Configuration_Management.Controls
{
    /// <summary>
    /// Avalonia-версия TreeView для дерева баз. Контейнеры строк — <see cref="LeveledTreeViewItem"/>,
    /// а вложенность и сдвиг уровней обеспечивает штатный механизм TreeView. Ручное вычисление
    /// уровня (присоединённое свойство Level), унаследованное от WPF, здесь не требуется и удалено.
    /// </summary>
    public class LeveledTreeView : TreeView
    {
        /// <summary>
        /// Тема оформления ищется по типу контрола, а для наследника её в Fluent нет:
        /// без этого шаблон не находится и контрол не отрисовывается вовсе.
        /// </summary>
        protected override Type StyleKeyOverride => typeof(TreeView);

        public LeveledTreeView()
        {
            AddHandler(KeyDownEvent, OnNavigationKeyDown, RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// Строка, которой выделение поставлено напрямую через <see cref="SelectRow"/>
        /// (локальное значение IsSelected). Локальное значение перекрывает штатную
        /// разметку выделения Avalonia (SetCurrentValue), поэтому погасить подсветку
        /// может только явный сброс здесь же, а не разметка данных.
        /// </summary>
        private TreeViewItem? _selectedRow;

        protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new LeveledTreeViewItem();

        // Контейнеры переиспользуются, поэтому прежняя привязка освобождается:
        // иначе на одном контейнере копились бы выражения привязки.
        private readonly ConditionalWeakTable<Control, IDisposable> _expandedBindings = new();

        /// <summary>
        /// Связывает раскрытие контейнера с моделью узла. Делается здесь, а не по
        /// событию ContainerPrepared у дерева: вложенные контейнеры готовит
        /// родительский TreeViewItem, и его событие до дерева не доходит, поэтому
        /// подгруппы оставались свёрнутыми независимо от модели. Подготовку своих
        /// детей TreeViewItem перенаправляет сюда же.
        /// </summary>
        protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
        {
            base.PrepareContainerForItemOverride(container, item, index);
            ReleaseExpandedBinding(container);

            if (container is not TreeViewItem treeItem || item is not GroupNodeViewModel)
                return;

            // Источником указан сам узел, а не DataContext контейнера: привязка
            // тогда не зависит от того, когда и чем контекст будет установлен.
            _expandedBindings.Add(treeItem, treeItem.Bind(TreeViewItem.IsExpandedProperty,
                new Binding("IsExpanded") { Mode = BindingMode.TwoWay, Source = item }));
        }

        /// <summary>
        /// Клик по строке выделяет именно её, а не другую копию тех же данных.
        /// Закреплённая база присутствует в дереве дважды (узел «Закреплённые»
        /// и собственная группа), а штатное выделение Avalonia красит ПЕРВЫЙ
        /// контейнер с данными базы по всему дереву (TreeContainerFromItem), поэтому
        /// клик по строке во «Все базы» подсвечивал её копию в начале списка
        /// (issue #301). Здесь подсветка ставится на конкретный контейнер под
        /// курсором; данные для модели поднимаются штатно: из контейнера приходит
        /// IsSelectedChanged, а с ним — SelectedItem дерева и правая панель.
        /// </summary>
        private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
                return;
            if (e.Source is not Visual source)
                return;

            var row = source.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            if (row is not null)
                SelectRow(row);
        }

        /// <summary>
        /// Ставит подсветку ровно на указанную строку: гасит локальную подсветку
        /// предыдущей и включает её на переданной. Локальные значения IsSelected
        /// перекрывают штатную разметку Avalonia (SetCurrentValue), поэтому прежняя
        /// подсветка гаснет только здесь — явно. Снимает подсветку со всех остальных
        /// контейнеров той же базы, чтобы при дубле (закреплённая база в узле
        /// «Закреплённые» и в своей группе) выделена была ровно одна строка.
        /// </summary>
        public void SelectRow(TreeViewItem row)
        {
            if (row is null)
                return;

            if (_selectedRow is { } previous && !ReferenceEquals(previous, row) && previous.IsSelected)
                previous.IsSelected = false;
            row.IsSelected = true;
            _selectedRow = row;
        }

        /// <summary>
        /// Навигация по видимым строкам: ↑/↓ — соседняя строка, Home/End —
        /// начало/конец, PageUp/PageDown — на страницу. Обработчик стоит на
        /// туннелировании: внутренняя прокрутка дерева лежит в маршруте ближе
        /// к строке и разбирает PageUp с PageDown сама, помечая их
        /// обработанными, так что до самого дерева они не доходят.
        /// Распределение взято у версии для Windows: голые клавиши переносят
        /// выделение, а с Ctrl прокручивают, не трогая его.
        ///
        /// ↑/↓ обрабатываются явно, а не отдаются штатной логике TreeView:
        /// стандартная навигация держится на фокусе, а в строках базы сидят
        /// фокусируемые кнопки (избранное, закрепление, действия, теги).
        /// Из-за этого при движении вниз выделение «перепрыгивало» на избранное,
        /// а дальше листалось от него. Здесь шаг считается по порядку видимых
        /// строк, а фокус ставится на контейнер, как делает версия для Windows.
        /// </summary>
        private void OnNavigationKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || e.Key is not (Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Up or Key.Down))
                return;

            // Стрелки вверх/вниз не перехватываем, если фокус в поле ввода
            // (поиск, инлайн-теги): там они двигают курсор, а не выделение.
            if (e.Key is Key.Up or Key.Down && FocusedElementIsTextBox)
                return;

            if (e.KeyModifiers == KeyModifiers.Control)
            {
                // Прокрутка с Ctrl работает только для перечисленных клавиш;
                // Ctrl+↑/↓ не назначена, оставляем их остальному маршруту.
                if (e.Key is Key.Up or Key.Down)
                    return;
                ScrollBy(e.Key);
                e.Handled = true;
                return;
            }

            if (e.KeyModifiers != KeyModifiers.None)
                return;

            // Пустой список: у TreeView в Avalonia эти клавиши считаются
            // направленными, и при пустом выборе он берёт первый элемент
            // представления, которого нет. Гасим событие до него.
            if (ItemsView.Count == 0)
            {
                e.Handled = true;
                return;
            }

            var rows = VisibleRows();
            if (rows.Count == 0)
                return;

            var current = CurrentRowIndex(rows);
            int target;
            if (e.Key is Key.Up or Key.Down)
            {
                if (current < 0)
                {
                    target = e.Key == Key.Down ? 0 : rows.Count - 1;
                }
                else
                {
                    var last = rows.Count - 1;
                    target = e.Key == Key.Down
                        ? (current >= last ? last : current + 1)
                        : (current <= 0 ? 0 : current - 1);
                }
                if (target == current)
                    return;
            }
            else
            {
                target = e.Key switch
                {
                    Key.Home => 0,
                    Key.End => rows.Count - 1,
                    Key.PageUp => PageStep(rows, current, back: true),
                    _ => PageStep(rows, current, back: false)
                };
                if (target < 0)
                    return;
            }

            e.Handled = true;

            // Выделение ставится напрямую на конкретный контейнер строки, а не
            // через SelectedItem: закреплённая база присутствует в дереве дважды
            // (узел «Закреплённые» и собственная группа), и SelectedItem всегда
            // резолвился бы в первое вхождение вверху, «перепрыгивая» подсветку
            // на закреплённую базу в начало списка. Карточка строки следит за
            // IsSelected собственного контейнера, поэтому фокус остаётся на нужной
            // копии; SelectedItem дерева поднимается из контейнера сам (штатный
            // путь выбора, как по клику), а с ним — правая панель и модель.
            // SelectRow дополнительно гасит локальную подсветку прежней строки:
            // без этого при движении по списку подсвеченными оставались бы обе.
            SelectRow(rows[target]);
            BringRowIntoView(rows[target]);
            rows[target].Focus();
        }

        /// <summary>
        /// Высота типовой строки списка (заголовок группы или строка базы). Используется как
        /// «нижняя граница» цели прокрутки для узла-ГРУППЫ: у контейнера группы Bounds.Height
        /// включает высоту всех дочерних строк, и прокрутка по ней уводила список на высоту
        /// всего поддерева (issue #255). Для группы достаточно показать её заголовок — одну
        /// строку, поэтому берём высоту первой видимой строки базы как эталонную.
        /// </summary>
        private double ReferenceRowHeight()
        {
            foreach (var row in VisibleRows())
            {
                if (row.DataContext is not GroupNodeViewModel && row.Bounds.Height > 0)
                    return row.Bounds.Height;
            }
            // Резерв: высота первого реализованного контейнера или вьюпорт прокрутки.
            return TreeScroll?.Viewport.Height > 0 ? TreeScroll.Viewport.Height : 32;
        }

        /// <summary>
        /// Прокручивает список к строке, не «перепрыгивая» вниз на высоту всей группы
        /// (issue #255). У контейнера группы высота включает все дочерние строки, поэтому
        /// штатный BringIntoView уводил список на несколько экранов при листании по папке.
        /// Шаг ограничен размером вьюпорта; следующее нажатие доводит выделение обычным шагом.
        /// </summary>
        private void BringRowIntoView(TreeViewItem item)
        {
            if (TreeScroll is not { } scroll)
            {
                item.BringIntoView();
                return;
            }

            var point = item.TranslatePoint(default, scroll);
            if (point is null)
            {
                item.BringIntoView();
                return;
            }

            var top = point.Value.Y;
            // У контейнера ГРУППЫ Bounds.Height включает высоту всех дочерних строк, поэтому
            // «выступ» за нижний край огромен и при листании клавишами по папке список
            // «перепрыгивал» вниз на высоту всего поддерева (issue #255). Цель прокрутки для
            // группы — показать её ЗАГОЛОВОК (одну строку): нижняя граница считается по высоте
            // типовой строки, а не по Bounds.Height контейнера. Для строки базы — её высота.
            var bottom = item.DataContext is GroupNodeViewModel
                ? top + ReferenceRowHeight()
                : top + item.Bounds.Height;
            var viewport = Math.Max(1, scroll.Viewport.Height);
            var current = scroll.Offset.Y;

            if (top < 0)
            {
                var step = Math.Min(-top, viewport);
                scroll.Offset = scroll.Offset.WithY(Math.Max(0, current - step));
            }
            else if (bottom > viewport)
            {
                var step = Math.Min(bottom - viewport, viewport);
                scroll.Offset = scroll.Offset.WithY(current + step);
            }
        }

        /// <summary>Фокус клавиатуры сейчас в текстовом поле (поиск, инлайн-теги).</summary>
        private bool FocusedElementIsTextBox =>
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

        /// <summary>
        /// Индекс текущей строки навигации. Определяется по контейнеру под
        /// фокусом либо под выделением, а не по SelectedItem: закреплённая база
        /// присутствует в дереве дважды (узел «Закреплённые» и собственная группа),
        /// и поиск по ссылке данных вернул бы первое вхождение, «перепрыгивая»
        /// выделение в начало списка.
        /// </summary>
        private int CurrentRowIndex(List<TreeViewItem> rows)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            for (var node = focused; node is not null; node = node.GetVisualParent())
            {
                if (node is not TreeViewItem tvi)
                    continue;
                var idx = rows.IndexOf(tvi);
                if (idx >= 0)
                    return idx;
                break;
            }

            for (var i = 0; i < rows.Count; i++)
                if (rows[i].IsSelected)
                    return i;

            return rows.FindIndex(row => ReferenceEquals(row.DataContext, SelectedItem));
        }

        /// <summary>Прокрутка без переноса выделения, вариант с Ctrl.</summary>
        private void ScrollBy(Key key)
        {
            if (TreeScroll is not { } scroll)
                return;

            var hidden = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            var target = key switch
            {
                Key.Home => 0,
                Key.End => hidden,
                Key.PageUp => scroll.Offset.Y - scroll.Viewport.Height,
                _ => scroll.Offset.Y + scroll.Viewport.Height
            };

            var next = scroll.Offset.WithY(Math.Clamp(target, 0, hidden));
            if (next != scroll.Offset)
                scroll.Offset = next;
        }

        private ScrollViewer? TreeScroll =>
            this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        /// <summary>
        /// Строки в порядке показа: обход контейнеров сверху вниз, в развёрнутые
        /// узлы с заходом внутрь. По координатам порядок не строится, потому что
        /// у части контейнеров пересчёт координат к дереву не удаётся, и такие
        /// строки сваливаются в начало.
        /// </summary>
        private List<TreeViewItem> VisibleRows()
        {
            var rows = new List<TreeViewItem>();
            Collect(this);
            return rows;

            void Collect(ItemsControl parent)
            {
                for (var i = 0; i < parent.ItemCount; i++)
                {
                    if (parent.ContainerFromIndex(i) is not TreeViewItem item)
                        continue;
                    rows.Add(item);
                    if (item.IsExpanded)
                        Collect(item);
                }
            }
        }

        /// <summary>
        /// Строка через экран от текущей. Отсчёт идёт по координатам, а не по
        /// числу строк: высота у групп и баз разная.
        /// </summary>
        private int PageStep(List<TreeViewItem> rows, int current, bool back)
        {
            if (current < 0)
                return back ? 0 : rows.Count - 1;

            // Отсчёт по координатам строки, а не по сумме высот контейнеров:
            // высота контейнера группы включает всех её детей, и шаг ушёл бы
            // во всю группу разом.
            var page = TreeScroll?.Viewport.Height ?? Bounds.Height;
            var from = Top(rows[current]);

            if (back)
            {
                for (var i = current - 1; i >= 0; i--)
                    if (Top(rows[i]) <= from - page)
                        return i;
                return 0;
            }

            for (var i = current + 1; i < rows.Count; i++)
                if (Top(rows[i]) >= from + page)
                    return i;
            return rows.Count - 1;

            double Top(TreeViewItem item) => item.TranslatePoint(default, this)?.Y ?? 0;
        }

        /// <summary>
        /// Возвращает контейнер строки для указанных данных по всему дереву
        /// (включая вложенные уровни групп) или null, если контейнер ещё не создан.
        /// Используется, чтобы вернуть клавиатурный фокус на строку после
        /// пересборки дерева, когда прежний контейнер уничтожен.
        /// </summary>
        public TreeViewItem? ContainerForItem(object data)
        {
            return Find(this, data);

            static TreeViewItem? Find(ItemsControl parent, object data)
            {
                for (var i = 0; i < parent.ItemCount; i++)
                {
                    if (parent.ContainerFromIndex(i) is not TreeViewItem item)
                        continue;
                    if (ReferenceEquals(item.DataContext, data))
                        return item;
                    if (item.IsExpanded)
                    {
                        var found = Find(item, data);
                        if (found is not null)
                            return found;
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Контейнер строки ВНУТРИ конкретного узла (а не первое вхождение по всему дереву):
        /// закреплённая база присутствует в дереве дважды, и общий <see cref="ContainerForItem"/>
        /// всегда отдавал бы строку «Закреплённых» (они идут первыми). Команде «Найти в списке»
        /// (issue #285) нужна строка во «Все базы» — внутри настоящей группы или «Без группы».
        /// </summary>
        public TreeViewItem? ContainerForItemWithin(GroupNodeViewModel node, object data)
        {
            var container = ContainerForItem(node);
            return container is null ? null : FindWithin(container, data);

            static TreeViewItem? FindWithin(ItemsControl parent, object data)
            {
                for (var i = 0; i < parent.ItemCount; i++)
                {
                    if (parent.ContainerFromIndex(i) is not TreeViewItem item)
                        continue;
                    if (ReferenceEquals(item.DataContext, data))
                        return item;
                    if (item.IsExpanded)
                    {
                        var found = FindWithin(item, data);
                        if (found is not null)
                            return found;
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Доводит строку до видимой области по её данным (issue #285). Контейнер строки
        /// далеко вниз внутри длинной раскрытой группы не реализован (контейнеры создаются
        /// только для видимой области), поэтому метод итеративно двигает внутренний
        /// ScrollViewer к цели: сначала к контейнеру домашнего узла — если он реализован,
        /// прокручивает к его заголовку, а если нет (заголовок выше вьюпорта) — вверх до его
        /// появления, — затем вниз к самой строке. Возвращает true, если контейнер строки
        /// найден и прокрутка к нему выполнена. При неудаче позиция прокрутки всё равно
        /// продвигается к цели: последующие попытки RevealFindInListAttempt продолжают с места.
        /// </summary>
        public bool EnsureDataVisible(object data, object? home)
        {
            if (ContainerForItem(data) is { } item)
            {
                item.BringIntoView();
                return true;
            }
            if (TreeScroll is not { } scroll)
                return false;

            const int maxSteps = 8;
            var viewport = Math.Max(1, scroll.Viewport.Height);
            var hidden = Math.Max(0, scroll.Extent.Height - viewport);

            // 1) Добираемся до домашнего узла: вниз, если его заголовок уже реализован,
            //    либо вверх, если он выше текущего вьюпорта и контейнер ещё не создан.
            for (var step = 0; step < maxSteps; step++)
            {
                if (home is { } homeData && ContainerForItem(homeData) is { } homeItem)
                {
                    homeItem.BringIntoView();
                    break;
                }
                if (scroll.Offset.Y <= 0)
                    return false;
                scroll.Offset = scroll.Offset.WithY(Math.Max(0, scroll.Offset.Y - viewport));
            }

            // 2) От заголовка домашнего узла спускаемся к строке цели.
            for (var step = 0; step < maxSteps; step++)
            {
                if (ContainerForItem(data) is { } target)
                {
                    target.BringIntoView();
                    return true;
                }
                if (scroll.Offset.Y >= hidden)
                    return false;
                scroll.Offset = scroll.Offset.WithY(Math.Min(hidden, scroll.Offset.Y + viewport));
            }
            return false;
        }

        /// <summary>
        /// Локальная «заглушка» выделения строк базы при подготовке контейнера.
        /// Штатное выделение Avalonia красит первый контейнер с данными базы по всему
        /// дереву (TreeContainerFromItem, SetCurrentValue), а закреплённая база есть
        /// в дереве дважды («Закреплённые» и своя группа) — клик или восстановление
        /// подсвечивали бы копию в начале списка (issue #301). Локальный false имеет
        /// приоритет выше SetCurrentValue и глушит такую разметку; реально подсветить
        /// строку может только <see cref="SelectRow"/>, ставящий локальный true ровно
        /// на нужный контейнер. Строки групп не трогаем: их данные уникальны, а сама
        /// подсветка группы идёт из модели (GroupNodeViewModel.IsSelected).
        /// </summary>
        protected override void ContainerForItemPreparedOverride(Control container, object? item, int index)
        {
            base.ContainerForItemPreparedOverride(container, item, index);

            if (item is Infobase && container is TreeViewItem row && !row.IsSelected)
                row.IsSelected = false;
        }

        protected override void ClearContainerForItemOverride(Control container)
        {
            // Контейнер уходит из дерева (пересборка, сворачивание): прежняя ссылка
            // на подсвеченную строку больше недействительна, иначе следующая SelectRow
            // гасила бы локальное значение уже чужой строки.
            if (ReferenceEquals(_selectedRow, container))
                _selectedRow = null;
            ReleaseExpandedBinding(container);
            base.ClearContainerForItemOverride(container);
        }

        private void ReleaseExpandedBinding(Control container)
        {
            if (!_expandedBindings.TryGetValue(container, out var previous))
                return;
            previous.Dispose();
            _expandedBindings.Remove(container);
        }
    }
}
#endif