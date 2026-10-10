#if WINDOWS
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Configuration_Management.Localization;
using Configuration_Management.Services;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог выбора варианта дистрибутива платформы 1С (issue #334): после нажатия
    /// «Скачать и установить» показывает список файлов, доступных для текущей ОС
    /// (x86/x64, полный/тонкий клиент), с пометкой рекомендуемого. Пользователь видит,
    /// что именно будет скачано, и выбирает вариант.
    /// </summary>
    public partial class PlatformDistributionPickerWindow : Window
    {
        /// <param name="options">Варианты дистрибутива для текущей ОС (см.
        /// <see cref="PlatformDistributionPicker.BuildOptions"/>).</param>
        public PlatformDistributionPickerWindow(IReadOnlyList<PlatformDistributionOption> options)
        {
            InitializeComponent();

            // issue #330 п.2/#334 п.2: список сгруппирован по заголовкам групп страницы
            // релиза (строки-разделители PlatformFileGroupHeaderItem + варианты).
            var groups = ViewModels.PlatformFileGroupViewModel.Build(
                options, LocalizationManager.T("PlatformDownload.Group.Default"));
            OptionsList.ItemsSource = ViewModels.PlatformFileGroupViewModel.Flatten(groups);

            // Предвыбираем рекомендуемый вариант (если есть).
            var items = OptionsList.ItemsSource.Cast<object>().ToList();
            var recommended = items.OfType<PlatformDistributionOption>()
                .FirstOrDefault(o => o.IsRecommended);
            if (recommended is not null)
                OptionsList.SelectedItem = recommended;
        }

        /// <summary>Выбранный пользователем вариант или null при отмене.</summary>
        public PlatformDistributionOption? Result => OptionsList.SelectedItem as PlatformDistributionOption;

        private void OnChoose_Click(object sender, RoutedEventArgs e)
        {
            if (Result is not null)
                DialogResult = true;
        }

        private void OnOptionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Result is not null)
                DialogResult = true;
        }
    }
}
#endif