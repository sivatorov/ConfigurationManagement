using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты чистой логики мультивыделения «для выделенных»:
/// правило секций дерева («Закреплённые» vs обычный список, issue #326)
/// и построение набора при правом клике (issue #313).
/// </summary>
public sealed class BatchSelectionHelperTests
{
    // ======================= Issue #326: секции не смешиваются =======================

    [Fact]
    public void ApplyModifiedClick_CtrlClickInOtherSection_ClearsCurrentSetAndStartsNew()
    {
        // Набор собран в обычном списке (секция не закреплённая)…
        var current = new[] { "regular-1", "regular-2" };

        // …Ctrl-клик по закреплённой строке не добавляет её к обычным, а начинает новый набор.
        var result = BatchSelectionHelper.ApplyModifiedClick(
            current,
            currentSectionIsPinned: false,
            targetId: "pinned-1",
            targetSectionIsPinned: true,
            modifier: "Ctrl");

        Assert.Equal(new[] { "pinned-1" }, result.OrderBy(x => x));
    }

    [Fact]
    public void ApplyModifiedClick_CtrlClickInsideSameSection_TogglesOnlyTarget()
    {
        var current = new[] { "regular-1" };

        // Ctrl+клик по второй обычной строке — добавление.
        var added = BatchSelectionHelper.ApplyModifiedClick(
            current, currentSectionIsPinned: false, "regular-2", targetSectionIsPinned: false, "Ctrl");
        Assert.Equal(new[] { "regular-1", "regular-2" }, added.OrderBy(x => x));

        // Повторный Ctrl+клик по той же строке — снятие (toggle).
        var removed = BatchSelectionHelper.ApplyModifiedClick(
            added, currentSectionIsPinned: false, "regular-2", targetSectionIsPinned: false, "Ctrl");
        Assert.Equal(new[] { "regular-1" }, removed.OrderBy(x => x));
    }

    [Fact]
    public void ApplyModifiedClick_ShiftRangeInsidePinnedSection_DoesNotIncludeRegularRows()
    {
        // Закреплённые строки идут в видимом порядке первыми; обычный список ниже.
        // Shift-диапазон в закреплениях должен пройти ТОЛЬКО по закреплённым строкам.
        var pinnedOrder = new[] { "pinned-1", "pinned-2", "pinned-3" };

        var result = BatchSelectionHelper.ApplyModifiedClick(
            currentIds: Array.Empty<string>(),
            currentSectionIsPinned: false,
            targetId: "pinned-3",
            targetSectionIsPinned: true,
            modifier: "Shift",
            visibleOrder: pinnedOrder,
            anchorId: "pinned-1");

        Assert.Equal(pinnedOrder, result.OrderBy(x => x));
    }

    [Fact]
    public void ApplyModifiedClick_ShiftClickFromOtherSectionAnchor_SelectsOnlyTarget()
    {
        // Якорь (обычная база) не принадлежит секции закреплений: диапазон строиться
        // не должен — в набор попадает только строка под Shift-кликом (#326).
        var current = new[] { "regular-anchor" };

        var result = BatchSelectionHelper.ApplyModifiedClick(
            current,
            currentSectionIsPinned: false,
            targetId: "pinned-5",
            targetSectionIsPinned: true,
            modifier: "Shift",
            visibleOrder: new[] { "pinned-1", "pinned-2", "pinned-3", "pinned-4", "pinned-5" },
            anchorId: "regular-anchor");

        Assert.Equal(new[] { "pinned-5" }, result.OrderBy(x => x));
    }

    [Fact]
    public void ApplyModifiedClick_ShiftRangeCrossesSections_ClearsOtherSectionFirst()
    {
        // Набор в закреплениях, Shift-клик по обычной строке: набор очищается,
        // диапазон строится по обычному порядку от якоря (если он в секции) до цели.
        var current = new[] { "pinned-1", "pinned-2" };

        var result = BatchSelectionHelper.ApplyModifiedClick(
            current,
            currentSectionIsPinned: true,
            targetId: "regular-3",
            targetSectionIsPinned: false,
            modifier: "Shift",
            visibleOrder: new[] { "regular-1", "regular-2", "regular-3" },
            anchorId: "regular-1");

        Assert.Equal(new[] { "regular-1", "regular-2", "regular-3" }, result.OrderBy(x => x));
    }

    [Fact]
    public void ApplyModifiedClick_NullOrEmptyTarget_ReturnsCurrentSet()
    {
        var current = new[] { "regular-1" };

        var noCtrlTarget = BatchSelectionHelper.ApplyModifiedClick(
            current, currentSectionIsPinned: false, null, targetSectionIsPinned: false, "Ctrl");
        Assert.Equal(current, noCtrlTarget);

        var noShiftTarget = BatchSelectionHelper.ApplyModifiedClick(
            current, currentSectionIsPinned: false, null, targetSectionIsPinned: false, "Shift");
        Assert.Equal(current, noShiftTarget);
    }

    // ======================= Issue #313: правый клик набор не меняет =======================

    [Fact]
    public void BuildRightClickSet_RowWasSimplyCurrent_NotAddedToSet()
    {
        // Правый клик сам по себе набор не меняет: «бывшая текущая» (выбранная
        // без Ctrl) попадает в набор НЕ здесь, а на этапе Ctrl-клика (issue #313,
        // см. ApplyModifiedClick + includeId). Для BuildRightClickSet действует
        // прежнее правило: строка, которой нет в наборе, не добавляется.
        var batch = new[] { "b", "c" };

        var result = BatchSelectionHelper.BuildRightClickSet(batch, rightClickedId: "c");

        Assert.Equal(new[] { "b", "c" }, result.OrderBy(x => x));
        Assert.DoesNotContain("a", result);
    }

    [Fact]
    public void BuildRightClickSet_RightClickOnUnmarkedRow_DoesNotAddIt()
    {
        var batch = new[] { "x", "y" };

        var result = BatchSelectionHelper.BuildRightClickSet(batch, rightClickedId: "z");

        Assert.Equal(new[] { "x", "y" }, result.OrderBy(x => x));
        Assert.DoesNotContain("z", result);
    }

    [Fact]
    public void BuildRightClickSet_RightClickOnMarkedRow_KeepsIt()
    {
        var batch = new[] { "x", "y" };

        var result = BatchSelectionHelper.BuildRightClickSet(batch, rightClickedId: "y");

        Assert.Equal(new[] { "x", "y" }, result.OrderBy(x => x));
    }

    [Fact]
    public void BuildRightClickSet_EmptySet_StaysEmpty()
    {
        var result = BatchSelectionHelper.BuildRightClickSet(Array.Empty<string>(), rightClickedId: "a");
        Assert.Empty(result);
    }

    // ======================= Issue #313: первый Ctrl-клик добавляет «текущую» =======================

    [Fact]
    public void ApplyModifiedClick_FirstCtrlClickOnOtherRow_AddsCurrentAndTarget()
    {
        // Правило 7OH (issue #313): «при клике с контролом не на текущей строке,
        // ставить внутреннюю галку выделения текущей строке тоже». Первый Ctrl-клик
        // по строке, отличной от «текущей» ("a"), добавляет в набор и "a", и цель "b".
        var set = new HashSet<string>(StringComparer.Ordinal);

        var result = BatchSelectionHelper.ApplyModifiedClick(
            set,
            currentSectionIsPinned: false,
            targetId: "b",
            targetSectionIsPinned: false,
            modifier: "Ctrl",
            includeId: "a",
            includeSectionIsPinned: false);

        Assert.Equal(new[] { "a", "b" }, result.OrderBy(x => x));
    }

