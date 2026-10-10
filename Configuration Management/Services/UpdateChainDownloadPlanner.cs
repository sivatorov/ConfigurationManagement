using System;
using System.Collections.Generic;
using System.IO;

namespace Configuration_Management.Services;

/// <summary>
/// Планирование докачки цепочки обновлений (issue #352, комментарий 7OH от 2026-10-09):
/// перед запуском загрузки цепочки определяет, какие файлы уже скачаны (существуют и
/// непусты), чтобы не качать их повторно и показать корректный счётчик «Скачивается
/// X из Y» — по реально требующим скачивания файлам, а не по всем шагам цепочки.
/// Чистая логика без сети и UI — для использования в WPF- и Avalonia-окнах проверки
/// обновлений и в юнит-тестах.
/// </summary>
public static class UpdateChainDownloadPlanner
{
    /// <summary>Файл считается скачанным, если он существует и его размер больше нуля.
    /// Временный файл загрузки (<c><имя><see cref="OneCUpdatesService.PartialSuffix"/></c>,
    /// оставшийся от прерванной загрузки) скачанным не считается — докачка пойдёт
    /// под финальное имя заново. Internal — для юнит-тестов.</summary>
    internal static bool IsDownloaded(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           !path.EndsWith(OneCUpdatesService.PartialSuffix, StringComparison.OrdinalIgnoreCase) &&
           File.Exists(path) &&
           new FileInfo(path).Length > 0;

    /// <summary>Индексы шагов цепочки (в исходном порядке), файлы которых требуется
    /// скачать: файл отсутствует, пуст или является временным файлом загрузки. Шаги
    /// с существующими непустыми файлами пропускаются (issue #352). Internal — для
    /// юнит-тестов.</summary>
    internal static List<int> SelectPendingSteps(IReadOnlyList<string> targetPaths)
    {
        var pending = new List<int>();
        for (var i = 0; i < targetPaths.Count; i++)
            if (!IsDownloaded(targetPaths[i]))
                pending.Add(i);

        return pending;
    }

    /// <summary>Начальная папка диалога выбора каталога цепочки (issue #352.2):
    /// сохранённая в настройках, если существует, иначе профиль пользователя. Общая
    /// для WPF- и Avalonia-окон проверки обновлений. Internal — для юнит-тестов.</summary>
    internal static string GetChainInitialFolder(string? settingsFolder)
        => !string.IsNullOrWhiteSpace(settingsFolder) && Directory.Exists(settingsFolder)
            ? settingsFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
