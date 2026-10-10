using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты детерминированного восстановления текущей строки после закрытия меню кликом
/// по пункту (issue #356, 0.3.11): расширенный предикат
/// <see cref="BatchSelectionHelper.ShouldRestoreCurrentSelectionAfterMenuCloseEx"/>
/// (сигнал closedByItemClick приоритетнее нестабильного hit-test) и поле
/// <c>closedByItemClick</c> в строке решения MenuCloseDecision (обратная совместимость).
/// </summary>
public sealed class MenuCloseRestoreExTests
{
    // ======================= ShouldRestoreCurrentSelectionAfterMenuCloseEx =======================

    [Fact]
    public void Ex_ClosedByItemClickTrue_RestoresEvenIfHeuristicFalse()
    {
        // Детерминированный сигнал «клик по пункту» срабатывает, хотя hit-test
        // Mouse.DirectlyOver в MenuClosed уже не видит пункт (нестабильный путь).
        Assert.True(BatchSelectionHelper.ShouldRestoreCurrentSelectionAfterMenuCloseEx(
            isTreeLikeMenuClosed: true,
            closedByItemClick: true,
            overMenuItemHeuristic: false,
            focusStillWithinWindow: true,
            modalDialogOpen: false,
            hasCurrentSelection: true));
    }

    [Fact]
    public void Ex_HeuristicTrueStillRestores_BackwardCompatible()
    {
        // Запасной путь (закрытие мышью мимо попапа) продолжает работать.
        Assert.True(BatchSelectionHelper.ShouldRestoreCurrentSelectionAfterMenuCloseEx(
            isTreeLikeMenuClosed: true,
            closedByItemClick: false,
            overMenuItemHeuristic: true,
            focusStillWithinWindow: true,
            modalDialogOpen: false,
            hasCurrentSelection: true));
    }

    [Fact]
    public void Ex_BothFalse_NoRestore()
    {
        // ESC/программное закрытие: ни сигнала клика по пункту, ни эвристики —
        // выбор не переносится.
        Assert.False(BatchSelectionHelper.ShouldRestoreCurrentSelectionAfterMenuCloseEx(
            isTreeLikeMenuClosed: true,
            closedByItemClick: false,
            overMenuItemHeuristic: false,
            focusStillWithinWindow: true,
            modalDialogOpen: false,
            hasCurrentSelection: true));
    }

    [Fact]
    public void Ex_NotTreeLikeMenu_False()
    {
        Assert.False(BatchSelectionHelper.ShouldRestoreCurrentSelectionAfterMenuCloseEx(
            isTreeLikeMenuClosed: false,
            closedByItemClick: true,
            overMenuItemHeuristic: true,
            focusStillWithinWindow: true,
            modalDialogOpen: false,
            hasCurrentSelection: true));
    }

    [Fact]
    public void Ex_ModalDialogOpen_NoRestore()
    {
        Assert.False(BatchSelectionHelper.ShouldRestoreCurrentSelectionAfterMenuCloseEx(
            isTreeLikeMenuClosed: true,
            closedByItemClick: true,
            overMenuItemHeuristic: false,
            focusStillWithinWindow: true,
            modalDialogOpen: true,
            hasCurrentSelection: true));
    }

    [Fact]
    public void Ex_FocusLostFromWindow_NoRestore()
    {
        Assert.False(BatchSelectionHelper.ShouldRestoreCurrentSelectionAfterMenuCloseEx(
            isTreeLikeMenuClosed: true,
            closedByItemClick: true,
            overMenuItemHeuristic: false,
            focusStillWithinWindow: false,
            modalDialogOpen: false,
            hasCurrentSelection: true));
    }

    [Fact]
    public void Ex_NoCurrentSelection_NoRestore()
    {
        Assert.False(BatchSelectionHelper.ShouldRestoreCurrentSelectionAfterMenuCloseEx(
            isTreeLikeMenuClosed: true,
            closedByItemClick: true,
            overMenuItemHeuristic: false,
            focusStillWithinWindow: true,
            modalDialogOpen: false,
            hasCurrentSelection: false));
    }

    // ======================= BuildMenuCloseDecisionLine: поле closedByItemClick =======================

    [Fact]
    public void DecisionLine_WithClosedByItemClick_FieldAppended()
    {
        var line = BatchSelectionHelper.BuildMenuCloseDecisionLine(
            restore: true,
            reason: "menuItem",
            clickDuringOpen: false,
            overTreeRow: false,
            overMenuItem: true,
            focusRestore: true,
            closedByItemClick: true);

        Assert.Contains("reason=menuItem", line, StringComparison.Ordinal);
        Assert.Contains("closedByItemClick=true", line, StringComparison.Ordinal);
        // Порядок полей: closedByItemClick — последний (обратная совместимость парсеров).
        Assert.EndsWith("closedByItemClick=true", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DecisionLine_WithClosedByItemClickFalse_FieldAppendedFalse()
    {
        var line = BatchSelectionHelper.BuildMenuCloseDecisionLine(
            restore: false,
            reason: "none",
            clickDuringOpen: true,
            overTreeRow: true,
            overMenuItem: false,
            focusRestore: false,
            closedByItemClick: false);

        Assert.Contains("closedByItemClick=false", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DecisionLine_WithoutClosedByItemClick_LegacyFormatUnchanged()
    {
        // Обратная совместимость: без нового поля строка совпадает со старым форматом.
        var line = BatchSelectionHelper.BuildMenuCloseDecisionLine(
            restore: true,
            reason: "rowUnderCursor",
            clickDuringOpen: true,
            overTreeRow: true,
            overMenuItem: false,
            focusRestore: true);

        Assert.Equal(
            "MenuCloseDecision: restore=true, reason=rowUnderCursor, clickDuringOpen=true, " +
            "overTreeRow=true, overMenuItem=false, focusRestore=true",
            line);
    }
}