    [Fact]
    public void ApplyModifiedClick_FirstCtrlClickOnCurrentRow_TogglesOnlyCurrent()
    {
        // Ctrl-клик по самой «текущей» строке — как раньше: строка просто входит
        // в набор (toggle), ничего лишнего не добавляется.
        var result = BatchSelectionHelper.ApplyModifiedClick(
            Array.Empty<string>(),
            currentSectionIsPinned: false,
            targetId: "a",
            targetSectionIsPinned: false,
            modifier: "Ctrl",
            includeId: "a",
            includeSectionIsPinned: false);

        Assert.Equal(new[] { "a" }, result.OrderBy(x => x));
    }

    [Fact]
    public void ApplyModifiedClick_CtrlClickWithNonEmptySet_KeepsToggleOnly()
    {
        // Набор уже не пуст — повторные Ctrl-клики остаются точечным toggle:
        // «текущая» не «допрыгивает» в набор на каждом клике.
        var current = new[] { "a", "b" };

        // "a" (текущая) уже в наборе — ничего не меняется кроме toggle цели "c".
        var added = BatchSelectionHelper.ApplyModifiedClick(
            current, currentSectionIsPinned: false, "c", targetSectionIsPinned: false, "Ctrl",
            includeId: "a", includeSectionIsPinned: false);
        Assert.Equal(new[] { "a", "b", "c" }, added.OrderBy(x => x));

        // Текущая "z" вне набора при непустом наборе НЕ добавляется (только toggle цели).
        var detached = BatchSelectionHelper.ApplyModifiedClick(
            current, currentSectionIsPinned: false, "c", targetSectionIsPinned: false, "Ctrl",
            includeId: "z", includeSectionIsPinned: false);
        Assert.Equal(new[] { "a", "b", "c" }, detached.OrderBy(x => x));
    }

    [Fact]
    public void ApplyModifiedClick_FirstCtrlClick_CurrentInOtherSection_NotAdded()
    {
        // Правило секций (#326): «текущая» из обычного списка не добавляется в набор,
        // который начинается Ctrl-кликом в «Закреплённых» (и наоборот).
        var result = BatchSelectionHelper.ApplyModifiedClick(
            Array.Empty<string>(),
            currentSectionIsPinned: false,
            targetId: "pinned-1",
            targetSectionIsPinned: true,
            modifier: "Ctrl",
            includeId: "regular-a",
            includeSectionIsPinned: false);

        Assert.Equal(new[] { "pinned-1" }, result.OrderBy(x => x));
        Assert.DoesNotContain("regular-a", result);
    }

    [Fact]
    public void ApplyModifiedClick_FirstCtrlClickInPinnedSection_AddsPinnedCurrentAndTarget()
    {
        // То же правило работает внутри «Закреплённых»: текущая закреплённая строка
        // добавляется вместе с целью Ctrl-клика (обе в секции закреплений).
        var result = BatchSelectionHelper.ApplyModifiedClick(
            Array.Empty<string>(),
            currentSectionIsPinned: false,
            targetId: "pinned-2",
            targetSectionIsPinned: true,
            modifier: "Ctrl",
            includeId: "pinned-1",
            includeSectionIsPinned: true);

        Assert.Equal(new[] { "pinned-1", "pinned-2" }, result.OrderBy(x => x));
    }

    // ======================= Регрессия полного сценария #313 =======================

    [Fact]
    public void Regression313_PlainClickThenCtrlClicksThenRightClick_KeepsFullSet()
    {
        // Сценарий из комментария пользователя к issue #313:
        // строка "a" была просто текущей (обычный клик, набор пуст), затем
        // Ctrl-кликами добавлены "b" и "c"; правый клик по "c" не должен терять
        // ни одной строки: "a" теперь входит в набор с первого Ctrl-клика
        // (как и просил пользователь), "b"/"c" — обычные Ctrl-toggle.
        var set = new HashSet<string>(StringComparer.Ordinal);

        // Обычный клик по "a" в UI вызывает ClearBatchSelection — набор остаётся пустым,
        // а UI передаёт "a" как includeId (текущую строку) при первом Ctrl-клике.
        Assert.Empty(set);

        set = BatchSelectionHelper.ApplyModifiedClick(set, currentSectionIsPinned: false, "b", false, "Ctrl",
            includeId: "a", includeSectionIsPinned: false);
        set = BatchSelectionHelper.ApplyModifiedClick(set, currentSectionIsPinned: false, "c", false, "Ctrl",
            includeId: "a", includeSectionIsPinned: false);

        var rightClick = BatchSelectionHelper.BuildRightClickSet(set, rightClickedId: "c");

        Assert.Equal(new[] { "a", "b", "c" }, rightClick.OrderBy(x => x));
    }

    [Fact]
    public void Regression313_FirstRowMarkedWithCtrl_SurvivesRightClick()
    {
        // Когда первую строку сразу пометили Ctrl (как описывает пользователь),
        // после правого клика она не должна пропадать.
        var set = new HashSet<string>(StringComparer.Ordinal);
        set = BatchSelectionHelper.ApplyModifiedClick(set, currentSectionIsPinned: false, "a", false, "Ctrl");
        set = BatchSelectionHelper.ApplyModifiedClick(set, currentSectionIsPinned: false, "b", false, "Ctrl");

        var rightClick = BatchSelectionHelper.BuildRightClickSet(set, rightClickedId: "b");

        Assert.Equal(new[] { "a", "b" }, rightClick.OrderBy(x => x));
    }

    // ======================= Issue #326: порядок закреплённой секции по данным узла =======================

    [Fact]
    public void BuildPinnedSectionOrder_FromWrappedItems_ReturnsBasesInNodeOrder()
    {
        // Узел «Закреплённые» несёт обёртки PinnedInfobaseItem (issue #314): порядок
        // Shift-диапазона строится по данным узла, а не по контейнерам (виртуализация
        // может не реализовать контейнеры вне видимой области — обход вернул бы пустой
        // порядок, и диапазон уходил в общий список, issue #326).
        var first = new Infobase { Id = "pinned-1", Name = "Первая" };
        var second = new Infobase { Id = "pinned-2", Name = "Вторая" };
        var third = new Infobase { Id = "pinned-3", Name = "Третья" };

        var order = BatchSelectionHelper.BuildPinnedSectionOrder(new object[]
        {
            new PinnedInfobaseItem(first),
            new PinnedInfobaseItem(second),
            new PinnedInfobaseItem(third)
        });

        Assert.Equal(new[] { first, second, third }, order);
        Assert.Equal(new[] { "pinned-1", "pinned-2", "pinned-3" }, order.Select(x => x.Id).ToArray());
    }

    [Fact]
    public void BuildPinnedSectionOrder_MixedRawAndWrapped_DeduplicatesSameBase()
    {
        // Допустимый резерв: узел может содержать и обёртки, и голые базы; одна и та же
        // база не должна повторяться в порядке секции.
        var only = new Infobase { Id = "pinned-1" };
        var other = new Infobase { Id = "pinned-2" };

        var order = BatchSelectionHelper.BuildPinnedSectionOrder(new object[]
        {
            new PinnedInfobaseItem(only),
            only, // дубль той же базы — пропускается
            new PinnedInfobaseItem(other)
        });

        Assert.Equal(new[] { only, other }, order);
    }

