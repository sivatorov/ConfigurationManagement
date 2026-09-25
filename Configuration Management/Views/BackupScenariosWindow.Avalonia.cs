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
/// Окно «Сценарии резервирования» (Avalonia/Linux): список сценариев с действиями
/// «Выполнить» (для выбранной ИБ), «Добавить», «Изменить», «Удалить».
/// </summary>
public sealed class BackupScenariosWindow : ModalWindowBase
{
    private readonly IBackupScenarioStore _store = AppServices.GetRequiredService<IBackupScenarioStore>();
    private readonly IDialogService _dialogs = AppServices.GetRequiredService<IDialogService>();
    private readonly Infobase? _infobase;
    private readonly MainViewModel? _vm;
    private readonly ListBox _list = new();
    private readonly Button _runButton = new();

    /// <param name="infobase">ИБ, для которой выполняется сценарий (может быть null).</param>
    /// <param name="vm">MainViewModel для выполнения сценария (может быть null).</param>
    public BackupScenariosWindow(Infobase? infobase = null, MainViewModel? vm = null)
    {
        _infobase = infobase;
        _vm = vm;
        Title = T("Backup.ScenariosTitle");
        Width = 720;
        Height = 540;
        MinWidth = 560;
        MinHeight = 430;
        FontSize = 13;
        Content = BuildRoot();
        Reload();
    }

    /// <summary>Показывает окно модально (синхронно).</summary>
    public bool ShowSync(Window? owner = null) => ShowDialogSync(owner);

    private static string T(string key) => LocalizationManager.T(key);

    private Control BuildRoot()
    {
        var dock = new DockPanel { Margin = new Avalonia.Thickness(14) };

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 12, 0, 0)
        };
        _runButton.Content = T("Backup.RunTitle");
        _runButton.IsEnabled = _infobase is not null;
        _runButton.Click += async (_, _) => await RunSelectedAsync();

        var add = new Button { Content = T("Common.Add") };
        add.Click += (_, _) => AddScenario();
        var edit = new Button { Content = T("Common.Edit") };
        edit.Click += (_, _) => EditSelected();
        var del = new Button { Content = T("Common.Delete") };
        del.Click += (_, _) => DeleteSelected();
        var close = new Button { Content = T("Common.Close") };
        close.Click += (_, _) => Close();

        // Темизация кнопок окна (issue #291): стили берутся из Controls.axaml/тем
        // Light-Dark, как у остальных окон приложения, а не штатный вид Fluent.
        foreach (var b in new Control[] { _runButton, add, edit, del, close })
        {
            b.Styled(ControlThemes.ModernButton);
            b.Width = 100;
        }

        bottom.Children.Add(_runButton);
        bottom.Children.Add(add);
        bottom.Children.Add(edit);
        bottom.Children.Add(del);
        bottom.Children.Add(close);

        _list.Margin = new Avalonia.Thickness(0, 0, 0, 8);
        DockPanel.SetDock(bottom, Dock.Bottom);

        dock.Children.Add(bottom);
        dock.Children.Add(_list);
        return dock;
    }

    private void Reload()
    {
        _list.ItemsSource = _store.LoadAll()
            .Select(s => new BackupScenarioItemViewModel(s))
            .ToList();
    }

    private BackupScenarioItemViewModel? Selected => _list.SelectedItem as BackupScenarioItemViewModel;

    private void AddScenario()
    {
        var edit = new BackupScenarioEditWindow();
        if (edit.ShowDialogSync(this) && edit.Result is { } scenario)
        {
            _store.Save(scenario);
            Reload();
        }
    }

    private void EditSelected()
    {
        if (Selected is not { } item)
            return;
        var edit = new BackupScenarioEditWindow(item.Scenario);
        if (edit.ShowDialogSync(this) && edit.Result is { } updated)
        {
            _store.Save(updated);
            Reload();
        }
    }

    private void DeleteSelected()
    {
        if (Selected is not { } item)
            return;
        if (_dialogs.Confirm(string.Format(T("Backup.DeleteConfirm"), item.Name), T("Backup.DeleteScenario")))
        {
            _store.Delete(item.Id);
            Reload();
        }
    }

    private async System.Threading.Tasks.Task RunSelectedAsync()
    {
        if (Selected is not { } item || _infobase is null || _vm is null)
            return;
        await _vm.RunBackupAsync(_infobase, item.Scenario);
    }
}
#endif