using System.Collections.Generic;
using Configuration_Management.Localization;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты фолбэка перевода на встроенные языки (issue #357): внешний файл языка
/// (Languages/ рядом с exe или в каталоге данных) с кодом «ru»/«en» переопределяет
/// встроенный словарь ЦЕЛИКОМ — в устаревшем внешнем файле нет ключей новых окон,
/// и интерфейс показывал ключи локализации («ConnectionReplace.Fields.Server»)
/// вместо текстов. Перевод обязан искать недостающий ключ во ВСТРОЕННЫХ ru/en.
/// Тестируется чистое ядро <see cref="LocalizationManager.TranslateFromDictionaries"/> —
/// без мутации singleton (параллельные тесты читают локализацию).
/// </summary>
public sealed class LocalizationFallbackTests
{
    /// <summary>Текущий (внешний/переопределённый) словарь: только старые ключи.</summary>
    private static readonly Dictionary<string, string> StaleExternalRu = new()
    {
        ["Old.Key"] = "Старое значение",
    };

    /// <summary>Встроенный русский словарь (фрагмент): ключи новых окон есть.</summary>
    private static readonly Dictionary<string, string> BuiltInRu = new()
    {
        ["ConnectionReplace.Fields.Server"] = "Сервер",
        ["ConnectionReplace.Scopes.All"] = "Все базы",
        ["ConnectionReplace.Modes.Substring"] = "Подстрока",
        ["ConnectionReplace.Fields.Server.Key"] = "Сервер",
        // issue #357 (0.3.11): ключи, отсутствовавшие во встроенных JSON 0.3.10.3.
        ["ConnectionReplace.FieldLabel"] = "Поле",
        ["ConnectionReplace.ScopeLabel"] = "Область",
        ["ConnectionReplace.ModeLabel"] = "Режим",
    };

    /// <summary>Встроенный английский словарь (фрагмент).</summary>
    private static readonly Dictionary<string, string> BuiltInEn = new()
    {
        ["En.Only.Key"] = "English value",
    };

    [Fact]
    public void TranslateFromDictionaries_KeyMissingInExternal_FallsBackToBuiltInRu()
    {
        // issue #357: во внешнем (устаревшем) файле ключа нового окна нет (а во
        // встроенном английском — тоже) — текст подставляется из ВСТРОЕННОГО русского
        // словаря, а НЕ «как есть» ключом.
        var result = LocalizationManager.TranslateFromDictionaries(
            StaleExternalRu, BuiltInEn, BuiltInRu, "ConnectionReplace.Fields.Server");

        Assert.Equal("Сервер", result);
    }

    [Fact]
    public void TranslateFromDictionaries_KeyPresentInExternal_ExternalWins()
    {
        // Ключ из внешнего файла берётся из него (переопределение сохраняет смысл).
        var result = LocalizationManager.TranslateFromDictionaries(
            StaleExternalRu, BuiltInEn, BuiltInRu, "Old.Key");

        Assert.Equal("Старое значение", result);
    }

    [Fact]
    public void TranslateFromDictionaries_NoCurrentDictionary_FallsBackToBuiltInEnThenRu()
    {
        // Нет текущего словаря (язык не загружен): сначала встроенный английский…
        var fromEn = LocalizationManager.TranslateFromDictionaries(
            null, BuiltInEn, BuiltInRu, "En.Only.Key");
        Assert.Equal("English value", fromEn);

        // …затем встроенный русский.
        var fromRu = LocalizationManager.TranslateFromDictionaries(
            null, null, BuiltInRu, "ConnectionReplace.Fields.Server");
        Assert.Equal("Сервер", fromRu);
    }

    [Fact]
    public void TranslateFromDictionaries_KeyMissingEverywhere_ReturnsKeyItself()
    {
        var result = LocalizationManager.TranslateFromDictionaries(
            StaleExternalRu, BuiltInEn, BuiltInRu, "Totally.Unknown.Key");

        Assert.Equal("Totally.Unknown.Key", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TranslateFromDictionaries_EmptyKey_ReturnsKeyAsIs(string? key)
    {
        var result = LocalizationManager.TranslateFromDictionaries(
            StaleExternalRu, BuiltInEn, BuiltInRu, key!);

        Assert.Equal(key, result);
    }

    [Theory]
    [InlineData("ConnectionReplace.FieldLabel", "Поле")]
    [InlineData("ConnectionReplace.ScopeLabel", "Область")]
    [InlineData("ConnectionReplace.ModeLabel", "Режим")]
    public void TranslateFromDictionaries_LabelsMissingInReleasedBuild_FallBackToBuiltIn(string key, string expected)
    {
        // Суть issue #357 (0.3.11): ключи FieldLabel/ScopeLabel/ModeLabel отсутствовали
        // во встроенных JSON сборки 0.3.10.3 — при внешнем устаревшем ru.json окно
        // показывало ключи вместо «Поле/Область/Режим». Фолбэк на встроенный словарь
        // обязан вернуть текст.
        var result = LocalizationManager.TranslateFromDictionaries(
            StaleExternalRu, BuiltInEn, BuiltInRu, key);

        Assert.Equal(expected, result);
    }
}
