using System;
using System.Globalization;
using Configuration_Management.Models;
using Configuration_Management.ViewModels;

namespace Configuration_Management.Services;

/// <summary>
/// Чистая логика мультивыделения «для выделенных» (0.3.9.90): правило секций
/// дерева («Закреплённые» vs обычный список, issue #326) и построение набора
/// при правом клике (issue #313). Без платформенных зависимостей — используется
/// обеими платформами (Windows/WPF и Linux/Avalonia).
/// </summary>
public static class BatchSelectionHelper
{
    /// <summary>
    /// Строка узла «Закреплённые» несёт обёртку <see cref="PinnedInfobaseItem"/>
    /// (уникальные данные строки, issues #301/#314), обычная строка базы — саму
    /// модель <see cref="Infobase"/>. По типу данных контейнера определяется
    /// принадлежность строки к секции.
    /// </summary>
    public static bool IsPinnedSection(object? rowData) => rowData is PinnedInfobaseItem;

    /// <summary>
    /// Разворачивает данные строки дерева до реальной базы: строка узла
    /// «Закреплённые» несёт обёртку <see cref="PinnedInfobaseItem"/> (issue #314),
    /// обычная строка — саму модель <see cref="Infobase"/>. Единая точка
    /// разворачивания для обеих платформ (порядок секций, issue #326).
    /// </summary>
    public static Infobase? Unwrap(object? rowData) => rowData switch
    {
        Infobase ib => ib,
        PinnedInfobaseItem pinned => pinned.Base,
        _ => null
    };

    /// <summary>
    /// Видимый порядок строк секции «Закреплённые» по ДАННЫМ узла, а не по
    /// контейнерам дерева (issue #326). Узел «Закреплённые» — плоский список
    /// обёрток <see cref="PinnedInfobaseItem"/> (или баз), поэтому его порядок
    /// однозначен и не зависит от виртуализации/развёрнутости групп — в отличие
    /// от обхода контейнеров, который под WPF/Avalonia может вернуть пустой или
    /// неполный список (контейнеры вне видимой области не реализованы). Пустой
    /// вход даёт пустой результат; Shift-диапазон в этом случае выбирает только
    /// цель (как при отсутствии порядка).
    /// </summary>
    /// <param name="pinnedItems">Элементы <c>Items</c> узла «Закреплённые».</param>
    public static List<Infobase> BuildPinnedSectionOrder(IEnumerable<object?> pinnedItems)
    {
        var result = new List<Infobase>();
        if (pinnedItems is null)
            return result;
        foreach (var item in pinnedItems)
        {
            if (Unwrap(item) is { } ib && !result.Contains(ib))
                result.Add(ib);
        }
        return result;
    }

