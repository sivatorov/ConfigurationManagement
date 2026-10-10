using System.Text.RegularExpressions;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Регресс-тест рассинхронизации локализации окна «Замена строк подключения»
/// (issue #357): выпущенная сборка 0.3.10.3 не содержала ключей
/// ConnectionReplace.FieldLabel/ScopeLabel/ModeLabel во встроенных JSON, и у
/// пользователей с внешним устаревшим Languages/ru.json подписи полей показывались
/// «как есть» ключами. Тест извлекает ВСЕ используемые окном ключи из XAML (обеих
/// разметок) и кода (WPF/Avalonia/ViewModel) и ассертит, что КАЖДЫЙ есть и во
/// встроенном ru.json, и во встроенном en.json (те же файлы встраиваются в сборку
/// как EmbeddedResource Localization\Languages\*.json).
/// </summary>
public sealed class ConnectionReplaceLocalizationTests
{
    /// <summary>Возвращает корень репозитория (как в SettingsWindowXamlResourcesTests).</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Configuration Management", "Views")) &&
                Directory.Exists(Path.Combine(dir.FullName, "ConfigurationManagement.Tests")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Корень репозитория не найден из тестового каталога.");
    }

    private static string ReadProjectFile(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(fullPath), $"Файл проекта не найден: {relativePath}");
        return File.ReadAllText(fullPath);
    }

    /// <summary>Файлы окна и его ViewModel, где используются ключи ConnectionReplace.*.</summary>
    private static readonly string[] SourceFiles =
    {
        Path.Combine("Configuration Management", "Views", "ConnectionReplaceWindow.xaml"),
        Path.Combine("Configuration Management", "Views", "ConnectionReplaceWindow.xaml.cs"),
        Path.Combine("Configuration Management", "Views", "ConnectionReplaceWindow.Avalonia.cs"),
        Path.Combine("Configuration Management", "ViewModels", "ConnectionReplaceViewModel.cs"),
        Path.Combine("Configuration Management", "ViewModels", "MainViewModel.ConnectionReplace.cs"),
    };

    private static HashSet<string> CollectUsedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles)
        {
            var content = ReadProjectFile(file);
            if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                // XAML: {loc:Loc ConnectionReplace.FieldLabel}.
                foreach (Match m in Regex.Matches(content, @"loc:Loc\s+(ConnectionReplace\.[A-Za-z0-9_.]+)"))
                    keys.Add(m.Groups[1].Value);
            }
            else
            {
                // Код: ключи используются СТРОКОВЫМИ литералами
                // T("ConnectionReplace.Fields.Server") — комментарии и имена файлов
                // в строковые литералы не попадают.
                foreach (Match m in Regex.Matches(
                             content, @"[""'](ConnectionReplace\.[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)[""']"))
                    keys.Add(m.Groups[1].Value);
            }
        }

        return keys;
    }

    private static HashSet<string> CollectDictionaryKeys(string relativePath)
    {
        var content = ReadProjectFile(relativePath);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(content, @"""(ConnectionReplace\.[A-Za-z0-9_.]+)""\s*:"))
            keys.Add(m.Groups[1].Value);
        return keys;
    }

    [Fact]
    public void ConnectionReplace_EveryUsedKey_PresentInBothBuiltInDictionaries()
    {
        var used = CollectUsedKeys();
        Assert.True(used.Count >= 25,
            "Ожидалось не менее 25 ключей ConnectionReplace.* в исходниках окна, найдено: " + used.Count);

        var ru = CollectDictionaryKeys(Path.Combine("Configuration Management", "Localization", "Languages", "ru.json"));
        var en = CollectDictionaryKeys(Path.Combine("Configuration Management", "Localization", "Languages", "en.json"));

        var missingInRu = used.Where(k => !ru.Contains(k)).ToList();
        var missingInEn = used.Where(k => !en.Contains(k)).ToList();

        Assert.True(missingInRu.Count == 0,
            "Ключи ConnectionReplace.*, отсутствующие во встроенном ru.json (issue #357): " +
            string.Join(", ", missingInRu));
        Assert.True(missingInEn.Count == 0,
            "Ключи ConnectionReplace.*, отсутствующие во встроенном en.json (issue #357): " +
            string.Join(", ", missingInEn));
    }

    [Theory]
    [InlineData("ConnectionReplace.FieldLabel")]
    [InlineData("ConnectionReplace.ScopeLabel")]
    [InlineData("ConnectionReplace.ModeLabel")]
    public void ConnectionReplace_LabelKeys_MissingInReleasedBuild_ArePresentNow(string key)
    {
        // Именно эти ключи отсутствовали во встроенных JSON сборки 0.3.10.3 (issue #357).
        var ru = CollectDictionaryKeys(Path.Combine("Configuration Management", "Localization", "Languages", "ru.json"));
        var en = CollectDictionaryKeys(Path.Combine("Configuration Management", "Localization", "Languages", "en.json"));
        Assert.Contains(key, ru);
        Assert.Contains(key, en);
    }

    [Fact]
    public void ConnectionReplace_DictionariesSameKeySet()
    {
        // Словари ru/en должны содержать одинаковый набор ключей окна —
        // отсутствие ключа в одном из них означает «ключ вместо текста» в этом языке.
        var ru = CollectDictionaryKeys(Path.Combine("Configuration Management", "Localization", "Languages", "ru.json"));
        var en = CollectDictionaryKeys(Path.Combine("Configuration Management", "Localization", "Languages", "en.json"));
        Assert.Equal(ru, en);
    }
}
