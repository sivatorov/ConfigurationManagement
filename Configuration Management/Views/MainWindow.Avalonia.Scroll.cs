#if LINUX
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Configuration_Management
{
    /// <summary>
    /// Прокрутка дерева и восстановление выделения главного окна (Avalonia/Linux).
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>Позиция прокрутки списка, снятая перед пересборкой дерева.</summary>
        private Avalonia.Vector? _treeScrollOffset;

        /// <summary>Максимум отложенных попыток довести строку до видимой области (issue #285).</summary>
        private const int MaxRevealAttempts = 3;

        /// <summary>Внутренняя прокрутка дерева: вертикаль ведёт сам TreeView.</summary>
        private ScrollViewer? TreeScroll =>
            _tree?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        /// <summary>
        /// Связывает внешнюю полосу прокрутки с деревом. Полоса стоит отдельным
        /// столбцом, вне горизонтальной прокрутки, поэтому остаётся у правого
        /// края области даже когда колонки шире окна.
        /// </summary>
        private void AttachVerticalScrollBar()
        {
            // Компактный режим пересобирает окно целиком, и дерево с полосой
            // становятся другими объектами. Поэтому сверяемся с самой прокруткой,
            // а не с признаком «уже привязывались»: иначе после переключения
            // полоса остаётся подписанной на выброшенный ScrollViewer.
            if (_listVerticalBar is not { } bar || TreeScroll is not { } scroll)
                return;
            if (ReferenceEquals(_boundTreeScroll, scroll) && ReferenceEquals(_boundScrollBar, bar))
                return;

            // Собственные полосы дерева скрываем не только присоединённым свойством
            // на самом дереве (оно может не дойти до внутреннего ScrollViewer шаблона),
            // но и напрямую на найденной прокрутке. Иначе её вертикальная полоса
            // рисуется у правого края содержимого дерева, а когда колонки шире окна
            // и включается горизонтальная прокрутка, оказывается поверх строк списка,
            // а не у правого края области. Полоса прячется, прокрутка остаётся.
            ScrollViewer.SetVerticalScrollBarVisibility(scroll, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollBarVisibility(scroll, ScrollBarVisibility.Disabled);

            foreach (var link in _scrollBarLinks)
                link.Dispose();
            _scrollBarLinks.Clear();
            _boundTreeScroll = scroll;
            _boundScrollBar = bar;

            void Sync()
            {
                if (_syncingScrollBar)
                    return;
                _syncingScrollBar = true;
                try
                {
                    // Горизонтальная прокрутка дерева не используется (её ведёт внешний
                    // заголовок), но при пересборке/старте внутренняя прокрутка может получить
                    // ненулевую горизонталь — из-за неё появляется «необоснованный»
                    // горизонтальный скролл (issue #255). Держим её на нуле.
                    if (scroll.Offset.X != 0)
                        scroll.Offset = scroll.Offset.WithX(0);

                    var hidden = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                    bar.Maximum = hidden;
                    bar.ViewportSize = scroll.Viewport.Height;
                    // Шаги берёт сама прокрутка по своему содержимому. Без этого
                    // у отдельной полосы остаются значения RangeBase по умолчанию,
                    // и щелчок по дорожке двигает список на десять точек вместо
                    // страницы, а стрелка на одну точку вместо строки.
                    bar.SmallChange = scroll.SmallChange.Height;
                    bar.LargeChange = scroll.LargeChange.Height;
                    bar.Value = Math.Min(scroll.Offset.Y, hidden);
                }
                finally { _syncingScrollBar = false; }
            }

            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.OffsetProperty)
                .Subscribe(new PropertyObserver<Vector>(_ => Sync())));
            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.ExtentProperty)
                .Subscribe(new PropertyObserver<Size>(_ => Sync())));
            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.ViewportProperty)
                .Subscribe(new PropertyObserver<Size>(_ => Sync())));
            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.SmallChangeProperty)
                .Subscribe(new PropertyObserver<Size>(_ => Sync())));
            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.LargeChangeProperty)
                .Subscribe(new PropertyObserver<Size>(_ => Sync())));
            Sync();
        }

        /// <summary>
        /// Запоминает позицию прокрутки до пересборки: список опустеет, и после
        /// неё прежнюю позицию узнать уже неоткуда.
        /// </summary>
        private void RememberTreeScroll()
            // Значение перезаписывается каждой пересборкой и не обнуляется после
            // применения: две пересборки подряд тогда восстановят одну и ту же
            // позицию, а не потеряют её из-за уже отработавшего вызова.
            => _treeScrollOffset = TreeScroll?.Offset ?? _treeScrollOffset;

        /// <summary>
        /// Возвращает выделение строки после пересборки дерева: узлы групп
        /// пересоздаются, и дерево теряет подсветку вместе с ними. Объекты баз
        /// при этом те же самые, поэтому выбранная база ищется по ссылке.
        /// Установка откладывается ниже компоновки, чтобы попасть после
        /// перестроения строк, а цель читается в момент выполнения: вызывающие
        /// меняют выбор уже после возврата из пересборки (создание, регистрация
        /// и удаление базы), и снятая заранее цель подсветила бы чужую строку.
        /// </summary>
        private void RestoreTreeSelection()
        {
            if (_vm is null)
                return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_vm is null)
                    return;

                var target = (object?)_vm.SelectedInfobase ?? _vm.SelectedGroupNode;
                if (target is not null && !ReferenceEquals(_tree.SelectedItem, target))
                {
                    // Выбор ставится напрямую, без обработчика: он уже согласован
                    // с вьюмоделью, и повторный проход только сбросил бы парное поле.
                    _tree.SelectionChanged -= OnTreeSelectionChanged;
                    try { _tree.SelectedItem = target; }
                    finally { _tree.SelectionChanged += OnTreeSelectionChanged; }
                }

                // Прокрутка возвращается последней: у дерева включено
                // AutoScrollToSelectedItem, и установка выбора синхронно тянет
                // строку в видимую область, затирая прежнюю позицию.
                if (_treeScrollOffset is { } offset && TreeScroll is { } scroll)
                    scroll.Offset = offset;

                // Вернуть клавиатурный фокус строке после закрытия модального
                // диалога (например, сохранения настроек базы): контейнер прежней
                // строки уничтожен пересборкой, и фокус осел на окне. Если сейчас
                // идёт ввод в текстовом поле (поиск, теги), фокус не трогаем,
                // чтобы не выбивать курсор из поля во время набора.
                if (target is not null
                    && FocusManager?.GetFocusedElement() is not TextBox
                    && _tree.ContainerForItem(target) is { } row)
                {
                    row.Focus();
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// «Найти в списке» (issue #285): команда переключила вкладку на «Все базы», раскрыла
        /// группы-предки и пересобрала дерево; цель уже выставлена во вьюмодели. Возврат
        /// прежней позиции прокрутки здесь отменяется (обнуляем offset, запомненный
        /// RememberTreeScroll), а повторная установка выбора доводит строку до видимой
        /// области через AutoScrollToSelectedItem. Post ставится ПОСЛЕ RestoreTreeSelection,
        /// поэтому итоговое положение прокрутки — у цели, а не на старом месте.
        /// </summary>
        private void RevealFindInList()
        {
            // Возврат прежней позиции прокрутки здесь отменяется (обнуляем offset, запомненный
            // RememberTreeScroll) — цель «Найти в списке» показать строку базы, а не старое место
            // списка (issue #285; кейс #252 «возврат после „Нет“» не затрагивается — у него свой
            // путь через RestoreTreeScrollAfterCancel).
            _treeScrollOffset = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_vm is null || _tree is null)
                    return;
                var target = (object?)_vm.SelectedInfobase ?? _vm.SelectedGroupNode;
                if (target is null)
                    return;
                if (!ReferenceEquals(_tree.SelectedItem, target))
                {
                    // Выбор ставится напрямую, без обработчика: он уже согласован
                    // с вьюмоделью (тот же приём, что в RestoreTreeSelection).
                    _tree.SelectionChanged -= OnTreeSelectionChanged;
                    try { _tree.SelectedItem = target; }
                    finally { _tree.SelectionChanged += OnTreeSelectionChanged; }
                }

                // Явно доводим строку до видимой области: RestoreTreeSelection (Post Background,
                // выполняется раньше) часто уже ставит SelectedItem, тогда блок выше пропускается,
                // и остаётся лишь AutoScrollToSelectedItem, который для цели глубоко за вьюпортом
                // ненадёжен (issue #285). Доводка через контейнер работает в любом случае.
                RevealFindInListAttempt(target, 0);
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Доводит контейнер строки цели до видимой области. Если контейнер ещё не создан
        /// (не прошёл проход компоновки), повторяет отложенно с ограничением числа попыток.
        /// </summary>
        private void RevealFindInListAttempt(object target, int attempt)
        {
            if (_vm is null || _tree is null)
                return;
            try
            {
                if (_tree.ContainerForItem(target) is { } container && container is Control row)
                {
                    row.BringIntoView();
                    return;
                }
            }
            catch
            {
                // Контейнер мог отсоединиться во время пересборки — выходим.
                return;
            }

            if (attempt < MaxRevealAttempts)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => RevealFindInListAttempt(target, attempt + 1),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Восстанавливает прежнюю позицию прокрутки после закрытия окна свойств базы без
        /// сохранения («Нет»). Пересборки дерева не было, поэтому события TreeRebuilding/
        /// TreeRebuilt не сработали и <see cref="RestoreTreeSelection"/> не вызвался, а при
        /// закрытии модального окна Avalonia сама подтягивает выбранную строку в видимую
        /// область — список «уезжает» вверх/вниз (issue #252). Возвращаем точный offset,
        /// запомненный <see cref="RememberTreeScroll"/> перед открытием окна.
        /// </summary>
        private void RestoreTreeScrollAfterCancel()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_treeScrollOffset is { } offset && TreeScroll is { } scroll)
                    scroll.Offset = offset;
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
#endif