    /// <summary>
    /// Применяет модифицированный (Ctrl/Shift) клик к набору мультивыделения с
    /// правилом секций (#326): клик в другой секции («Закреплённые» vs обычный
    /// список) не смешивает строки — текущий набор очищается, и клик начинает
    /// новый набор в своей секции. Внутри секции поведение прежнее: Ctrl —
    /// точечное переключение (первый Ctrl-клик по строке, отличной от «текущей»,
    /// добавляет в набор и «текущую» — issue #313), Shift — диапазон от якоря до
    /// цели по видимому порядку (порядок должен быть построен в пределах той же
    /// секции). Возвращает новый набор идентификаторов баз.
    /// </summary>
    /// <param name="currentIds">Текущий набор мультивыделения.</param>
    /// <param name="currentSectionIsPinned">
    /// Секция текущего набора: true — «Закреплённые». Значение игнорируется при пустом наборе.
    /// </param>
    /// <param name="targetId">Идентификатор базы под кликом.</param>
    /// <param name="targetSectionIsPinned">Секция строки под кликом (из данных контейнера).</param>
    /// <param name="modifier">"Ctrl" или "Shift" (регистр не важен).</param>
    /// <param name="visibleOrder">Видимый порядок строк в пределах секции цели (для Shift).</param>
    /// <param name="anchorId">Якорь диапазона (обычно последняя строка, выбранная без Ctrl).</param>
    /// <param name="includeId">
    /// «Текущая» строка (последняя выбранная без Ctrl, issue #313). При ПЕРВОМ
    /// Ctrl-клике (набор фактически пуст) по строке, отличной от неё, добавляется
    /// в набор вместе с целевой — как в проводнике; повторный Ctrl-клик по строке
    /// набора остаётся toggle. Не добавляется, если секция «текущей» отличается
    /// от секции цели (правило секций #326).
    /// </param>
    /// <param name="includeSectionIsPinned">Секция «текущей» строки (из данных её контейнера).</param>
    public static HashSet<string> ApplyModifiedClick(
        IReadOnlyCollection<string> currentIds,
        bool currentSectionIsPinned,
        string? targetId,
        bool targetSectionIsPinned,
        string? modifier,
        IReadOnlyList<string>? visibleOrder = null,
        string? anchorId = null,
        string? includeId = null,
        bool includeSectionIsPinned = false)
    {
        var result = new HashSet<string>(currentIds ?? Array.Empty<string>(), StringComparer.Ordinal);

        // Клик в другой секции не смешивает строки (#326): начинаем новый набор.
        if (result.Count > 0 && currentSectionIsPinned != targetSectionIsPinned)
            result.Clear();

        if (string.Equals(modifier, "Shift", StringComparison.OrdinalIgnoreCase))
        {
            // Диапазон строится только если якорь и цель есть в порядке секции;
            // иначе — только цель (как проводник при Shift-клике без активного якоря).
            if (targetId is null)
                return result;
            if (visibleOrder is null || visibleOrder.Count == 0)
            {
                result.Add(targetId);
                return result;
            }

            var iFrom = anchorId is null ? -1 : IndexOf(visibleOrder, anchorId);
            var iTo = IndexOf(visibleOrder, targetId);
            if (iFrom < 0 || iTo < 0)
            {
                result.Add(targetId);
                return result;
            }

            var lo = Math.Min(iFrom, iTo);
            var hi = Math.Max(iFrom, iTo);
            for (var i = lo; i <= hi; i++)
                result.Add(visibleOrder[i]);
            return result;
        }

        // Ctrl: точечное переключение (toggle).
        if (targetId is null)
            return result;

        // Первый Ctrl-клик по строке, отличной от «текущей» (issue #313):
        // «текущая» строка (последняя выбранная без Ctrl) добавляется в набор
        // вместе с целевой — иначе при правом клике «Для выделенных» она
        // пропадает из набора. Правило действует только когда набор фактически
        // пуст (после возможного сброса секции выше): повторный Ctrl-клик по
        // строке набора остаётся toggle. «Текущая» добавляется только если её
        // секция совпадает с секцией цели (#326) — закреплённые и обычные
        // строки в одном наборе не смешиваются.
        if (result.Count == 0 && includeId is { Length: > 0 }
            && !string.Equals(includeId, targetId, StringComparison.Ordinal)
            && includeSectionIsPinned == targetSectionIsPinned)
        {
            result.Add(includeId);
        }

        if (!result.Add(targetId))
            result.Remove(targetId);
        return result;
    }

    /// <summary>
    /// Набор «для выделенных» на момент правого клика (issue #313). Правый клик
    /// набор НЕ меняет: строка под курсором не добавляется и не снимается, даже
    /// если она была «просто текущей» (выбранной без Ctrl). Попадание «текущей»
    /// в набор теперь обеспечивается на этапе Ctrl-клика (<see cref="ApplyModifiedClick"/>,
    /// параметр <c>includeId</c>) — сам правый клик состав набора не трогает.
    /// Метод фиксирует семантику и возвращает фактический набор; его используют
    /// тесты регрессии сценария.
    /// </summary>
    public static IReadOnlyCollection<string> BuildRightClickSet(
        IReadOnlyCollection<string> batchIds, string? rightClickedId)
    {
        // Параметр rightClickedId принимается намеренно: он документирует, что
        // строка под правым кликом НЕ добавляется и не снимается, даже если она
        // была «просто текущей» (без Ctrl).
        _ = rightClickedId;
        return new HashSet<string>(batchIds ?? Array.Empty<string>(), StringComparer.Ordinal);
    }


    /// <summary>
    /// Снимок клика, которым закрыли контекстное меню дерева (issue #340, новый подход):
    /// кнопка, метка времени (мс с момента старта системы, <see cref="Environment.TickCount"/>)
    /// и координаты в дереве. Клик запоминается в момент закрытия меню, а его ПОВТОРНАЯ
    /// доставка в дерево (WPF/Avalonia освобождают захват попапа меню асинхронно)
    /// распознаётся по времени и позиции — без подавления по флагу, которое не срабатывало
    /// в четырёх прежних попытках.
    /// <para>
    /// Время берётся ЕДИНЫМИ часами (TickCount): сравнение не зависит от семантики штампа
    /// события платформы (WPF MouseEventArgs.Timestamp и Avalonia PointerEventArgs.Timestamp
    /// используют разные источники и единицы) и устойчиво к расхождению часов UTC.
    /// </para>
    /// </summary>
    public readonly record struct MenuCloseClickSnapshot(
        string Button,
        long TimestampMs,
        double X,
        double Y);

