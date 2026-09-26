#if WINDOWS
using System.Windows;
using System.Windows.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management;

/// <summary>
/// Окно создания/редактирования сценария резервирования (Windows/WPF).
/// Возвращает результат через свойство <see cref="Result"/> при подтверждении.
/// </summary>
public partial class BackupScenarioEditWindow : Window
{
    private readonly BackupScenarioEditViewModel _vm;
    private readonly IDialogService _dialogs;

    /// <summary>Готовый сценарий при подтверждении, иначе <c>null</c>.</summary>
    public BackupScenario? Result { get; private set; }

    /// <param name="scenario">Редактируемый сценарий или <c>null</c> для нового.</param>
    public BackupScenarioEditWindow(BackupScenario? scenario = null)
    {
        InitializeComponent();
        _dialogs = AppServices.GetRequiredService<IDialogService>();
        _vm = new BackupScenarioEditViewModel(scenario);
        DataContext = _vm;

        Title = T(scenario is null ? "Backup.AddScenario" : "Backup.EditScenario");
        NameLabel.Text = T("Backup.Name");
        TemplateLabel.Text = T("Backup.FileNameTemplate");
        TemplateHint.Text = T("Backup.TemplateHint");
        TimestampCheck.Content = T("Backup.IncludeTimestamp");
        FormatLabel.Text = T("Backup.Format");
        PrefixLabel.Text = T("Backup.BasePrefix");
        DirsLabel.Text = T("Backup.TargetDirectories");
        AddDirButton.Content = T("Common.Browse");
        RemoveDirButton.Content = T("Common.Delete");
        CredLabel.Text = T("Backup.Credentials");
        UseAuthCheck.Content = T("Backup.UseInfobaseAuth");
        UserLabel.Text = T("Backup.User");
        PasswordLabel.Text = T("Backup.Password");
        OkButton.Content = T("Common.Save");

        FormatCombo.ItemsSource = System.Enum.GetValues<BackupFormat>();
        PasswordBox.Password = _vm.Password;

        // Фокус в поле «Наименование» (issue #299): отложенный вызов после показа окна —
        // иначе при ShowDialog() фокус «съедается» до активации окна.
        Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                NameBox.Focus();
                if (scenario is null)
                    NameBox.SelectAll();
            }));
        };
    }

    private static string T(string key) => LocalizationManager.T(key);

    private void AddDir_Click(object sender, RoutedEventArgs e)
    {
        var path = _dialogs.OpenFolderDialog(T("Backup.TargetDirectories"));
        if (!string.IsNullOrWhiteSpace(path))
            _vm.TargetDirectories.Add(path);
    }

    private void RemoveDir_Click(object sender, RoutedEventArgs e)
    {
        if (DirList.SelectedIndex >= 0)
            _vm.RemoveDirectoryAt(DirList.SelectedIndex);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordBox.Password;
        var errorKey = _vm.Validate();
        if (errorKey is not null)
        {
            _dialogs.ShowWarning(T(errorKey), Title);
            return;
        }

        var scenario = new BackupScenario();
        _vm.ApplyTo(scenario);
        Result = scenario;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
#endif