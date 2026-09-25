#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.IO;
using MaterialDesignThemes.Wpf;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;

namespace Configuration_Management
{
    public partial class MainWindow
    {

        /// <summary>Данные строки, к которой запланирована прокрутка после выбора (issue #255).</summary>
        private object? _scrollTargetData;
        private bool _scrollQueued;

        /// <summary>
        /// Идёт «Найти в списке» (issue #285): цель уже выставлена во вьюмодели, и при
        /// восстановлении строки прокрутку нужно вести К ЦЕЛИ, а не возвращать прежнюю
        /// позицию. Устанавливается обработчиком RevealFindInListRequested и сбрасывается
        /// в RevealAndSelectAfterRebuild после применения.
        /// </summary>
        private bool _revealFindInListPending;

        /// <summary>Максимум «добирающих» проходов при поиске контейнера цели после пересборки (issue #285).</summary>
        private const int MaxRevealAttempts = 3;

        /// <summary>
        /// Собирает контейнеры видимых строк дерева в порядке их отображения
        /// (сверху вниз), включая строки развёрнутых подгрупп. Навигация ведётся
        /// по контейнерам, а не по объектам данных: закреплённая база присутствует
        /// в дереве дважды (узел «Закреплённые» и собственная группа), и работа
        /// с данными всякий раз возвращала бы первое (верхнее) вхождение.
        /// </summary>
        private List<TreeViewItem> GetVisibleTreeViewItems()
        {
            if (MainTree is null)
                return new List<TreeViewItem>();
            var rows = new List<TreeViewItem>();
            Collect(MainTree);
            return rows;

            void Collect(ItemsControl parent)
            {
                for (var i = 0; i < parent.Items.Count; i++)
                {
                    if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem item)
                        continue;
                    rows.Add(item);
                    if (item.IsExpanded)
                        Collect(item);
                }
            }
        }

        /// <summary>
        /// Индекс текущей строки навигации. Определяется по контейнеру под
        /// фокусом либо под выделением, а не по объекту данных: закреплённая
        /// база присутствует в дереве дважды (узел «Закреплённые» и собственная
        /// группа), и поиск по ссылке данных вернул бы первое вхождение,
        /// «перепрыгивая» выделение в начало списка.
        /// </summary>
        private int FindCurrentRowIndex(List<TreeViewItem> rows)
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
            for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
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