    /// <summary>
    /// Является ли событие повторной доставкой клика, которым закрыли контекстное меню
    /// (issue #340): кнопка совпадает, время в пределах допуска (мс), позиция — в пределах
    /// окрестности (px). Дедупликация по данным события, а не по флагу подавления:
    /// повтор «через мгновение» с другими координатами считается новым действием
    /// пользователя и обрабатывается штатно. Событие, наступившее РАНЬШЕ снимка,
    /// не может быть повторной доставкой того же клика.
    /// </summary>
    public static bool IsSameClick(
        MenuCloseClickSnapshot snapshot,
        string button,
        long timestampMs,
        double x,
        double y,
        int toleranceMs = 300,
        double tolerancePx = 12)
    {
        if (!string.Equals(snapshot.Button, button, StringComparison.OrdinalIgnoreCase))
            return false;
        if (timestampMs < snapshot.TimestampMs)
            return false;
        if (timestampMs - snapshot.TimestampMs > toleranceMs)
            return false;
        return Math.Abs(snapshot.X - x) <= tolerancePx && Math.Abs(snapshot.Y - y) <= tolerancePx;
    }

    /// <summary>
    /// Человекочитаемая строка модификаторов для диагностики trace.json (issue #340,
    /// 0.3.9.311, B-6): единый формат для WPF (ModifierKeys) и Avalonia (KeyModifiers).
    /// Пустое сочетание — "None"; порядок частей фиксирован (Ctrl+Shift+Alt).
    /// </summary>
    public static string FormatModifiers(bool ctrl, bool shift, bool alt)
    {
        var parts = new List<string>(3);
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        return parts.Count == 0 ? "None" : string.Join("+", parts);
    }

    /// <summary>
    /// Единообразная строка события клика по дереву для диагностики trace.json
    /// (issue #340, 0.3.9.311, B-6): координаты, модификаторы, целевая база,
    /// признак снимка клика, признак повторной доставки и секция строки.
    /// Используется И WPF (MouseDown/MouseUp), И Avalonia (PointerPressed/
    /// PointerReleased), чтобы записи обеих платформ имели одинаковую форму —
    /// по логу сразу видно симметрию/асимметрию поведения. Поля: x, y, modifiers,
    /// target, snapshot, redelivery, pinned. Целевая база может отсутствовать
    /// (промах/клик по служебному узлу) — пишется "null".
    /// </summary>
    /// <param name="clickEvent">Имя события: "MouseDown"/"MouseUp"/"PointerPressed"/"PointerReleased".</param>
    /// <param name="x">Координата X в координатах дерева.</param>
    /// <param name="y">Координата Y в координатах дерева.</param>
    /// <param name="modifiers">Модификаторы (см. <see cref="FormatModifiers"/>).</param>
    /// <param name="targetId">Идентификатор базы под кликом (null при промахе).</param>
    /// <param name="snapshotPresent">Присутствовал ли снимок клика, закрывавшего меню (путь A/C).</param>
    /// <param name="isRedelivery">Является ли событие повторной доставкой клика, закрывшего меню.</param>
    /// <param name="isPinnedSection">Секция строки: true — «Закреплённые» (issue #326).</param>
    public static string BuildClickTraceLine(
        string clickEvent,
        double x,
        double y,
        string modifiers,
        string? targetId,
        bool snapshotPresent,
        bool isRedelivery,
        bool isPinnedSection)
    {
        // Координаты форматируются с InvariantCulture: разделитель дробной части —
        // точка при ЛЮБОЙ локали ОС (в т.ч. русской), иначе записи WPF/Avalonia и
        // тесты разъезжались бы по локали («12,5» vs «12.5»). Точность — до сотых
        // (координаты клика обычно доли пикселя), нули не выводятся.
        var xs = x.ToString("0.##", CultureInfo.InvariantCulture);
        var ys = y.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{clickEvent}: x={xs}, y={ys}, modifiers={modifiers}, " +
               $"target={targetId ?? "null"}, snapshot={snapshotPresent}, " +
               $"redelivery={isRedelivery}, pinned={isPinnedSection}";
    }

