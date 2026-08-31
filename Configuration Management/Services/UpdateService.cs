#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Подсистема автоматического обновления (Windows/WPF). Запускает фоновую проверку
/// новых версий через GitHub Releases, показывает диалог «Доступна новая версия»
/// и обрабатывает выбор пользователя. При подтверждении скачивает self-contained
/// single-file <c>ConfigurationManagement.exe</c> и заменяет текущий исполняемый файл
/// через временный PowerShell-помощник, после чего перезапускает приложение.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Репозиторий GitHub, откуда берётся последний выпуск.</summary>
    private const string RepoBaseUrl = "https://github.com/sivatorov/ConfigurationManagement";

    /// <summary>Каталог (относительно %TEMP%) для загрузки и временных скриптов обновления.</summary>
    private const string UpdateTempDir = @"ConfigurationManagement\update";

    private readonly GitHubReleaseService _gitHub;
    private readonly IDialogService _dialogs;
    private readonly HttpClient _http;

    /// <summary>
    /// Флаг «автоматически обновлять приложение без подтверждения». Когда включён,
    /// фоновая проверка при обнаружении новой версии сама скачивает, устанавливает
    /// и перезапускает приложение, не показывая диалог «Скачать/Отмена». Значение
    /// устанавливается из настроек при старте приложения в <c>App.OnStartup</c>.
    /// Ручная проверка («Проверить обновления») всегда показывает диалог/результат,
    /// независимо от этого флага.
    /// </summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    public UpdateService(GitHubReleaseService gitHub, IDialogService dialogs)
    {
        _gitHub = gitHub;
        _dialogs = dialogs;

        _http = new HttpClient();
        // GitHub требует корректный User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ConfigurationManagement/1.0");
        // Self-contained single-file exe может весить десятки МБ — таймаут больше, чем у API.
        _http.Timeout = TimeSpan.FromMinutes(20);
    }

    /// <summary>
    /// Проверяет наличие новой версии приложения. Если версия новее и включён
    /// автоматический режим (<see cref="AutoUpdateEnabled"/>) — сразу запускает
    /// скачивание, установку и перезапуск без диалога. Иначе показывает диалог
    /// «Доступна новая версия». Вызывается из фона; переход в UI-поток выполняется
    /// внутри через Dispatcher, поэтому сам метод не блокирует интерфейс. Ошибки
    /// сети/парсинга и отображения диалога не всплывают наружу.
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var release = await _gitHub.GetLatestReleaseAsync().ConfigureAwait(false);
            if (release is null)
                return;

            // Сравниваем с текущей версией приложения.
            if (!GitHubReleaseService.IsNewerThan(release, VersionInfo.Display()))
                return;

            var app = Application.Current;
            if (app is null)
                return;

            if (AutoUpdateEnabled)
            {
                // Автоматический режим: не спрашиваем пользователя, а сразу скачиваем,
                // устанавливаем и перезапускаем приложение. Выполняем в UI-потоке через
                // Dispatcher; все ошибки обрабатываются внутри DownloadAndInstallAsync.
                await app.Dispatcher.InvokeAsync(() => _ = DownloadAndInstallAsync(release));
                return;
            }

            await app.Dispatcher.InvokeAsync(() => ShowUpdateDialog(release));
        }
        catch
        {
            // Фоновая проверка не должна ронять приложение.
        }
    }

    /// <summary>
    /// Ручная проверка обновлений (кнопка «Проверить обновления» во вкладке «О программе»).
    /// В отличие от фоновой, явно сообщает пользователю результат: ошибку проверки,
    /// «версия актуальна» или показывает диалог о доступной новой версии.
    /// </summary>
    public async Task CheckForUpdatesManualAsync()
    {
        try
        {
            var release = await _gitHub.GetLatestReleaseAsync().ConfigureAwait(false);
            if (release is null)
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.CheckFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            if (!GitHubReleaseService.IsNewerThan(release, VersionInfo.Display()))
            {
                ShowOnUi(() => _dialogs.ShowInfo(
                    LocalizationManager.T("Update.UpToDate"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            var app = Application.Current;
            if (app is null)
                return;

            await app.Dispatcher.InvokeAsync(() => ShowUpdateDialog(release));
        }
        catch
        {
            ShowOnUi(() => _dialogs.ShowError(
                LocalizationManager.T("Update.CheckFailed"),
                LocalizationManager.T("Update.NewVersionAvailable")));
        }
    }

    /// <summary>
    /// Показывает модальный диалог «Доступна новая версия» и при подтверждении
    /// пользователем запускает скачивание и установку.
    /// </summary>
    private void ShowUpdateDialog(ReleaseInfo release)
    {
        try
        {
            var window = new UpdateAvailableWindow(release);
            window.ShowDialog();
            if (window.DownloadRequested)
                // Загрузка выполняется в фоне; все ошибки обрабатываются внутри.
                _ = DownloadAndInstallAsync(release);
        }
        catch
        {
            // Диалог не должен ронять приложение.
        }
    }

    /// <summary>
    /// Скачивает Windows-версию (single-file exe) из GitHub releases во временный файл,
    /// запускает временный PowerShell-помощник, который после завершения основного процесса
    /// заменяет текущий исполняемый файл новым и перезапускает приложение, а затем закрывает
    /// текущее приложение. Программа всегда сама скачивает exe по прямой ссылке; окно/страница
    /// GitHub не открывается. Если прямой ссылки нет — показывается только локализованная ошибка.
    /// При любой другой ошибке также показывает локализованное сообщение.
    /// </summary>
    public async Task DownloadAndInstallAsync(ReleaseInfo release)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            {
                // Прямой ссылки на exe нет. Браузер/GitHub не открываем — просто
                // сообщаем пользователю, что загрузить обновление невозможно.
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.NoDownloadUrl"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            ShowOnUi(() => _dialogs.ShowInfo(
                LocalizationManager.T("Update.Downloading"),
                LocalizationManager.T("Update.NewVersionAvailable")));

            var targetExe = Environment.ProcessPath
                            ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(targetExe))
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.InstallFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            var newExe = await DownloadAsync(release.DownloadUrl!);
            if (newExe is null)
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.DownloadFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            var updaterScript = CreateUpdaterScript(targetExe, newExe, Environment.ProcessId);
            if (!LaunchUpdater(updaterScript))
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.InstallFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            ShowOnUi(() => _dialogs.ShowInfo(
                LocalizationManager.T("Update.RestartPrompt"),
                LocalizationManager.T("Update.NewVersionAvailable")));

            var app = Application.Current;
            app?.Dispatcher.Invoke(app.Shutdown);
        }
        catch (Exception ex)
        {
            ShowOnUi(() => _dialogs.ShowError(
                LocalizationManager.T("Update.InstallFailed") + "\n" + ex.Message,
                LocalizationManager.T("Update.NewVersionAvailable")));
        }
    }

    /// <summary>
    /// Скачивает exe по прямой ссылке во временный каталог. Возвращает путь к файлу
    /// или null при сетевой ошибке / пустом файле. Временный файл удаляется при неудаче.
    /// </summary>
    private async Task<string?> DownloadAsync(string url)
    {
        var dest = Path.Combine(Path.GetTempPath(), UpdateTempDir, "ConfigurationManagement.new.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync();
            await using (var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target);
            }

            return new FileInfo(dest).Length > 0 ? dest : null;
        }
        catch
        {
            TryDelete(dest);
            return null;
        }
    }

    /// <summary>
    /// Создаёт временный PowerShell-скрипт, который дожидается завершения основного процесса
    /// (по PID), заменяет текущий исполняемый файл скачанным, перезапускает приложение
    /// и удаляет сам скрипт. Возвращает путь к созданному скрипту.
    /// </summary>
    private static string CreateUpdaterScript(string targetExe, string newExe, int currentPid)
    {
        var scriptPath = Path.Combine(
            Path.GetTempPath(), UpdateTempDir, $"apply-update-{Guid.NewGuid():N}.ps1");

        var script = $@"
$ErrorActionPreference = 'Stop'
$target = '{Pq(targetExe)}'
$new = '{Pq(newExe)}'
$pidTarget = {currentPid}

# Ждём завершения основного процесса, чтобы exe не был заблокирован.
$maxWait = 120
$elapsed = 0
while ($elapsed -lt $maxWait) {{
    if (-not (Get-Process -Id $pidTarget -ErrorAction SilentlyContinue)) {{ break }}
    Start-Sleep -Milliseconds 500
    $elapsed++
}}
Start-Sleep -Seconds 1

# Заменяем текущий exe новым и запускаем обновлённое приложение.
Move-Item -Path $new -Destination $target -Force
Start-Process -FilePath $target

# Убираем временный скрипт.
Remove-Item -LiteralPath '{Pq(scriptPath)}' -Force -ErrorAction SilentlyContinue
";

        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        return scriptPath;
    }

    /// <summary>
    /// Запускает временный PowerShell-помощник скрытно, без ожидания его завершения.
    /// Возвращает true, если процесс удалось запустить.
    /// </summary>
    private static bool LaunchUpdater(string scriptPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Экранирует строку для одинарных кавычек PowerShell.</summary>
    private static string Pq(string value) => value.Replace("'", "''");

    /// <summary>Выполняет действие в UI-потоке, если вызывающий поток — не UI.</summary>
    private static void ShowOnUi(Action action)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        app.Dispatcher.Invoke(action);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Не критично — временный файл останется в %TEMP%.
        }
    }
}
#endif