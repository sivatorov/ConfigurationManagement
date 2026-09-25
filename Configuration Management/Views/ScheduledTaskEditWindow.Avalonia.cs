#if LINUX
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management;

/// <summary>
/// Форма задания по расписанию (Avalonia/Linux, issue #286): тип действия, время, дни недели,
/// сценарий резервирования, целевая база и файл конфигурации .cf.
/// Возвращает результат через свойство <see cref="Result"/> при подтверждении.
/// </summary>
public sealed class ScheduledTaskEditWindow : ModalWindowBase
{
    private readonly ScheduledTaskEditViewModel _vm;
    private readonly IDialogService _dialogs = AppServices.GetRequiredService<IDialogService>();

    private readonly TextBox _nameBox = new TextBox().Styled(ControlThemes.ModernTextBox);
    private readonly CheckBox _enabledCheck = new CheckBox().Styled(ControlThemes.CacheCleanCheckBox);
    private readonly ComboBox _kindCombo = new ComboBox().Styled(ControlThemes.ModernComboBox);
    private readonly TextBox _timeBox = new TextBox().Styled(ControlThemes.ModernTextBox);
    private readonly StackPanel _daysPanel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly CheckBox[] _dayChecks = new CheckBox[7];
    private readonly StackPanel _scenarioPanel = new() { Spacing = 6 };
    private readonly ComboBox _scenarioCombo = new ComboBox().Styled(ControlThemes.ModernComboBox);
    private readonly StackPanel _infobasePanel = new() { Spacing = 6 };
    private readonly ComboBox _infobaseCombo = new ComboBox().Styled(ControlThemes.ModernComboBox);
    private readonly StackPanel _cfgPanel = new() { Spacing = 6 };
    private readonly TextBox _cfgPathBox = new TextBox().Styled(ControlThemes.ModernTextBox);

    /// <summary>Готовое задание при подтверждении, иначе <c>null</c>.</summary>
    public ScheduledTask? Result { get; private set; }

    /// <param name="task">Редактируемое задание или <c>null</c> для нового.</param>
    public ScheduledTaskEditWindow(ScheduledTask? task = null)
    {
        var scenarios = AppServices.GetRequiredService<IBackupScenarioStore>().LoadAll();
        var infobases = AppServices.GetRequiredService<IInfobaseRepository>().Load();
        _vm = new ScheduledTaskEditViewModel(task, scenarios, infobases);

        Title = T(task is null ? "Schedule.AddTitle" : "Schedule.EditTitle");
        Width = 560;
        Height = 620;
        MinWidth = 500;
        MinHeight = 540;
        FontSize = 13;
        Content = BuildRoot();
        ApplyKindVisibility();
    }

    /// <summary>Показывает окно модально (синхронно).</summary>
    public bool ShowSync(Window? owner = null) => ShowDialogSync(owner);

    private static string T(string key) => LocalizationManager.T(key);