    /// <summary>
    /// Нужно ли записывать снимок клика, закрывшего контекстное меню (issue #340, новая
    /// стратегия): ТОЛЬКО для простого левого клика БЕЗ модификаторов. Ctrl/Shift-клики
    /// при открытом меню обрабатываются штатной логикой мультивыделения
    /// (ToggleBatchSelection/SelectRange) — снимок не должен их дедуплицировать или
    /// «перевыбирать», иначе мультивыделение подавлялось бы вместе с повторной доставкой.
    /// </summary>
    /// <param name="button">Кнопка клика ("Left"/"Right").</param>
    /// <param name="ctrlPressed">Зажат Control.</param>
    /// <param name="shiftPressed">Зажат Shift.</param>
    public static bool ShouldRecordMenuCloseSnapshot(string button, bool ctrlPressed, bool shiftPressed)
        => string.Equals(button, "Left", StringComparison.OrdinalIgnoreCase) && !ctrlPressed && !shiftPressed;

    /// <summary>
    /// Окно стабилизации выделения после закрытия контекстного меню дерева (issue #340,
    /// 0.3.9.308): обычный клик без модификаторов в этом окне (~1,5 с) после закрытия
    /// меню сопровождается стабилизацией <c>IsSelected</c> — отложенная переработка
    /// контейнеров (VirtualizingStackPanel, VirtualizationMode=Recycling) может сбросить
    /// подсветку «через мгновение». Общая константа для WPF и Avalonia.
    /// </summary>
    public const long MenuCloseStabilizeWindowMs = 1500;

    /// <summary>
    /// Нужно ли стабилизировать выделение после обычного клика по строке дерева
    /// (issue #340, 0.3.9.308). Признак РАСШИРЕН относительно прежнего (наличие снимка
    /// клика, закрывшего меню): снимок записывался только после длинной guard-цепочки
    /// <c>TryApplyTreeClickAfterMenuClosed</c> (кнопка мыши нажата, клик по строке,
    /// без модификаторов), поэтому при закрытии меню по ESC или кликом мимо строки
    /// стабилизация не запускалась вовсе — и выделение пропадало «через мгновение».
    /// Теперь стабилизация запускается и для ЛЮБОГО простого клика БЕЗ модификаторов
    /// в окне ~1,5 с (<see cref="MenuCloseStabilizeWindowMs"/>) после закрытия
    /// контекстного меню дерева. Ctrl/Shift-клики (мультивыделение) стабилизацией
    /// не затрагиваются — предикат требует клик без модификаторов.
    /// </summary>
    /// <param name="snapshotPresent">Присутствовал ли снимок клика, закрывавшего меню (путь A/C).</param>
    /// <param name="isPlainLeftClickWithoutModifiers">Обычный левый клик БЕЗ Ctrl/Shift.</param>
    /// <param name="lastMenuCloseTick">Метка последнего закрытия меню дерева (Environment.TickCount; 0 — меню не закрывалось).</param>
    /// <param name="nowTick">Текущая метка времени (Environment.TickCount).</param>
    /// <param name="windowMs">Окно стабилизации, мс (по умолчанию <see cref="MenuCloseStabilizeWindowMs"/>).</param>
    public static bool ShouldStabilizeAfterMenuClose(
        bool snapshotPresent,
        bool isPlainLeftClickWithoutModifiers,
        long lastMenuCloseTick,
        long nowTick,
        long windowMs)
    {
        if (!isPlainLeftClickWithoutModifiers)
            return false;
        if (snapshotPresent)
            return true;
        // Того же клика, закрывшего меню, могло не быть (ESC/клик мимо строки), но
        // меню закрылось недавно — стабилизация нужна для любого обычного клика в окне.
        return lastMenuCloseTick > 0
            && nowTick >= lastMenuCloseTick
            && nowTick - lastMenuCloseTick <= windowMs;
    }

    /// <summary>
    /// Окно «обычный клик предшествовал закрытию меню» (issue #340, 0.3.9.314): обычный
    /// клик по строке дерева в этом окне (~500 мс) ДО закрытия контекстного меню дерева
    /// сопровождается стабилизацией <c>IsSelected</c>. Общая константа для WPF и Avalonia.
    /// </summary>
    public const long MenuClosePrecedingClickWindowMs = 500;

