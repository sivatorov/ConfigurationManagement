using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Issue #356 «Хоткей для показать избранное»: сочетание меню закладок вынесено
/// в настройки (HotkeyBookmarksMenu, по умолчанию Ctrl+B) и участвует в проверке
/// дублей вместе с остальными хоткеями, а меню закладок участвует в стабилизации
/// выделения после закрытия (механизм issue #340) наравне с контекстным меню дерева.
/// </summary>
public sealed class BookmarksMenuHotkeyTests
{
    // ---- Дефолты и согласование хоткеев ----

    [Fact]
    public void AppSettings_BookmarksMenuHotkey_DefaultIsCtrlB()
    {
        var settings = new AppSettings();

        Assert.Equal("Ctrl+B", settings.HotkeyBookmarksMenu);
    }

    [Fact]
    public void AppSettings_Defaults_NoConflictBetweenShowFavoritesAndBookmarksMenu()
    {
        // По умолчанию «Показать избранное» не назначено, а меню закладок — Ctrl+B:
        // дефолты не конфликтуют, и автор issue #356 может назначить «Показать
        // избранное» на любую свободную клавишу.
        var settings = new AppSettings();

        Assert.Equal(string.Empty, settings.HotkeyShowFavorites);
        Assert.NotEqual(
            settings.HotkeyShowFavorites.Trim(),
            settings.HotkeyBookmarksMenu.Trim());
    }

#if !LINUX
    [Fact]
    public void FindDuplicateHotkeys_DetectsShowFavoritesConflictWithBookmarksMenu()
    {
        // Сценарий автора issue #356: Ctrl+B назначено и меню закладок, и
        // «Показать избранное» — окно настроек обязано предупредить о конфликте.
        var duplicates = SettingsViewModel.FindDuplicateHotkeys(new (string Name, string Key)[]
        {
            ("Меню закладок", "Ctrl+B"),
            ("Показать избранное", "Ctrl+B"),
            ("Найти в списке", "Ctrl+T")
        }).ToList();

        var duplicateKey = Assert.Single(duplicates);
        Assert.Equal("Ctrl+B", duplicateKey.Key, ignoreCase: true);
        Assert.Equal(2, duplicateKey.Count());
    }

    [Fact]
    public void FindDuplicateHotkeys_EmptyShowFavorites_IsNotConflict()
    {
        var duplicates = SettingsViewModel.FindDuplicateHotkeys(new (string Name, string Key)[]
        {
            ("Меню закладок", "Ctrl+B"),
            ("Показать избранное", ""),
            ("Найти в списке", "Ctrl+T")
        }).ToList();

        Assert.Empty(duplicates);
    }
#endif

    // ---- Участие меню закладок в стабилизации выделения (механизм #340) ----

    [Fact]
    public void IsTreeLikeMenu_TreeContextMenu_IsTracked()
    {
        Assert.True(BatchSelectionHelper.IsTreeLikeMenu(isTreeMenu: true, menuTag: null));
    }

    [Fact]
    public void IsTreeLikeMenu_TaggedBookmarksMenu_IsTracked()
    {
        Assert.True(BatchSelectionHelper.IsTreeLikeMenu(
            isTreeMenu: false, menuTag: BatchSelectionHelper.BookmarksMenuTag));
    }

    [Fact]
    public void IsTreeLikeMenu_TagIsCaseInsensitive()
    {
        Assert.True(BatchSelectionHelper.IsTreeLikeMenu(isTreeMenu: false, menuTag: "bookmarksmenu"));
    }

    [Fact]
    public void IsTreeLikeMenu_OtherMenus_AreNotTracked()
    {
        Assert.False(BatchSelectionHelper.IsTreeLikeMenu(isTreeMenu: false, menuTag: null));
        Assert.False(BatchSelectionHelper.IsTreeLikeMenu(isTreeMenu: false, menuTag: "SomethingElse"));
        Assert.False(BatchSelectionHelper.IsTreeLikeMenu(isTreeMenu: false, menuTag: 42));
    }

    [Fact]
    public void BookmarksMenuTag_HasStableValue()
    {
        // Метка ставится в ShowBookmarksMenu обеих платформ и читается
        // обработчиками закрытия меню: значение должно быть стабильным.
        Assert.Equal("BookmarksMenu", BatchSelectionHelper.BookmarksMenuTag);
    }
}
