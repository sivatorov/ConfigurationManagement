#if LINUX
using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management;

/// <summary>
/// Окно создания/редактирования сценария резервирования (Avalonia/Linux).
/// Возвращает результат через свойство <see cref="Result"/> при подтверждении.
/// </summary>
public sealed class BackupScenarioEditWindow : ModalWindowBase
{
    private readonly BackupScenarioEditViewModel _vm;
    private readonly IDialogService _dialogs = AppServices.GetRequiredService<IDialogService>();

    private readonly TextBox _nameBox = new TextBox().Styled(ControlThemes.ModernTextBox);
    private readonly TextBox _templateBox = new TextBox().Styled(ControlThemes.ModernTextBox);
    private readonly CheckBox _timestampCheck = new CheckBox().Styled(ControlThemes.CacheCleanCheckBox);
    private readonly ComboBox _formatCombo = new ComboBox().Styled(ControlThemes.ModernComboBox);
    private readonly TextBox _prefixBox = new TextBox().Styled(ControlThemes.ModernTextBox);
    private readonly ListBox _dirList = new();
    private readonly CheckBox _useAuthCheck = new CheckBox().Styled(ControlThemes.CacheCleanCheckBox);
    private readonly TextBox _userBox = new TextBox().Styled(ControlThemes.ModernTextBox);
    private readonly TextBox _passwordBox = new TextBox { PasswordChar = '\u25CF' }.Styled(ControlThemes.ModernTextBox);

    /// <summary>Готовый сценарий при подтверждении, иначе <c>null</c>.</summary>
    public BackupScenario? Result { get; private set; }

    /// <param name="scenario">Редактируемый сценарий или <c>null</c> для нового.</param>
    public BackupScenarioEditWindow(BackupScenario? scenario = null)
    {
        _vm = new BackupScenarioEditViewModel(scenario);
        Title = T(scenario is null ? "Backup.AddScenario" : "Backup.EditScenario");
        Width = 620;
        Height = 680;
        MinWidth = 540;
        MinHeight = 580;
        FontSize = 13;
        Content = BuildRoot();

        // Фокус в поле «Наименование» (issue #299): отложенно после показа окна —
        // синхронная установка в Opened слетает до активации модального диалога.
        Opened += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _nameBox.Focus();
                if (scenario is null)
                    _nameBox.SelectAll();
            }, Avalonia.Threading.DispatcherPriority.Background);
        };
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

    private static Control HintLabel(string text)
    {
        var hint = new TextBlock { Text = text, FontSize = 11 };
        ThemeBrushes.Bind(hint, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
        return hint;
    }

    private Control BuildRoot()
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(14), Spacing = 8 };

        panel.Children.Add(Label(T("Backup.Name")));
        _nameBox.Text = _vm.Name;
        panel.Children.Add(_nameBox);

        panel.Children.Add(Label(T("Backup.FileNameTemplate")));
        _templateBox.Text = _vm.FileNameTemplate;
        panel.Children.Add(_templateBox);
        panel.Children.Add(HintLabel(T("Backup.TemplateHint")));

        _timestampCheck.Content = T("Backup.IncludeTimestamp");
        _timestampCheck.IsChecked = _vm.IncludeTimestamp;
        panel.Children.Add(_timestampCheck);

        panel.Children.Add(Label(T("Backup.Format")));
        _formatCombo.ItemsSource = System.Enum.GetValues<BackupFormat>();
        _formatCombo.SelectedItem = _vm.Format;
        _formatCombo.HorizontalAlignment = HorizontalAlignment.Left;
        _formatCombo.MinWidth = 160;
        panel.Children.Add(_formatCombo);

        panel.Children.Add(Label(T("Backup.BasePrefix")));
        _prefixBox.Text = _vm.BasePrefix;
        panel.Children.Add(_prefixBox);

        panel.Children.Add(Label(T("Backup.TargetDirectories")));
        var dirButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addDir = new Button { Content = T("Common.Browse"), Width = 110 }.Styled(ControlThemes.SecondaryButton);
        addDir.Click += (_, _) => AddDirectory();
        var removeDir = new Button { Content = T("Common.Delete"), Width = 96 }.Styled(ControlThemes.SecondaryButton);
        removeDir.Click += (_, _) => RemoveDirectory();
        dirButtons.Children.Add(addDir);
        dirButtons.Children.Add(removeDir);
        panel.Children.Add(dirButtons);

        _dirList.ItemsSource = _vm.TargetDirectories;
        _dirList.Height = 110;
        panel.Children.Add(_dirList);

        panel.Children.Add(Label(T("Backup.Credentials")));
        _useAuthCheck.Content = T("Backup.UseInfobaseAuth");
        _useAuthCheck.IsChecked = _vm.UseInfobaseAuth;
        panel.Children.Add(_useAuthCheck);

        panel.Children.Add(Label(T("Backup.User")));
        _userBox.Text = _vm.User;
        panel.Children.Add(_userBox);

        panel.Children.Add(Label(T("Backup.Password")));
        _passwordBox.Text = _vm.Password;
        panel.Children.Add(_passwordBox);

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 10, 0, 0)
        };
        var ok = new Button { Content = T("Common.Save"), Width = 90 }.Styled(ControlThemes.DialogConfirmButton);
        ok.Click += (_, _) => OkClicked();
        var cancel = new Button { Content = T("Common.Cancel"), Width = 90 }.Styled(ControlThemes.DialogCancelButton);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        bottom.Children.Add(ok);
        bottom.Children.Add(cancel);
        panel.Children.Add(bottom);

        return new ScrollViewer { Content = panel };
    }

    private void AddDirectory()
    {
        var path = _dialogs.OpenFolderDialog(T("Backup.TargetDirectories"));
        if (!string.IsNullOrWhiteSpace(path))
            _vm.TargetDirectories.Add(path);
    }

    private void RemoveDirectory()
    {
        if (_dirList.SelectedIndex >= 0)
            _vm.RemoveDirectoryAt(_dirList.SelectedIndex);
    }

    private void OkClicked()
    {
        _vm.Name = _nameBox.Text ?? "";
        _vm.FileNameTemplate = _templateBox.Text ?? "";
        _vm.IncludeTimestamp = _timestampCheck.IsChecked == true;
        _vm.Format = _formatCombo.SelectedItem is BackupFormat f ? f : BackupFormat.Dt;
        _vm.BasePrefix = _prefixBox.Text ?? "";
        _vm.UseInfobaseAuth = _useAuthCheck.IsChecked == true;
        _vm.User = _userBox.Text ?? "";
        _vm.Password = _passwordBox.Text ?? "";

        var errorKey = _vm.Validate();
        if (errorKey is not null)
        {
            _dialogs.ShowWarning(T(errorKey), Title ?? "");
            return;
        }

        var scenario = new BackupScenario();
        _vm.ApplyTo(scenario);
        Result = scenario;
        DialogResult = true;
        Close();
    }
}
#endif