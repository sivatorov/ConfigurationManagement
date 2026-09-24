using System.Text;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

public sealed class IbasesV8iExporterTests
{
    [Fact]
    public void ExportAndImport_UseSectionReferencesForNestedGroups()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            // Реальный сценарий issue #165: корректная дочерняя секция соседствует с
            // ошибочной секцией, имя которой содержит полный путь как буквальный текст.
            File.WriteAllText(filePath, """
                [Child]
                ID=old-child-id
                Folder=/Parent

                [Parent\Child]
                ID=starter-duplicate-id
                Folder=/

                [Parent]
                ID=parent-id
                Folder=/

                [Database]
                ID=database-id
                Folder=Parent\Child
                Connect=File="C:\database";
                """, Encoding.Default);

            var groups = new List<Group>
            {
                new() { Id = "parent-id", Name = "Parent" },
                new() { Id = "child-id", Name = "Child", ParentId = "parent-id" }
            };
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "database-id",
                    Name = "Database",
                    Group = "Parent / Child",
                    Connection = new ConnectionSettings
                    {
                        Type = ConnectionType.File,
                        FilePath = @"C:\database"
                    }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var firstExport = File.ReadAllText(filePath, Encoding.Default);

            var headers = firstExport
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith('[') && line.EndsWith(']'))
                .ToList();

            Assert.Equal(1, headers.Count(header => header == "[Child]"));
            Assert.DoesNotContain("[Parent\\Child]", headers);
            Assert.Contains("[Parent]", headers);

            var nestedSection = GetSection(firstExport, "Child");
            Assert.Contains("ID=child-id", nestedSection);
            Assert.Contains("Folder=/Parent", nestedSection);
            Assert.DoesNotContain("Connect=", nestedSection);

            var rootSection = GetSection(firstExport, "Parent");
            Assert.Contains("Folder=/", rootSection);

            var databaseSection = GetSection(firstExport, "Database");
            Assert.Contains("Folder=/Parent/Child", databaseSection);

            // Повторный экспорт не должен менять файл или возвращать альтернативную форму.
            IbasesV8iExporter.Export(filePath, infobases, groups);
            Assert.Equal(firstExport, File.ReadAllText(filePath, Encoding.Default));

            // Обратный импорт обязан восстановить внутренний полный путь и ParentId.
            var importedBases = new List<Infobase>();
            var importedGroups = new List<Group>();
            IbasesV8iImporter.Import(filePath, importedBases, importedGroups);

            var importedParent = Assert.Single(importedGroups, g => g.Name == "Parent");
            var importedChild = Assert.Single(importedGroups, g => g.Name == "Child");
            Assert.Equal(importedParent.Id, importedChild.ParentId);
            Assert.Equal("Parent / Child", Assert.Single(importedBases).Group);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_RenamedBaseWithSameId_DoesNotCreateDuplicate()
    {
        // Сценарий issue #278: база переименована в приложении, а в файле (после
        // восстановления) лежит под старым именем с тем же ID. Экспорт не должен
        // создавать дубль — запись обновляется по ID на месте и переименовывается.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [OldBase]
                ID=dup-id
                Connect=File="C:\old";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "dup-id",
                    Name = "NewBase",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\new" }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            var headers = content
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith('[') && line.EndsWith(']'))
                .ToList();

            Assert.Single(headers, h => h == "[NewBase]");
            Assert.DoesNotContain("[OldBase]", headers);
            Assert.Contains("ID=dup-id", GetSection(content, "NewBase"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Import_MatchById_UpdatesNameFromFile_WithoutDuplicates()
    {
        // Сценарий issue #278 на стороне импорта: база в приложении переименована,
        // в файле — прежнее имя с тем же ID. Импорт сопоставляет базу по ID 1С и
        // возвращает имя из файла («информация приезжает обратно»), не создавая дубль
        // и не теряя настройки подключения.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [OldBase]
                ID=dup-id
                Connect=File="C:\old";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "dup-id",
                    Name = "NewBase",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\old" }
                }
            };

            IbasesV8iImporter.Import(filePath, infobases, groups);

            var db = Assert.Single(infobases);
            Assert.Equal("OldBase", db.Name); // имя из файла вернулось в приложение
            Assert.Equal("dup-id", db.Id);
            Assert.Equal(@"C:\old", db.Connection.FilePath);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_DeletionKeepsEntryWithMatchingId()
    {
        // Сценарий issue #278 на стороне удаления: секцию файла, чей ID 1С есть в
        // приложении, нельзя удалять только потому, что её имя не совпадает ни с одним
        // именем базы приложения. Запись, обновлённая по ID на шаге записи, переименована
        // (старое имя не остаётся дублем), а прочие секции с тем же ID сохраняются.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [OldBase]
                ID=same-id
                Connect=File="C:\old";

                [LegacySection]
                ID=same-id
                Connect=File="C:\legacy";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "same-id",
                    Name = "NewBase",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\new" }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            var headers = content
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith('[') && line.EndsWith(']'))
                .ToList();

            // Запись, обновлённая по ID, переименована — старого имени нет, дубля имён нет.
            Assert.Single(headers, h => h == "[NewBase]");
            Assert.DoesNotContain("[LegacySection]", headers);

            // Секция файла с ID из приложения (но другим именем) не удалена.
            Assert.Contains("[OldBase]", headers);
            Assert.Contains("ID=same-id", GetSection(content, "OldBase"));
            Assert.Equal(2, headers.Count);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Sync_RenameRestore_ReturnsNameFromFile_NoDuplicates()
    {
        // Полный сценарий issue #278: база переименована в приложении («Б» → «Б 2»),
        // сохранена (экспорт), затем файл восстановлен вручную до старого состояния.
        // Повторная синхронизация (импорт) обязана вернуть имя из файла без дублей.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Б]
                ID=base-id
                Connect=File="C:\base";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "base-id",
                    Name = "Б 2",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\base" }
                }
            };

            // 1. Сохранение (экспорт) после переименования — файл получает новое имя.
            IbasesV8iExporter.Export(filePath, infobases, groups);
            var exported = File.ReadAllText(filePath, Encoding.Default);
            Assert.Contains("[Б 2]", exported);
            Assert.DoesNotContain("[Б]", exported);
            Assert.Single(exported.Split('\n').Select(l => l.Trim()), l => l.StartsWith('[') && l.EndsWith(']'));

            // 2. Пользователь вручную восстанавливает файл до старого состояния.
            File.WriteAllText(filePath, """
                [Б]
                ID=base-id
                Connect=File="C:\base";
                """, Encoding.Default);

            // 3. Синхронизация (импорт) — имя в приложении возвращается к имени из файла.
            IbasesV8iImporter.Import(filePath, infobases, groups);

            var db = Assert.Single(infobases);
            Assert.Equal("Б", db.Name);
            Assert.Equal("base-id", db.Id);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Sync_RepeatedExportImport_DoesNotCreateDuplicates()
    {
        // Повторная синхронизация (экспорт/импорт несколько раз подряд) не должна
        // плодить дубли ни в приложении, ни в файле ibases.v8i.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Database]
                ID=db-id
                Connect=File="C:\database";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "db-id",
                    Name = "Database",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\database" }
                }
            };

            for (var i = 0; i < 3; i++)
            {
                IbasesV8iExporter.Export(filePath, infobases, groups);
                IbasesV8iImporter.Import(filePath, infobases, groups);
            }

            var db = Assert.Single(infobases);
            Assert.Equal("Database", db.Name);
            Assert.Equal("db-id", db.Id);

            var content = File.ReadAllText(filePath, Encoding.Default);
            var headers = content
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith('[') && line.EndsWith(']'))
                .ToList();
            Assert.Single(headers, h => h == "[Database]");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_RoundTrip_PreservesUnknownKeysAndOrder()
    {
        // Сценарий issue #277: экспорт не должен терять неизвестные ключи секции
        // (OrderInList/OrderInTree/External/WA/DisableLocalSpeechToText) и должен сохранять
        // их исходный порядок. Правка одной базы не должна удалять ключи соседних секций.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Database]
                ID=db-id
                OrderInList=10
                Connect=File="C:\database";
                OrderInTree=3
                External=1
                WA=0
                DisableLocalSpeechToText=1

                [Untouched]
                ID=untouched-id
                Connect=File="C:\untouched";
                External=0
                CustomKey=CustomValue
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "db-id",
                    Name = "Database",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\database" }
                },
                new()
                {
                    Id = "untouched-id",
                    Name = "Untouched",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\untouched" }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            var section = GetSection(content, "Database");
            Assert.Contains("OrderInList=10", section);
            Assert.Contains("OrderInTree=3", section);
            Assert.Contains("External=1", section);
            Assert.Contains("WA=0", section);
            Assert.Contains("DisableLocalSpeechToText=1", section);

            // Порядок неизвестных ключей должен быть сохранён.
            Assert.True(section.IndexOf("OrderInList=10", StringComparison.Ordinal) <
                        section.IndexOf("OrderInTree=3", StringComparison.Ordinal));
            Assert.True(section.IndexOf("OrderInTree=3", StringComparison.Ordinal) <
                        section.IndexOf("External=1", StringComparison.Ordinal));
            Assert.True(section.IndexOf("External=1", StringComparison.Ordinal) <
                        section.IndexOf("WA=0", StringComparison.Ordinal));
            Assert.True(section.IndexOf("WA=0", StringComparison.Ordinal) <
                        section.IndexOf("DisableLocalSpeechToText=1", StringComparison.Ordinal));

            // Неизвестные ключи чужой секции (не в списке приложения) тоже сохраняются.
            var untouched = GetSection(content, "Untouched");
            Assert.Contains("External=0", untouched);
            Assert.Contains("CustomKey=CustomValue", untouched);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_RoundTrip_PreservesKeyOrderIncludingKnownKeys()
    {
        // Сценарий issue #277: известные и неизвестные ключи перемешаны внутри секции.
        // Экспорт не должен пересобирать секцию в каноническом порядке — значения
        // обновляются на своих местах, а исходный порядок строк сохраняется. Отсутствующий
        // ключ (DefaultApp) дописывается в конец секции. Повторный экспорт идемпотентен.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Database]
                OrderInList=10
                ID=db-id
                Connect=File="C:\database";
                Version=8.3.27.1688
                OrderInTree=3
                App=ThinClient
                WA=0
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "db-id",
                    Name = "Database",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\database" },
                    PlatformVersion = "8.3.27.1688",
                    LaunchMode = "Тонкий клиент"
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            var lines = GetSectionLines(content, "Database");
            Assert.Equal(new[]
            {
                "[Database]",
                "OrderInList=10",
                "ID=db-id",
                "Connect=File=\"C:\\database\";",
                "Version=8.3.27.1688",
                "OrderInTree=3",
                "App=ThinClient",
                "WA=0",
                "DefaultApp=ThinClient"
            }, lines);

            // Повторный экспорт не должен менять файл.
            IbasesV8iExporter.Export(filePath, infobases, groups);
            Assert.Equal(content, File.ReadAllText(filePath, Encoding.Default));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_UpdateChangesOnlyNeededLines()
    {
        // Сценарий issue #277: изменение одного поля (путь к файловой базе) должно изменить
        // только строку Connect НА ЕЁ МЕСТЕ — остальные строки секции (включая неизвестные
        // ключи) не переставляются и не теряются, порядок секций файла сохраняется.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Base1]
                ID=id1
                OrderInList=1
                Connect=File="C:\old";
                Version=8.3.27.1688
                Custom=Value

                [Base2]
                ID=id2
                Connect=File="C:\two";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "id1",
                    Name = "Base1",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\new" },
                    PlatformVersion = "8.3.27.1688"
                },
                new()
                {
                    Id = "id2",
                    Name = "Base2",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\two" }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            // Порядок секций файла сохранён.
            Assert.True(content.IndexOf("[Base1]", StringComparison.Ordinal) <
                        content.IndexOf("[Base2]", StringComparison.Ordinal));

            // У Base1 изменилась только строка Connect (на своём месте), остальные строки
            // (в т.ч. пользовательский ключ Custom) сохранены. Нейтральные ключи режима
            // запуска (App/DefaultApp=Auto) не дописываются, если их не было в секции
            // (issue #277).
            var base1 = GetSectionLines(content, "Base1");
            Assert.Equal(new[]
            {
                "[Base1]",
                "ID=id1",
                "OrderInList=1",
                "Connect=File=\"C:\\new\";",
                "Version=8.3.27.1688",
                "Custom=Value"
            }, base1);

            // Строка Connect обновлена между OrderInList и Version (не перенесена в конец).
            var section = GetSection(content, "Base1");
            Assert.True(section.IndexOf("OrderInList=1", StringComparison.Ordinal) <
                        section.IndexOf("Connect=File=\"C:\\new\";", StringComparison.Ordinal));
            Assert.True(section.IndexOf("Connect=File=\"C:\\new\";", StringComparison.Ordinal) <
                        section.IndexOf("Version=", StringComparison.Ordinal));

            // У Base2 значение Connect не изменилось.
            var base2 = GetSectionLines(content, "Base2");
            Assert.Contains("Connect=File=\"C:\\two\";", base2);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_PreservesSectionOrderAndBlankLines()
    {
        // Сценарий issue #277: порядок секций файла и пустые строки ВНУТРИ секции
        // сохраняются при пересохранении; пустая строка-разделитель между секциями
        // больше не добавляется (стартер 1С удаляет её при старте).
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Alpha]
                ID=a1
                Connect=File="C:\alpha";

                Custom=KeepMe

                [Bravo]
                ID=b1
                Connect=File="C:\bravo";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "a1",
                    Name = "Alpha",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\alpha" }
                },
                new()
                {
                    Id = "b1",
                    Name = "Bravo",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\bravo" }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            // Порядок секций файла сохранён.
            Assert.True(content.IndexOf("[Alpha]", StringComparison.Ordinal) <
                        content.IndexOf("[Bravo]", StringComparison.Ordinal));

            // Пустая строка внутри секции Alpha сохранилась между Connect и Custom;
            // нейтральные ключи режима запуска не дописываются (issue #277).
            var alpha = GetSectionLines(content, "Alpha");
            Assert.Equal(new[]
            {
                "[Alpha]",
                "ID=a1",
                "Connect=File=\"C:\\alpha\";",
                "",
                "Custom=KeepMe"
            }, alpha);

            // Между секциями пустая строка-разделитель не добавляется (issue #277):
            // следующая секция начинается сразу после последней строки предыдущей.
            Assert.True(content.Contains(
                "Custom=KeepMe" + Environment.NewLine + "[Bravo]",
                StringComparison.Ordinal));
            Assert.DoesNotContain(
                "Custom=KeepMe" + Environment.NewLine + Environment.NewLine + "[Bravo]",
                content);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_UpdateWithoutDefaultApp_DoesNotAddNeutralAuto()
    {
        // Сценарий issue #277: в секции нет ключей App/DefaultApp, режим запуска в
        // приложении нейтральный («Автоматический»), а в исходном Connect нет Usr/Pwd —
        // экспорт не должен дописывать ни DefaultApp=Auto/App=Auto, ни Usr/Pwd.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Database]
                ID=db-id
                Connect=Srvr="localhost:51541";Ref="GamesScorer51";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "db-id",
                    Name = "Database",
                    LaunchMode = "Автоматический",
                    Connection = new ConnectionSettings
                    {
                        Type = ConnectionType.ClientServer,
                        Server = "localhost",
                        Port = 51541,
                        DatabaseName = "GamesScorer51",
                        User = "admin",
                        Password = "123"
                    }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            var section = GetSection(content, "Database");
            Assert.DoesNotContain("DefaultApp=", section);
            Assert.DoesNotContain("App=", section);
            Assert.DoesNotContain("Usr=", section);
            Assert.DoesNotContain("Pwd=", section);
            Assert.Contains("Connect=Srvr=\"localhost:51541\";Ref=\"GamesScorer51\";", section);

            // Повторный экспорт не должен менять файл (идемпотентность).
            IbasesV8iExporter.Export(filePath, infobases, groups);
            Assert.Equal(content, File.ReadAllText(filePath, Encoding.Default));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_UpdateWithExistingDefaultApp_UpdatesValueInPlace()
    {
        // Сценарий issue #277: если ключ DefaultApp/App БЫЛ в секции, его значение
        // обновляется на своём месте — даже на нейтральное «Auto» (правило только
        // про «не дописывать отсутствующий нейтральный ключ»). Исключение: App=Auto
        // из файла сохраняется (issue #277), чтобы приложение не перезаписывало его
        // производным от DefaultApp значением.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Database]
                ID=db-id
                Connect=File="C:\database";
                App=ThinClient
                DefaultApp=ThinClient
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "db-id",
                    Name = "Database",
                    LaunchMode = "Тонкий клиент",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\database" }
                }
            };

            // Тот же режим, что и в файле, — значения остаются на своих местах.
            IbasesV8iExporter.Export(filePath, infobases, groups);
            var lines = GetSectionLines(File.ReadAllText(filePath, Encoding.Default), "Database");
            Assert.Equal(new[]
            {
                "[Database]",
                "ID=db-id",
                "Connect=File=\"C:\\database\";",
                "App=ThinClient",
                "DefaultApp=ThinClient"
            }, lines);

            // Нейтральный режим в приложении при НАЛИЧИИ не-нейтрального ключа в секции
            // обновляет значение на «Auto» (ключ не удаляется и не пропускается).
            infobases[0].LaunchMode = "Автоматический";
            IbasesV8iExporter.Export(filePath, infobases, groups);
            var lines2 = GetSectionLines(File.ReadAllText(filePath, Encoding.Default), "Database");
            Assert.Equal(new[]
            {
                "[Database]",
                "ID=db-id",
                "Connect=File=\"C:\\database\";",
                "App=Auto",
                "DefaultApp=Auto"
            }, lines2);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_UpdateBaseWithAppAuto_DoesNotRewriteToThickClient()
    {
        // Точный сценарий из последнего комментария issue #277: в файле у базы стоит
        // App=Auto (режим по умолчанию), а DefaultApp=ThickClient. При импорте приложение
        // получает режим запуска «Толстый клиент» (производный от DefaultApp). Экспорт не
        // должен перезаписывать App=Auto на App=ThickClient — файл остаётся без изменений.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Database]
                ID=db-id
                Connect=File="C:\database";
                App=Auto
                DefaultApp=ThickClient
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "db-id",
                    Name = "Database",
                    // Приложение увидело «Толстый клиент» (из DefaultApp), хотя в файле App=Auto.
                    LaunchMode = "Толстый клиент",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\database" }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);
            // App=Auto из файла сохраняется (не перезаписывается на App=ThickClient),
            // DefaultApp остаётся ThickClient.
            Assert.Equal(new[]
            {
                "[Database]",
                "ID=db-id",
                "Connect=File=\"C:\\database\";",
                "App=Auto",
                "DefaultApp=ThickClient"
            }, GetSectionLines(content, "Database"));

            // Повторный экспорт идемпотентен.
            IbasesV8iExporter.Export(filePath, infobases, groups);
            Assert.Equal(content, File.ReadAllText(filePath, Encoding.Default));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_UpdateConnect_WithoutUsrPwd_PreservesComposition()
    {
        // Сценарий issue #277: цель подключения не изменилась, а в исходном Connect
        // Usr/Pwd не было — экспорт не должен дописывать их, даже когда в приложении
        // сохранены логин и пароль.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Database]
                ID=db-id
                Connect=Srvr="localhost:51541";Ref="GamesScorer51";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "db-id",
                    Name = "Database",
                    Connection = new ConnectionSettings
                    {
                        Type = ConnectionType.ClientServer,
                        Server = "localhost",
                        Port = 51541,
                        DatabaseName = "GamesScorer51",
                        User = "admin",
                        Password = "123"
                    }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            var section = GetSection(content, "Database");
            Assert.Contains("Connect=Srvr=\"localhost:51541\";Ref=\"GamesScorer51\";", section);
            Assert.DoesNotContain("Usr=", section);
            Assert.DoesNotContain("Pwd=", section);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_UpdateConnect_WithUsrPwd_RefreshesActualValues()
    {
        // Сценарий issue #277: Usr/Pwd БЫЛИ в исходном Connect — при неизменной цели
        // они остаются и обновляются актуальными значениями из приложения.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Database]
                ID=db-id
                Connect=Srvr="localhost:51541";Ref="GamesScorer51";Usr="old";Pwd="oldpass";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "db-id",
                    Name = "Database",
                    Connection = new ConnectionSettings
                    {
                        Type = ConnectionType.ClientServer,
                        Server = "localhost",
                        Port = 51541,
                        DatabaseName = "GamesScorer51",
                        User = "admin",
                        Password = "123"
                    }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            var section = GetSection(content, "Database");
            Assert.Contains("Connect=Srvr=\"localhost:51541\";Ref=\"GamesScorer51\";Usr=\"admin\";Pwd=\"123\";", section);

            // Повторный экспорт не должен менять файл (идемпотентность).
            IbasesV8iExporter.Export(filePath, infobases, groups);
            Assert.Equal(content, File.ReadAllText(filePath, Encoding.Default));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_UpdateConnect_TargetChanged_RebuildsFully()
    {
        // Сценарий issue #277: цель подключения изменилась (сервер/база или файл) —
        // Connect пересобирается целиком, включая Usr/Pwd из приложения.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Database]
                ID=db-id
                Connect=Srvr="old-server";Ref="OldBase";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "db-id",
                    Name = "Database",
                    Connection = new ConnectionSettings
                    {
                        Type = ConnectionType.ClientServer,
                        Server = "new-server",
                        Port = 51541,
                        DatabaseName = "NewBase",
                        User = "admin",
                        Password = "123"
                    }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);
            var section = GetSection(content, "Database");
            Assert.Contains("Connect=Srvr=\"new-server:51541\";Ref=\"NewBase\";Usr=\"admin\";Pwd=\"123\";", section);

            // Изменение файлового пути — тоже пересборка цели подключения.
            infobases[0].Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\new" };
            IbasesV8iExporter.Export(filePath, infobases, groups);
            var section2 = GetSection(File.ReadAllText(filePath, Encoding.Default), "Database");
            Assert.Contains("Connect=File=\"C:\\new\";", section2);
            Assert.DoesNotContain("Srvr=", section2);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_AddNewInfobase_NeutralMode_OmitsAppAndDefaultApp()
    {
        // Сценарий issue #277: новая база (её не было в файле) с нейтральным режимом
        // запуска не получает App/DefaultApp=Auto; Connect строится как раньше.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Existing]
                ID=existing-id
                Connect=File="C:\existing";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new()
                {
                    Id = "existing-id",
                    Name = "Existing",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\existing" }
                },
                new()
                {
                    Id = "new-id",
                    Name = "NewBase",
                    LaunchMode = "Автоматический",
                    Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\new" }
                }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            var lines = GetSectionLines(content, "NewBase");
            Assert.Equal(new[]
            {
                "[NewBase]",
                "ID=new-id",
                "Connect=File=\"C:\\new\";"
            }, lines);
            Assert.DoesNotContain("App=", content);
            Assert.DoesNotContain("DefaultApp=", content);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_NoBlankLinesBetweenSections_RemainsWithoutBlankLines()
    {
        // Сценарий issue #277: файл, в котором секции идут подряд без пустых строк,
        // при экспорте не должен получать пустые строки-разделители между секциями.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Alpha]
                ID=a1
                Connect=File="C:\alpha";
                [Bravo]
                ID=b1
                Connect=File="C:\bravo";
                [Gamma]
                ID=g1
                Connect=File="C:\gamma";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new() { Id = "a1", Name = "Alpha", Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\alpha" } },
                new() { Id = "b1", Name = "Bravo", Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\bravo" } },
                new() { Id = "g1", Name = "Gamma", Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\gamma" } }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            // Пустых строк нет нигде (ни внутри секций, ни между ними).
            Assert.DoesNotContain(Environment.NewLine + Environment.NewLine, content);

            // Порядок секций сохранён.
            Assert.True(content.IndexOf("[Alpha]", StringComparison.Ordinal) <
                        content.IndexOf("[Bravo]", StringComparison.Ordinal));
            Assert.True(content.IndexOf("[Bravo]", StringComparison.Ordinal) <
                        content.IndexOf("[Gamma]", StringComparison.Ordinal));

            // Повторный экспорт не должен менять файл (идемпотентность).
            IbasesV8iExporter.Export(filePath, infobases, groups);
            Assert.Equal(content, File.ReadAllText(filePath, Encoding.Default));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_AddNewInfobase_NoBlankLineBeforeNewSection()
    {
        // Сценарий issue #277: новая база дописывается в конец файла сразу после
        // последней строки предыдущей секции — без пустой строки-разделителя.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Existing]
                ID=existing-id
                Connect=File="C:\existing";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new() { Id = "existing-id", Name = "Existing", Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\existing" } },
                new() { Id = "new-id", Name = "NewBase", Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\new" } }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            // Новая секция идёт сразу после последней строки существующей, без пустой строки.
            Assert.Contains("Connect=File=\"C:\\existing\";" + Environment.NewLine + "[NewBase]", content);
            Assert.DoesNotContain(Environment.NewLine + Environment.NewLine, content);

            // Повторный экспорт не должен менять файл (идемпотентность).
            IbasesV8iExporter.Export(filePath, infobases, groups);
            Assert.Equal(content, File.ReadAllText(filePath, Encoding.Default));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void AddInfobasesToFile_AppendsNewSection_WithoutBlankLine()
    {
        // Сценарий issue #277 на пути AddInfobasesToFile: новая база дописывается в конец
        // без пустой строки перед секцией, чужие записи файла сохраняются.
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Existing]
                ID=existing-id
                Connect=File="C:\existing";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new() { Id = "new-id", Name = "NewBase", Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\new" } }
            };

            IbasesV8iExporter.AddInfobasesToFile(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            // Существующая запись сохранена, новая дописана без пустой строки-разделителя.
            Assert.Contains("[Existing]", content);
            Assert.Contains("Connect=File=\"C:\\existing\";" + Environment.NewLine + "[NewBase]", content);
            Assert.DoesNotContain(Environment.NewLine + Environment.NewLine, content);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Export_PreservesBlankLinesInsideSection_DropsSectionSeparators()
    {
        // Сценарий issue #277: пустая строка ВНУТРИ секции сохраняется дословно, а
        // пустые строки-разделители между секциями не восстанавливаются (стартер 1С
        // удаляет их при старте).
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            File.WriteAllText(filePath, """
                [Alpha]
                ID=a1
                Connect=File="C:\alpha";

                Custom=KeepMe

                [Bravo]
                ID=b1
                Connect=File="C:\bravo";
                """, Encoding.Default);

            var groups = new List<Group>();
            var infobases = new List<Infobase>
            {
                new() { Id = "a1", Name = "Alpha", Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\alpha" } },
                new() { Id = "b1", Name = "Bravo", Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\bravo" } }
            };

            IbasesV8iExporter.Export(filePath, infobases, groups);
            var content = File.ReadAllText(filePath, Encoding.Default);

            // Пустая строка внутри секции Alpha сохранена дословно.
            var alpha = GetSectionLines(content, "Alpha");
            Assert.Equal(new[]
            {
                "[Alpha]",
                "ID=a1",
                "Connect=File=\"C:\\alpha\";",
                "",
                "Custom=KeepMe"
            }, alpha);

            // Пустой строки-разделителя между секциями нет.
            Assert.Contains("Custom=KeepMe" + Environment.NewLine + "[Bravo]", content);

            // Повторный экспорт не должен менять файл (идемпотентность).
            IbasesV8iExporter.Export(filePath, infobases, groups);
            Assert.Equal(content, File.ReadAllText(filePath, Encoding.Default));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static string GetSection(string content, string name)
    {
        var marker = $"[{name}]";
        var start = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Section {marker} was not found.");

        var next = content.IndexOf("\n[", start + marker.Length, StringComparison.Ordinal);
        return next >= 0 ? content[start..next] : content[start..];
    }

    /// <summary>Возвращает строки секции (заголовок включён) без переводов строк; пустые строки сохраняются.</summary>
    private static List<string> GetSectionLines(string content, string name)
    {
        var lines = GetSection(content, name)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
