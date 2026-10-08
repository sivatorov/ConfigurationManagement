#if WINDOWS
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
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
            OptionsList.ItemsSource = options;

            // Предвыбираем рекомендуемый вариант (если есть).
            if (options is not null)
            {
                for (var i = 0; i < options.Count; i++)
                {
                    if (options[i].IsRecommended)
                    {
                        OptionsList.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        /// <summary>Выбранный пользователем вариант или null при отмене.</summary>
        public PlatformDistributionOption? Result => OptionsList.SelectedItem as PlatformDistributionOption;

        private void OnChoose_Click(object sender, RoutedEventArgs e)
        {
            if (OptionsList.SelectedItem is not null)
                DialogResult = true;
        }

        private void OnOptionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (OptionsList.SelectedItem is not null)
                DialogResult = true;
        }
    }
}
#endif