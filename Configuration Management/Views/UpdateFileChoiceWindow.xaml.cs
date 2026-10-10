#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Configuration_Management.Localization;
using Configuration_Management.Services;

namespace Configuration_Management;

/// <summary>
/// Мини-диалог выбора файла релиза (issue #352.1): «Дистрибутив обновления» /
/// «Полный дистрибутив» со страницы файлов версии. Выбранный вариант —
/// <see cref="SelectedChoice"/>; Esc/закрытие — null.
/// </summary>
public partial class UpdateFileChoiceWindow : Window
{
    /// <summary>Элемент списка выбора (подпись + имя файла).</summary>
    public sealed record ChoiceItem(string Caption, string FileName, UpdateFileChoice Choice);

    /// <summary>Выбранный пользователем вариант или null при отмене.</summary>
    public UpdateFileChoice? SelectedChoice { get; private set; }

    public UpdateFileChoiceWindow(IReadOnlyList<UpdateFileChoice> choices, string heading)
    {
        InitializeComponent();

        Title = LocalizationManager.T("Updates.FileChoice.Title");
        HeadingText.Text = heading;
        ChoicesList.ItemsSource = choices.Select(c => new ChoiceItem(
            LocalizationManager.T(c.CaptionKey), c.FileName, c)).ToList();

        ChoicesList.SelectionChanged += (_, _) =>
            OkButton.IsEnabled = ChoicesList.SelectedItem is ChoiceItem;

        var owner = Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        Loaded += (_, _) =>
        {
            if (ChoicesList.Items.Count == 1)
                ChoicesList.SelectedIndex = 0;
            ChoicesList.Focus();
        };
    }

    /// <summary>Показывает диалог модально и возвращает выбранный вариант (null при отмене).</summary>
    public static UpdateFileChoice? Pick(IReadOnlyList<UpdateFileChoice> choices, string heading)
    {
        var window = new UpdateFileChoiceWindow(choices, heading);
        window.ShowDialog();
        return window.SelectedChoice;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (ChoicesList.SelectedItem is ChoiceItem item)
        {
            SelectedChoice = item.Choice;
            DialogResult = true;
        }
    }

    private void OnChoicesListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChoicesList.SelectedItem is ChoiceItem item)
        {
            SelectedChoice = item.Choice;
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
#endif
