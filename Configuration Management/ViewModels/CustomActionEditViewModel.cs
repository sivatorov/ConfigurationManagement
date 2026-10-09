using System;
using System.Collections.Generic;
using System.Linq;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Форма редактирования пользовательского действия контекстного меню (0.3.9.195, функция 7):
/// наименование, команда-шаблон, область применения, интерпретатор, флажки, таймаут (в
/// секундах), горячая клавиша и рабочая папка. Чистый .NET, используется обеими платформами.
/// Таймаут хранится в секундах для удобства ввода и конвертируется в
/// <see cref="CustomAction.TimeoutMs"/> в <see cref="ApplyTo"/>.
/// </summary>
public class CustomActionEditViewModel : ViewModelBase
{
    /// <summary>
    /// Доступные токены подстановки (вставляются двойным кликом в поле команды): русские
    /// имена в фигурных скобках наравне с прежним синтаксисом <c>%…%</c> (цикл 0.3.9.194).
    /// </summary>
    public static readonly IReadOnlyList<ScriptTokenHint> AvailableTokens = new[]
    {
        new ScriptTokenHint("{ИмяБазы}", "CustomAction.TokenName"),
        new ScriptTokenHint("{СтрокаПодключения}", "CustomAction.TokenConnectionString"),
        new ScriptTokenHint("{Каталог}", "CustomAction.TokenCatalog"),
        new ScriptTokenHint("{ПутьИБ}", "CustomAction.TokenPath"),
        new ScriptTokenHint("{Id}", "CustomAction.TokenId"),
        new ScriptTokenHint("{Тип}", "CustomAction.TokenType"),
        new ScriptTokenHint("{Сервер}", "CustomAction.TokenServer"),
        new ScriptTokenHint("{ИмяНаСервере}", "CustomAction.TokenDatabase"),
        new ScriptTokenHint("{ИмяГруппы}", "CustomAction.TokenGroup"),
        new ScriptTokenHint("{Пользователь}", "CustomAction.TokenUser"),
        new ScriptTokenHint("{Пароль}", "CustomAction.TokenPassword"),
        new ScriptTokenHint("{Дата}", "CustomAction.TokenDate")
    };

    /// <summary>
    /// Доступные значения интерпретатора для выпадающего списка формы
    /// (порядок: Авто, cmd, PowerShell, sh) — как у сценариев запуска скриптов.
    /// </summary>
    public static IReadOnlyList<ScriptShell> ShellOptions { get; } =
        new[] { ScriptShell.Auto, ScriptShell.Cmd, ScriptShell.PowerShell, ScriptShell.Sh };

    /// <summary>Нижняя граница таймаута, секунды.</summary>
    public const int MinTimeoutSeconds = 1;

    /// <summary>Верхняя граница таймаута, секунды (10 минут).</summary>
    public const int MaxTimeoutSeconds = 600;

    /// <summary>Редактируемое действие (для сравнения по Id при проверке занятого хоткея); null — новое.</summary>
    private readonly CustomAction? _sourceAction;

    /// <summary>
    /// Остальные действия списка (0.3.9.198): их горячие клавиши считаются занятыми;
    /// собственное действие при редактировании исключается по Id.
    /// </summary>
    private readonly IReadOnlyList<CustomAction> _existingActions;

    /// <param name="action">Редактируемое действие или <c>null</c> для нового.</param>
    /// <param name="existingActions">
    /// Существующие действия из хранилища (0.3.9.198): их горячие клавиши считаются занятыми,
    /// кроме самого редактируемого действия (сравнение по Id). Дополнительно проверяются
    /// известные системные сочетания главного окна (<see cref="GetKnownSystemHotkeys"/>).
    /// Пусто/null — проверка конфликтов не выполняется (тесты, изолированный контекст).
    /// </param>
    public CustomActionEditViewModel(CustomAction? action, IReadOnlyList<CustomAction>? existingActions = null)
    {
        _sourceAction = action;
        _existingActions = existingActions ?? Array.Empty<CustomAction>();
        if (action is not null)
        {
            Name = action.Name;
            Command = action.Command ?? "";
            SelectedScope = action.Scope;
            SelectedShell = action.Shell;
            SupportsBatch = action.SupportsBatch;
            RunWithoutConfirm = action.RunWithoutConfirm;
            EscapeValues = action.EscapeValues;
            TimeoutSeconds = Math.Clamp(action.TimeoutMs / 1000, MinTimeoutSeconds, MaxTimeoutSeconds);
            Hotkey = action.Hotkey ?? "";
            WorkingDirectory = action.WorkingDirectory ?? "";
        }
    }

    /// <summary>Наименование действия (показывается в контекстном меню).</summary>
    public string Name { get; set; } = "";

    /// <summary>Команда/шаблон скрипта (может быть многострочной).</summary>
    public string Command { get; set; } = "";

    /// <summary>Область применения: база / группа / обе.</summary>
    public CustomActionScope SelectedScope { get; set; } = CustomActionScope.Both;

    /// <summary>Интерпретатор (shell): Auto — по платформе (cmd.exe/sh).</summary>
    public ScriptShell SelectedShell { get; set; } = ScriptShell.Auto;

    /// <summary>Показывать действие в блоке мультивыделения «Для выделенных (N)…».</summary>
    public bool SupportsBatch { get; set; }

    /// <summary>Выполнять без подтверждения (индивидуальный признак действия).</summary>
    public bool RunWithoutConfirm { get; set; }

