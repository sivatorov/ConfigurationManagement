using System;
using System.Collections.Generic;
using System.Linq;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Узел дерева каталога версий платформы в окне «Скачивание версии платформы 1С»
/// (issue #330: список должен быть деревом «8.x \ 8.x.yy \ полная версия» с сортировкой
/// по убыванию). Группы/линии несут только заголовок, листья — ссылку на релиз каталога.
/// </summary>
public sealed class PlatformCatalogNode
{
    /// <param name="name">Заголовок узла («8.3», «8.3.27», «8.3.27.2214»).</param>
    /// <param name="release">Релиз каталога у листьев; null у групп.</param>
    public PlatformCatalogNode(string name, PlatformRelease? release = null)
    {
        Name = name ?? string.Empty;
        Release = release;
    }

    /// <summary>Заголовок узла.</summary>
    public string Name { get; }

    /// <summary>Релиз каталога у листьев; null у линий и групп сборок.</summary>
    public PlatformRelease? Release { get; }

    /// <summary>Вложенные узлы (линия → группы сборок → сборки).</summary>
    public List<PlatformCatalogNode> Children { get; } = new();

    /// <summary>True — узел является листом (полной версией каталога).</summary>
    public bool IsLeaf => Release is not null;
}

/// <summary>
/// Чистый строитель дерева версий каталога платформы (issue #330): из отсортированного
/// списка релизов строит иерархию «линия (8.3/8.5) → группа сборок (8.3.27) → полная
/// версия (8.3.27.2214)». Сортировка на каждом уровне — по убыванию числовыми сегментами
/// (8.3.9 идёт ниже 8.3.27). Без сети и UI — покрывается юнит-тестами.
/// </summary>
public static class PlatformVersionTreeBuilder
{
    /// <summary>
    /// Строит дерево каталога. Релизы с пустой версией пропускаются; пустой вход даёт
    /// пустой список корней без исключений.
    /// </summary>
    public static IReadOnlyList<PlatformCatalogNode> BuildFromCatalog(IEnumerable<PlatformRelease>? releases)
    {
        var roots = new List<PlatformCatalogNode>();
        if (releases is null)
            return roots;

        var sorted = releases
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Version))
            .ToList();
        sorted.Sort((x, y) => OneCPlatformCatalogParser.CompareVersions(y.Version, x.Version));

        foreach (var release in sorted)
        {
            var segments = release.Version.Split('.');
            var lineName = segments.Length >= 2
                ? $"{segments[0]}.{segments[1]}"
                : release.Version;

            var line = roots.FirstOrDefault(n => string.Equals(n.Name, lineName, StringComparison.Ordinal));
            if (line is null)
            {
                line = new PlatformCatalogNode(lineName);
                roots.Add(line);
            }

            // Группа «8.x.yy» имеет смысл только для ПОЛНЫХ версий из 4 сегментов
            // («8.3.27.2214» → линия 8.3 → группа 8.3.27 → лист). Трёхсегментные
            // версии («8.5.42») — сразу лист линии 8.5 (issue #330).
            if (segments.Length >= 4)
            {
                var groupName = $"{segments[0]}.{segments[1]}.{segments[2]}";
                var group = line.Children.FirstOrDefault(n =>
                    string.Equals(n.Name, groupName, StringComparison.Ordinal));
                if (group is null)
                {
                    group = new PlatformCatalogNode(groupName);
                    line.Children.Add(group);
                }

                group.Children.Add(new PlatformCatalogNode(release.Version, release));
            }
            else
            {
                line.Children.Add(new PlatformCatalogNode(release.Version, release));
            }
        }

        return roots;
    }
}