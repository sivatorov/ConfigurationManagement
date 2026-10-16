using System;
using System.IO;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты резолвера компоновки меню «Утилиты» (issue #374): выбор стандартной компоновки
/// при выключенной правке и пользовательской при включённой; сбор «Новинок программы».
/// </summary>
public sealed class UtilitiesMenuResolverTests
{
    [Fact]
    public void Resolve_EditingDisabled_ReturnsDefaultLayout()
    {
        var settings = new AppSettings { UtilitiesMenuEditingEnabled = false };
        var layout = UtilitiesMenuResolver.Resolve(settings, null);
        Assert.NotNull(layout);
        Assert.Equal("CommandPalette", layout!.Root[0].ItemId);
    }

    [Fact]
    public void Resolve_EditingDisabled_IgnoresStore()
    {
        var settings = new AppSettings { UtilitiesMenuEditingEnabled = false };
        var store = new MemoryLayoutStore(new UtilitiesMenuLayout
        {
            Root = new System.Collections.Generic.List<UtilitiesMenuNode>
            {
                UtilitiesMenuLayouts.Item("ActualReleases")
            }
        });
        var layout = UtilitiesMenuResolver.Resolve(settings, store);
        // Правка выключена — файл компоновки игнорируется.
        Assert.Equal("CommandPalette", layout!.Root[0].ItemId);
    }

    [Fact]
    public void Resolve_EditingEnabled_NoStore_ReturnsDefaultLayout()
    {
        var settings = new AppSettings { UtilitiesMenuEditingEnabled = true };
        var layout = UtilitiesMenuResolver.Resolve(settings, null);
        Assert.NotNull(layout);
        // 11 пунктов/групп + 5 узлов-разделителей (issue #374).
        Assert.Equal(16, layout!.Root.Count);
    }

    [Fact]
    public void Resolve_EditingEnabled_WithStore_ReturnsCustomLayout()
    {
        var settings = new AppSettings { UtilitiesMenuEditingEnabled = true };
        var custom = new UtilitiesMenuLayout
        {
            Root = new System.Collections.Generic.List<UtilitiesMenuNode>
            {
                UtilitiesMenuLayouts.Item("ActualReleases"),
                UtilitiesMenuLayouts.Item("ServerMonitor", separatorBefore: true)
            }
        };
        var store = new MemoryLayoutStore(custom);
        var layout = UtilitiesMenuResolver.Resolve(settings, store);
        Assert.NotNull(layout);
        Assert.Equal(2, layout!.Root.Count);
        Assert.Equal("ActualReleases", layout.Root[0].ItemId);
        Assert.Equal("ServerMonitor", layout.Root[1].ItemId);
        Assert.True(layout.Root[1].SeparatorBefore);
    }

    [Fact]
    public void Resolve_NullSettings_ReturnsDefaultLayout()
    {
        var layout = UtilitiesMenuResolver.Resolve(null, null);
        Assert.NotNull(layout);
        Assert.Equal("CommandPalette", layout!.Root[0].ItemId);
    }

    [Fact]
    public void CollectNewItems_CurrentCatalog_HasNoUnplacedNewItems()
    {
        // В каталоге сейчас нет пунктов с признаком «новинка» (все пункты уже размещены
        // в стандартной компоновке), поэтому «Новинки программы» пустые.
        var layout = UtilitiesMenuLayouts.Default();
        var newItems = UtilitiesMenuResolver.CollectNewItems(layout);
        Assert.Empty(newItems);
    }

    [Fact]
    public void CollectNewItems_NullLayout_DoesNotThrow()
    {
        // Пустая компоновка (null) не должна ронять резолвер. Пункты-«новинки» каталога
        // (сейчас это NotificationChannels, добавлен в 0.3.12.69) при пустой компоновке
        // считаются неразмещёнными и попадают в «Новинки программы» — штатное поведение
        // (issue #374). Раньше тест ожидал пустой результат, но каталог с тех пор
        // получил новый пункт, и утверждение перестало соответствовать контракту.
        var newItems = UtilitiesMenuResolver.CollectNewItems(null);
        Assert.NotNull(newItems);
        Assert.Contains(newItems, i => i.Id == "NotificationChannels");
    }

    [Fact]
    public void CollectItemIds_CollectsItemsAcrossGroups()
    {
        var layout = UtilitiesMenuLayouts.Default();
        var ids = new System.Collections.Generic.HashSet<string>();
        UtilitiesMenuResolver.CollectItemIds(layout.Root, ids);

        Assert.Contains("CommandPalette", ids);
        Assert.Contains("CheckForUpdates", ids);
        // Пункты внутри групп тоже собираются.
        Assert.Contains("UnlockPrivateBases", ids);
        Assert.Contains("ConfigDiff", ids);
        Assert.Contains("SessionLock", ids);
        // Идентификаторы не дублируются: HashSet.
        Assert.True(ids.Count > 10);
    }

    /// <summary>Хранилище-заглушка для тестов (без файла на диске).</summary>
    private sealed class MemoryLayoutStore : IUtilitiesMenuLayoutStore
    {
        private readonly UtilitiesMenuLayout? _layout;

        public MemoryLayoutStore(UtilitiesMenuLayout? layout)
        {
            _layout = layout;
        }

        public string FilePath => Path.Combine(Path.GetTempPath(), "memory_utilities_menu_layout.json");

        public UtilitiesMenuLayout? Load() => _layout;

        public void Save(UtilitiesMenuLayout layout)
        {
            // Не сохраняем: тестовая заглушка.
        }
    }
}