    /// <summary>Экранировать подставляемые значения для выбранного shell (по умолчанию true).</summary>
    public bool EscapeValues { get; set; } = true;

    /// <summary>Таймаут ожидания завершения команды, секунды (диапазон 1…600).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Горячая клавиша действия (опционально), например «F8» или «Ctrl+Alt+F8».</summary>
    public string Hotkey { get; set; } = "";

    /// <summary>Рабочая папка процесса (опционально); пусто — каталог приложения.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>
    /// Валидация полей формы. Возвращает ключ локализации ошибки либо <c>null</c>,
    /// если всё корректно: имя и команда непустые, таймаут в диапазоне [1..600] секунд,
    /// горячая клавиша (если задана) не занята другим действием/системным сочетанием.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "CustomAction.NameRequired";
        if (string.IsNullOrWhiteSpace(Command))
            return "CustomAction.CommandRequired";
        if (TimeoutSeconds < MinTimeoutSeconds || TimeoutSeconds > MaxTimeoutSeconds)
            return "CustomAction.TimeoutInvalid";
        var hotkey = Hotkey?.Trim();
        if (!string.IsNullOrEmpty(hotkey) && IsHotkeyTaken(hotkey))
            return "CustomAction.HotkeyConflict";
        return null;
    }

    /// <summary>
    /// Занято ли сочетание (0.3.9.198): нормализованное сравнение без учёта регистра
    /// с хоткеями остальных действий (собственное действие исключается по Id) и с
    /// известными системными сочетаниями главного окна.
    /// </summary>
    private bool IsHotkeyTaken(string hotkey)
    {
        foreach (var existing in _existingActions)
        {
            // Свой хоткей редактируемого действия конфликтом не считается.
            if (_sourceAction is not null && existing.Id == _sourceAction.Id)
                continue;
            var value = existing.Hotkey?.Trim();
            if (string.IsNullOrEmpty(value))
                continue;
            if (string.Equals(value, hotkey, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var system in GetKnownSystemHotkeys())
        {
            if (string.Equals(system, hotkey, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Известные системные сочетания главного окна: настраиваемые хоткеи из настроек
    /// профиля (<see cref="AppSettings"/> через <see cref="IInfobaseRepository"/>) и жёстко
    /// заданные в коде («Выполнить скрипт» — F5). Редактор предупреждает о конфликте с ними
    /// (<see cref="Validate"/>), чтобы действие не перебивало штатные клавиши. При
    /// недоступности настроек возвращаются только жёсткие сочетания.
    /// </summary>
    public static IReadOnlyList<string> GetKnownSystemHotkeys()
    {
        var result = new List<string>();
        try
        {
            var settings = AppServices.GetRequiredService<IInfobaseRepository>().LoadSettings();
            if (settings is not null)
            {
                result.AddRange(new[]
                {
                    settings.HotkeyEnterprise, settings.HotkeyConfigurator, settings.HotkeyFavorite,
                    settings.HotkeyPin, settings.HotkeyAdd, settings.HotkeyDelete, settings.HotkeyEdit,
                    settings.HotkeyClearCache, settings.HotkeyLockApp, settings.HotkeySessionLock,
                    settings.HotkeyCheckUpdate, settings.HotkeyActualReleases, settings.HotkeyRunBackup,
                    settings.HotkeyExportsList, settings.HotkeyFindInList, settings.HotkeyCommandPalette,
                    settings.HotkeyBookmarksMenu,
                    settings.HotkeySwitchUser, settings.HotkeyClearSearch, settings.HotkeyClearTags,
                    settings.HotkeyRightPanelDetails, settings.HotkeyShowAll, settings.HotkeyShowFavorites,
                    settings.HotkeyShowRecent, settings.HotkeyShowRunning, settings.HotkeyZoomIn, settings.HotkeyZoomOut,
                    settings.HotkeyZoomReset, settings.HotkeyCheckIntegrity, settings.HotkeyServerConsole,
                    settings.ScreenshotHotkey
                });
            }
        }
        catch
        {
            // Редактор работает и без настроек: системные сочетания не известны.
        }
        // Жёстко заданные в коде сочетания (не настраиваются): «Выполнить скрипт» (F5).
        result.Add("F5");
        return result
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Переносит заполненные поля формы в действие. Идентификатор <see cref="CustomAction.Id"/>
    /// НЕ трогается (перезапись по тому же Id в хранилище не плодит дубликат).
    /// </summary>
    public void ApplyTo(CustomAction action)
    {
        action.Name = Name.Trim();
        action.Command = Command.Trim();
        action.Scope = SelectedScope;
        action.Shell = SelectedShell;
        action.SupportsBatch = SupportsBatch;
        action.RunWithoutConfirm = RunWithoutConfirm;
        action.EscapeValues = EscapeValues;
        action.TimeoutMs = Math.Clamp(TimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds) * 1000;
        action.Hotkey = Hotkey.Trim();
        action.WorkingDirectory = WorkingDirectory.Trim();
    }

    /// <summary>
    /// Пример полной командной строки действия с подстановками для указанной базы
    /// (живая подсказка в окне редактора): <see cref="ScriptParameterResolver.BuildActionShellCommandLine"/>
    /// с обёрткой выбранного интерпретатора действия.
    /// </summary>
    public static string BuildExampleCommandLine(CustomAction draft, Infobase? exampleBase, DateTime? now = null)
        => ScriptParameterResolver.BuildActionShellCommandLine(draft, exampleBase, now);
}