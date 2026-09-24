#if LINUX
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Configuration_Management.Localization;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management;

/// <summary>
/// Окно «Задания по расписанию» (Avalonia/Linux, issue #286): список заданий с действиями
/// «Выполнить сейчас», «Добавить», «Изменить», «Удалить».
/// </summary>
public sealed class ScheduledTasksWindow : ModalWindowBase
{
    private readonly IScheduledTaskStore _store = AppServices.GetRequiredService<IScheduledTaskStore>();
    private readonly IDialogService _dialogs = AppServices.GetRequiredService<IDialogService>();
    private readonly SchedulerService _scheduler = AppServices.GetRequiredService<SchedulerService>();
    private readonly ListBox _list = new();

    public ScheduledTasksWindow()
    {
        Title = T("Schedule.Title");
        Width = 760;
        Height = 560;
        MinWidth = 620;
        MinHeight = 440;
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
        var run = new Button { Content = T("Schedule.RunNow"), Width = 130 };
        run.Click += async (_, _) => await RunSelectedAsync();
        var add = new Button { Content = T("Common.Add"), Width = 96 };
        add.Click += (_, _) => AddTask();
        var edit = new Button { Content = T("Common.Edit"), Width = 96 };
        edit.Click += (_, _) => EditSelected();
        var del = new Button { Content = T("Common.Delete"), Width = 92 };
        del.Click += (_, _) => DeleteSelected();
        var close = new Button { Content = T("Common.Close"), Width = 90 };
        close.Click += (_, _) => Close();

        foreach (var b in new Control[] { run, add, edit, del, close })
            b.MinWidth = b.Width;

        bottom.Children.Add(run);
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
            .Select(t => new ScheduledTaskItemViewModel(t))
            .ToList();
    }

    private ScheduledTaskItemViewModel? Selected => _list.SelectedItem as ScheduledTaskItemViewModel;

    private void AddTask()
    {
        var edit = new ScheduledTaskEditWindow();
        if (edit.ShowDialogSync(this) && edit.Result is { } task)
        {
            _store.Save(task);
            Reload();
        }
    }

    private void EditSelected()
    {
        if (Selected is not { } item)
            return;
        var edit = new ScheduledTaskEditWindow(item.Task);
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
        if (_dialogs.Confirm(string.Format(T("Schedule.DeleteConfirm"), item.Name), T("Schedule.DeleteTitle")))
        {
            _store.Delete(item.Id);
            Reload();
        }
    }

    private async System.Threading.Tasks.Task RunSelectedAsync()
    {
        if (Selected is not { } item)
            return;
        var result = await _scheduler.RunNowAsync(item.Task);
        if (result is not null)
        {
            if (result.Success)
                _dialogs.ShowInfo(string.Join("\n", result.CreatedFiles), T("Schedule.RunNow"));
            else
                _dialogs.ShowError(result.ErrorMessage ?? T("Schedule.RunFailed"), T("Schedule.RunNow"));
        }
        Reload();
    }
}
#endif