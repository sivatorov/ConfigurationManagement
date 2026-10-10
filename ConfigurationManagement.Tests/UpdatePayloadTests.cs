using System.IO;
using System.IO.Compression;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты защиты загрузчика автообновления (issue #358): скачанный файл обязан быть
/// PE-образом («MZ») либо ZIP с exe внутри; HTML и ZIP без exe — отклоняются.
/// Логика чистая (UpdatePayload), сети нет.
/// </summary>
public sealed class UpdatePayloadTests : IDisposable
{
    private readonly string _dir;

    public UpdatePayloadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cm-update-payload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* не критично */ }
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] FakeExeBytes() => [0x4D, 0x5A, 0x90, 0x00, 1, 2, 3, 4];

    private static byte[] FakeHtmlBytes() =>
        System.Text.Encoding.UTF8.GetBytes("<!doctype html><html><body>moved</body></html>");

    [Fact]
    public void Classify_MzBytes_IsPortableExecutable()
    {
        var bytes = FakeExeBytes();
        Assert.Equal(UpdatePayload.PayloadSignature.PortableExecutable, UpdatePayload.Classify(bytes));
    }

    [Fact]
    public void Classify_PkBytes_IsZipArchive()
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 9, 9 };
        Assert.Equal(UpdatePayload.PayloadSignature.ZipArchive, UpdatePayload.Classify(bytes));
    }

    [Fact]
    public void Classify_HtmlBytes_IsUnknown()
    {
        var bytes = FakeHtmlBytes();
        Assert.Equal(UpdatePayload.PayloadSignature.Unknown, UpdatePayload.Classify(bytes));
    }

    [Fact]
    public void EnsureExecutablePayload_PeFile_PassThrough()
    {
        var path = WriteFile("ConfigurationManagement.0.3.11.0.new.exe", FakeExeBytes());

        var result = UpdatePayload.EnsureExecutablePayload(path, _dir);

        Assert.Equal(path, result);
        Assert.True(File.Exists(path));
    }

    /// <summary>Готовит путь к zip-архиву (файл не создаётся — ZipFile.Open в режиме Create).</summary>
    private string ZipPath() => Path.Combine(_dir, "ConfigurationManagement.0.3.11.0.new.exe");

    [Fact]
    public void EnsureExecutablePayload_ZipWithExeEntry_ExtractsExe()
    {
        var zipPath = ZipPath();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry(UpdatePayload.PayloadEntryName).Open())
        {
            entry.Write(FakeExeBytes());
        }

        var result = UpdatePayload.EnsureExecutablePayload(zipPath, _dir);

        Assert.NotNull(result);
        Assert.NotEqual(zipPath, result);
        Assert.True(File.Exists(result));
        // Распакованный файл начинается с «MZ».
        var head = File.ReadAllBytes(result!);
        Assert.True(UpdatePayload.Classify(head) == UpdatePayload.PayloadSignature.PortableExecutable);
    }

    [Fact]
    public void EnsureExecutablePayload_ZipWithoutExeEntry_ReturnsNull()
    {
        var zipPath = ZipPath();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("readme.txt").Open())
        {
            entry.Write("nothing"u8.ToArray());
        }

        var result = UpdatePayload.EnsureExecutablePayload(zipPath, _dir);

        Assert.Null(result);
        Assert.False(File.Exists(zipPath));
    }

    [Fact]
    public void EnsureExecutablePayload_HtmlFile_DeletedAndNull()
    {
        var path = WriteFile("ConfigurationManagement.0.3.11.0.new.exe", FakeHtmlBytes());

        var result = UpdatePayload.EnsureExecutablePayload(path, _dir);

        Assert.Null(result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void EnsureExecutablePayload_ZipWithExeInSubdirectory_ExtractsExe()
    {
        var zipPath = ZipPath();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("win-x64/" + UpdatePayload.PayloadEntryName).Open())
        {
            entry.Write(FakeExeBytes());
        }

        var result = UpdatePayload.EnsureExecutablePayload(zipPath, _dir);

        Assert.NotNull(result);
        Assert.True(File.Exists(result));
    }
}