    [Fact]
    public void BuildPinnedSectionOrder_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(BatchSelectionHelper.BuildPinnedSectionOrder(null!));
        Assert.Empty(BatchSelectionHelper.BuildPinnedSectionOrder(Array.Empty<object>()));
    }

    [Fact]
    public void Unwrap_PinnedWrapper_ReturnsBase()
    {
        var ib = new Infobase { Id = "pinned-1" };
        var wrapped = new PinnedInfobaseItem(ib);

        Assert.Same(ib, BatchSelectionHelper.Unwrap(wrapped));
        Assert.Same(ib, BatchSelectionHelper.Unwrap(ib));
        Assert.Null(BatchSelectionHelper.Unwrap(null));
        Assert.Null(BatchSelectionHelper.Unwrap("не база"));
    }

    [Fact]
    public void ApplyModifiedClick_ShiftInPinnedSection_LongerRegularList_OnlyPinnedRange()
    {
        // Сценарий 7OH (issue #326): обычный список заметно длиннее закреплённого.
        // Shift-клик от одной закреплённой базы до другой должен выделить ТОЛЬКО
        // закреплённый диапазон, а не «полсписка обычного» (порядок передаётся уже
        // в пределах секции — обычные строки в него не попадают).
        var pinnedOrder = new[] { "pinned-1", "pinned-2", "pinned-3", "pinned-4" };

        var result = BatchSelectionHelper.ApplyModifiedClick(
            currentIds: Array.Empty<string>(),
            currentSectionIsPinned: false,
            targetId: "pinned-4",
            targetSectionIsPinned: true,
            modifier: "Shift",
            visibleOrder: pinnedOrder,
            anchorId: "pinned-2");

        Assert.Equal(new[] { "pinned-2", "pinned-3", "pinned-4" }, result.OrderBy(x => x));
        Assert.All(result, id => Assert.StartsWith("pinned-", id, StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyModifiedClick_ShiftInPinnedSection_EmptyOrder_SelectsOnlyTarget()
    {
        // Пустой порядок секции (например, узел «Закреплённые» отсутствует): Shift-клик
        // НЕ должен проваливаться в резерв по общему списку — выбирается только цель (#326).
        var result = BatchSelectionHelper.ApplyModifiedClick(
            currentIds: Array.Empty<string>(),
            currentSectionIsPinned: false,
            targetId: "pinned-1",
            targetSectionIsPinned: true,
            modifier: "Shift",
            visibleOrder: Array.Empty<string>(),
            anchorId: "pinned-1");

        Assert.Equal(new[] { "pinned-1" }, result.OrderBy(x => x));
    }

    [Fact]
    public void ApplyModifiedClick_CtrlClicksInsidePinnedSection_DoNotTouchRegularList()
    {
        // Ctrl-клики по закреплённым строкам остаются в секции закреплений:
        // обычные строки в набор не попадают ни первым кликом (текущая из той же
        // секции добавляется вместе с целью — issue #313), ни последующими (#326).
        var set = new HashSet<string>(StringComparer.Ordinal);
        set = BatchSelectionHelper.ApplyModifiedClick(
            set, currentSectionIsPinned: false, "pinned-1", targetSectionIsPinned: true, "Ctrl",
            includeId: "pinned-0", includeSectionIsPinned: true);
        set = BatchSelectionHelper.ApplyModifiedClick(
            set, currentSectionIsPinned: true, "pinned-2", targetSectionIsPinned: true, "Ctrl");

        Assert.Equal(new[] { "pinned-0", "pinned-1", "pinned-2" }, set.OrderBy(x => x));
        Assert.All(set, id => Assert.StartsWith("pinned-", id, StringComparison.Ordinal));
    }

    [Fact]
    public void Regression313_ShiftRangeThenRightClick_KeepsRange()
    {
        // Shift-диапазон от "a" до "c" (якорь — последняя обычная строка);
        // правый клик по границе диапазона не снимает строки.
        var set = new HashSet<string>(StringComparer.Ordinal);
        set = BatchSelectionHelper.ApplyModifiedClick(
            set,
            currentSectionIsPinned: false,
            targetId: "c",
            targetSectionIsPinned: false,
            modifier: "Shift",
            visibleOrder: new[] { "a", "b", "c" },
            anchorId: "a");

        var rightClick = BatchSelectionHelper.BuildRightClickSet(set, rightClickedId: "c");

        Assert.Equal(new[] { "a", "b", "c" }, rightClick.OrderBy(x => x));
    }

    // ======================= Issue #340: клик, закрывший контекстное меню =======================
    // Новая стратегия (0.3.9.304): выбор применяется ШТАТНЫМ путём — повторной доставкой
    // клика в дерево (по живому контейнеру); применение в момент закрытия меню остаётся
    // только как FALLBACK (Dispatcher.BeginInvoke / Dispatcher.UIThread.Post), а после
    // применения запускается стабилизация IsSelected (EnsureSelectionStable). Чистой логикой
    // остаются: критерий записи снимка (только простой левый клик без модификаторов),
    // дедупликация «хвоста» по времени+позиции и решение стабилизации — они тестируются ниже.

    // ============ IsSameClick — дедупликация по времени+позиции (issue #340) ============
    // Время — в миллисекундах единой шкалы (Environment.TickCount), long.

    private const long T0 = 1_000_000L;

    [Fact]
    public void IsSameClick_SameTimeAndPosition_ReturnsTrue()
    {
        // Повторная доставка того же клика (время и позиция совпадают) — это «хвост»
        // клика, которым закрыли контекстное меню: его нужно подавить.
        var snapshot = new BatchSelectionHelper.MenuCloseClickSnapshot("Left", T0, 100, 200);

        Assert.True(BatchSelectionHelper.IsSameClick(snapshot, "Left", T0, 100, 200));
    }

    [Fact]
    public void IsSameClick_SmallDriftWithinTolerance_ReturnsTrue()
    {
        // Смещение в пределах допуска (время до 300 мс, позиция до 12 px) — тот же клик:
        // платформы доставляют координаты повторного события с небольшим дрейфом.
        var snapshot = new BatchSelectionHelper.MenuCloseClickSnapshot("Left", T0, 100, 200);

        Assert.True(BatchSelectionHelper.IsSameClick(
            snapshot, "Left", T0 + 250, 108, 208));
    }

    [Fact]
    public void IsSameClick_LaterThanTolerance_ReturnsFalse()
    {
        // Прошло больше допуска — это НОВЫЙ клик пользователя (например, следующий
        // после паузы или двойной клик для запуска базы): обрабатываем штатно.
        var snapshot = new BatchSelectionHelper.MenuCloseClickSnapshot("Left", T0, 100, 200);

        Assert.False(BatchSelectionHelper.IsSameClick(
            snapshot, "Left", T0 + 301, 100, 200));
    }

    [Fact]
    public void IsSameClick_EarlierThanSnapshot_ReturnsFalse()
    {
        // Событие РАНЬШЕ снимка не может быть повторной доставкой того же клика —
        // снимок записывается в момент закрытия меню, «хвост» всегда позже.
        var snapshot = new BatchSelectionHelper.MenuCloseClickSnapshot("Left", T0, 100, 200);

        Assert.False(BatchSelectionHelper.IsSameClick(
            snapshot, "Left", T0 - 50, 100, 200));
    }

    [Fact]
    public void IsSameClick_OtherPositionBeyondTolerance_ReturnsFalse()
    {
        // Клик по другой строке/области дерева — новое действие пользователя.
        var snapshot = new BatchSelectionHelper.MenuCloseClickSnapshot("Left", T0, 100, 200);

        Assert.False(BatchSelectionHelper.IsSameClick(
            snapshot, "Left", T0 + 50, 200, 200));
    }

    [Fact]
    public void IsSameClick_OtherButton_ReturnsFalse()
    {
        // Правая кнопка не может быть повторной доставкой левого клика, закрывшего меню.
        var snapshot = new BatchSelectionHelper.MenuCloseClickSnapshot("Left", T0, 100, 200);

        Assert.False(BatchSelectionHelper.IsSameClick(
            snapshot, "Right", T0, 100, 200));
    }

    // ============ Новая стратегия 0.3.9.304: снимок только без модификаторов ============

    [Fact]
    public void Snapshot_OnlyForPlainLeftClick_ModifiersNotCaptured()
    {
        // Снимок клика, закрывшего меню, записывается ТОЛЬКО для простого левого клика
        // без модификаторов (issue #340): Ctrl/Shift-клики обрабатываются штатной логикой
        // мультивыделения (ToggleBatchSelection/SelectRange) и не должны дедуплицироваться
        // или «перевыбираться» снимком.
        Assert.True(BatchSelectionHelper.ShouldRecordMenuCloseSnapshot("Left", ctrlPressed: false, shiftPressed: false));

        Assert.False(BatchSelectionHelper.ShouldRecordMenuCloseSnapshot("Left", ctrlPressed: true, shiftPressed: false));
        Assert.False(BatchSelectionHelper.ShouldRecordMenuCloseSnapshot("Left", ctrlPressed: false, shiftPressed: true));
        Assert.False(BatchSelectionHelper.ShouldRecordMenuCloseSnapshot("Left", ctrlPressed: true, shiftPressed: true));
        Assert.False(BatchSelectionHelper.ShouldRecordMenuCloseSnapshot("Right", ctrlPressed: false, shiftPressed: false));
    }

    // ============ Новая стратегия 0.3.9.304: fallback и стабилизация по данным ============

    [Fact]
    public void FallbackApply_IsIdempotent_WhenSelectionAlreadyApplied()
    {
        // Fallback/стабилизация применяют выбор по данным (SelectTreeRowByData) — операция
        // идемпотентна: при уже установленном выборе она не меняет состояние модели.
        var target = new Infobase { Id = "b1", Name = "База" };

        // Цель УЖЕ выбрана и контейнер подсвечен — восстанавливать нечего (None).
        Assert.Equal(
            BatchSelectionHelper.SelectionRestoreAction.None,
            BatchSelectionHelper.DecideSelectionRestore(target, target, containerIsSelected: true));

        // Цель выбрана, но контейнер потерял IsSelected (переработка виртуализацией) —
        // восстановление подсветки по данным; повторный SelectTreeRowByData безопасен.
        Assert.Equal(
            BatchSelectionHelper.SelectionRestoreAction.SelectByData,
            BatchSelectionHelper.DecideSelectionRestore(target, target, containerIsSelected: false));

        // Выбор ещё не сделан (fallback: повторная доставка клика не пришла) — применяем.
        Assert.Equal(
            BatchSelectionHelper.SelectionRestoreAction.SelectByData,
            BatchSelectionHelper.DecideSelectionRestore(null, target, containerIsSelected: false));

        // Пользователь перевыбрал ДРУГУЮ строку — стабилизация не вмешивается.
        var other = new Infobase { Id = "b2", Name = "Другая" };
        Assert.Equal(
            BatchSelectionHelper.SelectionRestoreAction.None,
            BatchSelectionHelper.DecideSelectionRestore(other, target, containerIsSelected: false));
    }

    [Fact]
    public void Stabilization_DoesNotTouchBatchSelection()
    {
        // Модель действий стабилизации (issue #340) намеренно не содержит сброса набора
        // мультивыделения (ClearBatchSelection) или переключения строки набора
        // (ToggleBatchSelection): единственные варианты — «не вмешиваться» и «установить
        // одиночный выбор по данным». Оба не изменяют набор «для выделенных».
        var actions = Enum.GetValues<BatchSelectionHelper.SelectionRestoreAction>();
        Assert.Equal(2, actions.Length);
        Assert.Contains(BatchSelectionHelper.SelectionRestoreAction.None, actions);
        Assert.Contains(BatchSelectionHelper.SelectionRestoreAction.SelectByData, actions);

        // Практическая проверка типовых состояний: решение всегда в рамках разрешённого
        // множества и не может «снять» набор (такого действия в модели нет).
        var target = new Infobase { Id = "b1" };
        var selected = new Infobase { Id = "b1" };
        foreach (var containerSelected in new[] { true, false })
        {
            var action = BatchSelectionHelper.DecideSelectionRestore(selected, target, containerSelected);
            Assert.True(action is BatchSelectionHelper.SelectionRestoreAction.None
                or BatchSelectionHelper.SelectionRestoreAction.SelectByData);
        }
    }

    [Fact]
    public void IsSameClick_StillMatchesRepeatedDelivery()
    {
        // Регресс: повторная доставка того же клика, которым закрыли меню, распознаётся
        // по времени+позиции (для отмены fallback). Снимок записан в момент закрытия меню;
        // «хвост» доставляется в дерево вскоре после — с небольшим дрейфом координат.
        var snapshot = new BatchSelectionHelper.MenuCloseClickSnapshot("Left", T0, 100, 200);

        // «Хвост» через ~80 мс в той же позиции — тот же клик.
        Assert.True(BatchSelectionHelper.IsSameClick(snapshot, "Left", T0 + 80, 100, 200));
        // «Хвост» с малым дрейфом в пределах допуска — тоже тот же клик.
        Assert.True(BatchSelectionHelper.IsSameClick(snapshot, "Left", T0 + 120, 104, 198));
    }

    // ============ Стабилизация при нереализованном контейнере (issue #340, F2) ============

    [Fact]
    public void DecideSelectionRestore_UnrealizedContainer_ReturnsSelectByData()
    {
        // F2: видимая-но-нереализованная строка (контейнер ещё не создан/переработан
        // виртуализацией после закрытия попапа) НЕ считается согласованной — выбор
        // восстанавливается по данным (SelectTreeRowByData идемпотентен и «догонит»
        // подсветку, когда контейнер появится).
        var target = new Infobase { Id = "b1", Name = "База" };

        // Модель совпадает (fallback уже поставил SelectedInfobase), контейнера нет —
        // восстановление требуется.
        Assert.Equal(
            BatchSelectionHelper.SelectionRestoreAction.SelectByData,
            BatchSelectionHelper.DecideSelectionRestore(target, target, containerIsSelected: false));
    }

    [Fact]
    public void DecideSelectionRestore_UserReselected_ReturnsNone_EvenWhenContainerUnrealized()
    {
        // Пользователь успел перевыбрать ДРУГУЮ строку — стабилизация не вмешивается,
        // даже если контейнер целевой строки ещё не реализован (F2 не должен «воевать»
        // с новым действием пользователя).
        var target = new Infobase { Id = "b1", Name = "База" };
        var other = new Infobase { Id = "b2", Name = "Другая" };

        Assert.Equal(
            BatchSelectionHelper.SelectionRestoreAction.None,
            BatchSelectionHelper.DecideSelectionRestore(other, target, containerIsSelected: false));
    }

    [Fact]
    public void DecideSelectionRestore_NullSelection_UnrealizedContainer_RequiresRestore()
    {
        // Ничего не выбрано, контейнер не реализован — восстановление по данным (fallback).
        var target = new Infobase { Id = "b1", Name = "База" };

        Assert.Equal(
            BatchSelectionHelper.SelectionRestoreAction.SelectByData,
            BatchSelectionHelper.DecideSelectionRestore(null, target, containerIsSelected: false));
    }

    // ============ Догоняющая стабилизация для нереализованного контейнера (issue #340, F1) ============

    [Fact]
    public void ShouldRetryRestore_UnrealizedContainer_WithinWindow_ReturnsTrue()
    {
        // F1: контейнер целевой строки ещё не реализован (Recycling после закрытия
        // попапа), пользователь не перевыбрал и окно «догоняния» (~800 мс) не исчерпано —
        // одноразовая повторная попытка нужна: когда контейнер появится, выбор по данным
        // (SelectTreeRowByData/SelectRow) «догонит» подсветку.
        Assert.True(BatchSelectionHelper.ShouldRetryRestoreForUnrealizedContainer(
            containerRealized: false, userReselected: false, elapsedMs: 0, maxChaseMs: 800));
        Assert.True(BatchSelectionHelper.ShouldRetryRestoreForUnrealizedContainer(
            containerRealized: false, userReselected: false, elapsedMs: 799, maxChaseMs: 800));
    }

    [Fact]
    public void ShouldRetryRestore_RealizedContainer_ReturnsFalse()
    {
        // Контейнер уже реализован — догоняющий таймер не нужен: стабилизация работает
        // через подписку на LayoutUpdated (SelectTreeRowByData сразу подсветит строку).
        Assert.False(BatchSelectionHelper.ShouldRetryRestoreForUnrealizedContainer(
            containerRealized: true, userReselected: false, elapsedMs: 0, maxChaseMs: 800));
    }

    [Fact]
    public void ShouldRetryRestore_UserReselected_ReturnsFalse()
    {
        // Пользователь успел перевыбрать другую строку — «догоняние» не вмешивается
        // (стабилизация отвечает только за целевой клик).
        Assert.False(BatchSelectionHelper.ShouldRetryRestoreForUnrealizedContainer(
            containerRealized: false, userReselected: true, elapsedMs: 0, maxChaseMs: 800));
    }

    [Fact]
    public void ShouldRetryRestore_BeyondWindow_ReturnsFalse()
    {
        // Окно «догоняния» исчерпано (>= maxChaseMs) или время ушло в прошлое — дальнейшие
        // попытки не нужны: состояние отдаётся штатной логике пользователя.
        Assert.False(BatchSelectionHelper.ShouldRetryRestoreForUnrealizedContainer(
            containerRealized: false, userReselected: false, elapsedMs: 800, maxChaseMs: 800));
        Assert.False(BatchSelectionHelper.ShouldRetryRestoreForUnrealizedContainer(
            containerRealized: false, userReselected: false, elapsedMs: 801, maxChaseMs: 800));
        Assert.False(BatchSelectionHelper.ShouldRetryRestoreForUnrealizedContainer(
            containerRealized: false, userReselected: false, elapsedMs: -1, maxChaseMs: 800));
    }

    // ============ Стабилизация при ПУСТОМ выборе (issue #340, 0.3.9.322) ============

    [Fact]
    public void ShouldContinueRestore_NullSelection_ReturnsTrue()
    {
        // Трасса 0.3.9.319: клик по строке при открытом меню «проглочен» попапом —
        // SelectedInfobase == null, выбор не применён вовсе. Цель известна (строка клика),
        // восстановление нужно продолжить (раньше выходили с userReselected и теряли выбор).
        var target = new Infobase { Id = "b1", Name = "База" };

        Assert.True(BatchSelectionHelper.ShouldContinueRestore(null, target));
    }

    [Fact]
    public void ShouldContinueRestore_SameSelection_ReturnsTrue()
    {
        // Цель уже выбрана в модели — продолжаем: следующий проход проверит подсветку
        // контейнера (matches) и при расхождении восстановит её по данным.
        var target = new Infobase { Id = "b1", Name = "База" };

        Assert.True(BatchSelectionHelper.ShouldContinueRestore(target, target));
    }

    [Fact]
    public void ShouldContinueRestore_DifferentSelection_ReturnsFalse()
    {
        // Пользователь успел перевыбрать ДРУГУЮ строку (не-null и не цель) — стабилизация
        // не вмешивается (это «перевыбор», а не потерянный клик).
        var target = new Infobase { Id = "b1", Name = "База" };
        var other = new Infobase { Id = "b2", Name = "Другая" };

        Assert.False(BatchSelectionHelper.ShouldContinueRestore(other, target));
    }

    // ============ Мультивыделение и клавиатурная навигация (issue #350) ============

    [Fact]
    public void ShouldClearBatchOnKeyboardNavigation_ActiveSetAndDifferentTarget_ReturnsTrue()
    {
        // Набор активен, стрелка двигает курсор на ДРУГУЮ базу — мультивыделение снимается,
        // как при обычном клике мышью.
        var set = new HashSet<string>(new[] { "b1", "b2" }, StringComparer.Ordinal);

        Assert.True(BatchSelectionHelper.ShouldClearBatchOnKeyboardNavigation(
            set, currentId: "b2", targetId: "b3"));
    }

    [Fact]
    public void ShouldClearBatchOnKeyboardNavigation_SameTarget_ReturnsFalse()
    {
        // Стрелка не меняет строку (цель == текущая) — набор не трогаем.
        var set = new HashSet<string>(new[] { "b1", "b2" }, StringComparer.Ordinal);

        Assert.False(BatchSelectionHelper.ShouldClearBatchOnKeyboardNavigation(
            set, currentId: "b2", targetId: "b2"));
    }

    [Fact]
    public void ShouldClearBatchOnKeyboardNavigation_EmptySet_ReturnsFalse()
    {
        // Пометок нет — очищать нечего.
        Assert.False(BatchSelectionHelper.ShouldClearBatchOnKeyboardNavigation(
            Array.Empty<string>(), currentId: "b1", targetId: "b2"));
    }

    [Fact]
    public void ShouldClearBatchOnKeyboardNavigation_NullSetOrIds_ReturnsFalse()
    {
        Assert.False(BatchSelectionHelper.ShouldClearBatchOnKeyboardNavigation(
            null, currentId: null, targetId: null));
        Assert.False(BatchSelectionHelper.ShouldClearBatchOnKeyboardNavigation(
            new HashSet<string>(new[] { "b1" }), currentId: null, targetId: null));
    }

    // ============ Расширенный признак стабилизации после закрытия меню (issue #340, 0.3.9.308) ============

    private const long StabilizeWindowMs = BatchSelectionHelper.MenuCloseStabilizeWindowMs;

    [Fact]
    public void ShouldStabilizeAfterMenuClose_SnapshotPresent_TrueForPlainLeftClick()
    {
        // Путь A/C (повторная доставка клика, закрывшего меню): снимок присутствовал —
        // обычный клик без модификаторов стабилизируется, даже если метка закрытия меню
        // не зафиксирована (например, _lastMenuCloseTick = 0).
        Assert.True(BatchSelectionHelper.ShouldStabilizeAfterMenuClose(
            snapshotPresent: true,
            isPlainLeftClickWithoutModifiers: true,
            lastMenuCloseTick: 0,
            nowTick: 1000,
            windowMs: StabilizeWindowMs));
    }

    [Fact]
    public void ShouldStabilizeAfterMenuClose_RecentMenuClose_TrueWithinWindow()
    {
        // Меню закрылось недавно (в пределах окна ~1,5 с), снимка нет (закрытие по ESC /
        // кликом мимо строки) — обычный клик стабилизируется.
        const long lastMenuCloseTick = 10_000;
        Assert.True(BatchSelectionHelper.ShouldStabilizeAfterMenuClose(
            snapshotPresent: false,
            isPlainLeftClickWithoutModifiers: true,
            lastMenuCloseTick: lastMenuCloseTick,
            nowTick: lastMenuCloseTick + StabilizeWindowMs,
            windowMs: StabilizeWindowMs));
        Assert.True(BatchSelectionHelper.ShouldStabilizeAfterMenuClose(
            snapshotPresent: false,
            isPlainLeftClickWithoutModifiers: true,
            lastMenuCloseTick: lastMenuCloseTick,
            nowTick: lastMenuCloseTick + 1,
            windowMs: StabilizeWindowMs));
    }

    [Fact]
    public void ShouldStabilizeAfterMenuClose_OutsideWindow_False()
    {
        // Меню закрылось ДАВНО (за пределами окна) — клик обрабатывается штатно,
        // стабилизация не требуется (и её запуск вмешивался бы в обычную работу).
        const long lastMenuCloseTick = 10_000;
        Assert.False(BatchSelectionHelper.ShouldStabilizeAfterMenuClose(
            snapshotPresent: false,
            isPlainLeftClickWithoutModifiers: true,
            lastMenuCloseTick: lastMenuCloseTick,
            nowTick: lastMenuCloseTick + StabilizeWindowMs + 1,
            windowMs: StabilizeWindowMs));
    }

    [Fact]
    public void ShouldStabilizeAfterMenuClose_NoMenuCloseNoSnapshot_False()
    {
        // Меню вообще не закрывалось (_lastMenuCloseTick = 0) и снимка нет — обычный
        // клик вне menu-close сценария не стабилизируется.
        Assert.False(BatchSelectionHelper.ShouldStabilizeAfterMenuClose(
            snapshotPresent: false,
            isPlainLeftClickWithoutModifiers: true,
            lastMenuCloseTick: 0,
            nowTick: 500,
            windowMs: StabilizeWindowMs));
    }

    [Fact]
    public void ShouldStabilizeAfterMenuClose_CtrlClick_False()
    {
        // Ctrl/Shift-клик (мультивыделение) стабилизацией не затрагивается — даже при
        // свежей метке закрытия меню: предикат требует клик без модификаторов.
        Assert.False(BatchSelectionHelper.ShouldStabilizeAfterMenuClose(
            snapshotPresent: true,
            isPlainLeftClickWithoutModifiers: false,
            lastMenuCloseTick: 10_000,
            nowTick: 10_100,
            windowMs: StabilizeWindowMs));
        Assert.False(BatchSelectionHelper.ShouldStabilizeAfterMenuClose(
            snapshotPresent: false,
            isPlainLeftClickWithoutModifiers: false,
            lastMenuCloseTick: 10_000,
            nowTick: 10_100,
            windowMs: StabilizeWindowMs));
    }

    // ============ Клик ПЕРЕД закрытием меню (issue #340, 0.3.9.314) ============
    // Второй реальный trace.json (0.3.9.311): MouseDown по строке приходит в дерево
    // ДО MenuClosed (snapshot=False, redelivery=False) — снимок не записывается (кнопка
    // отпущена к моменту закрытия), повторной доставки нет, и ни один штатный путь
    // стабилизацию не запускает. Если последний обычный клик по строке был
    // непосредственно перед закрытием меню дерева — стабилизацию нужно запускать
    // по цели этого клика (EnsureSelectionStable, причина "clickBeforeMenuClose").

    private const long PrecedingWindowMs = BatchSelectionHelper.MenuClosePrecedingClickWindowMs;

    [Fact]
    public void ShouldStabilizeForClickPrecedingMenuClose_WithinWindow_True()
    {
        // Клик по строке за ~200 мс до закрытия меню дерева — тот самый случай из
        // trace.json (между MouseDown и MenuClosed ~555 мс): снимка нет, но клик был
        // непосредственно перед закрытием — стабилизация нужна.
        const long clickTick = 10_000;
        Assert.True(BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose(
            clickTick, menuCloseTick: clickTick + 200, windowMs: PrecedingWindowMs));

        // Граница окна включительно (ровно 500 мс до закрытия — ещё «непосредственно»).
        Assert.True(BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose(
            clickTick, menuCloseTick: clickTick + PrecedingWindowMs, windowMs: PrecedingWindowMs));
    }

    [Fact]
    public void ShouldStabilizeForClickPrecedingMenuClose_BeyondWindow_False()
    {
        // Клик был ДАВНО (3 с до закрытия) — это не «клик, которым закрыли меню»:
        // стабилизация по нему не запускается (иначе вмешивалась бы в обычные клики).
        const long clickTick = 10_000;
        Assert.False(BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose(
            clickTick, menuCloseTick: clickTick + 3_000, windowMs: PrecedingWindowMs));
        Assert.False(BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose(
            clickTick, menuCloseTick: clickTick + PrecedingWindowMs + 1, windowMs: PrecedingWindowMs));
    }

    [Fact]
    public void ShouldStabilizeForClickPrecedingMenuClose_NoClick_False()
    {
        // Обычных кликов по строке дерева не было (метка 0) — стабилизировать нечего.
        Assert.False(BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose(
            0, menuCloseTick: 10_000, windowMs: PrecedingWindowMs));
    }

    [Fact]
    public void ShouldStabilizeForClickPrecedingMenuClose_MenuClosedBeforeClick_False()
    {
        // Меню закрылось РАНЬШЕ клика (обычный клик ПОСЛЕ закрытия) — это уже штатный
        // путь ShouldStabilizeAfterMenuClose («клик после закрытия», окно ~1,5 с), а не
        // «клик перед закрытием»: предикат должен вернуть false.
        const long clickTick = 10_000;
        Assert.False(BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose(
            clickTick, menuCloseTick: clickTick - 50, windowMs: PrecedingWindowMs));
    }

    // ============ Строка под курсором при закрытии меню (issue #340, 0.3.9.316) ============
    // Третий реальный trace.json (0.3.9.315): MenuClosed с курсором над строкой дерева
    // (overTreeRow=true), снимок клика не записан (guard-цепочка не прошла), а последний
    // обычный клик был за ~1,8 с до закрытия — вне окна "clickBeforeMenuClose" (500 мс),
    // поэтому ни один штатный путь стабилизацию не запускает. Если в окне ~2 с
    // (MenuCloseRecentMouseActivityWindowMs) был обычный клик без модификаторов, а меню
    // закрылось НЕ выбором пункта — выбор восстанавливается по строке ПОД КУРСОРОМ
    // (EnsureSelectionStable, причина "menuClosedOverRow").

    private const long RecentWindowMs = BatchSelectionHelper.MenuCloseRecentMouseActivityWindowMs;

    [Fact]
    public void ShouldRestoreSelectionForRowUnderCursor_OverRowWithRecentClick_True()
    {
        // Лог 0.3.9.315: обычный клик был ~1,8 с до закрытия меню (вне окна 500 мс, но
        // в пределах 2 с), курсор при закрытии над строкой дерева — восстановление нужно.
        const long clickTick = 10_000;
        Assert.True(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: false,
            recentClickWasPlainLeftWithoutModifiers: true,
            lastPlainClickTick: clickTick,
            menuCloseTick: clickTick + 1_800,
            nowTick: clickTick + 1_800,
            windowMs: RecentWindowMs));

        // Граница окна включительно (ровно 2 с до закрытия — ещё «недавний клик»).
        Assert.True(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: false,
            recentClickWasPlainLeftWithoutModifiers: true,
            lastPlainClickTick: clickTick,
            menuCloseTick: clickTick + RecentWindowMs,
            nowTick: clickTick + RecentWindowMs,
            windowMs: RecentWindowMs));
    }

    [Fact]
    public void ShouldRestoreSelectionForRowUnderCursor_CursorOverMenuItem_False()
    {
        // Закрытие выбором пункта меню: курсор над пунктом — строка дерева не меняется,
        // восстановление не запускается даже при свежем обычном клике.
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: true,
            overMenuItem: true,
            snapshotPresent: false,
            recentClickWasPlainLeftWithoutModifiers: true,
            lastPlainClickTick: 10_000,
            menuCloseTick: 10_200,
            nowTick: 10_200,
            windowMs: RecentWindowMs));
    }

    [Fact]
    public void ShouldRestoreSelectionForRowUnderCursor_CursorNotOverRow_False()
    {
        // Курсор вне строки дерева (клик мимо / служебная область) — цели для
        // восстановления нет.
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: false,
            overMenuItem: false,
            snapshotPresent: false,
            recentClickWasPlainLeftWithoutModifiers: true,
            lastPlainClickTick: 10_000,
            menuCloseTick: 10_200,
            nowTick: 10_200,
            windowMs: RecentWindowMs));
    }

    [Fact]
    public void ShouldRestoreSelectionForRowUnderCursor_NoRecentClick_False()
    {
        // Обычных кликов по строке дерева не было (метка 0) — закрытие ESC/программно
        // без мышиной активности: восстановление не запускается.
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: false,
            recentClickWasPlainLeftWithoutModifiers: true,
            lastPlainClickTick: 0,
            menuCloseTick: 10_200,
            nowTick: 10_200,
            windowMs: RecentWindowMs));
    }

    [Fact]
    public void ShouldRestoreSelectionForRowUnderCursor_MultiSelectClick_False()
    {
        // Последняя мышиная активность — НЕ обычный клик (Ctrl/Shift-клик, мультивыделение):
        // восстановление строки под курсором не трогает набор «для выделенных».
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: false,
            recentClickWasPlainLeftWithoutModifiers: false,
            lastPlainClickTick: 10_000,
            menuCloseTick: 10_200,
            nowTick: 10_200,
            windowMs: RecentWindowMs));
    }

    [Fact]
    public void ShouldRestoreSelectionForRowUnderCursor_ClickLongAgo_False()
    {
        // Клик был ДАВНО (3 с до закрытия — за пределами окна 2 с): это не свидетельство
        // «пользователь целился в строку под курсором», восстановление не запускается.
        const long clickTick = 10_000;
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: false,
            recentClickWasPlainLeftWithoutModifiers: true,
            lastPlainClickTick: clickTick,
            menuCloseTick: clickTick + 3_000,
            nowTick: clickTick + 3_000,
            windowMs: RecentWindowMs));
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: false,
            recentClickWasPlainLeftWithoutModifiers: true,
            lastPlainClickTick: clickTick,
            menuCloseTick: clickTick + RecentWindowMs + 1,
            nowTick: clickTick + RecentWindowMs + 1,
            windowMs: RecentWindowMs));
    }

    [Fact]
    public void ShouldRestoreSelectionForRowUnderCursor_SnapshotPresent_False()
    {
        // Снимок клика, закрывшего меню, записан — работают штатные пути A/C/snapshot,
        // восстановление по строке под курсором не нужно (избегаем двойной работы).
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: true,
            recentClickWasPlainLeftWithoutModifiers: true,
            lastPlainClickTick: 10_000,
            menuCloseTick: 10_200,
            nowTick: 10_200,
            windowMs: RecentWindowMs));
    }

    [Fact]
    public void ShouldRestoreSelectionForRowUnderCursor_ClickAfterClose_False()
    {
        // Обычный клик ПОСЛЕ закрытия меню — новое действие пользователя: его обработает
        // штатный путь ShouldStabilizeAfterMenuClose, а не восстановление по курсору.
        const long clickTick = 10_000;
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: false,
            recentClickWasPlainLeftWithoutModifiers: true,
            lastPlainClickTick: clickTick,
            menuCloseTick: clickTick - 50,
            nowTick: clickTick,
            windowMs: RecentWindowMs));
    }

    // ======================= Диагностика клика (issue #340, 0.3.9.311, B-6) =======================

    [Fact]
    public void BuildClickTraceLine_WpfAndAvalonia_SameShape()
    {
        // B-6 (0.3.9.311): единый ФОРМАТ строки события клика для обеих платформ —
        // WPF (MouseDown/MouseUp) и Avalonia (PointerPressed/PointerReleased). Имена
        // событий платформенные (различаются по дизайну), но форма записи — набор и
        // порядок полей (x, y, modifiers, target, snapshot, redelivery, pinned) и их
        // представление — обязана совпадать: асимметрия поведения платформ по логу
        // видна сразу, а не прячется за разными форматами.
        string FormOf(string line) => line[(line.IndexOf(':') + 1)..];

        var wpfLine = BatchSelectionHelper.BuildClickTraceLine(
            "MouseDown", 12.5, 33.25, "None", "base-42",
            snapshotPresent: true, isRedelivery: true, isPinnedSection: false);
        var avaloniaLine = BatchSelectionHelper.BuildClickTraceLine(
            "PointerPressed", 12.5, 33.25, "None", "base-42",
            snapshotPresent: true, isRedelivery: true, isPinnedSection: false);

        Assert.Equal(FormOf(wpfLine), FormOf(avaloniaLine));

        // События с модификаторами и пустой целью тоже единообразны по форме.
        var wpfUp = BatchSelectionHelper.BuildClickTraceLine(
            "MouseUp", 1, 2, "Ctrl+Shift", null, snapshotPresent: false, isRedelivery: false, isPinnedSection: true);
        var avaloniaReleased = BatchSelectionHelper.BuildClickTraceLine(
            "PointerReleased", 1, 2, "Ctrl+Shift", null, snapshotPresent: false, isRedelivery: false, isPinnedSection: true);
        Assert.Equal(FormOf(wpfUp), FormOf(avaloniaReleased));
    }

    [Fact]
    public void FormatModifiers_EmptyCombination_IsNone()
    {
        Assert.Equal("None", BatchSelectionHelper.FormatModifiers(ctrl: false, shift: false, alt: false));
        Assert.Equal("Ctrl", BatchSelectionHelper.FormatModifiers(ctrl: true, shift: false, alt: false));
        Assert.Equal("Shift", BatchSelectionHelper.FormatModifiers(ctrl: false, shift: true, alt: false));
        Assert.Equal("Alt", BatchSelectionHelper.FormatModifiers(ctrl: false, shift: false, alt: true));
        Assert.Equal("Ctrl+Shift", BatchSelectionHelper.FormatModifiers(ctrl: true, shift: true, alt: false));
        Assert.Equal("Ctrl+Shift+Alt", BatchSelectionHelper.FormatModifiers(ctrl: true, shift: true, alt: true));
    }

    [Fact]
    public void BuildClickTraceLine_NoTarget_MarksAsNull()
    {
        // Клик мимо строки/по служебному узлу: targetId отсутствует — в записи пишется
        // "null", формат не ломается (и не содержит секретов).
        var line = BatchSelectionHelper.BuildClickTraceLine(
            "MouseDown", 4, 5, "None", null,
            snapshotPresent: false, isRedelivery: false, isPinnedSection: false);
        Assert.Contains("target=null", line, StringComparison.Ordinal);
        Assert.DoesNotContain("password", line, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- ShouldRestoreSelectionAfterMenuClose (issue #340, 11-я итерация) ----------

    [Fact]
    public void ShouldRestoreSelectionAfterMenuClose_OverRowWithPopupClick_True_RegardlessOfRecency()
    {
        // Четвёртый реальный лог (0.3.9.316): клик, закрывший меню, полностью проглочен
        // попапом; последний обычный клик был ~2,9 с назад (за окном 2000 мс). Сигнал
        // «клик по попапу меню» НЕ зависит от давности — восстановление выполняется.
        Assert.True(BatchSelectionHelper.ShouldRestoreSelectionAfterMenuClose(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: false,
            clickDuringMenuOpen: true));
    }

    [Fact]
    public void ShouldRestoreSelectionAfterMenuClose_CursorOverMenuItem_False()
    {
        // Выбор пункта меню строку не меняет — восстановление не запускается, даже если
        // клик по попапу был.
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionAfterMenuClose(
            overTreeRow: true,
            overMenuItem: true,
            snapshotPresent: false,
            clickDuringMenuOpen: true));
    }

    [Fact]
    public void ShouldRestoreSelectionAfterMenuClose_NoPopupClick_EscOrProgrammatic_False()
    {
        // Закрытие ESC/программное не производит клика по попапу — восстановления нет.
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionAfterMenuClose(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: false,
            clickDuringMenuOpen: false));
    }

    [Fact]
    public void ShouldRestoreSelectionAfterMenuClose_SnapshotPresent_False()
    {
        // Снимок клика есть — работают штатные пути A/C/snapshot, восстановление по
        // строке под курсором не нужно (избегаем двойной работы).
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionAfterMenuClose(
            overTreeRow: true,
            overMenuItem: false,
            snapshotPresent: true,
            clickDuringMenuOpen: true));
    }

    [Fact]
    public void ShouldRestoreSelectionAfterMenuClose_CursorNotOverRow_False()
    {
        Assert.False(BatchSelectionHelper.ShouldRestoreSelectionAfterMenuClose(
            overTreeRow: false,
            overMenuItem: false,
            snapshotPresent: false,
            clickDuringMenuOpen: true));
    }

    // ---------- ShouldReturnKeyboardFocusToTree (issue #340, 11-я итерация) ----------

    [Fact]
    public void ShouldReturnKeyboardFocusToTree_FocusWasInTreeBeforeOpen_True()
    {
        // Комментарий 7OH 28/28: после закрытия меню фокус возвращается дереву — стрелки
        // снова работают, TAB уходит в список, а не на кнопку сворачивания.
        Assert.True(BatchSelectionHelper.ShouldReturnKeyboardFocusToTree(
            isTreeMenuClosed: true,
            focusWasInTreeBeforeMenuOpen: true,
            focusStillWithinWindow: true,
            modalDialogOpen: false));
    }

    [Fact]
    public void ShouldReturnKeyboardFocusToTree_FocusNotInTreeBeforeOpen_NoClick_False()
    {
        // Меню открыто не из дерева (кнопка с собственным меню), попап-клика не было —
        // фокус не отбираем у других элементов.
        Assert.False(BatchSelectionHelper.ShouldReturnKeyboardFocusToTree(
            isTreeMenuClosed: true,
            focusWasInTreeBeforeMenuOpen: false,
            focusStillWithinWindow: true,
            modalDialogOpen: false));
    }

    [Fact]
    public void ShouldReturnKeyboardFocusToTree_FocusNotInTreeBeforeOpen_ButPopupClickOverRow_True()
    {
        // Пользователь явно работал с деревом (клик по попапу с курсором над строкой) —
        // фокус возвращаем дереву.
        Assert.True(BatchSelectionHelper.ShouldReturnKeyboardFocusToTree(
            isTreeMenuClosed: true,
            focusWasInTreeBeforeMenuOpen: false,
            focusStillWithinWindow: true,
            modalDialogOpen: false,
            clickDuringMenuOpen: true,
            overTreeRow: true));
    }

    [Fact]
    public void ShouldReturnKeyboardFocusToTree_ModalDialogOpen_False()
    {
        // Открытое модальное окно — фокус не отбираем.
        Assert.False(BatchSelectionHelper.ShouldReturnKeyboardFocusToTree(
            isTreeMenuClosed: true,
            focusWasInTreeBeforeMenuOpen: true,
            focusStillWithinWindow: true,
            modalDialogOpen: true));
    }

    [Fact]
    public void ShouldReturnKeyboardFocusToTree_FocusLeftWindow_False()
    {
        // Фокус ушёл из окна (окно потеряло активность) — восстанавливать нечего.
        Assert.False(BatchSelectionHelper.ShouldReturnKeyboardFocusToTree(
            isTreeMenuClosed: true,
            focusWasInTreeBeforeMenuOpen: true,
            focusStillWithinWindow: false,
            modalDialogOpen: false));
    }

    [Fact]
    public void ShouldReturnKeyboardFocusToTree_NotTreeMenu_False()
    {
        Assert.False(BatchSelectionHelper.ShouldReturnKeyboardFocusToTree(
            isTreeMenuClosed: false,
            focusWasInTreeBeforeMenuOpen: true,
            focusStillWithinWindow: true,
            modalDialogOpen: false));
    }

    // ---------- BuildMenuCloseDecisionLine (issue #340, 11-я итерация) ----------

    [Fact]
    public void BuildMenuCloseDecisionLine_ContainsAllFields()
    {
        var line = BatchSelectionHelper.BuildMenuCloseDecisionLine(
            restore: true,
            reason: "rowUnderCursor",
            clickDuringOpen: true,
            overTreeRow: true,
            overMenuItem: false,
            focusRestore: true);

        Assert.StartsWith("MenuCloseDecision:", line, StringComparison.Ordinal);
        Assert.Contains("restore=true", line, StringComparison.Ordinal);
        Assert.Contains("reason=rowUnderCursor", line, StringComparison.Ordinal);
        Assert.Contains("clickDuringOpen=true", line, StringComparison.Ordinal);
        Assert.Contains("overTreeRow=true", line, StringComparison.Ordinal);
        Assert.Contains("overMenuItem=false", line, StringComparison.Ordinal);
        Assert.Contains("focusRestore=true", line, StringComparison.Ordinal);
    }
}