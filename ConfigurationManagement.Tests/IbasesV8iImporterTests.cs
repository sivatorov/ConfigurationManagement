using System.Text;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты безопасной дедупликации групп при импорте ibases.v8i (issue #351):
/// совпадения одного канонического полного пути недостаточно для удаления группы —
/// нужен ещё и признак настоящего дубликата (одинаковый Id или Name+ParentId).
/// Иначе «пропадали все папки»: живая группа (в т.ч. «осиротевшая» с тем же именем,
/// что и корневая) удалялась как дубликат. Также покрывается round-trip путей
/// Folder ↔ внутренний путь и взаимодействие с deleted_groups.json (регресс #327).
/// </summary>
public sealed class IbasesV8iImporterTests
{
    [Fact]
    public void RemoveDuplicateGroups_KeepsDistinctGroupsWithSameNameDifferentLevels()
    {
        // Одинаковые имена «Бухгалтерия» на разных уровнях (корень и вложенная) —
        // НЕ сливаются: пути разные, удалять нечего.
        var filePath = WriteIbases("""
            [Учёт]
            ID=uch-root
            Folder=/

            [Бухгалтерия]
            ID=buh-root
            Folder=/

            [Бухгалтерия]
            ID=buh-nested
            Folder=/Учёт

            [Base]
            ID=base-guid
            Folder=/Учёт/Бухгалтерия
            Connect=File="C:\db";

            """);

        var groups = new List<Group>();
        var infobases = new List<Infobase>();

        var result = IbasesV8iImporter.Import(filePath, infobases, groups);

        Assert.Equal(3, result.GroupsCreated);
        Assert.Equal(3, groups.Count);
        // Обе «Бухгалтерии» на месте: корневая и вложенная в «Учёт».
        Assert.Equal(2, groups.Count(g => g.Name == "Бухгалтерия"));
        var uch = Assert.Single(groups, g => g.Name == "Учёт" && string.IsNullOrWhiteSpace(g.ParentId));
        Assert.Contains(groups, g => g.Name == "Бухгалтерия" && g.ParentId == uch.Id);
        Assert.Contains(groups, g => g.Name == "Бухгалтерия" && string.IsNullOrWhiteSpace(g.ParentId));
    }

    [Fact]
    public void RemoveDuplicateGroups_KeepsOrphanWithSameNameAsRoot()
    {
        // Основной регресс issue #351: «осиротевшая» группа (родитель когда-то потерян,
        // например, удалён прежней дедупликацией) имеет тот же полный путь, что и
        // корневая одноимённая, но НЕ является её дубликатом — удалять нельзя.
        var filePath = WriteIbases("""
            [Base]
            ID=base-guid
            Folder=/
            Connect=File="C:\base";

            """);

        var groups = new List<Group>
        {
            new() { Id = "a", Name = "Бухгалтерия" },
            new() { Id = "b", Name = "Бухгалтерия", ParentId = "gone" }
        };
        var infobases = new List<Infobase>();

        IbasesV8iImporter.Import(filePath, infobases, groups);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Id == "a" && string.IsNullOrWhiteSpace(g.ParentId));
        Assert.Contains(groups, g => g.Id == "b" && g.ParentId == "gone");
    }

    [Fact]
    public void RemoveDuplicateGroups_KeepsGroupWithChildrenAndBases()
    {
        // «Дубликат» пути, у которого есть дочерние группы, не удаляется: победителем
        // становится группа с содержимым, а пустой одноимённый дубликат уходит.
        var filePath = WriteIbases("""
            [Base]
            ID=base-guid
            Folder=/
            Connect=File="C:\base";

            """);

        var groups = new List<Group>
        {
            new() { Id = "a", Name = "Учёт" },                       // пустая, без детей
            new() { Id = "b", Name = "Учёт" },                       // с ребёнком
            new() { Id = "c", Name = "Бухгалтерия", ParentId = "b" }
        };
        var infobases = new List<Infobase>();

        IbasesV8iImporter.Import(filePath, infobases, groups);

        Assert.Equal(2, groups.Count);
        Assert.DoesNotContain(groups, g => g.Id == "a");
        var uch = Assert.Single(groups, g => g.Name == "Учёт");
        Assert.Equal("b", uch.Id);
        var child = Assert.Single(groups, g => g.Name == "Бухгалтерия");
        Assert.Equal("b", child.ParentId); // ребёнок остался у своего родителя
    }

    [Fact]
    public void RemoveDuplicateGroups_RedirectsChildrenByIdAndByPath()
    {
        // Оба одноимённых дубликата имеют детей: второй удаляется, а его дети
        // перевешиваются на сохранённую группу — по Id (issue #165); при невозможности
        // прямого переназначения метод восстанавливает родителя по запомненному полному
        // пути (issue #351). Ни одна группа не должна остаться «сиротой».
        var filePath = WriteIbases("""
            [Base]
            ID=base-guid
            Folder=/
            Connect=File="C:\base";

            """);

        var groups = new List<Group>
        {
            new() { Id = "a", Name = "Учёт" },
            new() { Id = "b", Name = "Учёт" },
            new() { Id = "c", Name = "Бухгалтерия", ParentId = "a" },
            new() { Id = "d", Name = "Отчётность", ParentId = "b" }
        };
        var infobases = new List<Infobase>();

        IbasesV8iImporter.Import(filePath, infobases, groups);

        Assert.Equal(3, groups.Count);
        Assert.DoesNotContain(groups, g => g.Id == "b");
        var report = Assert.Single(groups, g => g.Id == "d");
        Assert.Equal("a", report.ParentId);
        var buh = Assert.Single(groups, g => g.Id == "c");
        Assert.Equal("a", buh.ParentId);
        // Инвариант: после дедупликации каждая группа с ParentId имеет живого родителя.
        Assert.All(
            groups.Where(g => !string.IsNullOrWhiteSpace(g.ParentId)),
            g => Assert.Contains(groups, other => other.Id == g.ParentId));
    }

    [Fact]
    public void Import_RespectsDeletedGroupPathsOnlyForEmptyGroups()
    {
        // Регресс #327: из deleted_groups.json путь с базами в файле восстанавливается,
        // а пустой удалённый путь — нет, даже в одном импорте.
        var filePath = WriteIbases("""
            [Old]
            ID=old-guid
            Folder=/

            [Old With Bases]
            ID=owb-guid
            Folder=/

            [Base]
            ID=base-guid
            Folder=/Old With Bases
            Connect=File="C:\db";

            """);

        var groups = new List<Group>();
        var infobases = new List<Infobase>();

        var result = IbasesV8iImporter.Import(filePath, infobases, groups,
            new[] { "Old", "Old With Bases" });

        Assert.DoesNotContain(groups, g => g.Name == "Old");
        Assert.Contains(groups, g => g.Name == "Old With Bases");
        Assert.Equal("Old With Bases", Assert.Single(infobases).Group);
    }

    [Fact]
    public void Import_RoundTripFolderPath()
    {
        // Гипотеза C: Folder=/НАН/Весь кобошоп из файла даёт внутренний путь
        // «НАН / Весь кобошоп», по которому база раскладывается в дерево.
        var filePath = WriteIbases("""
            [Base]
            ID=base-guid
            Folder=/НАН/Весь кобошоп
            Connect=File="C:\db";

            """);

        var groups = new List<Group>();
        var infobases = new List<Infobase>();

        IbasesV8iImporter.Import(filePath, infobases, groups);

        var ibase = Assert.Single(infobases);
        Assert.Equal("НАН / Весь кобошоп", ibase.Group);
        // Родительская цепочка создана: «НАН» и «НАН / Весь кобошоп».
        Assert.Equal(2, groups.Count);
        var nan = Assert.Single(groups, g => g.Name == "НАН" && string.IsNullOrWhiteSpace(g.ParentId));
        Assert.Contains(groups, g => g.Name == "Весь кобошоп" && g.ParentId == nan.Id);
    }

    private static string WriteIbases(string content)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-imp-{Guid.NewGuid():N}.v8i");
        File.WriteAllText(filePath, content, Encoding.Default);
        return filePath;
    }
}