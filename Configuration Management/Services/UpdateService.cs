#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// Флаг «автоматически обновлять приложение без подтверждения». Если включён —
    /// при обнаружении новой версии файл скачивается и обновление применяется
    /// молча (без диалога) через <see cref="DownloadAndInstallAutoAsync"/>. Если
    /// выключен — показывается единый диалог <see cref="UpdateAvailableWindow"/>
    /// с вопросом «Перезапустить сейчас / Обновить после закрытия». Значение
    /// устанавливается из настроек при старте приложения в <c>App.OnStartup</c>.
    /// </summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>
    /// Событие прогресса скачивания exe. Значение — проценты 0–100. Если длина файла
    /// заранее неизвестна (сервер не отдал Content-Length), передаётся −1, что означает
    /// «неопределённый прогресс» (индикатор переводится в режим IsIndeterminate).
    /// Вызывается из фонового потока, поэтому подписчик должен сам перейти в UI-поток.
    /// </summary>
    public event Action<double>? DownloadProgressChanged;

    /// <summary>
    /// Событие окончания попытки скачивания (успех или неудача). Нужно, чтобы скрыть
    /// индикатор прогресса в строке состояния главного окна. Также вызывается из фона.
    /// </summary>
    public event Action? DownloadFinished;

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
    /// Проверяет наличие новой версии приложения. Если версия новее — при включённом
    /// <see cref="AutoUpdateEnabled"/> применяет обновление молча (без диалога) через
    /// <see cref="DownloadAndInstallAutoAsync"/>, иначе показывает единый диалог
    /// обновления. Вызывается из фона; переход в UI-поток выполняется внутри через
    /// Dispatcher, поэтому сам метод не блокирует интерфейс. Ошибки сети/парсинга и
    /// отображения диалога не всплывают наружу.
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

            if (AutoUpdateEnabled)
            {
                // Автообновление включено — применяем новую версию без вопросов.
                await DownloadAndInstallAutoAsync(release).ConfigureAwait(false);
                return;
            }

            var app = Application.Current;
            if (app is null)
                return;

            // Автообновление выключено — показываем единый диалог, который сам
            // скачивает файл и задаёт пользователю вопрос «Перезапустить сейчас /
            // Обновить после закрытия».
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

            if (AutoUpdateEnabled)
            {
                // Автообновление включено — применяем новую версию без вопросов.
                await DownloadAndInstallAutoAsync(release).ConfigureAwait(false);
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
    /// Показывает единый модальный диалог обновления. Окно само скачивает файл,
    /// отображает прогресс и по завершении спрашивает, как применить обновление
    /// («Перезапустить сейчас» или «Обновить после закрытия») — без системного
    /// MessageBox. Все ошибки обрабатываются внутри окна/метода.
    /// </summary>
    private void ShowUpdateDialog(ReleaseInfo release)
    {
        try
        {
            var window = new UpdateAvailableWindow(release, this);
            window.ShowDialog();
        }
        catch
        {
            // Диалог не должен ронять приложение.
        }
    }

    /// <summary>
    /// Скачивает Windows-версию (single-file exe) из GitHub releases во временный файл
    /// с отображением прогресса в строке состояния главного окна (событие
    /// <see cref="DownloadProgressChanged"/>) и сразу применяет обновление режимом
    /// «Перезапустить сейчас», не задавая пользователю вопросов. ПРИМЕЧАНИЕ: метод
    /// сохранён для совместимости, но больше НЕ вызывается — как фоновая, так и ручная
    /// проверка всегда открывают единый диалог <see cref="UpdateAvailableWindow"/>,
    /// который сам скачивает файл и спрашивает пользователя, как применить обновление.
    /// </summary>
    public async Task DownloadAndInstallAutoAsync(ReleaseInfo release)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            {
                // Прямой ссылки на exe нет. Браузер/GitHub не открываем — просто
                // сообщаем пользователю, что загрузить обновление невозможно.
                ShowErrorOnUi(LocalizationManager.T("Update.NoDownloadUrl"));
                return;
            }

            var targetExe = ResolveTargetExe();
            if (targetExe is null)
            {
                ShowErrorOnUi(LocalizationManager.T("Update.InstallFailed"));
                return;
            }

            // Скачивание выполняется в фоне; прогресс передаётся в строку состояния
            // главного окна через событие DownloadProgressChanged.
            var newExe = await DownloadNewExeCoreAsync(release.DownloadUrl!);

            if (newExe is null)
            {
                ShowErrorOnUi(LocalizationManager.T("Update.DownloadFailed"));
                return;
            }

            // Автоматический режим всегда применяет обновление с перезапуском «сейчас».
            if (!ApplyRestartNow(targetExe, newExe))
            {
                ShowErrorOnUi(LocalizationManager.T("Update.InstallFailed"));
                return;
            }

            // Помощник запущен — закрываем приложение, чтобы exe освободился и был заменён.
            ShutdownNow();
        }
        catch (Exception ex)
        {
            ShowErrorOnUi(LocalizationManager.T("Update.InstallFailed") + "\n" + ex.Message);
        }
    }

    /// <summary>
    /// Скачивает exe по прямой ссылке во временный каталог. Возвращает путь к файлу
    /// или null при сетевой ошибке / пустом файле. Временный файл удаляется при неудаче.
    /// Во время записи отчитывается о прогрессе через <see cref="DownloadProgressChanged"/>.
    /// Сначала пробуется многопоточная загрузка (issue #284: провайдер режет скорость на
    /// одно соединение; менеджеры загрузок с несколькими потоками разгоняют её до
    /// максимума). При любом сбое параллельного режима — переход к однопоточной загрузке,
    /// устойчивой к обрывам: частично скачанный файл сохраняется, а при повторе докачивается
    /// с места обрыва через HTTP Range. Это решает проблему, когда на больших файлах
    /// (десятки МБ) провайдер/прокси сбрасывает соединение и раньше скачивание каждый раз
    /// начиналось с нуля и, при повторном обрыве, падало с ошибкой «не удалось скачать
    /// обновление».
    /// </summary>
    private async Task<string?> DownloadAsync(string url)
    {
        var dir = Path.Combine(Path.GetTempPath(), UpdateTempDir);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "ConfigurationManagement.new.exe");

        // Многопоточная загрузка по HTTP Range (N сегментов). Прогресс агрегированный:
        // сумма скачанного по сегментам / общий размер, публикуется не чаще раза на процент.
        var parallelResult = await ParallelDownloader.TryDownloadAsync(
            _http, url, dest, ReportDownloadProgressPercent);
        if (parallelResult is not null)
            return parallelResult;

        const int maxAttempts = 12;
        long totalBytes = -1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Сколько уже скачано — с этого байта продолжим (докачка через Range).
            var existing = TryGetFileLength(dest);

            // Файл уже полный — принимаем его.
            if (totalBytes > 0 && existing >= totalBytes)
                return existing > 0 ? dest : null;

            try
            {
                var result = await DownloadChunkAsync(url, dest, existing, totalBytes);
                if (result.FullTotal > 0)
                    totalBytes = result.FullTotal;
                if (result.Completed)
                {
                    var size = TryGetFileLength(dest);
                    if (totalBytes <= 0 || size >= totalBytes)
                        return size > 0 ? dest : null;
                }
            }
            catch
            {
                // Обрыв/ошибка соединения — частичный файл остаётся, повторяем с докачкой.
            }

            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 5)));
        }

        TryDelete(dest);
        return null;
    }

    /// <summary>
    /// Скачивает один фрагмент exe. Если <paramref name="start"/> > 0 — запрашивает
    /// остаток через HTTP Range и дописывает его в конец существующего файла. Если сервер
    /// не поддерживает Range (вернул 200 вместо 206) — файл перезаписывается с нуля.
    /// Возвращает признак успешного чтения потока до конца и полный размер файла.
    /// </summary>
    private async Task<(bool Completed, long FullTotal)> DownloadChunkAsync(
        string url, string dest, long start, long knownTotal)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (start > 0)
            request.Headers.Range = new RangeHeaderValue(start, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        // Режим записи: докачка в конец частичного файла (206) либо перезапись с нуля (200).
        var mode = FileMode.Create;
        var offset = 0L;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            mode = FileMode.Open;
            offset = start;
        }
        else if (start > 0)
        {
            // Сервер проигнорировал Range — начинаем заново с нуля, файл перезаписываем.
            mode = FileMode.Create;
            offset = 0;
        }

        // Полный размер файла для процентов прогресса: при докачке берём уже известный,
        // на первой попытке — из заголовка ответа (200: Content-Length, 206: Content-Range).
        var fullTotal = knownTotal > 0
            ? knownTotal
            : response.StatusCode == HttpStatusCode.PartialContent
                ? ParseContentRangeTotal(response.Content.Headers.ContentRange)
                : (response.Content.Headers.ContentLength ?? -1);

        await using var source = await response.Content.ReadAsStreamAsync();
        await using (var target = new FileStream(dest, mode, FileAccess.Write, FileShare.None))
        {
            target.Position = offset;
            // Крупный буфер (1 МБ) сокращает число системных вызовов чтения/записи,
            // а отчёт о прогрессе публикуется только на смене целого процента — иначе
            // на каждый блок приходилась бы отправка в поток интерфейса и замедляла бы
            // загрузку на Windows (issue #284). Если полный размер неизвестен — сообщаем
            // неопределённый прогресс раз в блок, что редко.
            var buffer = new byte[1024 * 1024];
            long readBytes = offset;
            var lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                readBytes += read;

                if (fullTotal > 0)
                {
                    var percent = (int)Math.Min(100, readBytes * 100 / fullTotal);
                    if (percent == lastPercent)
                        continue;
                    lastPercent = percent;
                }

                ReportDownloadProgress(fullTotal, readBytes);
            }
            await target.FlushAsync();
        }

        return (true, fullTotal);
    }

    /// <summary>Извлекает полный размер файла из заголовка Content-Range или −1.</summary>
    private static long ParseContentRangeTotal(ContentRangeHeaderValue? range)
    {
        try { return range?.Length ?? -1; }
        catch { return -1; }
    }

    /// <summary>Возвращает размер файла или 0, если файл отсутствует.</summary>
    private static long TryGetFileLength(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Публикует прогресс скачивания. Если общий размер известен заранее — проценты 0–100,
    /// иначе −1 (означает «неопределённый прогресс»).
    /// </summary>
    private void ReportDownloadProgress(long totalBytes, long readBytes)
    {
        if (DownloadProgressChanged is null)
            return;

        var progress = totalBytes > 0
            ? Math.Min(100, readBytes * 100.0 / totalBytes)
            : -1;

        DownloadProgressChanged(progress);
    }

    /// <summary>
    /// Публикует процент скачивания многопоточной загрузки (сумма по сегментам / общий
    /// размер). Интерфейс события <see cref="DownloadProgressChanged"/> не меняется, чтобы
    /// окна прогресса и автообновление продолжали работать как раньше.
    /// </summary>
    private void ReportDownloadProgressPercent(double percent)
    {
        try
        {
            DownloadProgressChanged?.Invoke(percent);
        }
        catch
        {
            // Подписчик не должен ронять загрузку.
        }
    }

    /// <summary>Уведомляет подписчиков о завершении попытки скачивания (успех или неудача).</summary>
    private void RaiseDownloadFinished()
    {
        try
        {
            DownloadFinished?.Invoke();
        }
        catch
        {
            // Подписчик не должен ронять процесс скачивания.
        }
    }

    /// <summary>Возвращает путь к текущему исполняемому файлу приложения или null.</summary>
    internal string? ResolveTargetExe()
    {
        var targetExe = Environment.ProcessPath
                        ?? Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrWhiteSpace(targetExe) ? null : targetExe;
    }

    /// <summary>
    /// Скачивает exe по прямой ссылке и поднимает события прогресса/завершения для строки
    /// состояния главного окна. Возвращает путь к файлу или null при неудаче.
    /// </summary>
    internal async Task<string?> DownloadNewExeCoreAsync(string downloadUrl)
    {
        try
        {
            return await DownloadAsync(downloadUrl);
        }
        finally
        {
            // Сообщаем подписчикам (главному окну), что попытка скачивания завершена,
            // чтобы скрыть индикатор прогресса в любом случае (успех или неудача).
            RaiseDownloadFinished();
        }
    }

    /// <summary>Применяет обновление режимом «Перезапустить сейчас». Возвращает true при успехе.</summary>
    internal bool ApplyRestartNow(string targetExe, string newExe)
    {
        var script = CreateUpdaterScript(targetExe, newExe, Environment.ProcessId, restart: true);
        return LaunchUpdater(script, NeedsElevation(targetExe));
    }

    /// <summary>Применяет обновление режимом «Обновить после закрытия». Возвращает true при успехе.</summary>
    internal bool ApplyAfterClose(string targetExe, string newExe)
    {
        var script = CreateUpdaterScript(targetExe, newExe, Environment.ProcessId, restart: false);
        return LaunchUpdater(script, NeedsElevation(targetExe));
    }

    /// <summary>Показывает локализованную ошибку в UI-потоке.</summary>
    internal void ShowErrorOnUi(string message) =>
        ShowOnUi(() => _dialogs.ShowError(message, LocalizationManager.T("Update.NewVersionAvailable")));

    /// <summary>Закрывает текущее приложение (вызывается после успешного запуска помощника).</summary>
    internal void ShutdownNow()
    {
        var app = Application.Current;
        app?.Dispatcher.Invoke(app.Shutdown);
    }

    /// <summary>
    /// Создаёт временный PowerShell-скрипт, который дожидается завершения основного процесса
    /// (по PID), заменяет текущий исполняемый файл скачанным и удаляет сам скрипт. Если
    /// параметр <paramref name="restart"/> равен true — после замены перезапускает приложение
    /// (режим «Перезапустить сейчас») и ждёт процесс с защитным таймаутом; если false —
    /// перезапуск не выполняется (режим «Обновить после закрытия») и ожидание длится до
    /// естественного завершения приложения. Возвращает путь к созданному скрипту.
    /// </summary>
    private static string CreateUpdaterScript(string targetExe, string newExe, int currentPid, bool restart)
    {
        var dir = Path.Combine(Path.GetTempPath(), UpdateTempDir);
        Directory.CreateDirectory(dir);

        var id = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(dir, $"apply-update-{id}.ps1");
        var logPath = Path.Combine(dir, $"apply-update-{id}.log");

        // Ожидание завершения основного процесса: с таймаутом для перезапуска «сейчас»,
        // без ограничения — для режима «после закрытия». Каждый шаг фиксируется в логе.
        var waitBlock = restart
            ? @"
# Ожидание завершения основного процесса (с таймаутом), чтобы exe не был заблокирован.
$maxWait = 120
$elapsed = 0
while ($elapsed -lt $maxWait) {
    if (-not (Get-Process -Id $pidTarget -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 500
    $elapsed++
}
Start-Sleep -Seconds 1
if (Get-Process -Id $pidTarget -ErrorAction SilentlyContinue) {
    Log ('WARNING: target process still running after ' + $maxWait + 's; continuing anyway')
} else {
    Log ('Target process exited after ~' + ($elapsed / 2) + 's')
}"
            : @"
# Ожидание естественного завершения основного процесса (режим «после закрытия»).
while (Get-Process -Id $pidTarget -ErrorAction SilentlyContinue) {
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 1
Log 'Target process exited (after-close mode).'";

        // Перезапуск приложения выполняем только по явному запросу пользователя.
        var relaunchBlock = restart
            ? @"
try {
    Start-Process -FilePath $target
    Log 'Relaunch started.'
} catch {
    Log ('Relaunch failed: ' + $_.Exception.Message)
}"
            : @"
# Перезапуск не требуется — обновление применится при следующем закрытии приложения.
Log 'Restart not requested (after-close mode).'";

        // Весь скрипт собирается как verbatim-строка с плейсхолдерами, которые заменяются
        // после подстановки значений, чтобы не экранировать фигурные скобки PowerShell.
        const string scriptTemplate = @"
$ErrorActionPreference = 'Stop'
$target = '@@TARGET@@'
$new = '@@NEW@@'
$pidTarget = @@PID@@
$restart = @@RESTART@@
$logFile = '@@LOGFILE@@'
$scriptPath = '@@SCRIPTPATH@@'

function Log([string]$msg) {
    try {
        Add-Content -LiteralPath $logFile -Value (('[{0}] ' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')) + $msg) -Encoding UTF8
    } catch {}
}

Log ('Updater started. restart=' + $restart)
Log ('Target: ' + $target)
Log ('New: ' + $new)
Log ('PidTarget: ' + $pidTarget)

@@WAIT_BLOCK@@

# Заменяем текущий exe новым (с повторами на случай кратковременной блокировки файла).
$moved = $false
$attempts = 0
while (-not $moved -and $attempts -lt 10) {
    $attempts++
    try {
        Move-Item -Path $new -Destination $target -Force
        $moved = $true
        Log ('Move-Item succeeded on attempt ' + $attempts)
    } catch {
        Log ('Move-Item attempt ' + $attempts + ' failed: ' + $_.Exception.Message)
        Start-Sleep -Milliseconds 500
    }
}
if (-not $moved) {
    Log 'FATAL: failed to replace target executable.'
    Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
    exit 1
}

@@RELAUNCH_BLOCK@@

# Убираем временный скрипт.
Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
Log 'Updater finished.'
";

        var script = scriptTemplate
            .Replace("@@TARGET@@", Pq(targetExe))
            .Replace("@@NEW@@", Pq(newExe))
            .Replace("@@PID@@", currentPid.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("@@RESTART@@", restart ? "1" : "0")
            .Replace("@@LOGFILE@@", Pq(logPath))
            .Replace("@@SCRIPTPATH@@", Pq(scriptPath))
            .Replace("@@WAIT_BLOCK@@", waitBlock)
            .Replace("@@RELAUNCH_BLOCK@@", relaunchBlock);

        // Кодировка с BOM обязательна: Windows PowerShell 5.1 читает .ps1 без BOM как ANSI
        // (активная кодовая страница, напр. cp1251), из-за чего не-ASCII пути портятся и
        // Move-Item «тихо» не находит файл. С BOM скрипт читается как UTF-8 корректно.
        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
        return scriptPath;
    }

    /// <summary>
    /// Запускает временный PowerShell-помощник скрытно, без ожидания его завершения.
    /// Используется 64-битная версия PowerShell (см. <see cref="ResolvePowerShellPath"/>) с
    /// политикой выполнения Bypass. Если целевой каталог установки не доступен на запись
    /// текущему пользователю (<paramref name="elevate"/> и процесс запущен не от
    /// администратора) — помощник запускается через UAC (<c>runas</c>) с правами
    /// администратора, что позволяет заменить exe в защищённой папке (например
    /// <c>C:\Program Files\...</c>). Возвращает true, если процесс удалось запустить
    /// (или запрос UAC подтверждён), иначе false (ошибку покажет вызывающий код в UI;
    /// отказ пользователя от UAC также даёт false). Все действия помощника пишутся в лог-файл
    /// рядом со скриптом, поэтому сбой на любом шаге (скрипт не стартовал / exe заблокирован /
    /// Move-Item упал / перезапуск не выполнен) доступен для диагностики.
    /// </summary>
    private static bool LaunchUpdater(string scriptPath, bool elevate)
    {
        try
        {
            if (!File.Exists(scriptPath))
                return false;

            // Когда каталог установки требует прав администратора, а текущий процесс не
            // повышен — запускаем помощник через ShellExecute с глаголом runas (UAC).
            // Отказ пользователя от запроса повышения выбрасывает Win32Exception → false.
            if (elevate && !IsCurrentProcessElevated())
            {
                var psiRunAs = new ProcessStartInfo
                {
                    FileName = ResolvePowerShellPath(),
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"{scriptPath}\""
                };
                Process.Start(psiRunAs);
                // При ShellExecute/RunAs Process.Start может вернуть null даже при успехе
                // (граница повышения прав), поэтому считаем успехом отсутствие исключения.
                return true;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ResolvePowerShellPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-File");
            // Путь передаётся отдельным аргументом списка — кавычки/пробелы в пути
            // обрабатываются корректно, без ручного экранирования строки Arguments.
            psi.ArgumentList.Add(scriptPath);

            var process = Process.Start(psi);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Определяет, требуется ли повышение прав для замены исполняемого файла: целевой
    /// каталог установки не доступен текущему пользователю на запись. Иначе (обычная
    /// установка в папку пользователя) возвращает false и UAC не запрашивается.
    /// </summary>
    private static bool NeedsElevation(string targetExe)
    {
        try
        {
            var dir = Path.GetDirectoryName(targetExe);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;
            return !IsDirectoryWritable(dir);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Проверяет, можно ли создать и удалить временный файл в указанном каталоге.</summary>
    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".cm_write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Проверяет, запущен ли текущий процесс с правами администратора.</summary>
    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Возвращает путь к PowerShell. Предпочитается 64-битная версия: из 32-битного процесса
    /// обращение к %SystemRoot%\System32 перенаправляется на SysWOW64 (32-битный PowerShell),
    /// поэтому сначала пробуется каталог Sysnative (доступен только из 32-битных процессов и
    /// указывает на настоящую 64-битную System32). Из 64-битного процесса Sysnative недоступен —
    /// тогда берётся System32 напрямую. Последний запасной вариант — powershell из PATH.
    /// </summary>
    private static string ResolvePowerShellPath()
    {
        var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(sysRoot))
            return "powershell.exe";

        var candidates = new[]
        {
            Path.Combine(sysRoot, "Sysnative", "WindowsPowerShell", "v1.0", "powershell.exe"),
            Path.Combine(sysRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "powershell.exe";
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