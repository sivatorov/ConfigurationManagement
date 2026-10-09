#if LINUX
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Utilities;
using Avalonia.VisualTree;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Обработчики событий главного окна (Avalonia/Linux): загрузка окна, сторожевая
    /// таймерная защита оверлея, подписки на вьюмодель и выделение строки дерева.
    /// </summary>
    public partial class MainWindow : Window
    {
        private void OnWindowLoaded(object? sender, RoutedEventArgs e)
        {
            // Инициализация выполняется синхронно при загрузке окна. Откладывать её на
            // следующий кадр нельзя: во время неё могут открываться модальные диалоги
            // (импорт/восстановление конфига), и внутри отложенного колбэка их вложенный
            // цикл сообщений приводил к зависанию приложения.
            _vm?.Initialize();
            // Декор главного окна строился в конструкторе по значению по умолчанию:
            // на этом этапе _settings во вьюмодели ещё не загружены (Initialize читает
            // их только сейчас), поэтому UseSystemTitleBar всегда возвращал false, и
            // сохранённая «Системная рамка окна» после перезапуска не применялась
            // (issue #222; у дополнительных окон настройки к моменту их создания уже
            // были загружены, поэтому там всё работало). После загрузки настроек
            // применяем сохранённое значение повторно: если оно отличается от того,
            // что выбрано при построении, обновляем декор и пересобираем содержимое
            // под нужный режим до привязки прокрутки/горячих клавиш. Прозрачность и
            // непрозрачность окна согласуются внутри ApplySystemDecorations через
            // _opaqueWindow, повторное применение их не ломает.
            var savedSystemTitleBar = _vm?.UseSystemTitleBar ?? false;
            if (savedSystemTitleBar != _useSystemTitleBar)
                ApplySystemTitleBar(savedSystemTitleBar);
            // Настройки читаются здесь, уже после построения содержимого, поэтому
            // переключатели верхней панели строились по значениям по умолчанию
            // и не показывали сохранённое состояние до первого щелчка.
            // Initialize присваивает поля напрямую, без уведомлений, так что
            // обработчик изменений вьюмодели их тоже не догонял.
            SyncTopBarToggles();
            RegisterHotkeys();
            // Шаблон дерева готов только после загрузки окна, раньше внутренней
            // прокрутки ещё нет.
            AttachVerticalScrollBar();
            // Дедупликация клика, которым закрыли контекстное меню строки (issue #340).
            AttachTreeMenuCloseClickDedup();
            // issue #340 (0.3.9.308): файл диагностики trace.json создаётся при КАЖДОМ
            // старте (startup-запись не зависит от событий меню — в 0.3.9.306 файл не
            // появлялся, т.к. запись выполнялась только внутри условных вызовов Log),
            // а открытие/закрытие контекстных меню пишется БЕЗУСЛОВНО через класс-
            // обработчик IsOpenProperty. Меню дерева дополнительно фиксирует
            // _lastMenuCloseTick — расширенный признак запуска стабилизации выделения.
            MenuCloseTrace.EnsureStarted();
            ContextMenu.IsOpenProperty.Changed.AddClassHandler<ContextMenu>(OnTreeContextMenuIsOpenChanged);
            // Масштаб строк списка (issue #303): применяем сохранённое значение и
            // включаем Ctrl+колесо над деревом — как в редакторах.
            _vm?.ApplyListZoom();
            _tree.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, (_, e) =>
            {
                if (_vm is null)
                    return;
                // Ctrl+колесо — масштаб строк списка (issue #303), как в редакторах.
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
                {
                    _vm.ZoomListBy(e.Delta.Y > 0 ? 0.1 : -0.1);
                    e.Handled = true;
                    return;
                }
                // Shift+колесо — горизонтальная прокрутка списка (issue #309): её ведёт
                // внешний ScrollViewer (listArea), общий с заголовком колонок. Обработчик
                // идёт туннелем раньше штатного скроллера дерева, чтобы тот не «уводил»
                // Offset.X — содержимое дерева двигает внешний контейнер (AttachVerticalScrollBar).
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
                {
                    var delta = e.Delta;
                    if (MathUtilities.IsZero(delta.X))
                        delta = new Vector(delta.Y, delta.X);
                    if (_listScroll is { } horizontal)
                    {
                        var hidden = Math.Max(0, horizontal.Extent.Width - horizontal.Viewport.Width);
                        var next = horizontal.Offset.WithX(
                            Math.Clamp(horizontal.Offset.X - delta.X * WheelScrollStep, 0, hidden));
                        if (next != horizontal.Offset)
                        {
                            horizontal.Offset = next;
                            e.Handled = true;
                        }
                    }
                    return;
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            if (_vm is not null)
            {
                // Переназначение клавиш меняет и привязки, и подписи в меню.
                _vm.HotkeysChanged += (_, _) =>
                {
                    RegisterHotkeys();
                    if (_tree is not null)
                        _tree.ContextMenu = BuildRowContextMenu();
                };

            }
            SetupTray();
            // Блокировка приложения (issue #294): если приложение было заблокировано
            // в прошлом сеансе (закрыто из трея, выход), при запуске окно открывается
            // с оверлеем и окном ввода пароля. Блокировка снимается только верным
            // паролем; закрытие запроса прячет окно обратно.
            try { _vm?.RestoreAppLockOnStartup(); } catch { /* не блокируем запуск */ }
            // Страховка от «зависшего» оверлея загрузки (issue #153): если фоновая
            // инициализация не завершилась за разумное время — например, на медленном
            // программном рендере/в виртуализации индетерминантный индикатор крутится
            // бесконечно, а блокирующий подложкой оверлей съедает ввод. Таймер сбрасывает
            // IsLoading, и окно возвращается к отзывчивости, даже если инициализация
            // где-то повисла.
            ArmLoadingOverlayWatchdog();
        }

        /// <summary>Максимальное время показа оверлея загрузки перед принудительным скрытием.</summary>
        private static readonly TimeSpan LoadingOverlayMaxDuration = TimeSpan.FromSeconds(30);

        // ================= Клик, закрывший контекстное меню (issue #340) =================

        /// <summary>
        /// Снимок клика, которым закрыли контекстное меню строки (issue #340, новая стратегия).
        /// Первичный клик запоминается (время, позиция) только для простого левого клика БЕЗ
        /// модификаторов; используется для распознавания ПОВТОРНОЙ доставки того же
        /// PointerPressed в дерево (попап меню освобождает перехват асинхронно) и для
        /// fallback, если повторной доставки не будет.
        /// </summary>
        private BatchSelectionHelper.MenuCloseClickSnapshot? _menuCloseClickSnapshot;

        /// <summary>
        /// Флаг «клик, закрывший меню, ещё не обработан» (issue #340, новая стратегия):
        /// взводится при первичном клике при открытом меню, снимается повторной доставкой
        /// или fallback-обработчиком <see cref="ApplyMenuCloseFallback"/>. Применение выбора
        /// однократно и идемпотентно.
        /// </summary>
        private bool _menuClosePendingApply;

        /// <summary>Целевая база клика, закрывшего меню (для fallback, issue #340).</summary>
        private Infobase? _menuCloseTarget;

        /// <summary>Секция целевой строки: true — «Закреплённые» (для fallback, issue #340).</summary>
        private bool _menuCloseTargetIsPinnedSection;

        /// <summary>
        /// Метка последнего закрытия контекстного меню ДЕРЕВА (issue #340, 0.3.9.308):
        /// единые часы <see cref="Environment.TickCount"/>. Фиксируется БЕЗУСЛОВНО в
        /// <see cref="OnTreeContextMenuIsOpenChanged"/> (в отличие от снимка клика, который
        /// писался только при клике по строке при открытом меню). Используется как
        /// расширенный признак запуска стабилизации выделения
        /// (BatchSelectionHelper.ShouldStabilizeAfterMenuClose) для любого обычного клика
        /// без модификаторов в окне ~1,5 с после закрытия меню.
        /// </summary>
        private long _lastMenuCloseTick;

        /// <summary>
        /// Последний обычный клик по строке дерева (issue #340, 0.3.9.314): единые часы
        /// <see cref="Environment.TickCount"/>, целевая база и секция строки. Записывается
        /// в туннельной фазе PointerPressed (обычный левый клик без модификаторов по строке
        /// базы, <see cref="OnTreeMenuCloseClickDedup_PointerPressed"/>) и используется при
        /// закрытии меню дерева (<see cref="OnTreeContextMenuIsOpenChanged"/>): клик мог
        /// прийти в дерево ДО закрытия меню (второй реальный trace.json: snapshot=False,
        /// redelivery=False) — снимок не записывается (кнопка отпущена к моменту закрытия),
        /// повторной доставки нет, и стабилизацию нужно запускать по цели этого клика
        /// (BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose, причина
        /// "clickBeforeMenuClose"). Очищается после использования либо перезаписывается
        /// следующим кликом.
        /// </summary>
        private (long Tick, Infobase Base, bool IsPinnedSection)? _lastPlainTreeClick;

        /// <summary>
        /// Последняя позиция указателя в координатах ДЕРЕВА (issue #340, 0.3.9.311, B-4):
        /// обновляется обработчиком <c>PointerMoved</c> окна (<see cref="AttachTreeMenuCloseClickDedup"/>)
        /// и используется в записи <c>MenuClosedCursor</c> — у
        /// <see cref="OnTreeContextMenuIsOpenChanged"/> нет события указателя, а позиция
        /// показывает, куда указывала мышь в момент закрытия меню (клик по строке / мимо /
        /// выбор пункта / ESC). (-1,-1) — движение не зафиксировано.
        /// </summary>
        private Point _lastTreePointerPos = new(-1, -1);

        /// <summary>
        /// Метка последнего движения указателя НАД ГЛАВНЫМ ОКНОМ (issue #340, 0.3.9.316):
        /// единые часы <see cref="Environment.TickCount"/>. Позиция <see cref="_lastTreePointerPos"/>
        /// кэшируется из PointerMoved окна, а движение НАД попапом контекстного меню
        /// (отдельный top-level) в это окно не приходит — по свежести метки решается,
        /// можно ли доверять «строке под курсором» при закрытии меню (см.
        /// <see cref="OnTreeContextMenuIsOpenChanged"/>).
        /// </summary>
        private long _lastTreePointerMoveTick;

        /// <summary>
        /// Метка ОТКРЫТИЯ контекстного меню дерева (issue #340, 0.3.9.316): единые часы
        /// <see cref="Environment.TickCount"/>. Вместе с <see cref="_lastTreePointerMoveTick"/>
        /// отличает закрытие выбором пункта меню (указатель над меню — свежего движения
        /// над окном нет, позиция устаревшая) от закрытия кликом по строке дерева.
        /// </summary>
        private long _treeMenuOpenedTick;

        /// <summary>
        /// Метка последнего левого клика по ПОПАПУ контекстного меню дерева (issue #340,
        /// 11-я итерация): 0 — клика не было. Любой клик, пока меню открыто, попадает в попап
        /// ДО того, как будет проглочен/доставлен дальше — надёжный сигнал «меню закрыто
        /// кликом» БЕЗ привязки к давности (в отличие от окон 500/2000 мс; лог 0.3.9.316, 7OH).
        /// </summary>
        private long _treeMenuOpenClickTick;

        /// <summary>
        /// Был ли клавиатурный фокус в дереве ДО открытия контекстного меню дерева (issue #340,
        /// 11-я итерация): после закрытия меню фокус возвращается дереву, если он был там до
        /// открытия (комментарий 7OH 28/28: стрелки не работают, TAB уходит на кнопку
        /// сворачивания).
        /// </summary>
        private bool _keyboardFocusWasInTreeBeforeMenuOpen;

        /// <summary>
        /// Подписывает обработку клика, закрывшего контекстное меню строки (issue #340).
        /// Туннельная фаза ОКНА срабатывает раньше обработчиков контрола LeveledTreeView.
        /// Выбор применяет ШТАТНАЯ логика контрола (OnRowPointerPressed) — по живому
        /// контейнеру; здесь только фиксируется клик (снимок + флаг) для fallback и
        /// распознаётся повторная доставка, чтобы отменить fallback. Событие НЕ гасится.
        /// Снимок живёт до первого отпускания кнопки мыши (двойной клик не блокируется).
        /// </summary>
        private void AttachTreeMenuCloseClickDedup()
        {
            AddHandler(InputElement.PointerPressedEvent, OnTreeMenuCloseClickDedup_PointerPressed, RoutingStrategies.Tunnel);
            AddHandler(InputElement.PointerReleasedEvent, OnTreeMenuCloseClickDedup_PointerReleased, RoutingStrategies.Tunnel);
            // B-4 (0.3.9.311): запоминаем последнюю позицию указателя в координатах дерева
            // (см. <see cref="_lastTreePointerPos"/>) — используется записью MenuClosedCursor.
            // 0.3.9.316: вместе с позицией фиксируется метка движения (см.
            // <see cref="_lastTreePointerMoveTick"/>) — для отличия свежей позиции над
            // строкой от устаревшей (указатель остался над попапом меню).
            AddHandler(InputElement.PointerMovedEvent, (_, e) =>
            {
                if (_tree is not null)
                {
                    _lastTreePointerPos = e.GetPosition(_tree);
                    _lastTreePointerMoveTick = Environment.TickCount;
                }
            }, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// Класс-обработчик открытия/закрытия контекстных меню (issue #340, 0.3.9.308):
        /// БЕЗУСЛОВНАЯ запись MenuOpened/MenuClosed в trace.json — диагностика не зависит
        /// от guard-условий клика. Для меню дерева дополнительно фиксируется метка
        /// закрытия <see cref="_lastMenuCloseTick"/> (расширенный признак стабилизации
        /// выделения: меню могло закрыться по ESC/кликом мимо строки, когда снимок клика
        /// не записывался вовсе).
        /// </summary>
        private void OnTreeContextMenuIsOpenChanged(ContextMenu menu, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is not bool isOpen)
                return;
            // Меню закладок (issue #356) обрабатывается наравне с контекстным меню
            // дерева: клик по строке, закрывший меню, должен выбирать строку.
            var isTreeMenu = BatchSelectionHelper.IsTreeLikeMenu(
                ReferenceEquals(menu, _tree?.ContextMenu), menu.Tag);
            MenuCloseTrace.Log(isOpen
                ? $"MenuOpened: isTreeMenu={isTreeMenu}"
                : $"MenuClosed: isTreeMenu={isTreeMenu}");
            if (isOpen && isTreeMenu)
            {
                _treeMenuOpenedTick = Environment.TickCount;
                // issue #340 (0.3.9.317, 11-я итерация): сброс попап-клика, фиксация фокуса
                // до открытия и подписка на левый клик по попапу меню (надёжный сигнал
                // «меню закрыто кликом»).
                _treeMenuOpenClickTick = 0;
                _keyboardFocusWasInTreeBeforeMenuOpen = _tree?.IsKeyboardFocusWithin == true;
                menu.PointerPressed += OnTreeMenuPopupPointerPressed;
                MenuCloseTrace.Log($"MenuOpenedFocus: wasInTree={_keyboardFocusWasInTreeBeforeMenuOpen}, " +
                                   $"tick={_treeMenuOpenedTick}");
            }
            if (!isOpen && isTreeMenu)
            {
                _lastMenuCloseTick = Environment.TickCount;
                // issue #340 (0.3.9.317): отписка от кликов по попапу меню (см. выше).
                menu.PointerPressed -= OnTreeMenuPopupPointerPressed;

                // Строка базы ПОД УКАЗАТЕЛЕМ на момент закрытия меню дерева (issue #340,
                // 0.3.9.316): цель восстановления выбором (MenuClosedOverRow), если меню
                // закрылось кликом, который попап проглотил и не доставил в дерево.
                Infobase? cursorInfobase = null;
                var cursorPinnedSection = false;

                // B-4 (0.3.9.311): координаты указателя и признак «курсор над строкой
                // дерева» на момент закрытия меню — по логу видно, ЧЕМ именно закрыто
                // меню (кликом по строке / кликом мимо / выбором пункта / ESC). Запись
                // безусловная: даже если снимок клика не записан (guard-цепочка не
                // пройдена), положение мыши фиксируется. Позиция берётся из последнего
                // PointerMoved окна (в координатах дерева, см. <see cref="_lastTreePointerPos"/>).
                var cursorX = _lastTreePointerPos.X;
                var cursorY = _lastTreePointerPos.Y;
                var overTreeRow = false;
                TreeViewItem? cursorRow = null;
                if (_tree is not null && _tree.InputHitTest(_lastTreePointerPos) is { } hitElement)
                    cursorRow = (hitElement as Visual)
                        ?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
                overTreeRow = cursorRow is not null;
                if (cursorRow?.DataContext is Infobase or PinnedInfobaseItem)
                {
                    cursorInfobase = BatchSelectionHelper.Unwrap(cursorRow.DataContext);
                    cursorPinnedSection = BatchSelectionHelper.IsPinnedSection(cursorRow.DataContext);
                }
                MenuCloseTrace.Log($"MenuClosedCursor: x={cursorX:0.#}, y={cursorY:0.#}, " +
                                   $"overTreeRow={overTreeRow}, keyboardFocusWithin={IsKeyboardFocusWithin}");

                // issue #340 (0.3.9.316): в Avalonia нет прямого аналога WPF
                // Mouse.DirectlyOver для закрывающегося попапа — позиция кэшируется из
                // PointerMoved ГЛАВНОГО окна, а движение НАД меню в него не приходит.
                // Если во время открытого меню указатель НЕ двигался над окном, «строка
                // под курсором» — устаревшая позиция (обычно над строкой правого клика,
                // которая уже выбрана), что характерно для закрытия выбором пункта меню:
                // трактуем как overMenuItem (восстановление не запускаем; защита «строка
                // под курсором == текущий выбор» ниже всё равно не даст перенести выбор).
                var overMenuItemApprox = _lastTreePointerMoveTick < _treeMenuOpenedTick;

                // issue #340 (0.3.9.314): клик по строке мог прийти в дерево ДО закрытия
                // меню (второй реальный trace.json: PointerPressed → MenuClosed,
                // snapshot=False, redelivery=False) — снимок не записывается (кнопка
                // отпущена к моменту закрытия), повторной доставки «хвоста» нет, и ни один
                // штатный путь стабилизацию не запускает. Если последний обычный клик по
                // строке был непосредственно (≤500 мс, окно MenuClosePrecedingClickWindowMs)
                // перед закрытием меню ДЕРЕВА — стабилизируем выбор по цели этого клика.
                // Отложенный запуск (Post): компоновка после закрытия попапа устаканится;
                // стабилизация идемпотентна, доводит выбор до сходимости (15 проходов /
                // 1,5 с) и не трогает мультивыделение.
                var precedingClick = _lastPlainTreeClick;
                _lastPlainTreeClick = null;
                // issue #340 (0.3.9.317, 11-я итерация): «клик по попапу меню» — надёжный
                // сигнал того, что меню закрыто КЛИКОМ (а не ESC/программно), без привязки
                // к давности клика (метка из OnTreeMenuPopupPointerPressed).
                var clickDuringMenuOpen = _treeMenuOpenClickTick > _treeMenuOpenedTick;
                var restoreScheduled = false;
                var restoreReason = "none";
                if (_menuCloseClickSnapshot is null &&
                    precedingClick is { } lastPlainClick &&
                    BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose(
                        lastPlainClick.Tick,
                        _lastMenuCloseTick,
                        BatchSelectionHelper.MenuClosePrecedingClickWindowMs))
                {
                    restoreReason = "clickBeforeMenuClose";
                    var stabilizeTarget = lastPlainClick.Base;
                    var stabilizePinned = lastPlainClick.IsPinnedSection;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        EnsureSelectionStable(stabilizeTarget, stabilizePinned,
                            reason: "clickBeforeMenuClose"));
                }
                // issue #340 (0.3.9.316, 10-я итерация): третий реальный trace.json
                // (0.3.9.315) показал сценарий, где НИ один штатный путь не запускает
                // стабилизацию: меню закрылось с указателем над строкой дерева
                // (overTreeRow=true), снимок клика не записан, а последний обычный клик
                // был за пределами окна "clickBeforeMenuClose" (500 мс) — например ~1,8 с
                // (клик, начавший цепочку действий, был ещё ДО открытия меню). Клик,
                // которым пользователь ЗАКРЫЛ меню, вероятно, проглочен попапом и не дошёл
                // до дерева — его цель это строка ПОД УКАЗАТЕЛЕМ: восстанавливаем выбор по
                // ней (reason "menuClosedOverRow"). Исключения — в предикате
                // ShouldRestoreSelectionForRowUnderCursor (overMenuItem, снимок, ESC/без
                // недавнего клика, мультивыделение).
                else if (precedingClick is { } activityEvidence &&
                         BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
                             overTreeRow: overTreeRow,
                             overMenuItem: overMenuItemApprox,
                             snapshotPresent: _menuCloseClickSnapshot is not null,
                             recentClickWasPlainLeftWithoutModifiers: true,
                             lastPlainClickTick: activityEvidence.Tick,
                             menuCloseTick: _lastMenuCloseTick,
                             nowTick: Environment.TickCount,
                             windowMs: BatchSelectionHelper.MenuCloseRecentMouseActivityWindowMs) &&
                         cursorInfobase is not null)
                {
                    restoreScheduled = true;
                    restoreReason = "menuClosedOverRow";
                    var restoreTarget = cursorInfobase;
                    var restorePinned = cursorPinnedSection;
                    var evidenceTick = activityEvidence.Tick;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        ApplyRowUnderCursorRestore(restoreTarget, restorePinned, evidenceTick));
                }
                // issue #340 (0.3.9.317, 11-я итерация): четвёртый реальный лог (0.3.9.316)
                // показал, что клик, закрывший меню, попап глотает ПОЛНОСТЬЮ, и НИ ОДИН путь
                // с окнами давности (500/2000 мс) не может его увидеть — последний обычный
                // клик был ~2,9 с назад (за пределами MenuCloseRecentMouseActivityWindowMs).
                // Сигнал «клик по попапу меню» (OnTreeMenuPopupPointerPressed) фиксирует факт
                // независимо от давности. Восстановление по строке ПОД УКАЗАТЕЛЕМ при
                // overTreeRow && !overMenuItem && !snapshot && clickDuringMenuOpen.
                else if (cursorInfobase is not null &&
                         BatchSelectionHelper.ShouldRestoreSelectionAfterMenuClose(
                             overTreeRow: overTreeRow,
                             overMenuItem: overMenuItemApprox,
                             snapshotPresent: _menuCloseClickSnapshot is not null,
                             clickDuringMenuOpen: clickDuringMenuOpen))
                {
                    restoreScheduled = true;
                    restoreReason = "rowUnderCursor";
                    var restoreTarget = cursorInfobase;
                    var restorePinned = cursorPinnedSection;
                    var evidenceTick = _treeMenuOpenClickTick;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        ApplyRowUnderCursorRestore(restoreTarget, restorePinned, evidenceTick));
                }

                // Возврат клавиатурного фокуса дереву после закрытия меню (issue #340,
                // 11-я итерация, комментарий 7OH 28/28): после пропажи выделения дерево
                // теряет фокус — стрелки не работают, TAB уходит на кнопку сворачивания.
                var focusRestore = BatchSelectionHelper.ShouldReturnKeyboardFocusToTree(
                    isTreeMenuClosed: true,
                    focusWasInTreeBeforeMenuOpen: _keyboardFocusWasInTreeBeforeMenuOpen,
                    focusStillWithinWindow: IsKeyboardFocusWithin,
                    modalDialogOpen: HasOpenModalDialog(),
                    clickDuringMenuOpen: clickDuringMenuOpen,
                    overTreeRow: overTreeRow);
                if (focusRestore)
                    Avalonia.Threading.Dispatcher.UIThread.Post(FocusTreeAfterMenuClose);

                MenuCloseTrace.Log(BatchSelectionHelper.BuildMenuCloseDecisionLine(
                    restore: restoreScheduled,
                    reason: restoreReason,
                    clickDuringOpen: clickDuringMenuOpen,
                    overTreeRow: overTreeRow,
                    overMenuItem: overMenuItemApprox,
                    focusRestore: focusRestore));
            }
        }

        /// <summary>
        /// Левый клик по ПОПАПУ контекстного меню дерева (issue #340, 11-я итерация): обычный
        /// клик во время открытого меню попадает в попап ДО того, как будет проглочен или
        /// доставлен дальше. Метка — единые часы <see cref="Environment.TickCount"/>; надёжный
        /// сигнал «меню закрыто кликом» независимо от давности клика.
        /// </summary>
        private void OnTreeMenuPopupPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _treeMenuOpenClickTick = Environment.TickCount;
            var pos = e.GetPosition(_tree);
            MenuCloseTrace.Log($"MenuClickDuringOpen: tick={_treeMenuOpenClickTick}, " +
                               $"x={pos.X:0.#}, y={pos.Y:0.#}, source=popup");
        }

        private void OnTreeMenuCloseClickDedup_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_tree is null || e.Source is not Visual source)
                return;

            var pos = e.GetPosition(_tree);

            // Строка базы/группы под курсором (для записи снимка и расширенного признака
            // стабилизации, issue #340, 0.3.9.308).
            var rowItem = source.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();

            // Снимок присутствовал в момент клика (F1): стабилизация выполняется не только
            // при совпавшей повторной доставке (путь A), но и когда снимок был сброшен до
            // доставки (путь C) — это тот же клик, закрывший меню.
            var menuCloseSnapshotPresent = _menuCloseClickSnapshot is not null;
            // Снимок, только что записанный ПЕРВИЧНЫМ кликом при открытом меню (путь B):
            // выбор ещё не применялся (его применит повторная доставка) — стабилизацию
            // на этом клике не запускаем, чтобы не конкурировать с контролом.
            var snapshotJustRecorded = false;

            // Повторная доставка клика, которым закрыли контекстное меню (issue #340, новая
            // стратегия): гасить событие НЕЛЬЗЯ — выбор должна применить штатная логика
            // контрола (OnRowPointerPressed), работающая с живым контейнером. Здесь только
            // снимаем флаг fallback и даём событию дойти до контрола.
            var isMenuCloseRedelivery = false;
            if (_menuCloseClickSnapshot is { } snapshot)
            {
                isMenuCloseRedelivery = BatchSelectionHelper.IsSameClick(snapshot, "Left", Environment.TickCount, pos.X, pos.Y);
                if (isMenuCloseRedelivery)
                {
                    _menuClosePendingApply = false;
                    _menuCloseTarget = null;
                    _menuCloseTargetIsPinnedSection = false;
                }
                // Снимок устарел (прошло больше допуска) или клик в другом месте — это новое
                // действие пользователя: просто сбрасываем снимок, обработка штатная.
                _menuCloseClickSnapshot = null;
            }

            // B-2 (0.3.9.311): БЕЗУСЛОВНАЯ запись PointerPressed по дереву — координаты,
            // цель, модификаторы и состояние снимка фиксируются при ЛЮБОМ клике. Прежняя
            // запись писалась только при наличии снимка (путь A/C) и не оставляла следов
            // в «путях без снимка» (обычный клик вне окна стабилизации, Ctrl/Shift-клик).
            MenuCloseTrace.Log(BatchSelectionHelper.BuildClickTraceLine(
                "PointerPressed",
                pos.X, pos.Y,
                BatchSelectionHelper.FormatModifiers(
                    (e.KeyModifiers & KeyModifiers.Control) != 0,
                    (e.KeyModifiers & KeyModifiers.Shift) != 0,
                    (e.KeyModifiers & KeyModifiers.Alt) != 0),
                BatchSelectionHelper.Unwrap(rowItem?.DataContext)?.Id,
                menuCloseSnapshotPresent,
                isMenuCloseRedelivery,
                BatchSelectionHelper.IsPinnedSection(rowItem?.DataContext)));

            // issue #340 (0.3.9.314): запоминаем «последний обычный клик по строке дерева».
            // Клик мог прийти в дерево ДО закрытия контекстного меню (второй реальный
            // trace.json: PointerPressed → MenuClosed, snapshot=False, redelivery=False) —
            // снимок не записывается (кнопка отпущена к моменту закрытия), повторной
            // доставки нет; цель этого клика используется в OnTreeContextMenuIsOpenChanged
            // (ShouldStabilizeForClickPrecedingMenuClose) для запуска стабилизации, если
            // клик был ≤500 мс до закрытия меню.
            if (rowItem is not null &&
                rowItem.DataContext is Infobase or PinnedInfobaseItem &&
                e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed &&
                (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0 &&
                BatchSelectionHelper.Unwrap(rowItem.DataContext) is { } plainClickBase)
            {
                var plainClickPinned = BatchSelectionHelper.IsPinnedSection(rowItem.DataContext);
                _lastPlainTreeClick = (Environment.TickCount, plainClickBase, plainClickPinned);
                MenuCloseTrace.Log($"LastPlainClick: target={plainClickBase.Id}, " +
                                   $"tick={_lastPlainTreeClick.Value.Tick}, pinned={plainClickPinned}");
            }

            // Первичный клик по строке базы при ОТКРЫТОМ контекстном меню: меню закрывается
            // этим кликом, его повторная доставка в дерево (после освобождения попапа)
            // обработается ШТАТНО контролом (OnRowPointerPressed) — он применит выбор к
            // живому контейнеру. Здесь запоминаем клик (снимок + флаг) ТОЛЬКО для fallback
            // на случай, если повторной доставки не будет. Снимок пишется только для
            // простого левого клика БЕЗ модификаторов (Ctrl/Shift — штатное мультивыделение).
            // Выбор НЕ применяем и событие НЕ гасим.
            if (_tree.ContextMenu is { IsOpen: true } &&
                rowItem is not null &&
                rowItem.DataContext is Infobase or PinnedInfobaseItem &&
                e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed &&
                BatchSelectionHelper.ShouldRecordMenuCloseSnapshot(
                    "Left",
                    (e.KeyModifiers & KeyModifiers.Control) != 0,
                    (e.KeyModifiers & KeyModifiers.Shift) != 0) &&
                BatchSelectionHelper.Unwrap(rowItem.DataContext) is { } clickedBase)
            {
                _menuCloseClickSnapshot = new BatchSelectionHelper.MenuCloseClickSnapshot(
                    "Left", Environment.TickCount, pos.X, pos.Y);
                _menuClosePendingApply = true;
                _menuCloseTarget = clickedBase;
                _menuCloseTargetIsPinnedSection = BatchSelectionHelper.IsPinnedSection(rowItem.DataContext);
                snapshotJustRecorded = true;

                // Диагностика (issue #340, F-поля): активность/видимость окна и число
                // открытых контекстных меню — для проверки гипотезы S4 (деактивация окна
                // закрытием попапа меню и сброс состояния до повторной доставки клика).
                // Аналог _openContextMenus.Count в Avalonia — состояние ContextMenu дерева.
                MenuCloseTrace.Log($"TryApply: snapshot=(Left,t={Environment.TickCount},x={pos.X:0.#},y={pos.Y:0.#}), " +
                                   $"target={clickedBase.Id}, pending=true, pinned={_menuCloseTargetIsPinnedSection}, " +
                                   $"IsVisible={IsVisible}, IsActive={IsActive}, " +
                                   $"openMenusCount={(_tree?.ContextMenu?.IsOpen == true ? 1 : 0)}");

                // Fallback: если повторная доставка клика не придёт (или контрол не применит
                // выбор), выбор ставится по данным; идемпотентен — сработает только пока
                // _menuClosePendingApply взведён и пользователь не перевыбрал строку.
                Avalonia.Threading.Dispatcher.UIThread.Post(ApplyMenuCloseFallback);

                // Контрольный дамп через 500 мс после клика (issue #340, диагностика):
                // итоговое состояние выделения — SelectedItem дерева, модель SelectedInfobase,
                // подсветка контейнера и размер набора мультивыделения.
                var row = _tree.FindRowForData(clickedBase, _menuCloseTargetIsPinnedSection);
                var dumpTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                dumpTimer.Tick += (_, _) =>
                {
                    dumpTimer.Stop();
                    var selectedItem = BatchSelectionHelper.Unwrap(_tree.SelectedItem);
                    var selectedModel = _vm?.SelectedInfobase;
                    MenuCloseTrace.Log(
                        $"Dump500ms: target={clickedBase.Id}, SelectedItem={(selectedItem?.Id ?? "null")}, " +
                        $"SelectedInfobase={(selectedModel?.Id ?? "null")}, " +
                        $"row.IsSelected={row?.IsSelected}, batch.Count={_vm?.BatchSelectedCount ?? 0}");
                };
                dumpTimer.Start();
            }

            // issue #340 (0.3.9.308): РАСШИРЕННЫЙ признак стабилизации выделения. Выбор
            // строки применяет ШТАТНО контрол LeveledTreeView (OnRowPointerPressed) по
            // живому контейнеру; здесь, в туннельной фазе, стабилизация запускается
            // ОТЛОЖЕННО (после текущей обработки события), чтобы не конкурировать
            // с применением выбора. Предикат ShouldStabilizeAfterMenuClose истинен для
            // любого обычного клика без модификаторов по строке базы в окне ~1,5 с после
            // закрытия контекстного меню дерева — не только при снимке клика (прежний
            // признак зависел от успешной записи снимка и не срабатывал при закрытии
            // меню по ESC/кликом мимо строки). Первичный клик при открытом меню (путь B,
            // снимок только что записан) стабилизацию не запускает — её выполнит
            // повторная доставка того же клика.
            if (!snapshotJustRecorded &&
                rowItem is not null &&
                rowItem.DataContext is Infobase or PinnedInfobaseItem &&
                e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed &&
                (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0 &&
                BatchSelectionHelper.Unwrap(rowItem.DataContext) is { } stabilizeBase &&
                BatchSelectionHelper.ShouldStabilizeAfterMenuClose(
                    snapshotPresent: menuCloseSnapshotPresent,
                    isPlainLeftClickWithoutModifiers: true,
                    lastMenuCloseTick: _lastMenuCloseTick,
                    nowTick: Environment.TickCount,
                    windowMs: BatchSelectionHelper.MenuCloseStabilizeWindowMs))
            {
                var stabilizePinned = BatchSelectionHelper.IsPinnedSection(rowItem.DataContext);
                MenuCloseTrace.Log($"PointerPressed: stabilizeRequested=true, target={stabilizeBase.Id}, " +
                                   $"snapshotPresent={menuCloseSnapshotPresent}, pinned={stabilizePinned}");
                // B-5 (0.3.9.311): причина стабилизации фиксируется в стартовой записи —
                // снимок клика присутствовал (путь A/C) или меню закрылось недавно без
                // снимка (ESC/клик мимо строки).
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    EnsureSelectionStable(stabilizeBase, stabilizePinned,
                        reason: menuCloseSnapshotPresent ? "snapshot" : "recentMenuClose"));
            }
        }

        private void OnTreeMenuCloseClickDedup_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // B-2 (0.3.9.311): БЕЗУСЛОВНАЯ запись PointerReleased по дереву — те же поля,
            // что у PointerPressed (координаты, цель, модификаторы, состояние снимка).
            if (_tree is not null && e.Source is Visual sourceReleased)
            {
                var relPos = e.GetPosition(_tree);
                var relRow = sourceReleased.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
                MenuCloseTrace.Log(BatchSelectionHelper.BuildClickTraceLine(
                    "PointerReleased",
                    relPos.X, relPos.Y,
                    BatchSelectionHelper.FormatModifiers(
                        (e.KeyModifiers & KeyModifiers.Control) != 0,
                        (e.KeyModifiers & KeyModifiers.Shift) != 0,
                        (e.KeyModifiers & KeyModifiers.Alt) != 0),
                    BatchSelectionHelper.Unwrap(relRow?.DataContext)?.Id,
                    _menuCloseClickSnapshot is not null,
                    false,
                    BatchSelectionHelper.IsPinnedSection(relRow?.DataContext)));

                // issue #340 (0.3.9.316): симметрично WPF — «последний обычный клик»
                // фиксируется и на отпускании левой кнопки над строкой базы (см.
                // OnInfobaseTree_PreviewMouseLeftButtonUp): свежая метка активности мыши
                // для восстановления выделения после закрытия меню (MenuClosedOverRow),
                // даже если PointerPressed этого клика дерево не получил.
                if (relRow?.DataContext is Infobase or PinnedInfobaseItem &&
                    (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0 &&
                    BatchSelectionHelper.Unwrap(relRow.DataContext) is { } relBase)
                {
                    var relPinned = BatchSelectionHelper.IsPinnedSection(relRow.DataContext);
                    _lastPlainTreeClick = (Environment.TickCount, relBase, relPinned);
                    MenuCloseTrace.Log($"LastPlainClick (PointerReleased): target={relBase.Id}, " +
                                       $"tick={_lastPlainTreeClick.Value.Tick}, pinned={relPinned}");
                }
            }

            // Снимок сбрасывается; флаг _menuClosePendingApply намеренно НЕ трогаем — если
            // повторная доставка не пришла, выбор применит fallback (ApplyMenuCloseFallback).
            if (_menuCloseClickSnapshot is not null)
            {
                _menuCloseClickSnapshot = null;
                MenuCloseTrace.Log("PointerReleased: snapshotCleared=true");
            }
        }

        /// <summary>
        /// Fallback-применение выбора клика, которым закрыли контекстное меню (issue #340,
        /// новая стратегия, Avalonia). Штатный путь — повторная доставка PointerPressed в
        /// контрол (OnRowPointerPressed) — применяет выбор к живому контейнеру и снимает
        /// флаг <see cref="_menuClosePendingApply"/>. Fallback нужен на случай, если повторной
        /// доставки не произошло: применяет выбор по ДАННЫМ (<see cref="SelectRowByData"/>)
        /// и запускает стабилизацию (<see cref="EnsureSelectionStable"/>). Идемпотентен.
        /// </summary>
        private void ApplyMenuCloseFallback()
        {
            if (!_menuClosePendingApply)
            {
                MenuCloseTrace.Log("Fallback: ran=false (флаг уже снят штатной доставкой)");
                return;
            }
            _menuClosePendingApply = false;

            var target = _menuCloseTarget;
            var isPinnedSection = _menuCloseTargetIsPinnedSection;
            _menuCloseTarget = null;
            _menuCloseTargetIsPinnedSection = false;
            if (target is null || _vm is null || _tree is null)
                return;

            // Пользователь успел перевыбрать другую строку — не вмешиваемся.
            if (_vm.SelectedInfobase is { } current && !ReferenceEquals(current, target))
            {
                MenuCloseTrace.Log($"Fallback: ran=true, target={target.Id}, userReselected=true");
                return;
            }

            // Клик был без модификаторов — семантика обычного клика: единственный выбор.
            var containerFound = _tree.FindRowForData(target, isPinnedSection) is not null;
            _vm.ClearBatchSelection();
            SelectRowByData(target, isPinnedSection);
            MenuCloseTrace.Log($"Fallback: ran=true, target={target.Id}, containerFound={containerFound}, " +
                               $"selectedByData=true, pinned={isPinnedSection}");
            // B-5 (0.3.9.311): fallback работает по снимку клика — причина "snapshot".
            EnsureSelectionStable(target, isPinnedSection, reason: "snapshot");
        }

        /// <summary>
        /// Восстанавливает выбор строки ПОД УКАЗАТЕЛЕМ после закрытия контекстного меню
        /// (issue #340, 10-я итерация, Avalonia; симметрично WPF): меню закрылось с
        /// указателем над строкой дерева, снимок клика не записан, а последний обычный
        /// клик мыши был в окне <see cref="BatchSelectionHelper.MenuCloseRecentMouseActivityWindowMs"/>
        /// (клик, закрывший меню, проглочен попапом и не дошёл до дерева — строка под
        /// указателем и есть его цель). Семантика обычного клика без модификаторов:
        /// единственный выбор; мультивыделение снимается ТОЛЬКО если строка под указателем
        /// отличается от текущего выбора (строка уже выбранная — только стабилизация,
        /// набор «для выделенных» не трогаем). Если пользователь успел перевыбрать другую
        /// строку — не вмешиваемся. Выбор применяется по данным и дополнительно
        /// стабилизируется (<see cref="EnsureSelectionStable"/>, причина "menuClosedOverRow").
        /// </summary>
        /// <param name="target">Строка базы под указателем в момент закрытия меню.</param>
        /// <param name="isPinnedSection">Секция строки: true — «Закреплённые» (issue #326).</param>
        /// <param name="evidenceTick">Метка последнего обычного клика (для диагностики).</param>
        private void ApplyRowUnderCursorRestore(Infobase target, bool isPinnedSection, long evidenceTick)
        {
            if (_vm is null || _tree is null || target is null)
                return;

            // Пользователь успел перевыбрать другую строку — не вмешиваемся.
            if (_vm.SelectedInfobase is { } current && !ReferenceEquals(current, target))
            {
                MenuCloseTrace.Log($"MenuClosedOverRow: ran=false, target={target.Id}, userReselected=true");
                return;
            }

            var selectionChanged = !ReferenceEquals(_vm.SelectedInfobase, target);
            if (selectionChanged)
            {
                // Семантика обычного клика: единственный выбор строки под указателем.
                _vm.ClearBatchSelection();
                SelectRowByData(target, isPinnedSection);
            }
            MenuCloseTrace.Log($"MenuClosedOverRow: ran=true, target={target.Id}, pinned={isPinnedSection}, " +
                               $"selected={(selectionChanged ? "applied" : "same")}, " +
                               $"precedingClickTick={evidenceTick}");
            EnsureSelectionStable(target, isPinnedSection, reason: "menuClosedOverRow");
        }

        /// <summary>
        /// Возвращает клавиатурный фокус дереву после закрытия контекстного меню (issue #340,
        /// 11-я итерация, Avalonia): комментарий 7OH 28/28 — после пропажи выделения стрелки
        /// перестают работать, TAB уходит на кнопку сворачивания (дерево теряет клавиатурный
        /// фокус). Фокус ставится на контейнер ТЕКУЩЕГО выбора (если реализован), иначе — на
        /// само дерево. Идемпотентно; вызывается только при взведённом предикате
        /// <see cref="BatchSelectionHelper.ShouldReturnKeyboardFocusToTree"/>.
        /// </summary>
        private void FocusTreeAfterMenuClose()
        {
            if (_tree is null || _vm is null)
                return;

            if (_vm.SelectedInfobase is { } selected)
            {
                var row = _tree.FindRowForData(selected, pinnedSection: false)
                          ?? _tree.FindRowForData(selected, pinnedSection: true);
                if (row is not null)
                {
                    row.Focus();
                    MenuCloseTrace.Log($"MenuFocusRestore: restored=true, target={selected.Id}, focused=row");
                    return;
                }
            }

            _tree.Focus();
            MenuCloseTrace.Log($"MenuFocusRestore: restored=true, target=MainTree, focused=tree");
        }

        /// <summary>
        /// Выбирает строку базы по ДАННЫМ (issue #340, Avalonia): подсветка ставится на
        /// контейнер нужной секции (закреплённая база дублируется в «Закреплённых» и в
        /// своей группе), модель синхронизируется. Строка вне видимой области (контейнер
        /// не реализован) — выбор остаётся на модели и будет подсвечен при появлении.
        /// </summary>
        private void SelectRowByData(Infobase target, bool isPinnedSection)
        {
            if (_vm is null)
                return;
            if (_tree.FindRowForData(target, isPinnedSection) is { } row)
                _tree.SelectRow(row);
            _vm.SelectedInfobase = target;
            _vm.SelectedGroupNode = null;
        }

        /// <summary>
        /// Кратковременная «конвергентная» стабилизация выделения после клика, которым закрыли
        /// контекстное меню (issue #340, седьмая попытка, Avalonia): одноразовая подписка на
        /// LayoutUpdated держится ДО СХОДИМОСТИ (до 10 срабатываний или ~1000 мс, F2) —
        /// отложенная переработка контейнеров после закрытия попапа может произойти позже
        /// прежних 3 проходов/~200 мс. Проверяет соответствие модели и контейнера и
        /// восстанавливает выбор по данным. Мультивыделение не затрагивается; защита от
        /// рекурсии — восстановление только при фактическом расхождении.
        /// </summary>
        /// <param name="reason">Причина запуска стабилизации для стартовой записи
        /// (issue #340, 0.3.9.311, B-5): "snapshot" — клик, закрывший меню, зафиксирован
        /// снимком (путь A/C или fallback); "recentMenuClose" — меню закрылось недавно
        /// без снимка (ESC/клик мимо строки), стабилизация по расширенному признаку.</param>
        private void EnsureSelectionStable(Infobase? target, bool isPinnedSection, string reason = "unknown")
        {
            if (_tree is null || target is null || _vm is null)
                return;

            // B-5 (0.3.9.311): стартовая запись стабилизации — причина, целевая база,
            // выбранная база модели и реализация/подсветка контейнера на момент старта.
            var startRow = _tree.FindRowForData(target, isPinnedSection);
            MenuCloseTrace.Log($"EnsureStableStart: target={target.Id}, reason={reason}, " +
                               $"SelectedInfobase={(_vm.SelectedInfobase?.Id ?? "null")}, " +
                               $"containerIsSelected={startRow?.IsSelected}, " +
                               $"containerRealized={startRow is not null}");

            var passes = 0;
            const int maxPasses = 15;   // F2: расширено с 10 (план 0.3.9.306, 2.4)
            var startTick = Environment.TickCount;
            const int timeoutMs = 1500; // F2: расширено с 1000 (план 0.3.9.306, 2.4)
            const int chaseDelayMs = 800; // F1: одноразовый «догоняющий» таймер

            // Диагностика (issue #340): актуальный SelectedItem дерева и время с начала
            // стабилизации — чтобы по логу видеть, «уезжал» ли SelectedItem к моменту
            // завершения подписки.
            string SelectedItemId() => BatchSelectionHelper.Unwrap(_tree?.SelectedItem)?.Id ?? "null";

            EventHandler onLayoutUpdated = null!;
            onLayoutUpdated = (_, _) =>
            {
                passes++;
                var timeSinceStartMs = Environment.TickCount - startTick;
                if (passes > maxPasses || timeSinceStartMs >= timeoutMs)
                {
                    _tree.LayoutUpdated -= onLayoutUpdated;
                    MenuCloseTrace.Log($"EnsureStable: target={target.Id}, pass={passes}, done=true, " +
                                       $"selectedItemId={SelectedItemId()}, timeSinceStartMs={timeSinceStartMs}");
                    return;
                }

                // issue #340 (0.3.9.322): «перевыбрал ДРУГУЮ строку» — только когда выбор
                // ЕСТЬ и не совпадает с целью; при ПУСТОМ выборе (клик «проглочен» попапом,
                // выбор не применён) продолжаем и восстанавливаем цель по данным ниже.
                if (!BatchSelectionHelper.ShouldContinueRestore(_vm.SelectedInfobase, target))
                {
                    _tree.LayoutUpdated -= onLayoutUpdated;
                    MenuCloseTrace.Log($"EnsureStable: target={target.Id}, pass={passes}, userReselected=true, " +
                                       $"selectedItemId={SelectedItemId()}, timeSinceStartMs={timeSinceStartMs}");
                    return;
                }

                var matches = SelectionMatchesTarget(target, isPinnedSection);
                var containerRealized = _tree.FindRowForData(target, isPinnedSection) is not null;
                var restoreApplied = false;
                if (!matches)
                {
                    SelectRowByData(target, isPinnedSection);
                    restoreApplied = true;
                }
                // B-5 (0.3.9.311): в каждый проход добавляются SelectedInfobase модели,
                // подсветка контейнера (containerIsSelected) и факт применения выбора
                // по данным (selectedByData) с результатом поиска контейнера (containerFound).
                var passRow = _tree.FindRowForData(target, isPinnedSection);
                MenuCloseTrace.Log($"EnsureStable: target={target.Id}, pass={passes}, matches={matches}, " +
                                   $"containerRealized={containerRealized}, action={(matches ? "skip" : "restored")}, " +
                                   $"selectedByData={restoreApplied}, containerFound={passRow is not null}, " +
                                   $"selectedItemId={SelectedItemId()}, " +
                                   $"SelectedInfobase={(_vm.SelectedInfobase?.Id ?? "null")}, " +
                                   $"containerIsSelected={passRow?.IsSelected}, timeSinceStartMs={timeSinceStartMs}");
            };

            _tree.LayoutUpdated += onLayoutUpdated;

            // F1 (план 0.3.9.306, 2.4): «догоняющая» стабилизация для нереализованного
            // контейнера. Если в момент старта контейнер целевой строки ещё не реализован
            // (виртуализация Recycling после закрытия попапа), подписка на LayoutUpdated
            // может закончиться раньше, чем контейнер появится, а строка без контейнера
            // не подсвечивается (SelectRowByData при отсутствии контейнера только ставит
            // модель). Одноразовый DispatcherTimer (~800 мс) ПОСЛЕ завершения подписки
            // проверяет реализацию контейнера и применяет выбор (SelectRow), если
            // подсветка так и не встала. Идемпотентно; при перевыборе не вмешивается.
            var containerRealizedAtStart = _tree.FindRowForData(target, isPinnedSection) is not null;
            if (BatchSelectionHelper.ShouldRetryRestoreForUnrealizedContainer(
                    containerRealizedAtStart, userReselected: false, elapsedMs: 0, maxChaseMs: chaseDelayMs))
            {
                var chaseTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(chaseDelayMs)
                };
                chaseTimer.Tick += (_, _) =>
                {
                    chaseTimer.Stop();
                    var timeSinceStartMs = Environment.TickCount - startTick;
                    if (_tree is null || _vm is null || target is null)
                        return;
                    // Пользователь перевыбрал другую строку — не вмешиваемся
                    // (при ПУСТОМ выборе цель ещё не восстановлена — продолжаем, issue #340).
                    if (!BatchSelectionHelper.ShouldContinueRestore(_vm.SelectedInfobase, target))
                        return;

                    var row = _tree.FindRowForData(target, isPinnedSection);
                    if (row is null)
                    {
                        MenuCloseTrace.Log($"EnsureStable: target={target.Id}, chase=notRealized, " +
                                           $"selectedItemId={SelectedItemId()}, timeSinceStartMs={timeSinceStartMs}");
                        return;
                    }
                    if (!row.IsSelected)
                    {
                        _tree.SelectRow(row);
                        MenuCloseTrace.Log($"EnsureStable: target={target.Id}, chase=applied, " +
                                           $"selectedItemId={SelectedItemId()}, timeSinceStartMs={timeSinceStartMs}");
                    }
                    else
                    {
                        MenuCloseTrace.Log($"EnsureStable: target={target.Id}, chase=ok, " +
                                           $"selectedItemId={SelectedItemId()}, timeSinceStartMs={timeSinceStartMs}");
                    }
                };
                chaseTimer.Start();
            }
        }

        /// <summary>
        /// Соответствует ли фактическое выделение дерева целевой базе (issue #340, F2, Avalonia):
        /// SelectedItem дерева разворачивается до той же базы И контейнер строки РЕАЛИЗОВАН
        /// и подсвечен. Видимая-но-нереализованная строка (контейнер ещё перерабатывается)
        /// согласованной НЕ считается — стабилизация восстановит выбор по данным
        /// (SelectRowByData идемпотентен) и продолжит подписку до сходимости
        /// (см. <see cref="EnsureSelectionStable"/>).
        /// </summary>
        private bool SelectionMatchesTarget(Infobase target, bool isPinnedSection)
        {
            if (!ReferenceEquals(BatchSelectionHelper.Unwrap(_tree.SelectedItem), target))
                return false;

            var row = _tree.FindRowForData(target, isPinnedSection);
            return row is not null && row.IsSelected;
        }

        /// <summary>
        /// Запускает одноразовый таймер, который по истечении <see cref="LoadingOverlayMaxDuration"/>
        /// сбрасывает флаг <c>IsLoading</c>, если тот всё ещё взведён. Это последний рубеж:
        /// индикатор не должен оставаться на экране и жечь CPU/блокировать ввод бесконечно.
        /// </summary>
        private void ArmLoadingOverlayWatchdog()
        {
            var timer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = LoadingOverlayMaxDuration
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_vm is not null && _vm.IsLoading)
                {
                    _vm.IsLoading = false;
                    _vm.LogWarning("Оверлей загрузки скрыт по таймауту (фоновая инициализация не завершилась)");
                }
            };
            timer.Start();
        }

        /// <summary>Снимает обработчики вьюмодели, навешенные прошлой сборкой окна.</summary>
        private void DetachViewModelHandlers()
        {
            if (_vm is null)
                return;
            if (_groupNodesChanged is not null)
                _vm.GroupNodes.CollectionChanged -= _groupNodesChanged;
            if (_flatItemsChanged is not null)
                _vm.FlatItems.CollectionChanged -= _flatItemsChanged;
            if (_tagFiltersRebuilt is not null)
                _vm.TagFiltersRebuilt -= _tagFiltersRebuilt;
            if (_vmPropertyChanged is not null)
                _vm.PropertyChanged -= _vmPropertyChanged;
        }

        private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_vm is null)
                return;
            var selected = _tree.SelectedItem;
            switch (selected)
            {
                // Обёртка строки узла «Закреплённые» (issue #301): во вьюмодель отдаём
                // реальную базу, чтобы правая панель и команды работали как раньше.
                case PinnedInfobaseItem pinned:
                    _vm.SelectedInfobase = pinned.Base;
                    _vm.SelectedGroupNode = null;
                    break;
                case Infobase ib:
                    _vm.SelectedInfobase = ib;
                    _vm.SelectedGroupNode = null;
                    break;
                case GroupNodeViewModel g:
                    _vm.SelectedGroupNode = g;
                    _vm.SelectedInfobase = null;
                    break;
                default:
                    break;
            }
        }
    }
}
#endif