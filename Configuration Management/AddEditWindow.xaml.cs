using System.Windows;
using System.Windows.Controls;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог выбора типа добавляемого элемента (информационная база или группа),
    /// аналогичный стартовому окну «1С:Предприятие».
    /// </summary>
    public partial class AddEditWindow : Window
    {
        /// <summary>Выбранный тип элемента: "Infobase" или "Group".</summary>
        public string SelectedType { get; private set; } = "Infobase";

        public AddEditWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Обновляет выбранный тип при переключении радиокнопок.
        /// </summary>
        private void OnOption_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton)
            {
                SelectedType = radioButton.Tag as string ?? "Infobase";
            }
        }

        /// <summary>
        /// Закрывает диалог с положительным результатом.
        /// </summary>
        private void OnNext_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}