    /// <summary>
    /// Нужно ли стабилизировать выделение для обычного клика по строке дерева, который
    /// произошёл НЕПОСРЕДСТВЕННО ПЕРЕД закрытием контекстного меню (issue #340, 0.3.9.314).
    /// По второму реальному trace.json (0.3.9.311) MouseDown по строке приходит в дерево
    /// ДО MenuClosed: снимок клика по guard-цепочке TryApplyTreeClickAfterMenuClosed не
    /// записывается (к моменту OnContextMenuClosed левая кнопка уже отпущена — снимок
    /// требует Mouse.LeftButton == Pressed), повторной доставки «хвоста» нет, поэтому ни
    /// один штатный путь (снимок пути A/C, fallback, <see cref="ShouldStabilizeAfterMenuClose"/>)
    /// стабилизацию не запускает — переработка контейнеров виртуализацией
    /// (VirtualizingStackPanel, Recycling) сбрасывает IsSelected «через мгновение».
    /// Предикат закрывает это звено: обычный клик был в окне
    /// <see cref="MenuClosePrecedingClickWindowMs"/> до закрытия меню — стабилизацию нужно
    /// запустить по цели этого клика (см. EnsureSelectionStable, причина "clickBeforeMenuClose").
    /// </summary>
    /// <param name="lastPlainClickTick">Метка последнего обычного клика по строке дерева
    /// (единые часы Environment.TickCount; 0 — обычных кликов не было).</param>
    /// <param name="menuCloseTick">Метка закрытия контекстного меню дерева (Environment.TickCount).</param>
    /// <param name="windowMs">Окно «клик перед закрытием», мс (по умолчанию <see cref="MenuClosePrecedingClickWindowMs"/>).</param>
    public static bool ShouldStabilizeForClickPrecedingMenuClose(
        long lastPlainClickTick, long menuCloseTick, long windowMs)
    {
        if (lastPlainClickTick <= 0)
            return false;
        if (menuCloseTick < lastPlainClickTick)
            return false;
        return menuCloseTick - lastPlainClickTick <= windowMs;
    }

    /// <summary>
    /// Окно «недавняя активность мыши перед закрытием меню» (issue #340, 10-я итерация):
    /// обычный клик по строке дерева в последние ~2 с перед закрытием контекстного меню
    /// считается свидетельством, что пользователь работал мышью в дереве — клик, который
    /// должен был закрыть меню, мог быть проглочен попапом и НЕ дойти до дерева (третий
    /// реальный trace.json 0.3.9.315: MouseDown/LastPlainClick → MenuOpened → MenuClosed,
    /// второго MouseDown в логе нет, cursor над строкой при закрытии). Общая константа
    /// для WPF и Avalonia.
    /// </summary>
    public const long MenuCloseRecentMouseActivityWindowMs = 2000;

    /// <summary>
    /// Нужно ли восстановить выбор строки ПОД КУРСОРОМ после закрытия контекстного меню
    /// дерева (issue #340, 10-я итерация). Третий реальный trace.json (0.3.9.315) показал
    /// сценарий, в котором ни один штатный путь стабилизации не срабатывает: меню
    /// закрылось с курсором над строкой дерева (overTreeRow=true), снимок клика не записан
    /// (guard-цепочка TryApplyTreeClickAfterMenuClosed не прошла — левая кнопка отпущена),
    /// а клик, которым пользователь начал цепочку действий, был за пределами окна
    /// <see cref="MenuClosePrecedingClickWindowMs"/> (в логе ~1,8 с — предикат
    /// ShouldStabilizeForClickPrecedingMenuClose не сработал). Меню закрылось НЕ выбором
    /// пункта (overMenuItem=false) и НЕ бездействием: в окне
    /// <see cref="MenuCloseRecentMouseActivityWindowMs"/> был обычный клик мыши без
    /// модификаторов. Вероятно, клик по строке был проглочен попапом и не дошёл до дерева
    /// (повторной доставки нет) — строка под курсором и есть его цель, выбор нужно
    /// восстановить. Исключения: курсор над пунктом меню (overMenuItem=true — выбор пункта
    /// меню строку не меняет), наличие снимка клика (работают штатные пути A/C/snapshot),
    /// отсутствие недавнего обычного клика (закрытие ESC/программно), клик ДАВНО (за
    /// пределами окна), а также мультивыделение: evidence — только ОБЫЧНЫЙ клик без
    /// Ctrl/Shift (<paramref name="recentClickWasPlainLeftWithoutModifiers"/>), поэтому
    /// Ctrl/Shift-активность сразу отсекает ветку.
    /// </summary>
    /// <param name="overTreeRow">Курсор в момент закрытия над строкой дерева (hit-test по координатам).</param>
    /// <param name="overMenuItem">Курсор в момент закрытия над пунктом меню.</param>
    /// <param name="snapshotPresent">Присутствовал ли снимок клика, закрывавшего меню (путь A/C).</param>
    /// <param name="recentClickWasPlainLeftWithoutModifiers">
    /// Признак того, что недавняя мышиная активность — ОБЫЧНЫЙ левый клик без Ctrl/Shift
    /// (мультивыделение не затрагивается).
    /// </param>
    /// <param name="lastPlainClickTick">Метка последнего обычного клика (единые часы Environment.TickCount; 0 — кликов не было).</param>
    /// <param name="menuCloseTick">Метка закрытия меню дерева (Environment.TickCount).</param>
    /// <param name="nowTick">Текущая метка времени (Environment.TickCount).</param>
    /// <param name="windowMs">Окно «недавний клик», мс (по умолчанию <see cref="MenuCloseRecentMouseActivityWindowMs"/>).</param>
    public static bool ShouldRestoreSelectionForRowUnderCursor(
        bool overTreeRow,
        bool overMenuItem,
        bool snapshotPresent,
        bool recentClickWasPlainLeftWithoutModifiers,
        long lastPlainClickTick,
        long menuCloseTick,
        long nowTick,
        long windowMs)
    {
        if (!overTreeRow || overMenuItem)
            return false;
        if (snapshotPresent)
            return false;
        if (!recentClickWasPlainLeftWithoutModifiers)
            return false;
        if (lastPlainClickTick <= 0 || menuCloseTick <= 0)
            return false;
        if (nowTick < menuCloseTick)
            return false;
        // Клик ПОСЛЕ закрытия меню — новое действие пользователя, обрабатывается штатно
        // (ShouldStabilizeAfterMenuClose): восстановление по строке под курсором не нужно.
        if (lastPlainClickTick >= menuCloseTick)
            return false;
        return menuCloseTick - lastPlainClickTick <= windowMs;
    }

