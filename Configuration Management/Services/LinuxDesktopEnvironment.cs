#if LINUX
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Configuration_Management.Services
{
    /// <summary>
    /// Определение окружения рабочего стола на Linux по переменным среды.
    /// Отдельной библиотеки для этого не нужно: и XDG, и сами среды публикуют
    /// себя именно так, любая обёртка читала бы те же переменные.
    /// </summary>
    public static class LinuxDesktopEnvironment
    {
        /// <summary>Семейство рабочего стола.</summary>
        public enum Desktop { Unknown, Gnome, Kde, Xfce, Cinnamon, Mate, Lxde, Lxqt, Deepin }

        /// <summary>Текущее окружение рабочего стола.</summary>
        public static Desktop Current { get; } = DetectDesktop();

        /// <summary>Тип сессии: «x11», «wayland» или пустая строка.</summary>
        public static string SessionType { get; } =
            (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? string.Empty).ToLowerInvariant();

        /// <summary>Сессия Wayland (важно для позиционирования окон и трея).</summary>
        public static bool IsWayland => SessionType == "wayland";

        /// <summary>
        /// GNOME Shell без расширения AppIndicator не показывает иконку в трее.
        /// Определяется по окружению: расширение может быть и установлено,
        /// поэтому это признак риска, а не гарантия отсутствия трея.
        /// </summary>
        public static bool TrayMayBeUnavailable => Current == Desktop.Gnome;

        private static Desktop DetectDesktop()
        {
            var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
            if (string.IsNullOrWhiteSpace(raw))
                raw = Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP");
            if (string.IsNullOrWhiteSpace(raw))
                raw = Environment.GetEnvironmentVariable("DESKTOP_SESSION");

            // XDG_CURRENT_DESKTOP может содержать список через двоеточие, например «ubuntu:GNOME».
            foreach (var part in (raw ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var d = Match(part.Trim());
                if (d != Desktop.Unknown)
                    return d;
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KDE_FULL_SESSION")))
                return Desktop.Kde;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GNOME_DESKTOP_SESSION_ID")))
                return Desktop.Gnome;

            return Desktop.Unknown;
        }

        private static Desktop Match(string name) => name.ToLowerInvariant() switch
        {
            "kde" or "plasma" or "plasma5" or "plasma6" => Desktop.Kde,
            "gnome" or "gnome-classic" or "gnome-flashback" or "unity" => Desktop.Gnome,
            "xfce" or "xubuntu" => Desktop.Xfce,
            "x-cinnamon" or "cinnamon" => Desktop.Cinnamon,
            "mate" => Desktop.Mate,
            "lxde" => Desktop.Lxde,
            "lxqt" => Desktop.Lxqt,
            "deepin" or "dde" => Desktop.Deepin,
            _ => Desktop.Unknown
        };

        /// <summary>
        /// Как показать файл в конкретном менеджере. Способы различаются:
        /// одни принимают ключ выделения, другие выделяют файл по одному лишь
        /// пути, а thunar от пути к файлу открывает сам файл приложением
        /// по умолчанию, поэтому ему передаётся каталог и выделения не будет.
        /// </summary>
        private static readonly Dictionary<string, (string Argument, bool PassFile)> SelectSyntax =
            new(StringComparer.Ordinal)
            {
                ["dolphin"] = ("--select", true),
                ["nautilus"] = ("--select", true),
                ["caja"] = ("--select", true),
                ["nemo"] = (string.Empty, true),
                ["dde-file-manager"] = ("--show-item", true),
                ["thunar"] = (string.Empty, false)
            };

        /// <summary>
        /// Файловые менеджеры в порядке предпочтения для текущего окружения:
        /// родной для среды идёт первым, остальные как запасные. Возвращаются
        /// только те, что действительно есть в PATH. Вместе с командой отдаётся
        /// способ показа: ключ выделения и признак того, что менеджеру нужен
        /// путь к файлу, а не к каталогу.
        /// </summary>
        public static IReadOnlyList<(string Command, string Argument, bool PassFile)> FileManagers()
        {
            var native = Current switch
            {
                Desktop.Kde => new[] { "dolphin" },
                Desktop.Gnome => new[] { "nautilus" },
                Desktop.Xfce => new[] { "thunar" },
                Desktop.Cinnamon => new[] { "nemo" },
                Desktop.Mate => new[] { "caja" },
                Desktop.Deepin => new[] { "dde-file-manager" },
                _ => Array.Empty<string>()
            };

            var order = native
                .Concat(new[] { "nautilus", "dolphin", "nemo", "thunar", "caja", "dde-file-manager" })
                .Distinct(StringComparer.Ordinal);

            var result = new List<(string, string, bool)>();
            foreach (var cmd in order)
            {
                if (!ExistsInPath(cmd) || !SelectSyntax.TryGetValue(cmd, out var syntax))
                    continue;
                result.Add((cmd, syntax.Argument, syntax.PassFile));
            }
            return result;
        }

        /// <summary>Проверяет наличие исполняемого файла в PATH.</summary>
        public static bool ExistsInPath(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
                return false;

            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir, command);
                    if (File.Exists(full))
                        return true;
                }
                catch
                {
                    // недоступный каталог в PATH — пропускаем
                }
            }
            return false;
        }

        /// <summary>Краткое описание окружения для журнала.</summary>
        public static string Describe() =>
            $"{Current}, сессия {(string.IsNullOrEmpty(SessionType) ? "неизвестна" : SessionType)}";
    }
}
#endif
