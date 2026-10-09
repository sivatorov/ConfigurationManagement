#if WINDOWS
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Configuration_Management.Models;

namespace Configuration_Management
{
    /// <summary>Строка списка диалога удаления: версия + состояние флажка.</summary>
    public sealed class PlatformOldVersionChoice
    {
        /// <param name="version">Информация об установленной версии.</param>
        public PlatformOldVersionChoice(PlatformVersionInfo version)
            => Version = version;

        /// <summary>Информация об установленной версии (Display + путь каталога).</summary>
        public PlatformVersionInfo Version { get; }

        /// <summary>Отображаемый текст (номер версии с разрядностью).</summary>
        public string Display => Version.Display;

        /// <summary>True — версия отмечена к удалению (по умолчанию все отмечены).</summary>
        public bool IsChecked { get; set; } = true;
    }

    /// <summary>
    /// Диалог выбора удаляемых старых версий платформы 1С (issue #334): список версий
    /// с флажками (по умолчанию отмечены все), пользователь выбирает, что удалить.
    /// Кнопка удаления активна, пока отмечена хотя бы одна версия; <see cref="Result"/>
    /// возвращает выбранные версии, отмена (Esc/кнопка «Отмена») — null.
    /// </summary>
    public partial class PlatformOldVersionsWindow : Window
    {
        private readonly ObservableCollection<PlatformOldVersionChoice> _choices = new();

        /// <param name="candidates">Кандидаты на удаление (см.
        /// <see cref="Services.OldVersionCleaner.SelectCandidates"/>).</param>
        public PlatformOldVersionsWindow(IEnumerable<PlatformVersionInfo> candidates)
        {
            InitializeComponent();

            foreach (var candidate in candidates ?? Enumerable.Empty<PlatformVersionInfo>())
                _choices.Add(new PlatformOldVersionChoice(candidate));

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
