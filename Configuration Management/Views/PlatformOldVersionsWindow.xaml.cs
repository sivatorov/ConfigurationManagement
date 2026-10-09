#if WINDOWS
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management
{
    /// <summary>Строка списка диалога удаления (issue #334): версия, признаки риска
    /// («новейшая», «используется базами», «используется запущенными процессами 1С»)
    /// и состояние флажка. По умолчанию отмечены только версии без признаков риска —
    /// решение, что считать старым, остаётся за пользователем.</summary>
    public sealed class PlatformOldVersionChoice
    {
        /// <param name="entry">Версия с признаками риска
        /// (<see cref="Services.OldVersionCleanupEntry"/>).</param>
        public PlatformOldVersionChoice(Services.OldVersionCleanupEntry entry)
        {
            Version = entry.Version;
            IsNewest = entry.IsNewest;
            IsUsedByBases = entry.IsUsedByBases;
            IsUsedByProcesses = entry.IsUsedByProcesses;
            IsChecked = entry.IsCheckedByDefault;

            var badges = new List<string>();
            if (IsNewest)
                badges.Add(LocalizationManager.T("PlatformUpdate.OldVersions.Badge.Newest"));
            if (IsUsedByBases)
                badges.Add(LocalizationManager.T("PlatformUpdate.OldVersions.Badge.UsedByBases"));
            if (IsUsedByProcesses)
                badges.Add(LocalizationManager.T("PlatformUpdate.OldVersions.Badge.UsedByProcesses"));
            Badges = badges.Count == 0 ? string.Empty : " — " + string.Join(", ", badges);
        }

        /// <summary>Информация об установленной версии (Display + путь каталога).</summary>
        public PlatformVersionInfo Version { get; }

        /// <summary>True — новейшая установленная версия.</summary>
        public bool IsNewest { get; }

        /// <summary>True — на версию ссылается хотя бы одна база.</summary>
        public bool IsUsedByBases { get; }

        /// <summary>True — из каталога версии запущен процесс 1С.</summary>
        public bool IsUsedByProcesses { get; }

        /// <summary>Отображаемый текст (номер версии с разрядностью).</summary>
        public string Display => Version.Display;

        /// <summary>Текстовые пометки риска (пусто — признаков нет).</summary>
        public string Badges { get; }

        /// <summary>True — версия отмечена к удалению.</summary>
        public bool IsChecked { get; set; }
    }

    /// <summary>
    /// Диалог выбора удаляемых старых версий платформы 1С (issue #334, доработка по
    /// комментарию автора): в списке ПОКАЗЫВАЮТСЯ ВСЕ установленные версии — включая
    /// новейшую и используемые базами/процессами (с текстовыми пометками); ничего
    /// не фильтруется, пользователь сам решает, что считать старым. По умолчанию
    /// отмечены только версии без признаков риска; попытка удалить спорную версию
    /// дополнительно подтверждается в <see cref="ViewModels.PlatformUpdateViewModel"/>.
    /// Кнопка удаления активна, пока отмечена хотя бы одна версия; <see cref="Result"/>
    /// возвращает выбранные версии, отмена (Esc/кнопка «Отмена») — null.
    /// </summary>
    public partial class PlatformOldVersionsWindow : Window
    {
        private readonly ObservableCollection<PlatformOldVersionChoice> _choices = new();

        /// <param name="entries">ВСЕ установленные версии с признаками риска (см.
        /// <see cref="Services.OldVersionCleaner.SelectDeletionEntries"/>).</param>
        public PlatformOldVersionsWindow(IEnumerable<Services.OldVersionCleanupEntry> entries)
        {
            InitializeComponent();

            foreach (var entry in entries ?? Enumerable.Empty<Services.OldVersionCleanupEntry>())
                _choices.Add(new PlatformOldVersionChoice(entry));

            VersionsList.ItemsSource = _choices;
            DeleteButton.IsEnabled = _choices.Any(c => c.IsChecked);
        }

        /// <summary>Версии, отмеченные пользователем к удалению (не null при DialogResult=true).</summary>
        public IReadOnlyList<PlatformVersionInfo>? Result => _choices
            .Where(c => c.IsChecked)
            .Select(c => c.Version)
            .ToList();

        private void OnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_choices.Any(c => c.IsChecked))
                DialogResult = true;
        }

        private void OnItemCheckedChanged(object sender, RoutedEventArgs e)
        {
            DeleteButton.IsEnabled = _choices.Any(c => c.IsChecked);
        }
    }
}
#endif