    /// <summary>
    /// Нужно ли восстановить выбор строки ПОД КУРСОРОМ после закрытия контекстного меню
    /// (issue #340, 11-я итерация): НЕЗАВИСИМО от давности клика. Новый надёжный сигнал —
    /// «клик по ПОПАПУ самого меню»: обычный левый клик во время открытого меню попадает
    /// в попап (ContextMenu WPF / ContextMenu Avalonia) ДО того, как будет проглочен или
    /// доставлен дальше, поэтому факт «пользователь кликнул, пока меню было открыто»
    /// фиксируется без привязки к окну давности. Четвёртый реальный лог (0.3.9.316, 7OH):
    /// клик, закрывший меню, полностью проглочен попапом (ни MouseDown, ни MouseUp в дерево
    /// не пришли), последний зафиксированный обычный клик лежал за окном
    /// <see cref="MenuCloseRecentMouseActivityWindowMs"/> (2000 мс) — любой путь с окнами
    /// давности не срабатывает в принципе. Исключения: курсор над пунктом меню
    /// (overMenuItem — выбор пункта строку не меняет), наличие снимка клика (работают
    /// штатные пути A/C/snapshot), отсутствие попап-клика (ESC/программное закрытие/
    /// потеря фокуса окна).
    /// </summary>
    /// <param name="overTreeRow">Курсор в момент закрытия над строкой дерева (hit-test по координатам).</param>
    /// <param name="overMenuItem">Курсор в момент закрытия над пунктом меню.</param>
    /// <param name="snapshotPresent">Присутствовал ли снимок клика, закрывавшего меню (путь A/C).</param>
    /// <param name="clickDuringMenuOpen">
    /// Признак того, что во время открытого меню был обычный левый клик по ПОПАПУ меню дерева
    /// (метка Environment.TickCount из обработчика PreviewMouseLeftButtonDown/PointerPressed,
    /// подписанного на само меню при открытии).
    /// </param>
    public static bool ShouldRestoreSelectionAfterMenuClose(
        bool overTreeRow,
        bool overMenuItem,
        bool snapshotPresent,
        bool clickDuringMenuOpen)
    {
        if (!overTreeRow || overMenuItem)
            return false;
        if (snapshotPresent)
            return false;
        if (!clickDuringMenuOpen)
            return false;
        return true;
    }

