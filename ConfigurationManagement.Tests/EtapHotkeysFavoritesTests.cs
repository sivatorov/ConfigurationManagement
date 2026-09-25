using System.Collections.Generic;
using Configuration_Management.Models;
using Configuration_Management.ViewModels;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты «Горячие клавиши для избранных» (закладки 1–9): отображение номера
/// слота на модели, дефолты и нормализация настроек, а также чистая логика
/// слотов (<see cref="BookmarkSlotHelper"/>), вынесенная из платформенных версий.
/// Горячие клавиши напрямую не тестируются (среда Avalonia/WPF в тестах не гоняется).
/// </summary>
public sealed class EtapHotkeysFavoritesTests
{
    private static Infobase CreateInfobase(string id, string name) => new()
    {
        Id = id,
        Name = name
    };

    // ---- Отображение номера закладки ----

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1")]
    [InlineData(5, "5")]
    [InlineData(9, "9")]
    [InlineData(10, "")]
    [InlineData(-1, "")]
    public void FavoriteHotkeyDisplay_ValidatesRange(int number, string expected)
    {
        var ib = new Infobase { FavoriteHotkeyNumber = number };

        Assert.Equal(expected, ib.FavoriteHotkeyDisplay);
    }

    // ---- Дефолты и нормализация настроек ----

    [Fact]
    public void AppSettings_FavoriteHotkeyIds_DefaultIsEmpty()
    {
        var settings = new AppSettings();

        Assert.NotNull(settings.FavoriteHotkeyIds);
        Assert.Empty(settings.FavoriteHotkeyIds);
    }

    [Fact]
    public void AppSettings_NormalizeForLoad_ReplacesNullFavoriteHotkeyIdsWithEmptyList()
    {
        var settings = new AppSettings { FavoriteHotkeyIds = null! };

        settings.NormalizeForLoad();

        Assert.NotNull(settings.FavoriteHotkeyIds);
        Assert.Empty(settings.FavoriteHotkeyIds);
    }

    // ---- Стабильный ключ базы ----

    [Fact]
    public void FavoriteKey_PrefersIdOverNameFallback()
    {
        var withId = CreateInfobase("base-1", "Бухгалтерия");
        var withoutId = CreateInfobase("", "ЗУП");

        Assert.Equal("base-1", BookmarkSlotHelper.FavoriteKey(withId));
        Assert.Equal("name:ЗУП", BookmarkSlotHelper.FavoriteKey(withoutId));
    }

    // ---- Явный слот ----

    [Fact]
    public void AssignSlot_PlacesKeyAtRequestedNumber()
    {
        var keys = new List<string> { "a", "b", "c" };

        BookmarkSlotHelper.AssignSlot(keys, "x", 2);

        Assert.Equal(new[] { "a", "x", "b", "c" }, keys);
    }

    [Fact]
    public void AssignSlot_OutOfRange_DoesNothing()
    {
        var keys = new List<string> { "a" };

        var ok = BookmarkSlotHelper.AssignSlot(keys, "b", 0);
        Assert.False(ok);
        var ok9 = BookmarkSlotHelper.AssignSlot(keys, "b", 10);
        Assert.False(ok9);
        Assert.Equal(new[] { "a" }, keys);
    }

    [Fact]
    public void AssignSlot_CapsAtNineSlots()
    {
        var keys = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9" };

        // 10-й элемент переполняет лимит — последний слот освобождается.
        BookmarkSlotHelper.AssignSlot(keys, "10", 5);

        Assert.Equal(9, keys.Count);
    }

    // ---- Следующий свободный слот ----

    [Fact]
    public void AssignNextFreeSlot_TakesFirstFreePosition()
    {
        var keys = new List<string> { "a", "c" };

        var ok = BookmarkSlotHelper.AssignNextFreeSlot(keys, "b");

        Assert.True(ok);
        Assert.Equal(new[] { "a", "c", "b" }, keys);
    }

    [Fact]
    public void AssignNextFreeSlot_DuplicateIsNoop()
    {
        var keys = new List<string> { "a" };

        var ok = BookmarkSlotHelper.AssignNextFreeSlot(keys, "a");

        Assert.True(ok);
        Assert.Equal(new[] { "a" }, keys);
    }

    [Fact]
    public void AssignNextFreeSlot_FailsWhenAllNineOccupied()
    {
        var keys = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9" };

        var ok = BookmarkSlotHelper.AssignNextFreeSlot(keys, "10");

        Assert.False(ok);
        Assert.Equal(9, keys.Count);
    }

    // ---- Снятие и очистка ----

    [Fact]
    public void RemoveFromSlot_RemovesKeyAndCompacts()
    {
        var keys = new List<string> { "a", "b", "c" };

        var ok = BookmarkSlotHelper.RemoveFromSlot(keys, "b");

        Assert.True(ok);
        Assert.Equal(new[] { "a", "c" }, keys);
    }

    [Fact]
    public void RemoveFromSlot_MissingKeyReturnsFalse()
    {
        var keys = new List<string> { "a" };

        Assert.False(BookmarkSlotHelper.RemoveFromSlot(keys, "zzz"));
    }

    [Fact]
    public void ClearAllSlots_EmptiesList()
    {
        var keys = new List<string> { "a", "b" };

        BookmarkSlotHelper.ClearAllSlots(keys);

        Assert.Empty(keys);
    }

    // ---- Поиск по номеру ----