    private static Control Label(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        };
        // Цвет подписи из темы (issue #291), как в остальных окнах Avalonia.
        ThemeBrushes.Bind(label, TextBlock.ForegroundProperty, "TextPrimaryColorBrush");
        return label;
    }

    private Control BuildRoot()
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(14), Spacing = 8 };

        panel.Children.Add(Label(T("Schedule.Name")));
        _nameBox.Text = _vm.Name;
        panel.Children.Add(_nameBox);

        _enabledCheck.Content = T("Schedule.Enabled");
        _enabledCheck.IsChecked = _vm.Enabled;
        panel.Children.Add(_enabledCheck);

        panel.Children.Add(Label(T("Schedule.Kind")));
        _kindCombo.ItemsSource = _vm.KindOptions;
        _kindCombo.SelectedItem = _vm.KindOptions.FirstOrDefault(o => o.Value == _vm.Kind.ToString());
        _kindCombo.SelectionChanged += (_, _) => ApplyKindVisibility();
        _kindCombo.HorizontalAlignment = HorizontalAlignment.Left;
        _kindCombo.MinWidth = 280;
        panel.Children.Add(_kindCombo);

        panel.Children.Add(Label(T("Schedule.Time")));
        _timeBox.Text = _vm.Time;
        panel.Children.Add(_timeBox);

        panel.Children.Add(Label(T("Schedule.Days")));
        BuildDayChecks();
        panel.Children.Add(_daysPanel);

        // Сценарий резервирования.
        _scenarioCombo.ItemsSource = _vm.ScenarioOptions;
        _scenarioCombo.SelectedItem = _vm.ScenarioOptions.FirstOrDefault(o => o.Value == _vm.SelectedScenarioId);
        _scenarioCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scenarioPanel.Children.Add(Label(T("Schedule.Scenario")));
        _scenarioPanel.Children.Add(_scenarioCombo);
        panel.Children.Add(_scenarioPanel);

        // Информационная база.
        _infobaseCombo.ItemsSource = _vm.InfobaseOptions;
        _infobaseCombo.SelectedItem = _vm.InfobaseOptions.FirstOrDefault(o => o.Value == _vm.SelectedInfobaseId);
        _infobaseCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _infobasePanel.Children.Add(Label(T("Schedule.Base")));
        _infobasePanel.Children.Add(_infobaseCombo);
        panel.Children.Add(_infobasePanel);

        // Файл конфигурации .cf.
        _cfgPathBox.Text = _vm.ConfigFilePath;
        var browse = new Button { Content = T("Schedule.Browse"), Width = 100 }.Styled(ControlThemes.SecondaryButton);
        browse.Click += (_, _) => BrowseCfgFile();
        var cfgRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Right);
        cfgRow.Children.Add(browse);
        cfgRow.Children.Add(_cfgPathBox);
        _cfgPanel.Children.Add(Label(T("Schedule.CfgFile")));
        _cfgPanel.Children.Add(cfgRow);
        panel.Children.Add(_cfgPanel);

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 10, 0, 0)
        };
        var ok = new Button { Content = T("Common.Save"), Width = 110 }.Styled(ControlThemes.DialogConfirmButton);
        ok.Click += (_, _) => OkClicked();
        var cancel = new Button { Content = T("Common.Cancel"), Width = 100 }.Styled(ControlThemes.DialogCancelButton);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        bottom.Children.Add(ok);
        bottom.Children.Add(cancel);
        panel.Children.Add(bottom);

        return new ScrollViewer { Content = panel };
    }

    private void BuildDayChecks()
    {
        var days = new[]
        {
            ("Monday", "Schedule.Days.Mon", DayOfWeek.Monday),
            ("Tuesday", "Schedule.Days.Tue", DayOfWeek.Tuesday),
            ("Wednesday", "Schedule.Days.Wed", DayOfWeek.Wednesday),
            ("Thursday", "Schedule.Days.Thu", DayOfWeek.Thursday),
            ("Friday", "Schedule.Days.Fri", DayOfWeek.Friday),
            ("Saturday", "Schedule.Days.Sat", DayOfWeek.Saturday),
            ("Sunday", "Schedule.Days.Sun", DayOfWeek.Sunday)
        };
        for (var i = 0; i < days.Length; i++)
        {
            // Флажки дней недели темизируются (issue #291), как и остальные элементы окна.
            var check = new CheckBox { Content = T(days[i].Item2) }.Styled(ControlThemes.CacheCleanCheckBox);
            check.IsChecked = days[i].Item3 switch
            {
                DayOfWeek.Monday => _vm.IsMonday,
                DayOfWeek.Tuesday => _vm.IsTuesday,
                DayOfWeek.Wednesday => _vm.IsWednesday,
                DayOfWeek.Thursday => _vm.IsThursday,
                DayOfWeek.Friday => _vm.IsFriday,
                DayOfWeek.Saturday => _vm.IsSaturday,
                _ => _vm.IsSunday
            };
            _dayChecks[i] = check;
            _daysPanel.Children.Add(check);
        }
    }

    private ScheduledTaskKind SelectedKind
    {
        get
        {
            var value = (_kindCombo.SelectedItem as ScheduledTaskOption)?.Value ?? "Backup";
            return Enum.TryParse<ScheduledTaskKind>(value, out var kind) ? kind : ScheduledTaskKind.Backup;
        }
    }

    private void ApplyKindVisibility()
    {
        var kind = SelectedKind;
        _scenarioPanel.IsVisible = kind is ScheduledTaskKind.Backup or ScheduledTaskKind.BackupThenUpdateConfig;
        _infobasePanel.IsVisible = kind is ScheduledTaskKind.Backup
            or ScheduledTaskKind.UpdateConfig
            or ScheduledTaskKind.BackupThenUpdateConfig;
        _cfgPanel.IsVisible = kind is ScheduledTaskKind.UpdateConfig or ScheduledTaskKind.BackupThenUpdateConfig;
    }

    private void BrowseCfgFile()
    {
        var path = _dialogs.OpenFileDialog(T("Schedule.CfgFile"), "*.cf");
        if (!string.IsNullOrWhiteSpace(path))
            _cfgPathBox.Text = path;
    }

    private void OkClicked()
    {
        _vm.Name = _nameBox.Text ?? "";
        _vm.Enabled = _enabledCheck.IsChecked == true;
        _vm.Kind = SelectedKind;
        _vm.Time = _timeBox.Text ?? "";
        _vm.ConfigFilePath = _cfgPathBox.Text ?? "";
        _vm.SelectedScenarioId = (_scenarioCombo.SelectedItem as ScheduledTaskOption)?.Value ?? "";
        _vm.SelectedInfobaseId = (_infobaseCombo.SelectedItem as ScheduledTaskOption)?.Value ?? "";
        _vm.IsMonday = _dayChecks[0].IsChecked == true;
        _vm.IsTuesday = _dayChecks[1].IsChecked == true;
        _vm.IsWednesday = _dayChecks[2].IsChecked == true;
        _vm.IsThursday = _dayChecks[3].IsChecked == true;
        _vm.IsFriday = _dayChecks[4].IsChecked == true;
        _vm.IsSaturday = _dayChecks[5].IsChecked == true;
        _vm.IsSunday = _dayChecks[6].IsChecked == true;

        var errorKey = _vm.Validate();
        if (errorKey is not null)
        {
            _dialogs.ShowWarning(T(errorKey), Title ?? "");
            return;
        }

        var task = new ScheduledTask();
        _vm.ApplyTo(task);
        Result = task;
        DialogResult = true;
        Close();
    }
}
#endif