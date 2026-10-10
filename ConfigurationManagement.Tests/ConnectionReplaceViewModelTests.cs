using System.Collections.Generic;
using System.Linq;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты вью-модели окна «Заменить в строках подключения…» (0.3.9.189, функция 6
/// «Массовая замена в строке подключения баз»): построение предпросмотра и сводки,
/// пометка «грязного» состояния при изменении входных параметров, применение с
/// колбэком записей отката и запрет повторного применения без нового предпросмотра,
/// откат последней замены, валидация регулярного выражения, крайние случаи (пустой
/// искомый текст, базы без подключения и без совпадений) и фильтрация видимых баз
/// по приватности (чистый хелпер моста MainViewModel).
/// </summary>
public sealed class ConnectionReplaceViewModelTests
{
    // ---------- Хелперы ----------

    private static Infobase ServerBase(string id, string name, string server = "server1", string group = "")
        => new()
        {
            Id = id,
            Name = name,
            Group = group,
            Connection = new ConnectionSettings
            {
                Type = ConnectionType.ClientServer,
                Server = server,
                DatabaseName = "База",
                Port = 1541
            }
        };

    /// <summary>База с дефолтными (пустыми) настройками подключения — «без подключения».</summary>
    private static Infobase EmptyBase(string id, string name, string group = "")
        => new() { Id = id, Name = name, Group = group };

    private static ConnectionReplaceViewModel CreateVm(
        IReadOnlyList<Infobase> candidates,
        ConnectionReplaceScope scope = ConnectionReplaceScope.AllBases,
        Action<IReadOnlyList<ConnectionReplaceUndoEntry>>? onApplied = null,
        Action? onUndone = null)
        => new(candidates, scope, onApplied, onUndone);

    private static void SetServerReplace(
        ConnectionReplaceViewModel vm,
        string find = "server1",
        string replace = "server2")
    {
        vm.FindText = find;
        vm.ReplaceText = replace;
        vm.SelectedField = ConnectionField.Server;
        vm.SelectedMatchMode = ConnectionMatchMode.Exact;
        vm.IgnoreCase = false;
    }

    // ---------- Предпросмотр и сводка ----------

    [Fact]
    public void RefreshPreview_FillsRowsAndSummary()
    {
        var candidates = new List<Infobase>
        {
            ServerBase("1", "База А", server: "server1"),
            ServerBase("2", "База Б", server: "server1"),
            ServerBase("3", "База В", server: "server2")
        };
        var vm = CreateVm(candidates);
        SetServerReplace(vm);

        vm.RefreshPreviewCommand.Execute(null);

        Assert.Equal(2, vm.PreviewRows.Count);
        Assert.Equal(2, vm.AffectedCount);
        Assert.Equal(1, vm.NoMatchCount);
        Assert.Equal(0, vm.EmptyCount);
        Assert.False(vm.IsPreviewDirty);
        Assert.True(vm.CanApply);
        Assert.Equal(
            string.Format(LocalizationManager.T("ConnectionReplace.Summary.Format"), 2, 2, 1, 0),
            vm.SummaryText);
        Assert.All(vm.PreviewRows, r => Assert.True(r.Changed));
        Assert.All(vm.PreviewRows, r => Assert.Equal(ConnectionField.Server, r.Field));
        Assert.All(vm.PreviewRows, r => Assert.Equal("server1", r.BeforeText));
        Assert.All(vm.PreviewRows, r => Assert.Equal("server2", r.AfterText));
        Assert.Empty(vm.ErrorMessage);
        Assert.Empty(vm.ResultText);
    }

    [Fact]
    public void EmptyAndNoMatchCandidates_AreCountedInSummary()
    {
        var candidates = new List<Infobase>
        {
            ServerBase("1", "Совпадение", server: "server1"),
            EmptyBase("2", "Без подключения"),
            ServerBase("3", "Другой сервер", server: "other")
        };
        var vm = CreateVm(candidates);
        SetServerReplace(vm);

        vm.RefreshPreview();

        Assert.Equal(1, vm.AffectedCount);
        Assert.Equal(1, vm.NoMatchCount);
        Assert.Equal(1, vm.EmptyCount);
        Assert.Single(vm.PreviewRows);
        Assert.True(vm.CanApply);
        Assert.Equal(
            string.Format(LocalizationManager.T("ConnectionReplace.Summary.Format"), 1, 1, 1, 1),
            vm.SummaryText);
    }

    [Fact]
    public void NoMatches_DisablesApply()
    {
        var candidates = new List<Infobase> { ServerBase("1", "База", server: "server9") };
        var vm = CreateVm(candidates);
        SetServerReplace(vm, find: "server1");

        vm.RefreshPreview();

        Assert.Empty(vm.PreviewRows);
        Assert.Equal(0, vm.AffectedCount);
        Assert.Equal(1, vm.NoMatchCount);
        Assert.False(vm.CanApply);
        Assert.False(vm.IsPreviewDirty); // предпросмотр актуален — просто без совпадений
    }

    // ---------- «Грязное» состояние ----------

    [Theory]
    [InlineData(0)] // FindText
    [InlineData(1)] // ReplaceText
    [InlineData(2)] // SelectedField
    [InlineData(3)] // SelectedScope
    [InlineData(4)] // SelectedMatchMode
    [InlineData(5)] // IgnoreCase
    public void InputChange_MarksDirty_AndClearsPreview(int propertyIndex)
    {
        var candidates = new List<Infobase> { ServerBase("1", "База") };
        var vm = CreateVm(candidates);
        SetServerReplace(vm);
        vm.RefreshPreview();
        Assert.False(vm.IsPreviewDirty);
        Assert.NotEmpty(vm.PreviewRows);
        Assert.True(vm.CanApply);

        switch (propertyIndex)
        {
            case 0:
                vm.FindText = "server9";
                break;
            case 1:
                vm.ReplaceText = "server3";
                break;
            case 2:
                vm.SelectedField = ConnectionField.Ref;
                break;
            case 3:
                vm.SelectedScope = ConnectionReplaceScope.CurrentGroup;
                break;
            case 4:
                vm.SelectedMatchMode = ConnectionMatchMode.Prefix;
                break;
            default:
                vm.IgnoreCase = !vm.IgnoreCase;
                break;
        }

        Assert.True(vm.IsPreviewDirty);
        Assert.Empty(vm.PreviewRows);
        Assert.False(vm.CanApply);
        Assert.Equal(0, vm.AffectedCount);
        Assert.Empty(vm.SummaryText);
    }

    [Fact]
    public void CanApply_RequiresAffected_AndNotDirty()
    {
        var candidates = new List<Infobase>
        {
            ServerBase("1", "База А", server: "server1"),
            ServerBase("2", "База Б", server: "other")
        };
        var vm = CreateVm(candidates);
        SetServerReplace(vm);

        vm.RefreshPreview();
        Assert.True(vm.CanApply);

        // Смена режима на префикс тоже даёт совпадение — новый предпросмотр снова активен.
        vm.SelectedMatchMode = ConnectionMatchMode.Prefix;
        Assert.False(vm.CanApply);
        vm.RefreshPreview();
        Assert.True(vm.CanApply);
        Assert.False(vm.IsPreviewDirty);
    }

    // ---------- Регулярное выражение ----------

    [Fact]
    public void InvalidRegex_SetsError_DisablesApply_AndClearsPreview()
    {
        var candidates = new List<Infobase> { ServerBase("1", "База") };
        var vm = CreateVm(candidates);
        vm.FindText = "[";
        vm.ReplaceText = "x";
        vm.SelectedMatchMode = ConnectionMatchMode.Regex;

        vm.RefreshPreview();

        Assert.NotEmpty(vm.ErrorMessage);
        Assert.False(vm.CanApply);
        Assert.Empty(vm.PreviewRows);
        Assert.Equal(0, vm.AffectedCount);
    }

    // ---------- Применение ----------

    [Fact]
    public void Apply_CallsOnApplied_WithUndoEntries_SetsResult()
    {
        var candidates = new List<Infobase>
        {
            ServerBase("1", "База А", server: "server1"),
            ServerBase("2", "База Б", server: "server1")
        };
        IReadOnlyList<ConnectionReplaceUndoEntry>? applied = null;
        var vm = CreateVm(candidates, onApplied: entries => applied = entries);
        SetServerReplace(vm);
        vm.RefreshPreview();

        vm.ApplyCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.Equal(2, applied!.Count);
        Assert.All(applied, e => Assert.Equal("server1", e.Before.Server));
        Assert.All(applied, e => Assert.Equal("server2", e.After.Server));
        // Базы реально мутированы планировщиком (Apply), VM передал записи отката.
        Assert.All(candidates, c => Assert.Equal("server2", c.Connection.Server));
        Assert.Equal(
            string.Format(LocalizationManager.T("ConnectionReplace.Result.AppliedFormat"), 2),
            vm.ResultText);
        Assert.True(vm.CanUndo);
        Assert.False(vm.CanApply);
        Assert.Empty(vm.PreviewRows);
    }

    [Fact]
    public void SecondApply_WithoutNewPreview_IsBlocked()
    {
        var candidates = new List<Infobase> { ServerBase("1", "База") };
        var calls = 0;
        var vm = CreateVm(candidates, onApplied: _ => calls++);
        SetServerReplace(vm);
        vm.RefreshPreview();

        vm.Apply();
        vm.Apply();

        Assert.Equal(1, calls);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void Apply_WithoutPreview_IsNoOp()
    {
        var candidates = new List<Infobase> { ServerBase("1", "База") };
        var calls = 0;
        var vm = CreateVm(candidates, onApplied: _ => calls++);
        SetServerReplace(vm); // поля заполнены, но «Найти» не выполнялось

        vm.Apply();

        Assert.Equal(0, calls);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanUndo);
        Assert.Equal("server1", candidates[0].Connection.Server);
    }

    // ---------- Откат ----------

    [Fact]
    public void UndoLast_RestoresConnections_CallsOnUndone_AndDisablesUndo()
    {
        var candidates = new List<Infobase>
        {
            ServerBase("1", "База А", server: "server1"),
            ServerBase("2", "База Б", server: "server1")
        };
        var undone = false;
        var vm = CreateVm(candidates, onUndone: () => undone = true);
        SetServerReplace(vm);
        vm.RefreshPreview();
        vm.Apply();
        Assert.All(candidates, c => Assert.Equal("server2", c.Connection.Server));

        vm.UndoLastCommand.Execute(null);

        Assert.True(undone);
        Assert.All(candidates, c => Assert.Equal("server1", c.Connection.Server));
        Assert.False(vm.CanUndo);
        Assert.False(vm.CanApply);
        Assert.Empty(vm.PreviewRows);
        Assert.Equal(
            string.Format(LocalizationManager.T("ConnectionReplace.UndoDoneFormat"), 2),
            vm.ResultText);
    }

    [Fact]
    public void UndoLast_WithoutApply_IsNoOp()
    {
        var candidates = new List<Infobase> { ServerBase("1", "База") };
        var vm = CreateVm(candidates);

        vm.UndoLast();

        Assert.False(vm.CanUndo);
        Assert.Equal("server1", candidates[0].Connection.Server);
    }

    // ---------- Крайние случаи ----------

    [Fact]
    public void EmptyFind_LeavesPreviewEmpty_DisablesApply()
    {
        var candidates = new List<Infobase> { ServerBase("1", "База") };
        var vm = CreateVm(candidates);
        vm.FindText = "   ";

        vm.RefreshPreview();

        Assert.False(vm.CanApply);
        Assert.Empty(vm.PreviewRows);
        Assert.True(vm.IsPreviewDirty); // предпросмотр так и не построен
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public void Defaults_AreSane()
    {
        var vm = CreateVm(new List<Infobase> { ServerBase("1", "База") });

        Assert.Equal(string.Empty, vm.FindText);
        Assert.Equal(string.Empty, vm.ReplaceText);
        Assert.Equal(ConnectionField.Server, vm.SelectedField);
        Assert.Equal(ConnectionMatchMode.Substring, vm.SelectedMatchMode);
        Assert.True(vm.IgnoreCase);
        Assert.Equal(ConnectionReplaceScope.AllBases, vm.SelectedScope);
        Assert.True(vm.IsPreviewDirty);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanUndo);
        Assert.Empty(vm.PreviewRows);
        Assert.NotNull(vm.RefreshPreviewCommand);
        Assert.NotNull(vm.ApplyCommand);
        Assert.NotNull(vm.UndoLastCommand);
    }

    // ---------- Мост MainViewModel: фильтрация видимых баз (чистый хелпер) ----------

    [Fact]
    public void FilterVisibleInfobases_HidesPrivateWhenLocked()
    {
        var list = new List<Infobase>
        {
            ServerBase("1", "Обычная"),
            ServerBase("2", "Приватная")
        };
        list[1].IsPrivate = true;

        var all = ConnectionReplacementPlanner.FilterVisibleInfobases(list, canShowPrivateBases: true);
        Assert.Equal(2, all.Count);

        var hidden = ConnectionReplacementPlanner.FilterVisibleInfobases(list, canShowPrivateBases: false);
        Assert.Single(hidden);
        Assert.Equal("1", hidden[0].Id);
    }

    [Fact]
    public void FilterVisibleInfobases_DoesNotMutateSource_AndSkipsNulls()
    {
        var list = new List<Infobase>
        {
            ServerBase("1", "Обычная"),
            null!,
            ServerBase("2", "Приватная")
        };
        list[2].IsPrivate = true;

        var visible = ConnectionReplacementPlanner.FilterVisibleInfobases(list, canShowPrivateBases: false);

        Assert.Single(visible);
        Assert.Equal("1", visible[0].Id);
        Assert.Equal(3, list.Count); // исходный список не тронут
        Assert.Same(list[0], visible[0]);
    }

    // ---------- Issue #357: закрытый ComboBox показывает ключ вместо значения ----------

    /// <summary>
    /// Closed ComboBox (WPF SelectionBoxItem и Avalonia без SelectionBoxItemTemplate)
    /// может рендерить выбранный элемент через ToString() — он обязан возвращать
    /// локализованный DisplayText, а не авто-представление позиционной записи.
    /// </summary>
    [Fact]
    public void DisplayItem_ToString_ReturnsDisplayText()
    {
        var item = new DisplayItem<ConnectionField>(ConnectionField.Server, "Сервер");
        Assert.Equal("Сервер", item.ToString());
    }

    [Fact]
    public void DisplayItem_ToString_EqualsDisplayText_ForEveryVmItem()
    {
        var vm = CreateVm(new List<Infobase>());

        foreach (var item in vm.Fields)
        {
            Assert.Equal(item.DisplayText, item.ToString());
            Assert.False(string.IsNullOrWhiteSpace(item.ToString()));
        }
        foreach (var item in vm.Scopes)
            Assert.Equal(item.DisplayText, item.ToString());
        foreach (var item in vm.Modes)
            Assert.Equal(item.DisplayText, item.ToString());
    }

    /// <summary>
    /// ToString() элементов совпадает с локализованным текстом из хелперов VM
    /// (FieldDisplayText/ScopeDisplayText/ModeDisplayText): закрытая часть комбобокса
    /// показывает ровно тот же текст, что и элементы списка, при любом словаре
    /// (в тестовой среде LocalizationManager возвращает ключ — и ToString() тоже
    /// возвращает его, а не авто-представление записи).
    /// </summary>
    [Fact]
    public void DisplayItem_ToString_MatchesDisplayHelpers()
    {
        var vm = CreateVm(new List<Infobase>());

        foreach (var item in vm.Fields)
        {
            Assert.Equal(
                ConnectionReplaceViewModel.FieldDisplayText(item.Value),
                item.ToString());
        }
        foreach (var item in vm.Modes)
        {
            Assert.Equal(
                ConnectionReplaceViewModel.ModeDisplayText(item.Value),
                item.ToString());
        }
        foreach (var item in vm.Scopes)
        {
            Assert.Equal(item.DisplayText, item.ToString());
        }
    }
}