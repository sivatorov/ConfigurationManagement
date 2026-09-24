#if WINDOWS
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;
using Microsoft.Win32;

namespace Configuration_Management;

/// <summary>
/// Форма задания по расписанию (Windows/WPF, issue #286): тип действия, время, дни недели,
/// сценарий резервирования, целевая база и файл конфигурации .cf.
/// </summary>
public partial class ScheduledTaskEditWindow : Window
{
    private readonly ScheduledTaskEditViewModel _vm;
    private readonly IDialogService _dialogs;

    /// <summary>Готовое задание при подтверждении, иначе <c>null</c>.</summary>
    public ScheduledTask? Result { get; private set; }

    /// <param name="task">Редактируемое задание или <c>null</c> для нового.</param>
    public ScheduledTaskEditWindow(ScheduledTask? task = null)
    {
        InitializeComponent();
        _dialogs = AppServices.GetRequiredService<IDialogService>();

        var scenarios = AppServices.GetRequiredService<IBackupScenarioStore>().LoadAll();
        var infobases = AppServices.GetRequiredService<IInfobaseRepository>().Load();
        _vm = new ScheduledTaskEditViewModel(task, scenarios, infobases);

        Title = T(task is null ? "Schedule.AddTitle" : "Schedule.EditTitle");
        OkButton.Content = T("Common.Save");
        BrowseButton.Content = T("Schedule.Browse");

        NameBox.Text = _vm.Name;
        EnabledCheck.IsChecked = _vm.Enabled;
        TimeBox.Text = _vm.Time;
        CfgPathBox.Text = _vm.ConfigFilePath;

        KindCombo.ItemsSource = _vm.KindOptions;
        KindCombo.DisplayMemberPath = "Text";
        KindCombo.SelectedValuePath = "Value";
        KindCombo.SelectedValue = _vm.Kind.ToString();

        ScenarioCombo.ItemsSource = _vm.ScenarioOptions;
        ScenarioCombo.DisplayMemberPath = "Text";
        ScenarioCombo.SelectedValuePath = "Value";
        ScenarioCombo.SelectedValue = string.IsNullOrEmpty(_vm.SelectedScenarioId) ? null : _vm.SelectedScenarioId;

        InfobaseCombo.ItemsSource = _vm.InfobaseOptions;
        InfobaseCombo.DisplayMemberPath = "Text";
        InfobaseCombo.SelectedValuePath = "Value";
        InfobaseCombo.SelectedValue = string.IsNullOrEmpty(_vm.SelectedInfobaseId) ? null : _vm.SelectedInfobaseId;

        MonCheck.IsChecked = _vm.IsMonday;
        TueCheck.IsChecked = _vm.IsTuesday;
        WedCheck.IsChecked = _vm.IsWednesday;
        ThuCheck.IsChecked = _vm.IsThursday;
        FriCheck.IsChecked = _vm.IsFriday;
        SatCheck.IsChecked = _vm.IsSaturday;
        SunCheck.IsChecked = _vm.IsSunday;

        MonCheck.Content = T("Schedule.Days.Mon");
        TueCheck.Content = T("Schedule.Days.Tue");
        WedCheck.Content = T("Schedule.Days.Wed");
        ThuCheck.Content = T("Schedule.Days.Thu");
        FriCheck.Content = T("Schedule.Days.Fri");
        SatCheck.Content = T("Schedule.Days.Sat");
        SunCheck.Content = T("Schedule.Days.Sun");

        ApplyKindVisibility();
    }

    private static string T(string key) => LocalizationManager.T(key);

    private ScheduledTaskKind SelectedKind
    {
        get
        {
            var value = KindCombo.SelectedValue as string ?? "Backup";
            return Enum.TryParse<ScheduledTaskKind>(value, out var kind) ? kind : ScheduledTaskKind.Backup;
        }
    }

    /// <summary>Показывает секции, нужные для выбранного типа действия.</summary>
    private void ApplyKindVisibility()
    {
        var kind = SelectedKind;
        var needsScenario = kind is ScheduledTaskKind.Backup or ScheduledTaskKind.BackupThenUpdateConfig;
        var needsInfobase = kind is ScheduledTaskKind.Backup
            or ScheduledTaskKind.UpdateConfig
            or ScheduledTaskKind.BackupThenUpdateConfig;
        var needsCfg = kind is ScheduledTaskKind.UpdateConfig or ScheduledTaskKind.BackupThenUpdateConfig;

        ScenarioPanel.Visibility = needsScenario ? Visibility.Visible : Visibility.Collapsed;
        InfobasePanel.Visibility = needsInfobase ? Visibility.Visible : Visibility.Collapsed;
        CfgPanel.Visibility = needsCfg ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KindCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyKindVisibility();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = T("Schedule.CfgFileRequired"),
            Filter = "Конфигурация 1С (*.cf)|*.cf|Все файлы (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            CfgPathBox.Text = dlg.FileName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _vm.Name = NameBox.Text ?? "";
        _vm.Enabled = EnabledCheck.IsChecked == true;
        _vm.Time = TimeBox.Text ?? "";
        _vm.Kind = SelectedKind;
        _vm.ConfigFilePath = CfgPathBox.Text ?? "";
        _vm.SelectedScenarioId = ScenarioCombo.SelectedValue as string ?? "";
        _vm.SelectedInfobaseId = InfobaseCombo.SelectedValue as string ?? "";
        _vm.IsMonday = MonCheck.IsChecked == true;
        _vm.IsTuesday = TueCheck.IsChecked == true;
        _vm.IsWednesday = WedCheck.IsChecked == true;
        _vm.IsThursday = ThuCheck.IsChecked == true;
        _vm.IsFriday = FriCheck.IsChecked == true;
        _vm.IsSaturday = SatCheck.IsChecked == true;
        _vm.IsSunday = SunCheck.IsChecked == true;

        var error = _vm.Validate();
        if (error is not null)
        {
            _dialogs.ShowWarning(T(error), Title);
            return;
        }

        var task = new ScheduledTask();
        _vm.ApplyTo(task);
        Result = task;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
#endif