            return rows.FindIndex(item =>
                (item.DataContext is Infobase ib && ReferenceEquals(ib, _viewModel.SelectedInfobase)) ||
                (item.DataContext is GroupNodeViewModel gn && ReferenceEquals(gn, _viewModel.SelectedGroupNode)));
        }

        /// <summary>
        /// Выделяет указанный узел дерева (группу или базу), синхронизирует модель
        /// и переводит фокус на соответствующий TreeViewItem, чтобы дальнейшая
        /// навигация стрелками была стабильной и не «прыгала» на кнопки.
        /// </summary>
        private void SelectTreeNode(object node)
        {
            var item = FindTreeViewItemForData(node);
            switch (node)
            {
                case Infobase infobase:
                    if (item is not null)
                        ApplySelection(item, infobase);
                    else
                        _viewModel.SelectedInfobase = infobase;
                    break;
                case GroupNodeViewModel group when group.Group is not null:
                    if (item is not null)
                        ApplyGroupSelection(item, group);
                    else
                        _viewModel.SelectedGroupNode = group;
                    break;
            }

            if (item is not null)
            {
                item.Focus();
                Keyboard.Focus(item);
            }
            else
            {
                Keyboard.Focus(MainTree);
            }

            // Прокручиваем список к выбранной строке (отложенно, чтобы контейнер
            // виртуализированного узла успел создаться после установки выделения).
            QueueScrollSelectedIntoView(item);
        }

        /// <summary>
        /// Выделяет конкретную строку дерева и синхронизирует модель, не ища
        /// контейнер заново по данным: у закреплённой базы данные встречаются
        /// дважды (узел «Закреплённые» и собственная группа), и повторный поиск
        /// вернул бы первую (верхнюю) копию, «перепрыгивая» выделение в начало.
        /// </summary>
        private void SelectRowItem(TreeViewItem item)
        {
            switch (item.DataContext)
            {
                case Infobase infobase:
                    ApplySelection(item, infobase);
                    break;
                case GroupNodeViewModel group when group.Group is not null:
                    ApplyGroupSelection(item, group);
                    break;
            }

            // Фокус переносится и на служебные узлы («Закреплённые», «Без группы»):
            // у них модель не выделяется, но навигация должна продолжиться оттуда.
            item.Focus();
            System.Windows.Input.Keyboard.Focus(item);

            // Прокрутка к строке, как у SelectTreeNode.
            QueueScrollSelectedIntoView(item);
        }

        /// <summary>
        /// Восстанавливает последнюю выбранную строку списка (базу или группу) после запуска.
        /// Строка определяется по сохранённым значениям
        /// <see cref="ViewModels.MainViewModel.LastSelectedInfobaseId"/> и
        /// <see cref="ViewModels.MainViewModel.LastSelectedGroupPath"/>.
        /// </summary>
        private void RestoreLastSelection()
        {
            if (MainTree is null || _viewModel is null)
                return;

            object? target = null;

            var infobaseId = _viewModel.LastSelectedInfobaseId;
            if (!string.IsNullOrEmpty(infobaseId))
            {
                var ib = _viewModel.Infobases.FirstOrDefault(
                    i => string.Equals(i.Id, infobaseId, StringComparison.Ordinal));
                if (ib is not null)
                    target = ib;
            }
            else
            {
                var groupPath = _viewModel.LastSelectedGroupPath;
                if (!string.IsNullOrEmpty(groupPath))
                {
                    var groupNode = _viewModel.FindGroupNodeByPath(groupPath);
                    if (groupNode is not null)
                        target = groupNode;
                }
            }

            if (target is null)
                return;

            // Отложенно, чтобы виртуализированное дерево успело сгенерировать
            // контейнер строки после первой отрисовки.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (target is Infobase infobase)
                    SelectTreeNode(infobase);
                else if (target is GroupNodeViewModel group)
                    SelectTreeNode(group);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Восстанавливает выделение и клавиатурный фокус выбранной строки дерева после
        /// пересборки списка (например, после сохранения настроек базы). Прежний контейнер
        /// строки уничтожен заменой коллекции <see cref="ViewModels.MainViewModel.GroupNodes"/>,
        /// поэтому подсветка и фокус пропадают вместе с ним. Выделение восстанавливается всегда;
        /// клавиатурный фокус возвращается только если сейчас не идёт ввод в текстовом поле
        /// (поиск, теги) — чтобы не выбивать курсор при наборе.
        /// </summary>
        private void RestoreTreeKeyboardFocus()
        {
            if (MainTree is null || _viewModel is null)
                return;

            // Один атомарный проход на приоритете Render (до отрисовки следующего кадра):
            // выделение, раскрытие предков и восстановление позиции прокрутки применяются
            // в одном синхронном вызове, без промежуточной отрисовки между ними. Раньше это
            // были два прохода (Loaded → ApplicationIdle): между «сдвигом к строке» (BringIntoView)
            // и «возвратом позиции» успевал отрисоваться кадр, из-за чего список «пролистывался
            // повыше», а потом возвращался к активной строке (issue #252).
            Dispatcher.BeginInvoke(new Action(() => RevealAndSelectAfterRebuild()),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        /// <summary>
        /// Окно узнало, что выполняется «Найти в списке» (issue #285): цель уже выставлена
        /// во вьюмодели, и при восстановлении строки прокрутку нужно вести К ЦЕЛИ, а не
        /// возвращать прежнюю позицию. Флаг сбрасывается в RevealAndSelectAfterRebuild.
        /// </summary>
        private void OnFindInListRequested() => _revealFindInListPending = true;

        /// <summary>
        /// Восстанавливает выделение и клавиатурный фокус выбранной строки после пересборки.
        /// С учётом виртуализации: при виртуализации контейнер дочерней строки существует только
        /// внутри раскрытой группы, поэтому сначала раскрывается цепочка групп-предков цели,
        /// затем контейнер выбирается, доводится до видимой области и (вне текстового поля) получает
        /// фокус. Цель читается в момент выполнения, поэтому порядок установки SelectedInfobase
        /// относительно пересборки не важен.
        /// </summary>
        private void RevealAndSelectAfterRebuild(int attempt = 0)
        {
            if (MainTree is null || _viewModel is null)
                return;

            try
            {
                var target = (object?)_viewModel.SelectedInfobase ?? _viewModel.SelectedGroupNode;
                if (target is null)
                    return;

                // Цепочка групп от корня к родителю цели (Group == null — спец-узлы «Без группы»/«Закреплённые»).
                // Для цели-базы родитель — её группа; для цели-группы — её родитель. Раскрываем именно
                // ПРЕДКОВ, чтобы отредактированная группа осталась свёрнутой, если была свёрнута.
                GroupNodeViewModel? leaf = target switch
                {
                    Infobase ib => FindGroupNodeByInfobase(ib),
                    GroupNodeViewModel gn => gn.Parent,
                    _ => null
                };
                var stack = new Stack<GroupNodeViewModel>();
                for (var g = leaf; g is not null && g.Group is not null; g = g.Parent)
                    stack.Push(g);
                // Раскрываем цепочку групп от корня к цели: BringIntoView гарантирует, что группа
                // попадает в видимую область и материализуется (иначе при виртуализации её контейнер
                // может отсутствовать), после чего раскрытие реализует дочерние строки.
                // Группы, которые пользователь свернул, принудительно не раскрываем: иначе свёрнутая
                // группа, внутри которой выбрана база, после любой пересборки дерева (таймер
                // синхронизации, редактирование, сортировка) разворачивалась бы обратно, и она
                // «не сворачивалась» бы. Скрытый элемент просто останется не выделенным.
                foreach (var group in stack)
                {
                    // Группы, которые пользователь свернул, принудительно не раскрываем.
                    // Опора только на group.IsExpanded недостаточна: к моменту пересборки
                    // IsExpanded у свёрнутой группы может быть true (авторазворачивание при
                    // поиске/фильтре либо значение ещё не применено), а ключ свёрнутой
                    // группы уже сохранён в _collapsedGroups. Иначе свёрнутый родитель,
                    // внутри которого выбрана база во вложенной группе «домашнее», после
                    // любой пересборки раскрывался бы обратно и «не сворачивался».
                    if (!group.IsExpanded || _viewModel.IsGroupCollapsed(group.NodeKey))
                        continue;
                    if (FindTreeViewItemForData(group) is { } gItem)
                    {
                        // Прокрутка к контейнеру предка. Сам контейнер уже реализован:
                        // FindTreeViewItemForData отдаёт только созданные. Прокрутка заставляет
                        // виртуализацию достроить соседние строки, контейнеры потомков
                        // появляются в проходе разметки ниже.
                        // Раскрытие приходит из модели через OneWay-привязку (Setter внутри
                        // DataTrigger в ItemContainerStyle). Прямая установка gItem.IsExpanded
                        // оставляла бы на контейнере local value, у которого приоритет выше,
                        // и группа переставала сворачиваться и кнопкой в строке, и командой
                        // «Свернуть всё». По той же причине такие установки ранее убраны
                        // из ApplyGroupExpandedState.
                        gItem.BringIntoView();
                    }
                }

                // Один проход разметки доводит каскад раскрытия до конца в пределах
                // реализованного диапазона: виртуализация достраивает контейнеры раскрытых
                // веток, и контейнер цели ниже уже существует. Строку далеко за вьюпортом
                // это не создаёт — тогда поиск ниже вернёт null, и «добирающий» проход по
                // ApplicationIdle повторяет раскладку и поиск (issue #285).
                MainTree.UpdateLayout();

                var item = FindTreeViewItemForData(target);
                if (item is null)
                {
                    // Контейнер цели ещё не создан: строка ниже реализованного диапазона
                    // виртуализации либо ветка раскрылась после первого прохода разметки.
                    // Запланированный ниже повтор (ApplicationIdle) ещё раз вызывает
                    // UpdateLayout и поиск; при неудаче за отведённое число попыток выходим.
                    if (attempt < MaxRevealAttempts)
                    {
                        Dispatcher.BeginInvoke(new Action(() => RevealAndSelectAfterRebuild(attempt + 1)),
                            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                    return;
                }

                switch (target)
                {
                    case Infobase infobase:
                        ApplySelection(item, infobase);
                        break;
                    case GroupNodeViewModel group when group.Group is not null:
                        ApplyGroupSelection(item, group);
                        break;
                    default:
                        return;
                }

                if (_revealFindInListPending)
                {
                    // «Найти в списке» (issue #285): доводим строку цели до видимой области.
                    // Возврат прежней позиции здесь отменяется: RestoreTreeScrollAfterRebuild
                    // вернул бы позицию предыдущей вкладки («Избранное»/«Закреплённые») и
                    // «спрятал» целевую базу.
                    _revealFindInListPending = false;
                    ScrollSelectedIntoView(item);
                }
                else
                {
                    // Позицию прокрутки возвращаем в том же синхронном проходе, до отрисовки
                    // следующего кадра (issue #252). Промежуточный BringIntoView к цели не делаем:
                    // восстановление позиции (RestoreTreeScrollAfterRebuild) само приводит вьюпорт
                    // к сохранённому offset/якорю, а лишний сдвиг к строке давал двухфазный «скачок»
                    // списка «повыше» → к активной строке. Если группа и верхняя видимая строка
                    // не изменились — RestoreTreeScrollAfterRebuild вернёт управление, не тронув
                    // позицию вовсе.
                    RestoreTreeScrollAfterRebuild();
                }

                // Клавиатурный фокус возвращаем строке. Защищаем только поле поиска: после закрытия
                // модального окна настроек WPF может временно держать фокус на каком-либо контроле,
                // и строгая проверка «не TextBox» оставила бы базу без фокуса. Во время набора в поиске
                // курсор из поля не выбиваем.
                if (SearchTextBox is null || !ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, SearchTextBox))
                {
                    item.Focus();
                    System.Windows.Input.Keyboard.Focus(item);
                }
            }
            catch { /* элемент мог отсоединиться во время пересборки */ }
        }

        /// <summary>
        /// Планирует прокрутку к строке по её данным, а не по контейнеру. В режиме
        /// Recycling-виртуализации контейнер переиспользуется под другие строки, поэтому
        /// захват ссылки на контейнер в отложенном вызове к моменту исполнения мог указывать
        /// уже на другую базу — список «прыгал» туда-сюда, а при автоповторе клавиши «вниз»
        /// в очереди копились десятки таких вызовов, которые доигрывали и после отпускания
        /// клавиши (issue #255). Вызовы склеиваются по флагу: за проход исполняется только
        /// последняя цель.
        /// </summary>
        private void QueueScrollSelectedIntoView(TreeViewItem? item)
        {
            if (item is null)
                return;
            _scrollTargetData = item.DataContext;
            if (_scrollQueued)
                return;
            _scrollQueued = true;
            Dispatcher.BeginInvoke(new Action(DeferredScrollSelectedIntoView),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void DeferredScrollSelectedIntoView()
        {
            _scrollQueued = false;
            var data = _scrollTargetData;
            _scrollTargetData = null;
            if (data is null)
                return;
            var item = FindTreeViewItemForData(data);
            if (item is null)
                return;
            ScrollSelectedIntoView(item);
        }

        /// <summary>
        /// Прокручивает список так, чтобы указанный элемент дерева был в зоне видимости.
        /// Использует внутренний ScrollViewer дерева (вертикальная прокрутка списка).
        /// </summary>
        private void ScrollSelectedIntoView(TreeViewItem? item)
        {
            if (item is null)
                return;

            var scrollViewer = GetTreeScrollViewer();
            if (scrollViewer is null)
                return;

            try
            {
                // При прокрутке ВНИЗ ниже края вьюпорта из-за Recycling-виртуализации
                // реализуются НОВЫЕ контейнеры строк, раскладка которых к этому моменту
                // ещё не выполнена: позиция (TransformToAncestor) и ActualHeight окажутся
                // неактуальными, и величина прокрутки получится больше одной строки.
                // Доводим раскладку ТОЛЬКО для только что реализованного/ещё не измеренного
                // контейнера: синхронная UpdateLayout на автоповторе клавиши «вниз» по уже
                // разложенным строкам давала десятки раскладок за проход, «рывки» и
                // «доигрывание» после отпускания клавиши (issue #255).
                if (!item.IsMeasureValid || !item.IsArrangeValid)
                    item.UpdateLayout();

                // TransformToAncestor(scrollViewer) даёт позицию элемента ОТНОСИТЕЛЬНО
                // вьюпорта (уже с учётом прокрутки): top/bottom лежат в диапазоне видимой
                // области (0..ViewportHeight), отрицательные — выше верха вьюпорта.
                // Их нельзя сравнивать с VerticalOffset — это смещение в координатах
                // контента (растёт при прокрутке вниз). Смешивание систем координат
                // давало «прыжки» списка к началу и «прятало» выбранную базу внизу.
                var point = item.TransformToAncestor(scrollViewer).Transform(new Point(0, 0));
                var top = point.Y;                        // относительно вьюпорта
                var viewportBottom = scrollViewer.ViewportHeight;

                // Ограничиваем шаг размером вьюпорта. У контейнера ГРУППЫ ActualHeight включает
                // высоту всех дочерних строк, поэтому «выступ» за нижний край огромен и при
                // листании клавишами по папке список «перепрыгивал» вниз на несколько экранов
                // (issue #255). Цель прокрутки для группы — показать её ЗАГОЛОВОК (одну строку),
                // а не всё поддерево: поэтому нижняя граница считается по высоте типовой строки,
                // а не по ActualHeight контейнера группы (иначе список «уводило» на высоту всех
                // детей сразу, а ограничение шага вьюпортом заставляло его «прыгать» в самый низ
                // и обратно при автоповторе клавиши). Шаг по-прежнему ограничен вьюпортом.
                var bottom = item.DataContext is GroupNodeViewModel
                    ? top + ReferenceRowHeight()
                    : top + item.ActualHeight;

                if (top < 0)
                {
                    // Элемент выше верха вьюпорта: поднимаем, но не более чем на вьюпорт.
                    var step = Math.Min(-top, viewportBottom);
                    scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - step);
                }
                else if (bottom > viewportBottom)
                {
                    // Элемент ниже низа вьюпорта: опускаем, но не более чем на вьюпорт.
                    var step = Math.Min(bottom - viewportBottom, viewportBottom);
                    scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + step);
                }
            }
            catch
            {
                // Элемент мог отсоединиться от визуального дерева — игнорируем.
            }
        }

        /// <summary>
        /// Высота типовой строки списка (заголовок группы или строка базы). Используется как
        /// «нижняя граница» цели прокрутки для узла-ГРУППЫ: у контейнера группы ActualHeight
        /// включает высоту всех дочерних строк, и прокрутка по ней уводила список на высоту
        /// всего поддерева (issue #255). Для группы достаточно показать её заголовок — одну
        /// строку, поэтому берём высоту первой реализованной строки базы как эталонную.
        /// </summary>
        private double ReferenceRowHeight()
        {
            if (FindFirstInfobaseItem(MainTree) is { } first && first.ActualHeight > 0)
                return first.ActualHeight;
            // Резерв: стабильная высота ОДНОЙ строки, а не высота вьюпорта (issue #255). Возврат
            // ViewportHeight делал шаг прокрутки у нижней папки равным целому экрану — «перескок
            // вниз» при долистывании до последних групп/папок. Типовая высота строки (заголовок
            // группы/базы) не зависит от размера окна и даёт шаг ровно в одну строку.
            return 32;
        }

        /// <summary>
        /// Возвращает контейнер TreeViewItem для указанного DataContext
        /// (поиск по всем раскрытым уровням дерева).
        /// </summary>
        private TreeViewItem? FindTreeViewItemForData(object data)
        {
            if (MainTree is null)
                return null;
            return FindTreeViewItemIn(MainTree, data);
        }

        private static TreeViewItem? FindTreeViewItemIn(ItemsControl parent, object data)
        {
            for (var i = 0; i < parent.Items.Count; i++)
            {
                if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem tvi)
                    continue;

                if (ReferenceEquals(tvi.DataContext, data))
                    return tvi;

                if (tvi.Items.Count > 0)
                {
                    var found = FindTreeViewItemIn(tvi, data);
                    if (found is not null)
                        return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Находит узел группы, в котором размещена указанная база.
        /// </summary>
        private GroupNodeViewModel? FindGroupNodeByInfobase(Infobase infobase)
        {
            foreach (var root in _viewModel.GroupNodes)
            {
                var found = FindInNode(root, infobase);
                if (found is not null)
                    return found;
            }
            return null;
        }

        private static GroupNodeViewModel? FindInNode(GroupNodeViewModel node, Infobase infobase)
        {
            foreach (var child in node.Children)
            {
                var found = FindInNode(child, infobase);
                if (found is not null)
                    return found;
            }
            if (node.Infobases.Any(ib => ReferenceEquals(ib, infobase)))
                return node;
            return null;
        }

        /// <summary>
        /// Синхронизирует выделение в дереве с выбранной базой.
        /// При выборе группы снимает выделение базы.
        /// </summary>
        private void OnMainTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Выбором базы управляет code-behind через обработчики кликов
            // (OnInfobaseTree_PreviewMouseLeftButtonDown), которые явно устанавливают
            // TreeViewItem.IsSelected и SelectedInfobase. Здесь лишь фиксируем результат
            // изменения выбранного элемента, не трогая свойство Infobase.IsSelected.
            // Ранее двухсторонняя привязка IsSelected к модели порождала каскад событий
            // SelectedItemChanged (база дублируется в «Закреплённых» и в своей группе),
            // что приводило к бесконечной рекурсии и StackOverflowException.
            if (e.NewValue is Infobase infobase)
            {
                _viewModel.SelectedInfobase = infobase;
                // Выбор базы снимает выбор группы.
                _viewModel.SelectedGroupNode = null;
            }
            else if (e.NewValue is GroupNodeViewModel groupNode)
            {
                // Выбор группы снимает выбор базы и фиксирует выбранную группу.
                _viewModel.SelectedInfobase = null;
                _viewModel.SelectedGroupNode = groupNode;
            }
            else if (e.NewValue is null)
            {
                _viewModel.SelectedInfobase = null;
                _viewModel.SelectedGroupNode = null;
            }

            // Принудительно пересчитываем состояние кнопок («Изменить», «Удалить» и др.),
            // чтобы они активировались при программной установке выделения в дереве.
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// Завершает синхронизацию IsExpanded после «Развернуть всё» / «Свернуть всё».
        /// Модель уже приведена к нужному состоянию через <see cref="GroupNodeViewModel.IsExpanded"/>
        /// (PropertyChanged), поэтому контейнеры обновляются сами из OneWay-привязки
        /// (MainWindow.xaml:1723). Здесь остаётся только заставить виртуализацию
        /// (VirtualizingStackPanel) догенерировать контейнеры вновь развёрнутых веток.
        ///
        /// Прямые установки <see cref="TreeViewItem.IsExpanded"/> (local value) намеренно
        /// удалены (issue #160): у local value выше приоритет, чем у OneWay-привязки из
        /// DataTrigger, из-за чего после команды мышь перестаёт сворачивать/разворачивать
        /// отдельные папки. Обход по <c>ItemContainerGenerator.ContainerFromIndex</c> также
        /// ненадёжен под виртуализацией — для нереализованных строк он возвращает null,
        /// поэтому первое нажатие действовало лишь на видимые ветки, а остальные — со второго.
        /// Каскадная генерация здесь не нужна: разворачивание родителя порождает контейнеры
        /// детей, а те читают уже выставленный IsExpanded из модели.
        /// </summary>
        internal void ApplyGroupExpandedState(bool expand)
        {
            if (MainTree is null)
                return;

            // Один проход разметки доводит каскад разворачивания до конца.
            MainTree.UpdateLayout();
        }

        private void OnMainTree_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true;
        }

        /// <summary>
        /// Устанавливает выделение указанного узла группы и синхронизирует
        /// выбранную группу в модели представления (снимая выбор базы).
        /// Без Focus()/BringIntoView — позиция прокрутки списка не меняется.
        /// </summary>
        private void ApplyGroupSelection(TreeViewItem item, GroupNodeViewModel groupNode)
        {
            item.IsSelected = true;
            _viewModel.SelectedInfobase = null;
            _viewModel.SelectedGroupNode = groupNode;

            // Принудительно пересчитываем состояние кнопок («Изменить», «Удалить» и др.),
            // т.к. программная установка выделения не всегда гарантирует автоматический
            // пересчёт CanExecute команд через CommandManager.
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// Устанавливает выделение указанного элемента дерева и синхронизирует
        /// выбранную базу в модели представления.
        /// Без Focus()/BringIntoView — позиция прокрутки списка не меняется.
        /// </summary>
        private void ApplySelection(TreeViewItem item, Infobase infobase)
        {
            item.IsSelected = true;
            _viewModel.SelectedInfobase = infobase;
        }

        /// <summary>
        /// Ищет предка заданного типа в визуальном дереве.
        /// </summary>
        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current is not null)
            {
                if (current is T typed)
                    return typed;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

    }
}
#endif