    [Theory]
    [InlineData(1, "a")]
    [InlineData(3, "c")]
    [InlineData(0, null)]
    [InlineData(5, null)]
    public void FindKeyBySlot_ReturnsKeyOrNull(int number, string? expected)
    {
        var keys = new List<string> { "a", "b", "c" };

        Assert.Equal(expected, BookmarkSlotHelper.FindKeyBySlot(keys, number));
    }

    // ---- «Найти в списке» (issue #285) ----

    [Fact]
    public void AppSettings_HotkeyFindInList_DefaultIsCtrlT()
    {
        var settings = new AppSettings();

        Assert.Equal("Ctrl+T", settings.HotkeyFindInList);
    }

    [Fact]
    public void AppSettings_NormalizeForLoad_PreservesHotkeyFindInList()
    {
        var settings = new AppSettings { HotkeyFindInList = "Ctrl+Shift+F" };

        settings.NormalizeForLoad();

        Assert.Equal("Ctrl+Shift+F", settings.HotkeyFindInList);
    }

    /// <summary>
    /// Команда «Найти в списке» переключается на «Все базы», а база из «Избранного»
    /// или «Закреплённых» — это тот же экземпляр, что и в общем списке: режимы лишь
    /// фильтруют общую коллекцию, поэтому после переключения цель не «теряется»
    /// и выделение/прокрутка применяются к актуальной строке.
    /// </summary>
    [Fact]
    public void FindInList_FavoriteAndPinnedViewsShareSameInstances()
    {
        var favorite = CreateInfobase("base-1", "Бухгалтерия");
        favorite.IsFavorite = true;
        favorite.IsPinned = true;
        var all = new List<Infobase> { favorite, CreateInfobase("base-2", "ЗУП") };

        var favorites = all.Where(i => i.IsFavorite).ToList();
        var pinned = all.Where(i => i.IsPinned).ToList();

        Assert.Single(favorites);
        Assert.Single(pinned);
        Assert.Same(all[0], favorites[0]);
        Assert.Same(all[0], pinned[0]);
    }

    /// <summary>
    /// Чистая логика «Найти в списке» (issue #285): раскрытие цепочки групп-предков
    /// снимает их ключи из набора свёрнутых. Узлы дерева пересоздаются при пересборке
    /// по набору свёрнутых групп, поэтому оставшийся ключ свёрнутой группы-предка
    /// снова свернул бы её и спрятал целевую базу (см. ExpandChainToRoot).
    /// </summary>
    [Fact]
    public void ExpandChainToRoot_UncollapsesAncestorsAndRemovesKeys()
    {
        var top = new GroupNodeViewModel(null, marker: "Top");
        var mid = new GroupNodeViewModel(null, parent: top, marker: "Mid");
        var deep = new GroupNodeViewModel(null, parent: mid, marker: "Deep");
        top.SetExpandedSilent(false);
        mid.SetExpandedSilent(false);
        deep.SetExpandedSilent(false);

        var collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            top.NodeKey,
            mid.NodeKey,
            deep.NodeKey
        };

        MainViewModel.ExpandChainToRoot(new[] { top, mid, deep }, collapsed);

        Assert.True(top.IsExpanded);
        Assert.True(mid.IsExpanded);
        Assert.True(deep.IsExpanded);
        Assert.DoesNotContain(top.NodeKey, collapsed);
        Assert.DoesNotContain(mid.NodeKey, collapsed);
        Assert.DoesNotContain(deep.NodeKey, collapsed);
    }

    /// <summary>
    /// Поиск строки «Найти в списке» (issue #285): домашний узел базы во «Все базы» —
    /// настоящая группа, а не дубль в «Закреплённых», который в дереве стоит первым.
    /// Иначе команда выделяла бы строку закреплений вместо строки общего списка
    /// («Из избранного перешло в Закрепленные»).
    /// </summary>
    [Fact]
    public void FindInfobaseHomeNode_SkipsPinnedDuplicateAndPrefersRealGroup()
    {
        var infobase = CreateInfobase("base-1", "Бухгалтерия");
        infobase.IsPinned = true;

        var group = new GroupNodeViewModel(null, marker: "GroupRoot");
        group.Infobases.Add(infobase);

        var pinned = new GroupNodeViewModel(null, marker: GroupNodeViewModel.PinnedMarker);
        pinned.Infobases.Add(infobase);

        // Порядок корней как в RebuildGroupTree: закреплённые идут первыми.
        var roots = new List<GroupNodeViewModel> { pinned, group };

        var home = GroupNodeViewModel.FindInfobaseHomeNode(roots, infobase);

        Assert.Same(group, home);
    }

    /// <summary>
    /// База без группы во «Все базы» живёт в узле «Без группы»; дубль в «Закреплённых»
    /// снова пропускается (issue #285).
    /// </summary>
    [Fact]
    public void FindInfobaseHomeNode_NoGroupFallsBackToNoGroupNode()
    {
        var infobase = CreateInfobase("base-2", "База без группы");
        infobase.IsPinned = true;

        var noGroup = new GroupNodeViewModel(null, marker: GroupNodeViewModel.NoGroupMarker);
        noGroup.Infobases.Add(infobase);

        var pinned = new GroupNodeViewModel(null, marker: GroupNodeViewModel.PinnedMarker);
        pinned.Infobases.Add(infobase);

        var roots = new List<GroupNodeViewModel> { pinned, noGroup };

        var home = GroupNodeViewModel.FindInfobaseHomeNode(roots, infobase);

        Assert.Same(noGroup, home);
    }
}