    /// <summary>
    /// Нужно ли вернуть клавиатурный фокус дереву после закрытия контекстного меню
    /// (issue #340, 11-я итерация). Комментарий 7OH 28/28: после пропажи выделения дерево
    /// теряет клавиатурный фокус — стрелки не работают, TAB уходит на кнопку сворачивания.
    /// Фокус возвращаем, когда закрылось меню ДЕРЕВА, окно сохраняет клавиатурный фокус
    /// (IsKeyboardFocusWithin) и до открытия меню фокус был в дереве; либо пользователь
    /// явно кликал по попапу с курсором над строкой (восстановление выбора подразумевает
    /// и возврат управления деревом). Открытое модальное окно — фокус не трогаем.
    /// </summary>
    /// <param name="isTreeMenuClosed">Закрылось ли контекстное меню ДЕРЕВА.</param>
    /// <param name="focusWasInTreeBeforeMenuOpen">Был ли клавиатурный фокус в дереве до открытия меню.</param>
    /// <param name="focusStillWithinWindow">Остался ли клавиатурный фокус в пределах окна (IsKeyboardFocusWithin).</param>
    /// <param name="modalDialogOpen">Открыто ли модальное диалоговое окно (фокус не отбираем).</param>
    /// <param name="clickDuringMenuOpen">Был ли обычный левый клик по попапу меню (признак работы с деревом).</param>
    /// <param name="overTreeRow">Курсор в момент закрытия над строкой дерева.</param>
    public static bool ShouldReturnKeyboardFocusToTree(
        bool isTreeMenuClosed,
        bool focusWasInTreeBeforeMenuOpen,
        bool focusStillWithinWindow,
        bool modalDialogOpen,
        bool clickDuringMenuOpen = false,
        bool overTreeRow = false)
    {
        if (!isTreeMenuClosed || modalDialogOpen || !focusStillWithinWindow)
            return false;
        if (focusWasInTreeBeforeMenuOpen)
            return true;
        // Меню открыто не из дерева (например, с кнопки с собственным меню), но пользователь
        // кликал по попапу с курсором над строкой — он работал с деревом, возвращаем фокус.
        return clickDuringMenuOpen && overTreeRow;
    }

    /// <summary>
    /// Единая строка решения восстановления после закрытия меню дерева (issue #340,
    /// 11-я итерация): пишется в журнал MenuCloseTrace в момент MenuClosed. Поля: итоговое
    /// решение (restore), причина (rowUnderCursor / esc / menuItem / snapshot / noClick /
    /// notOverRow), сигналы (clickDuringOpen, overTreeRow, overMenuItem) и решение по
    /// возврату клавиатурного фокуса (focusRestore). Общий формат для WPF и Avalonia.
    /// </summary>
    /// <param name="restore">Принято ли решение восстанавливать выбор строки под курсором.</param>
    /// <param name="reason">Короткая причина решения (для разбора по логу).</param>
    /// <param name="clickDuringOpen">Был ли клик по попапу меню во время открытого меню.</param>
    /// <param name="overTreeRow">Курсор в момент закрытия над строкой дерева.</param>
    /// <param name="overMenuItem">Курсор в момент закрытия над пунктом меню.</param>
    /// <param name="focusRestore">Нужно ли вернуть клавиатурный фокус дереву.</param>
    public static string BuildMenuCloseDecisionLine(
        bool restore,
        string reason,
        bool clickDuringOpen,
        bool overTreeRow,
        bool overMenuItem,
        bool focusRestore)
    {
        return $"MenuCloseDecision: restore={(restore ? "true" : "false")}, reason={reason}, " +
               $"clickDuringOpen={(clickDuringOpen ? "true" : "false")}, " +
               $"overTreeRow={(overTreeRow ? "true" : "false")}, " +
               $"overMenuItem={(overMenuItem ? "true" : "false")}, " +
               $"focusRestore={(focusRestore ? "true" : "false")}";
    }

    /// <summary>
    /// Действия стабилизации выделения после клика, которым закрыли контекстное меню
    /// (issue #340, новая стратегия). Список намеренно минимален: только установка
    /// одиночного выбора по данным (SelectTreeRowByData/SelectRow). Действий «сбросить
    /// набор мультивыделения» (ClearBatchSelection) или «переключить строку набора»
    /// (ToggleBatchSelection) здесь НЕТ — стабилизация никогда не трогает набор
    /// «для выделенных».
    /// </summary>
    public enum SelectionRestoreAction
    {
        /// <summary>Восстановление не требуется (выбор уже корректен или пользователь перевыбрал).</summary>
        None,

        /// <summary>Восстановить выбор по данным базы (идемпотентно, без сброса набора).</summary>
        SelectByData
    }

