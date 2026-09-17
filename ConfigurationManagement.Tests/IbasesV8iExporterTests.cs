using System.Text;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

public sealed class IbasesV8iExporterTests
{
    [Fact]
    public void Export_RewritesNestedGroupToNativeStarterFormatAndStaysIdempotent()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"ibases-{Guid.NewGuid():N}.v8i");
        try
        {
            // Реальный сценарий issue #165: одна папка присутствует одновременно в форме,
            // которую раньше писал экспортёр, и в нативной форме штатного стартера 1С.
            File.WriteAllText(filePath, """
                [Child]
                ID=old-child-id
                Folder=Parent

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

            var nativeNestedPath = $"Parent{Path.DirectorySeparatorChar}Child";
            var headers = firstExport
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith('[') && line.EndsWith(']'))
                .ToList();

            Assert.Equal(1, headers.Count(header => header == $"[{nativeNestedPath}]"));
            Assert.DoesNotContain("[Child]", headers);
            Assert.Contains("[Parent]", headers);

            var nestedSection = GetSection(firstExport, nativeNestedPath);
            Assert.Contains("ID=child-id", nestedSection);
            Assert.Contains("Folder=/", nestedSection);
            Assert.DoesNotContain("Connect=", nestedSection);

            var rootSection = GetSection(firstExport, "Parent");
            Assert.Contains("Folder=/", rootSection);

            var databaseSection = GetSection(firstExport, "Database");
            Assert.Contains($"Folder={nativeNestedPath}", databaseSection);

            // Повторный экспорт не должен менять файл или возвращать альтернативную форму.
            IbasesV8iExporter.Export(filePath, infobases, groups);
            Assert.Equal(firstExport, File.ReadAllText(filePath, Encoding.Default));
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
}
