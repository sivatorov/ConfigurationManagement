#if WINDOWS
using System.Windows;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Модальный диалог «Доступна новая версия» (Windows/WPF).
/// Показывает текущую и доступную версии, краткое описание выпуска (Body)
/// и кнопки «Скачать» / «Отмена».
/// </summary>
public partial class UpdateAvailableWindow : Window
{
    /// <summary>true — пользователь нажал «Скачать».</summary>
    public bool DownloadRequested { get; private set; }

    public UpdateAvailableWindow(ReleaseInfo release)
    {
        InitializeComponent();

        Title = LocalizationManager.T("Update.NewVersionAvailable");
        HeadingText.Text = LocalizationManager.T("Update.NewVersionAvailable");
        CurrentVersionText.Text = string.Format(
            LocalizationManager.T("Update.CurrentVersion"), VersionInfo.Display());
        NewVersionText.Text = string.Format(
            LocalizationManager.T("Update.NewVersion"), NormalizeTag(release.TagName));
        WhatsNewLabel.Text = LocalizationManager.T("Update.WhatsNew");
        BodyText.Text = string.IsNullOrWhiteSpace(release.Body)
            ? LocalizationManager.T("Update.NoDescription")
            : release.Body;

        // На старте главного окна ещё нет, и первым MainWindow становится само это
        // окно: присваивание Owner самому себе бросает ArgumentException. Без
        // владельца окно просто открывается по центру экрана.
        var owner = Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Loaded += (_, _) => DownloadButton.Focus();
    }

    /// <summary>Обрезает ведущий символ «v» у тега версии для отображения.</summary>
    private static string NormalizeTag(string tag) =>
        !string.IsNullOrEmpty(tag) && (tag[0] == 'v' || tag[0] == 'V') ? tag.Substring(1) : tag;

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        DownloadRequested = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DownloadRequested = false;
        Close();
    }
}
#endif