    /// <summary>
    /// Решение стабилизации для целевой базы (issue #340): нужно ли восстанавливать
    /// выбор по данным. Идемпотентность: если целевая база УЖЕ выбрана в модели и её
    /// контейнер подсвечен — восстановление не требуется; повторное применение
    /// (SelectTreeRowByData) при уже установленном выборе не меняет состояние модели.
    /// Если пользователь успел перевыбрать ДРУГУЮ строку — стабилизация не вмешивается
    /// (вернёт <see cref="SelectionRestoreAction.None"/>). В остальных случаях (цель не
    /// выбрана или контейнер потерял IsSelected из-за переработки виртуализацией) —
    /// выбор восстанавливается по данным.
    /// </summary>
    /// <param name="selectedInfobase">Текущая выбранная база модели (может быть null).</param>
    /// <param name="target">База, выбранная кликом, которым закрыли меню.</param>
    /// <param name="containerIsSelected">Подсвечен ли контейнер целевой строки (true, если контейнер реализован).</param>
    public static SelectionRestoreAction DecideSelectionRestore(
        Infobase? selectedInfobase, Infobase? target, bool containerIsSelected)
    {
        if (target is null)
            return SelectionRestoreAction.None;
        // Цель уже выбрана и контейнер подсвечен — восстанавливать нечего (идемпотентность).
        if (ReferenceEquals(selectedInfobase, target) && containerIsSelected)
            return SelectionRestoreAction.None;
        // Пользователь перевыбрал другую строку — стабилизация не вмешивается.
        if (selectedInfobase is not null && !ReferenceEquals(selectedInfobase, target))
            return SelectionRestoreAction.None;
        return SelectionRestoreAction.SelectByData;
    }

    /// <summary>
    /// Нужна ли «догоняющая» стабилизация для НЕреализованного контейнера
    /// (issue #340, F1, план 0.3.9.306): контейнер целевой строки ещё не реализован
    /// виртуализацией (Recycling после закрытия попапа) — WPF TreeView/Avalonia не
    /// подсвечивают строку без контейнера, а подписка на LayoutUpdated может
    /// закончиться раньше, чем контейнер появится. Критерий запуска одноразового
    /// таймера (~800 мс): контейнер не реализован, пользователь не перевыбрал
    /// другую строку и окно «догоняния» не исчерпано.
    /// </summary>
    /// <param name="containerRealized">Реализован ли контейнер целевой строки в данный момент.</param>
    /// <param name="userReselected">Перевыбрал ли пользователь другую строку (стабилизация не вмешивается).</param>
    /// <param name="elapsedMs">Время, прошедшее с начала стабилизации.</param>
    /// <param name="maxChaseMs">Окно «догоняния» (рекомендация плана — ~800 мс).</param>
    public static bool ShouldRetryRestoreForUnrealizedContainer(
        bool containerRealized, bool userReselected, int elapsedMs, int maxChaseMs)
        => !containerRealized && !userReselected && elapsedMs >= 0 && elapsedMs < maxChaseMs;

    /// <summary>
    /// Нужно ли продолжить восстановление выбора цели стабилизации (issue #340, 0.3.9.322).
    /// По трассе 0.3.9.319 самый частый сценарий пользователя — клик по строке дерева при
    /// ОТКРЫТОМ контекстном меню (reason "clickBeforeMenuClose"): попап «проглатывает»
    /// клик, выбор не применяется вовсе (<c>SelectedInfobase == null</c>), а прежнее
    /// условие «!ReferenceEquals(null, target)» ошибочно считало это перевыбором и выходило
    /// с userReselected — выделение терялось. Продолжаем восстановление, когда выбор ПУСТ
    /// (цель известна — строка реального клика) или равен цели; прекращаем только при
    /// выборе ДРУГОЙ базы (пользователь сам перевыбрал — не вмешиваемся).
    /// </summary>
    public static bool ShouldContinueRestore(Infobase? currentSelection, Infobase target)
        => currentSelection is null || ReferenceEquals(currentSelection, target);

    /// <summary>
    /// Нужно ли снять мультивыделение при клавиатурной навигации без модификаторов
    /// (issue #350): набор «для выделенных» активен и целевая строка отличается от
    /// «текущей». Семантика обычного клика: движение курсором стрелками ↑/↓ снимает
    /// пометки, как клик мышью. Если целевая строка совпадает с текущей (стрелка ничего
    /// не двигает) или набор пуст — ничего не трогаем. Идентификаторы сравниваются
    /// регистрозависимо (Ordinal), как в наборе мультивыделения.
    /// </summary>
    /// <param name="currentIds">Текущий набор идентификаторов мультивыделения.</param>
    /// <param name="currentId">Идентификатор «текущей» (последней выбранной) базы.</param>
    /// <param name="targetId">Идентификатор целевой строки навигации.</param>
    public static bool ShouldClearBatchOnKeyboardNavigation(
        IReadOnlyCollection<string>? currentIds, string? currentId, string? targetId)
        => currentIds is { Count: > 0 }
           && !string.Equals(currentId, targetId, StringComparison.Ordinal);

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal))
                return i;
        return -1;
    }
}