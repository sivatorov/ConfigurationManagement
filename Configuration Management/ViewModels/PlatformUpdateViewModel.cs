using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// ViewModel окна «Обновление платформы 1С»: единый список установленных и доступных
/// версий технологической платформы (колонки Версия/Размер/Статус/Совместимые базы),
/// проверка каталога портала, загрузка дистрибутива и установка. Сетевые операции
/// (получение каталога, подгрузка файлов, выбор дистрибутива) выполняются через
/// <see cref="IPlatformUpdateService"/>, а загрузка файла, установка, диалог
/// сохранения, выбор дистрибутива/удаляемых версий и чтение установленных версий —
/// через инжектируемые делегаты: класс остаётся чистым и покрывается тестами на
/// fake-сервисах без сети и UI.
/// </summary>
public sealed class PlatformUpdateViewModel : ViewModelBase
{
    private readonly IPlatformUpdateService _service;
    private readonly IInfobaseRepository _infobaseRepository;
    private readonly Func<IReadOnlyList<string>> _loadInstalledVersions;
    private readonly Func<string, string, IProgress<double>?, CancellationToken, Task<string?>> _downloadDistribution;
    private readonly Func<string, string, string?, IProgress<string>?, CancellationToken,
        Task<(bool Success, string? ErrorKey, int ExitCode)>> _installFromZip;
    private readonly Func<string?, string?> _saveFileDialog;

    // Проверка готовности к установке (этап 0.3.9.214) — всё через делегаты,
    // чтобы класс оставался чистым и тестируемым.
    private readonly Func<IReadOnlyList<string>> _loadRunningProcesses;
    private readonly Func<bool> _isAdministrator;
    private readonly Func<string, long?> _getFreeBytes;
    private readonly Func<string, bool> _hasValidSignature;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Action<string, string, NotificationKind, NotificationEvent> _notify;
    private readonly Services.IAppLogger? _appLogger;

    /// <summary>Менеджер фоновых загрузок (issue #334 п.1, 0.3.12.2): если задан,
    /// скачивания «Только скачать»/«Скачать и установить» регистрируются в нём —
    /// продолжаются после закрытия окна, видны в индикаторе главного окна и
    /// отменяются из него. Null — прежнее поведение (тесты).</summary>
    private readonly Services.BackgroundDownloadManager? _backgroundDownloads;

    // Удаление старых версий (этап 0.3.9.215) — всё через делегаты, чтобы класс
    // оставался чистым и тестируемым на обеих платформах.
    private readonly Func<IReadOnlyList<PlatformVersionInfo>> _loadInstalledVersionInfos;
    private readonly Func<IReadOnlyList<string>> _loadRunningBinPaths;
    private readonly Func<PlatformVersionInfo, IProgress<string>?, CancellationToken,
        Task<(bool Success, string? ErrorKey)>>? _deleteVersionDirectory;
    private readonly Func<string, string> _buildUninstallCommand;
    private readonly Action<string> _copyToClipboard;

    /// <summary>
    /// Диалог выбора удаляемых старых версий (issue #334): по ПОЛНОМУ списку
    /// установленных версий с признаками риска
    /// (<see cref="OldVersionCleaner.SelectDeletionEntries"/>) возвращает подмножество,
    /// которое пользователь выбрал для удаления, или null (отмена). null — делегат
    /// не задан (тесты/окружение без UI): используется общий диалог подтверждения
    /// <c>_confirmDialog</c> на версии без признаков риска.
    /// </summary>
    private readonly Func<IReadOnlyList<OldVersionCleanupEntry>, IReadOnlyList<PlatformVersionInfo>?>? _chooseVersionsToDelete;

    /// <summary>True — Windows-ветка удаления (инжектирован делегат удаления каталога);
    /// false — Linux-ветка (показ команды sudo с копированием в буфер).</summary>
    private readonly bool _useWindowsDelete;

    /// <summary>
    /// Маршаллер изменения UI-состояния в поток Dispatcher (issues #334/#330): обновление
    /// коллекций (<see cref="Rows"/>) и связанных свойств выполняется ТОЛЬКО в UI-потоке,
    /// иначе WPF CollectionView бросает NotSupportedException. null — прямой вызов
    /// (юнит-тесты); окна передают платформенный маршаллер через <see cref="UiDispatch"/>.
    /// </summary>
    private readonly Action<Action>? _dispatchToUi;

    /// <summary>
    /// Диалог выбора варианта дистрибутива (issue #334): по списку доступных для текущей
    /// ОС файлов возвращает выбранный вариант или null (отмена → рекомендуемый). null —
    /// диалог не показывается (тесты, окружение без UI), берётся рекомендуемый вариант.
    /// </summary>
    private readonly Func<IReadOnlyList<PlatformDistributionOption>, PlatformDistributionOption?>? _chooseDistribution;

    private readonly StringBuilder _log = new();
    private IReadOnlyList<PlatformRelease> _availableReleases = new List<PlatformRelease>();
    private bool _isBusy;
    private double _progress;
    private PlatformUpdateRowViewModel? _selectedRow;
    private bool _installHadWarnings;

    /// <summary>Строки окна: установленные ∪ доступные версии (по убыванию).</summary>
    public ObservableCollection<PlatformUpdateRowViewModel> Rows { get; } = new();

    /// <summary>Выполняется ли сетевая или установочная операция (блокирует команды).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                RefreshCommands();
        }
    }

    /// <summary>Общий прогресс операции (0..1).</summary>
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, Math.Clamp(value, 0, 1));
    }

    /// <summary>Текст журнала операции (строки добавляются через <see cref="AppendLog"/>).</summary>
    public string LogText => _log.ToString();

    /// <summary>Выбранная строка списка (для команд «Скачать и установить»/«Только скачать»).
    /// При выборе строки с совместимыми базами их список (первые 5 + счётчик) пишется
    /// в журнал окна.</summary>
    public PlatformUpdateRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                RefreshCommands();
                LogCompatibleBases(value);
            }
        }
    }

    /// <summary>Команда «Проверить обновления».</summary>
    public RelayCommand CheckCommand { get; }

    /// <summary>Команда «Скачать и установить» (Windows: загрузка в %TEMP% + установщик).</summary>
    public RelayCommand DownloadAndInstallCommand { get; }

    /// <summary>Команда «Только скачать» (сохранение дистрибутива через диалог).</summary>
    public RelayCommand DownloadOnlyCommand { get; }

    /// <summary>Команда «Удалить старые версии…»: отбор кандидатов
    /// (<see cref="OldVersionCleaner.SelectCandidates"/>), диалог подтверждения,
    /// Windows — удаление каталогов версий последовательно, Linux — показ команды
    /// sudo с копированием в буфер; уведомление о результате.</summary>
    public RelayCommand RemoveOldVersionsCommand { get; }

    /// <param name="service">Сервис каталога версий платформы (портал releases.1c.ru).</param>
    /// <param name="infobaseRepository">Репозиторий информационных баз (совместимость версий).</param>
    /// <param name="loadInstalledVersions">Читает установленные версии платформы
    /// (на Windows — <c>PlatformVersionService.FindInstalledVersionInfos</c>).</param>
    /// <param name="downloadDistribution">Загружает файл дистрибутива по прямой ссылке
    /// с прогрессом; возвращает путь сохранённого файла или null при ошибке/отмене.</param>
    /// <param name="installFromZip">Устанавливает дистрибутив из zip (Windows —
    /// <c>PlatformInstaller.InstallFromZipAsync</c>); возвращает результат установки.</param>
    /// <param name="saveFileDialog">Диалог сохранения файла: имя по умолчанию → путь
    /// или null при отмене.</param>
    public PlatformUpdateViewModel(
        IPlatformUpdateService service,
        IInfobaseRepository infobaseRepository,
        Func<IReadOnlyList<string>> loadInstalledVersions,
        Func<string, string, IProgress<double>?, CancellationToken, Task<string?>> downloadDistribution,
        Func<string, string, string?, IProgress<string>?, CancellationToken,
            Task<(bool Success, string? ErrorKey, int ExitCode)>> installFromZip,
        Func<string?, string?>? saveFileDialog = null,
        Func<IReadOnlyList<string>>? loadRunningProcesses = null,
        Func<bool>? isAdministrator = null,
        Func<string, long?>? getFreeBytes = null,
        Func<string, bool>? hasValidSignature = null,
        Func<string, string, bool>? confirmDialog = null,
        Action<string, string, NotificationKind, NotificationEvent>? notify = null,
        Services.IAppLogger? appLogger = null,
        Func<IReadOnlyList<PlatformVersionInfo>>? loadInstalledVersionInfos = null,
        Func<IReadOnlyList<string>>? loadRunningBinPaths = null,
        Func<PlatformVersionInfo, IProgress<string>?, CancellationToken,
            Task<(bool Success, string? ErrorKey)>>? deleteVersionDirectory = null,
        Func<string, string>? buildUninstallCommand = null,
        Action<string>? copyToClipboard = null,
        Action<Action>? dispatchToUi = null,
        Func<IReadOnlyList<PlatformDistributionOption>, PlatformDistributionOption?>? chooseDistribution = null,
        Func<IReadOnlyList<OldVersionCleanupEntry>, IReadOnlyList<PlatformVersionInfo>?>? chooseVersionsToDelete = null,
        Services.BackgroundDownloadManager? backgroundDownloads = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatchToUi = dispatchToUi;
        _chooseDistribution = chooseDistribution;
        _backgroundDownloads = backgroundDownloads;
        _infobaseRepository = infobaseRepository ?? throw new ArgumentNullException(nameof(infobaseRepository));
        _loadInstalledVersions = loadInstalledVersions ?? throw new ArgumentNullException(nameof(loadInstalledVersions));
        _downloadDistribution = downloadDistribution ?? throw new ArgumentNullException(nameof(downloadDistribution));
        _installFromZip = installFromZip ?? throw new ArgumentNullException(nameof(installFromZip));
        _saveFileDialog = saveFileDialog ?? (_ => null);
        _chooseVersionsToDelete = chooseVersionsToDelete;

        // Проверка готовности к установке (этап 0.3.9.214): по умолчанию — «проблем нет»,
        // чтобы существующие вызовы (WPF-окно этапа 213 и тесты) продолжали работать.
        _loadRunningProcesses = loadRunningProcesses ?? (() => Array.Empty<string>());
        _isAdministrator = isAdministrator ?? (() => true);
        _getFreeBytes = getFreeBytes ?? (_ => null);
        _hasValidSignature = hasValidSignature ?? (_ => true);
        _confirmDialog = confirmDialog ?? ((_, _) => true);
        _notify = notify ?? ((_, _, _, _) => { });
        _appLogger = appLogger;

        // Удаление старых версий (этап 0.3.9.215): по умолчанию — «ничего не удаляем»
        // (Windows-ветка без делегата удаления выродилась бы в ложный успех, поэтому
        // признак платформы определяется наличием делегата удаления каталога).
        _loadInstalledVersionInfos = loadInstalledVersionInfos ?? (() => Array.Empty<PlatformVersionInfo>());
        _loadRunningBinPaths = loadRunningBinPaths ?? (() => Array.Empty<string>());
        _useWindowsDelete = deleteVersionDirectory is not null;
        _deleteVersionDirectory = deleteVersionDirectory;
        _buildUninstallCommand = buildUninstallCommand ?? (_ => string.Empty);
        _copyToClipboard = copyToClipboard ?? (_ => { });

        CheckCommand = new RelayCommand(async () => await CheckUpdatesAsync(), () => !IsBusy);
        DownloadAndInstallCommand = new RelayCommand(
            async () => await DownloadAndInstallAsync(), () => !IsBusy && SelectedRow is not null);
        DownloadOnlyCommand = new RelayCommand(
            async () => await DownloadOnlyAsync(), () => !IsBusy && SelectedRow is not null);
        RemoveOldVersionsCommand = new RelayCommand(
            async () => await RemoveOldVersionsAsync(), () => !IsBusy);
    }

    /// <summary>Добавляет строку в журнал и уведомляет UI (<see cref="LogText"/>).
    /// Журнал ограничивается хвостом (защита от бесконечного роста при длинной загрузке).
    /// Выполняется строго в UI-потоке (issue #334): операции удаления/установки продолжаются
    /// после <c>ConfigureAwait(false)</c> в фоновом потоке, а уведомление
    /// <c>PropertyChanged</c> из фонового потока заставляет окна (обработчики
    /// PropertyChanged, автопрокрутка) и привязки работать с UI-объектами вне потока
    /// Dispatcher. С маршаллером (окна передают <see cref="UiDispatch"/>) тело уходит
    /// в UI-поток; без маршаллера (тесты) — прямой вызов.</summary>
    public void AppendLog(string message)
    {
        UiDispatch.Run(_dispatchToUi, () =>
        {
            _log.AppendLine(message ?? string.Empty);

            const int maxLength = 64 * 1024;
            const int keepTail = 32 * 1024;
            if (_log.Length > maxLength)
            {
                var text = _log.ToString();
                _log.Clear();
                _log.Append(text.Substring(text.Length - keepTail));
            }

            OnPropertyChanged(nameof(LogText));
        });
    }

    /// <summary>Читает установленные версии платформы через инжектируемый делегат.
    /// Ошибки чтения не роняют модель — пишутся в журнал, возвращается пустой список.</summary>
    public IReadOnlyList<string> LoadInstalledAsync()
    {
        try
        {
            return _loadInstalledVersions() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>True — статус ошибки связан с авторизацией на портале 1С (требуется вход,
    /// вход не подтверждён, исчерпан лимит попыток): для таких ошибок в журнал окна
    /// добавляется расширенный совет <c>PlatformUpdate.AuthAdvice</c> (issue #334/#330/#323).</summary>
    private static bool IsAuthIssue(PortalFetchStatus status)
        => status is PortalFetchStatus.AuthRequired or PortalFetchStatus.AuthFailed
            or PortalFetchStatus.LoginLimitReached
            // Форма входа изменилась (OAuth/JS-челлендж) — тоже «авторизация», совет
            // открыть login.1c.ru в браузере уместен (issue #323/#330/#334).
            or PortalFetchStatus.FormUnavailable;

    /// <summary>Проверяет каталог версий платформы на портале 1С и перестраивает
    /// список строк (установленные ∪ доступные) с числом совместимых баз. Статус
    /// ошибки (авторизация/сеть/404) пишется в журнал ключом локализации;
    /// исключения сервиса гасятся.</summary>
    public async Task CheckUpdatesAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Progress = 0;
        AppendLog(LocalizationManager.T("PlatformUpdate.Status.Checking"));
        _appLogger?.Info("Обновление платформы: получение каталога версий с портала 1С");
        try
        {
            var result = await _service.GetAvailableReleasesAsync().ConfigureAwait(false);
            if (result.Status != PortalFetchStatus.Ok)
            {
                // Пустой/некорректный ключ ошибки не должен показывать пользователю
                // пустое сообщение: подставляем общий ключ сетевой ошибки.
                var errorKey = string.IsNullOrWhiteSpace(result.ErrorKey)
                    ? PlatformUpdateService.ErrorNetwork
                    : result.ErrorKey;
                var errorText = LocalizationManager.T(errorKey);
                AppendLog(errorText);
                // Расширенный совет при проблемах авторизации портала: что проверить и когда
                // повторить (issue #334/#330/#323).
                if (IsAuthIssue(result.Status))
                    AppendLog(LocalizationManager.T("PlatformUpdate.AuthAdvice"));
                _appLogger?.Warn($"Обновление платформы: каталог не получен — {errorKey}");
                NotifyError(errorText);
                return;
            }

            // issue #334: продолжение после ConfigureAwait(false) идёт на пуле потоков,
            // а RebuildRows меняет ObservableCollection Rows (WPF CollectionView DataGrid) —
            // NotSupportedException «изменение SourceCollection из потока, отличного от
            // Dispatcher». Все изменения коллекции и зависимых свойств — только в UI-потоке.
            UiDispatch.Run(_dispatchToUi, () =>
            {
                _availableReleases = result.Releases ?? new List<PlatformRelease>();
                RebuildRows(LoadInstalledAsync(), _availableReleases);
                AppendLog(string.Format(
                    LocalizationManager.T("PlatformUpdate.Progress.Done"),
                    _availableReleases.FirstOrDefault()?.Version ?? "—"));
                // issue #334: лог пишется ПОСЛЕ применения результата. Маршаллер UI
                // асинхронный (Dispatcher.InvokeAsync/Post не ждут), раньше строка ниже
                // читала СТАРОЕ значение _availableReleases — «получено 0 версий каталога»
                // даже при успешном ответе портала.
                _appLogger?.Info($"Обновление платформы: получено {_availableReleases.Count} версий каталога");
            });
        }
        catch (Exception ex)
        {
            // Исключение провайдера гасится: понятное сообщение в журнал + уведомление,
            // приложение не падает (issue #334). Тип исключения логируется без секретов.
            var errorText = $"{LocalizationManager.T(PlatformUpdateService.ErrorNetwork)}: {ex.Message}";
            AppendLog(errorText);
            _appLogger?.Error(
                $"Обновление платформы: исключение при проверке каталога: {ex.GetType().Name}: {ex.Message}", ex);
            NotifyError(errorText);
        }
        finally
        {
            // Анти-мигание: прогресс при ошибке остаётся 0, а не «прыгает» в 1.
            IsBusy = false;
        }
    }

    /// <summary>«Скачать и установить» (Windows): ленивая подгрузка файлов выбранного
    /// релиза, выбор дистрибутива, загрузка во временный каталог
    /// <c>%TEMP%\cm_platformdl_<guid></c> с прогрессом, установка через инжектируемый
    /// установщик и перечитывание установленных версий. Все сетевые/установочные вызовы —
    /// через делегаты (тесты подменяют их fake).</summary>
    public async Task DownloadAndInstallAsync()
    {
        if (IsBusy || SelectedRow is null)
            return;

        var row = SelectedRow;
        IsBusy = true;
        row.IsDownloading = true;
        row.Progress = 0;
        try
        {
            if (!await EnsureReleaseFilesAsync(row).ConfigureAwait(false))
                return;

            // issue #334: сначала показываем пользователю варианты дистрибутива для его
            // ОС (x86/x64, полный/тонкий клиент) — «а какую оно пытается скачивать?»,
            // затем скачиваем и устанавливаем выбранный файл.
            var picked = await ResolvePickedFileAsync(row).ConfigureAwait(false);
            if (picked is null)
            {
                // Диагностика уже записана в журнал ResolvePickedFileAsync (файлы релиза,
                // причина отсутствия варианта); скачивание и установка не выполняются.
                return;
            }

            AppendLog(string.Format(
                LocalizationManager.T("PlatformUpdate.Progress.SelectedDistribution"),
                picked.FileName));

            var targetDir = Path.Combine(Path.GetTempPath(), "cm_platformdl_" + Guid.NewGuid().ToString("N"));
            var zipPath = Path.Combine(targetDir, OneCUpdatesService.BuildTargetFileName(row.Version, picked.FileName));

            AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Progress.Download"), picked.FileName));
            // issue #334: полная диагностика шага — прямая ссылка и путь сохранения,
            // в журнал окна И файловый журнал («в логах пусто» недопустимо).
            AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Diag.DownloadUrl"), picked.Url));
            AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Diag.SavePath"), zipPath));
            _appLogger?.Info(
                $"Обновление платформы {row.Version}: скачивание {picked.FileName} из {picked.Url} в {zipPath}");

            // issue #334 п.1 (0.3.12.2): скачивание перед установкой регистрируется в
            // менеджере фоновых загрузок — видно в индикаторе главного окна, отменяется
            // из него; реальный токен отмены вместо CancellationToken.None.
            var entry = _backgroundDownloads is null
                ? null
                : _backgroundDownloads.Start($"platform-update:{row.Version}:{picked.FileName}", picked.FileName);
            var downloadCancellationToken = entry?.Cancellation.Token ?? CancellationToken.None;
            var downloadId = entry?.Id;
            var progress = new Progress<double>(v =>
            {
                row.Progress = v;
                Progress = v;
                if (downloadId is not null)
                    _backgroundDownloads!.ReportProgress(downloadId, v);
            });
            string? downloaded;
            try
            {
                downloaded = await _downloadDistribution(picked.Url, zipPath, progress, downloadCancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Отмена из индикатора главного окна: состояние Cancelled уже выставлено
                // менеджером в Cancel() (если отмена шла через него); ниже общий
                // «не удалось скачать»-путь выставит Fail для остальных случаев.
                AppendLog(LocalizationManager.T("Main.Downloads.Cancelled"));
                _appLogger?.Info($"Обновление платформы {row.Version}: скачивание {picked.FileName} отменено пользователем");
                downloaded = null;
            }
            catch (Exception)
            {
                // Ошибка загрузки — состояние Failed в менеджере (сетевой ключ), затем
                // прежняя обработка исключения внешним catch.
                if (downloadId is not null)
                    _backgroundDownloads!.Fail(downloadId, "PlatformUpdate.Error.NetworkError");
                throw;
            }
            if (string.IsNullOrWhiteSpace(downloaded))
            {
                if (downloadId is not null && downloadCancellationToken.IsCancellationRequested)
                    _backgroundDownloads!.Fail(downloadId, "Main.Downloads.Cancelled");
                else if (downloadId is not null)
                    _backgroundDownloads!.Fail(downloadId, "PlatformUpdate.Error.NetworkError");
                AppendLog(LocalizationManager.T("PlatformUpdate.Error.Network"));
                _appLogger?.Error(
                    $"Обновление платформы {row.Version}: не удалось скачать {picked.FileName} из {picked.Url}");
                return;
            }

            if (downloadId is not null)
                _backgroundDownloads!.Complete(downloadId);

            row.Progress = 1;
            Progress = 1;
            // issue #334: фактический размер скачанного файла — в журнал окна и файловый журнал.
            LogDownloadedSize(downloaded);

            // Проверка готовности к установке (этап 0.3.9.214): занятые процессы 1С,
            // права администратора, свободное место, подпись файла. При замечаниях —
            // диалог подтверждения; отмена останавливает операцию до запуска установщика.
            _installHadWarnings = false;
            var preflight = RunPreflight(downloaded, targetDir, picked.SizeBytes);
            if (preflight.Count > 0)
            {
                foreach (var warning in preflight)
                    AppendLog(warning.Text);

                _appLogger?.Warn(
                    $"Обновление платформы: замечания перед установкой версии {row.Version} — " +
                    string.Join("; ", preflight.Select(w => w.Text)));

                var message = string.Join("\n", preflight.Select(w => w.Text)) + "\n\n" +
                    string.Format(LocalizationManager.T("PlatformUpdate.Confirm.InstallMessage"), row.Version);
                var confirmed = _confirmDialog(
                    LocalizationManager.T("PlatformUpdate.Confirm.InstallTitle"), message);
                AppendLog(confirmed
                    ? LocalizationManager.T("PlatformUpdate.Preflight.Continue")
                    : LocalizationManager.T("PlatformUpdate.Error.Cancelled"));
                if (!confirmed)
                {
                    _appLogger?.Info("Обновление платформы: установка отменена пользователем");
                    return;
                }

                _installHadWarnings = preflight.Any(w => w.Kind == PlatformPreflightWarningKind.Warning);
            }

            AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Progress.Install"), row.Version));
            _appLogger?.Info($"Обновление платформы: запуск установщика версии {row.Version}");

            var installLog = new Progress<string>(AppendLog);
            var result = await _installFromZip(downloaded, row.Version, null, installLog, CancellationToken.None)
                .ConfigureAwait(false);

            _appLogger?.Info($"Обновление платформы: установщик версии {row.Version} завершился с кодом {result.ExitCode}");

            if (!result.Success)
            {
                var errorText = string.IsNullOrWhiteSpace(result.ErrorKey)
                    ? LocalizationManager.T("PlatformUpdate.Error.Network")
                    : LocalizationManager.T(result.ErrorKey);
                AppendLog(errorText);
                NotifyError(string.Format(LocalizationManager.T("Notify.PlatformUpdateError"), errorText));
                return;
            }

            AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Progress.Done"), row.Version));
            _appLogger?.Info($"Обновление платформы: перечитывание установленных версий после установки {row.Version}");
            await RefreshInstalledAsync().ConfigureAwait(false);

            // Уведомление о результате: частичный успех (были предупреждения, например
            // не проверена подпись) — Warning, иначе — Success.
            NotifyResult(string.Format(LocalizationManager.T("Notify.PlatformUpdateDone"), row.Version));
        }
        catch (Exception ex)
        {
            AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}");
            NotifyError(string.Format(
                LocalizationManager.T("Notify.PlatformUpdateError"),
                $"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}"));
        }
        finally
        {
            row.IsDownloading = false;
            row.Progress = 1;
            IsBusy = false;
        }
    }

    /// <summary>«Только скачать»: ленивая подгрузка файлов релиза, выбор дистрибутива,
    /// диалог сохранения (инжектируемый делегат) и загрузка в указанный путь.
    /// Отмена диалога (null) — no-op с записью в журнал. Скачивание регистрируется
    /// в менеджере фоновых загрузок (issue #334 п.1, 0.3.12.2), если он передан.</summary>
    public async Task DownloadOnlyAsync()
    {
        if (IsBusy || SelectedRow is null)
            return;

        var row = SelectedRow;
        IsBusy = true;
        // issue #334 п.1 (0.3.12.2): фоновая запись и токен видны в catch/finally —
        // состояние выставляется ровно один раз (Complete/Fail).
        Services.BackgroundDownloadManager.ActiveDownload? entry = null;
        try
        {
            if (!await EnsureReleaseFilesAsync(row).ConfigureAwait(false))
                return;

            // issue #334: выбор варианта дистрибутива (см. DownloadAndInstallAsync).
            var picked = await ResolvePickedFileAsync(row).ConfigureAwait(false);
            if (picked is null)
            {
                // Диагностика уже записана в журнал ResolvePickedFileAsync (в т.ч. отмена
                // выбора, issue #334 п.2, 0.3.12.2); скачивание не выполняется.
                return;
            }

            AppendLog(string.Format(
                LocalizationManager.T("PlatformUpdate.Progress.SelectedDistribution"),
                picked.FileName));

            var defaultName = OneCUpdatesService.BuildTargetFileName(row.Version, picked.FileName);
            var targetPath = _saveFileDialog(defaultName);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                AppendLog(LocalizationManager.T("PlatformUpdate.Error.Cancelled"));
                return;
            }

            row.IsDownloading = true;
            row.Progress = 0;
            AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Progress.Download"), picked.FileName));
            // issue #334: полная диагностика шага — ссылка и путь сохранения, в журнал
            // окна И файловый журнал («в логах пусто» недопустимо).
            AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Diag.DownloadUrl"), picked.Url));
            AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Diag.SavePath"), targetPath));
            _appLogger?.Info(
                $"Обновление платформы {row.Version}: скачивание {picked.FileName} из {picked.Url} в {targetPath}");

            // issue #334 п.1 (0.3.12.2): загрузка регистрируется в менеджере фоновых
            // загрузок — продолжается после закрытия окна, видна в индикаторе главного
            // окна, отменяется из него; реальный токен отмены вместо CancellationToken.None.
            var downloadId = _backgroundDownloads is null
                ? null
                : $"platform-update:{row.Version}:{picked.FileName}";
            entry = downloadId is null ? null : _backgroundDownloads!.Start(downloadId, picked.FileName);
            var downloadCancellationToken = entry?.Cancellation.Token ?? CancellationToken.None;

            var progress = new Progress<double>(v =>
            {
                row.Progress = v;
                Progress = v;
                if (downloadId is not null)
                    _backgroundDownloads!.ReportProgress(downloadId, v);
            });
            var saved = await _downloadDistribution(picked.Url, targetPath, progress, downloadCancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(saved))
            {
                if (downloadId is not null && downloadCancellationToken.IsCancellationRequested)
                    _backgroundDownloads!.Fail(downloadId, "Main.Downloads.Cancelled");
                else if (downloadId is not null)
                    _backgroundDownloads!.Fail(downloadId, "PlatformUpdate.Error.NetworkError");
                AppendLog(LocalizationManager.T("PlatformUpdate.Error.Network"));
                _appLogger?.Error(
                    $"Обновление платформы {row.Version}: не удалось скачать {picked.FileName} из {picked.Url}");
                NotifyError(string.Format(
                    LocalizationManager.T("Notify.PlatformUpdateError"),
                    LocalizationManager.T("PlatformUpdate.Error.Network")));
                return;
            }

            if (downloadId is not null)
                _backgroundDownloads!.Complete(downloadId);

            row.Progress = 1;
            Progress = 1;
            // issue #334: фактический размер скачанного файла — в журнал окна и файловый журнал.
            LogDownloadedSize(saved);
            AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Progress.Done"), targetPath));
            _appLogger?.Info($"Обновление платформы: дистрибутив сохранён в «{targetPath}»");

            // Уведомление о завершении загрузки (категория Update, вид — успех).
            _notify(
                LocalizationManager.T("PlatformUpdate.WindowTitle"),
                string.Format(LocalizationManager.T("PlatformUpdate.Progress.Done"), targetPath),
                NotificationKind.Success,
                NotificationEvent.Update);
        }
        catch (OperationCanceledException)
        {
            // Отмена из индикатора главного окна (issue #334 п.1): состояние Cancelled
            // уже выставлено менеджером в Cancel(); здесь журнал.
            AppendLog(LocalizationManager.T("Main.Downloads.Cancelled"));
            _appLogger?.Info("Обновление платформы: скачивание отменено пользователем");
        }
        catch (Exception ex)
        {
            AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}");
            NotifyError(string.Format(
                LocalizationManager.T("Notify.PlatformUpdateError"),
                $"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}"));
        }
        finally
        {
            // Успешное завершение — запись Completed; при отмене/ошибке состояние
            // выставлено ранее (Fail/Cancel в менеджере).
            if (entry is not null && entry.IsActive)
                _backgroundDownloads!.Complete(entry.Id);
            row.IsDownloading = false;
            IsBusy = false;
        }
    }

    /// <summary>Перечитывает установленные версии и перестраивает список строк по
    /// уже полученному каталогу (без повторного сетевого запроса). Вызывается после
    /// успешной установки — новая версия появляется в списке как установленная.</summary>
    public async Task RefreshInstalledAsync()
    {
        try
        {
            if (_availableReleases.Count == 0)
                return;
            // Перестройка Rows мутирует ObservableCollection — строго в UI-потоке
            // (issue #334: после удаления/установки метод вызывается из продолжений
            // с ConfigureAwait(false), а CollectionView запрещает менять SourceCollection
            // вне потока Dispatcher).
            UiDispatch.Run(_dispatchToUi, () =>
            {
                RebuildRows(LoadInstalledAsync(), _availableReleases);
                AppendLog(LocalizationManager.T("PlatformUpdate.Status.Installed"));
            });
        }
        catch (Exception ex)
        {
            AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// «Удалить старые версии…» (этап 0.3.9.215, issue #334, доработка по комментарию
    /// автора): показывает в диалоге ВСЕ установленные версии с признаками
    /// (<see cref="OldVersionCleaner.SelectDeletionEntries"/> — «новейшая», «используется
    /// базами», «используется запущенными процессами»), ничего не отфильтровывая —
    /// пользователь сам решает, что считать старым. «Нет версий для удаления» — только
    /// когда платформа 1С вообще не установлена. Защита от глупостей: по умолчанию
    /// отмечены только версии без признаков риска; при попытке удалить новейшую или
    /// используемую версию — отдельное предупреждение с подтверждением
    /// (<see cref="OldVersionCleaner.BuildDeletionConfirmations"/>). Windows: удаляет
    /// каталоги выбранных версий последовательно через инжектируемый делегат
    /// (<c>PlatformInstaller.DeleteVersionDirectoryAsync</c>) и перестраивает список.
    /// Linux: показывает команды удаления (<c>PlatformInstaller.BuildSudoUninstallCommand</c>)
    /// в журнале и копирует их в буфер обмена (делегат). Результат — уведомление
    /// (<see cref="NotificationEvent.Update"/>, ключ «Notify.PlatformUpdateRemoved»).
    /// Отмена диалога — no-op с записью в журнал.
    /// </summary>
    public async Task RemoveOldVersionsAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            IReadOnlyList<PlatformVersionInfo> installed;
            try
            {
                installed = _loadInstalledVersionInfos() ?? Array.Empty<PlatformVersionInfo>();
            }
            catch (Exception ex)
            {
                installed = Array.Empty<PlatformVersionInfo>();
                AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}");
            }

            IReadOnlyList<Infobase> bases;
            try
            {
                bases = _infobaseRepository.Load() ?? new List<Infobase>();
            }
            catch (Exception ex)
            {
                bases = new List<Infobase>();
                AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}");
            }

            IReadOnlyList<string> runningBinPaths;
            try
            {
                runningBinPaths = _loadRunningBinPaths() ?? Array.Empty<string>();
            }
            catch
            {
                runningBinPaths = Array.Empty<string>();
            }

            // «Нет версий для удаления» — только когда на компьютере вообще не
            // установлено ни одной платформы 1С (issue #334, комментарий автора).
            if (installed.Count == 0)
            {
                AppendLog(LocalizationManager.T("PlatformUpdate.RemoveNothing"));
                _appLogger?.Info("Обновление платформы: нет установленных версий платформы 1С");
                // issue #334: результат операции должен быть ВИДЕН пользователю —
                // информационное уведомление вместо «молчаливой» строки в журнале.
                _notify(
                    LocalizationManager.T("PlatformUpdate.WindowTitle"),
                    LocalizationManager.T("PlatformUpdate.RemoveNothing"),
                    NotificationKind.Info,
                    NotificationEvent.Update);
                return;
            }

            // issue #334: ПОЛНЫЙ список версий с признаками риска (новейшая, используется
            // базами/процессами) — ничего не фильтруется, решает пользователь.
            var entries = OldVersionCleaner.SelectDeletionEntries(installed, bases, runningBinPaths);
            _appLogger?.Info(
                $"Обновление платформы: установлено {entries.Count} версий, "
                + $"с признаками риска — {entries.Count(e => e.HasRiskMarkers)}");

            // Диалог со списком всех версий, где пользователь выбирает, что удалить
            // (окна); без делегата (тесты/окружение без UI) — общий вопрос на список
            // версий без признаков риска.
            IReadOnlyList<PlatformVersionInfo> selected;
            if (_chooseVersionsToDelete is not null)
            {
                var chosen = _chooseVersionsToDelete(entries);
                if (chosen is null || chosen.Count == 0)
                {
                    AppendLog(LocalizationManager.T("PlatformUpdate.Error.Cancelled"));
                    _appLogger?.Info("Обновление платформы: удаление старых версий отменено пользователем");
                    return;
                }

                selected = chosen;
            }
            else
            {
                selected = entries
                    .Where(e => e.IsCheckedByDefault)
                    .Select(e => e.Version)
                    .ToList();
                if (selected.Count == 0)
                {
                    AppendLog(LocalizationManager.T("PlatformUpdate.Error.Cancelled"));
                    _appLogger?.Info("Обновление платформы: нет версий без признаков риска — удаление не предложено");
                    return;
                }

                var listText = string.Join("\n", selected.Select(c => $"• {c.Display}"));
                var message = string.Format(
                    LocalizationManager.T("PlatformUpdate.Confirm.RemoveMessage"), listText);
                var confirmed = _confirmDialog(LocalizationManager.T("PlatformUpdate.Confirm.RemoveTitle"), message);
                if (!confirmed)
                {
                    AppendLog(LocalizationManager.T("PlatformUpdate.Error.Cancelled"));
                    _appLogger?.Info("Обновление платформы: удаление старых версий отменено пользователем");
                    return;
                }
            }

            // issue #334: защита от глупостей — при удалении новейшей/используемой
            // версии отдельное предупреждение с подтверждением (решение за пользователем).
            var warnings = OldVersionCleaner.BuildDeletionConfirmations(entries, selected);
            if (warnings.Count > 0)
            {
                var lines = warnings.Select(FormatCleanupWarning).ToList();
                foreach (var line in lines)
                    AppendLog(line);
                // issue #334: детали риска (имена баз, пути процессов) — в файловый
                // журнал в машинном виде, независимо от локализации.
                _appLogger?.Warn("Обновление платформы: предупреждения удаления спорных версий — "
                    + string.Join(" | ", warnings.Select(w =>
                        $"{w.VersionDisplay}: {w.Kind} {string.Join("/", w.Details)}")));
                var riskMessage = string.Format(
                    LocalizationManager.T("PlatformUpdate.Confirm.RemoveRiskMessage"),
                    string.Join("\n", lines));
                var riskConfirmed = _confirmDialog(
                    LocalizationManager.T("PlatformUpdate.Confirm.RemoveRiskTitle"), riskMessage);
                AppendLog(riskConfirmed
                    ? LocalizationManager.T("PlatformUpdate.Preflight.Continue")
                    : LocalizationManager.T("PlatformUpdate.Error.Cancelled"));
                if (!riskConfirmed)
                {
                    _appLogger?.Info("Обновление платформы: удаление старых версий отменено пользователем");
                    return;
                }
            }

            // Linux-ветка: команды sudo в журнал + копирование в буфер (без удаления из GUI).
            if (!_useWindowsDelete)
            {
                foreach (var candidate in selected)
                {
                    var command = _buildUninstallCommand(OldVersionCleaner.CleanVersion(candidate.Display));
                    AppendLog(command);
                    _copyToClipboard(command);
                }

                AppendLog(LocalizationManager.T("PlatformUpdate.Linux.Copied"));
                _appLogger?.Info(
                    $"Обновление платформы: показаны команды удаления для {selected.Count} версий");
                NotifyResult(string.Format(
                    LocalizationManager.T("Notify.PlatformUpdateRemoved"),
                    string.Join(", ", selected.Select(c => c.Display))));
                return;
            }

            // Windows-ветка: последовательное удаление каталогов выбранных версий.
            var removed = new List<string>();
            var failedCount = 0;
            var deleteLog = new Progress<string>(AppendLog);
            foreach (var candidate in selected)
            {
                AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Progress.Install"), candidate.Display));
                _appLogger?.Info($"Обновление платформы: удаление каталога версии {candidate.Display}");

                var result = await _deleteVersionDirectory!(candidate, deleteLog, CancellationToken.None)
                    .ConfigureAwait(false);
                if (result.Success)
                {
                    removed.Add(candidate.Display);
                    AppendLog(string.Format(LocalizationManager.T("PlatformUpdate.Progress.Done"), candidate.Display));
                }
                else
                {
                    failedCount++;
                    var errorText = string.IsNullOrWhiteSpace(result.ErrorKey)
                        ? LocalizationManager.T("PlatformUpdate.Error.DeleteFailed")
                        : LocalizationManager.T(result.ErrorKey);
                    AppendLog(errorText);
                    _appLogger?.Error($"Обновление платформы: {errorText} — {candidate.Display}");
                }
            }

            if (removed.Count > 0)
            {
                _appLogger?.Info("Обновление платформы: перечитывание установленных версий после удаления");
                // RefreshInstalledAsync маршалит перестройку Rows в UI-поток сам
                // (issue #334: продолжение после ConfigureAwait(false) — фоновый поток).
                await RefreshInstalledAsync().ConfigureAwait(false);
            }

            if (failedCount > 0)
            {
                NotifyError(string.Format(
                    LocalizationManager.T("Notify.PlatformUpdateError"),
                    LocalizationManager.T("PlatformUpdate.Error.DeleteFailed")));
            }
            else if (removed.Count > 0)
            {
                NotifyResult(string.Format(
                    LocalizationManager.T("Notify.PlatformUpdateRemoved"),
                    string.Join(", ", removed)));
            }
        }
        catch (Exception ex)
        {
            AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}");
            NotifyError(string.Format(
                LocalizationManager.T("Notify.PlatformUpdateError"),
                $"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Пишет в журнал список совместимых баз выбранной строки
    /// (первые 5 имён + счётчик остальных) при выборе версии в окне.</summary>
    private void LogCompatibleBases(PlatformUpdateRowViewModel? row)
    {
        if (row is null || row.CompatibleBaseNames.Count == 0)
            return;

        var total = row.CompatibleBaseNames.Count;
        var names = string.Join(", ", row.CompatibleBaseNames.Take(5));
        var text = total <= 5
            ? $"{row.Version}: {names}"
            : $"{row.Version}: {names} … ещё {total - 5}";
        AppendLog(text);
    }

    /// <summary>Лениво подгружает файлы дистрибутива релиза строки
    /// (<see cref="IPlatformUpdateService.LoadReleaseFilesAsync"/>).</summary>
    /// <returns>True — файлы готовы (или уже были загружены).</returns>
    private async Task<bool> EnsureReleaseFilesAsync(PlatformUpdateRowViewModel row)
    {
        if (row.Release is null)
        {
            AppendLog(LocalizationManager.T("PlatformUpdate.Error.NotFound"));
            return false;
        }

        if (row.Release.Files.Count > 0)
            return true;

        var result = await _service.LoadReleaseFilesAsync(row.Release).ConfigureAwait(false);
        if (result.Status != PortalFetchStatus.Ok)
        {
            AppendLog(LocalizationManager.T(result.ErrorKey));
            if (IsAuthIssue(result.Status))
                AppendLog(LocalizationManager.T("PlatformUpdate.AuthAdvice"));
            return false;
        }

        // issue #330 (комментарий 7OH): страница version_files получена, но файлов
        // не распознано — операция не продолжается с «пустым» выбором дистрибутива,
        // пользователь получает понятное сообщение.
        if (row.Release.Files.Count == 0)
        {
            AppendLog(string.Format(
                LocalizationManager.T("PlatformDownload.Error.NoFiles"), row.Version));
            return false;
        }

        row.NotifyFilesChanged();
        return true;
    }

    /// <summary>
    /// Разрешает файл дистрибутива для выбранной строки (issue #334): строит варианты
    /// для текущей ОС через <see cref="PlatformDistributionPicker.BuildOptions"/>; при
    /// нескольких вариантах показывает диалог выбора (делегат, реализация — в окнах:
    /// окна сами маршалируют показ в UI-поток); при одном варианте или отсутствии
    /// делегата — рекомендуемый. null — файлов нет.
    /// </summary>
    private Task<PlatformReleaseFile?> ResolvePickedFileAsync(PlatformUpdateRowViewModel row)
    {
        var files = row.Release?.Files ?? (IReadOnlyList<PlatformReleaseFile>)Array.Empty<PlatformReleaseFile>();
        var options = PlatformDistributionPicker.BuildOptions(
            files, OperatingSystem.IsWindows(), Environment.Is64BitOperatingSystem);

        // issue #334: ПОЛНАЯ диагностика выбора дистрибутива в журнал окна И файловый
        // журнал — какие файлы есть у релиза и почему вариант не выбран. Ранее при
        // пустом списке вариантов пользователь получал вводящее в заблуждение
        // «setup.exe не найден в архиве», а файловый журнал оставался пустым.
        var fileListing = files.Count == 0
            ? "—"
            : string.Join("; ", files.Select(DescribeDistributionFile));
        AppendLog(string.Format(
            LocalizationManager.T("PlatformUpdate.Diag.DistributionFiles"), fileListing));
        _appLogger?.Info(
            $"Обновление платформы {row.Version}: файлы релиза — {fileListing}; "
            + $"подходящих вариантов для текущей ОС — {options.Count}");

        if (options.Count == 0)
        {
            // issue #334: вместо «setup.exe не найден в архиве» — точное объяснение:
            // в каталоге версии нет дистрибутива для текущей ОС, показан список файлов.
            var message = string.Format(
                LocalizationManager.T("PlatformUpdate.Diag.NoDistributions"), row.Version, fileListing);
            AppendLog(message);
            _appLogger?.Warn(
                $"Обновление платформы {row.Version}: нет дистрибутива для текущей ОС "
                + $"(файлы релиза: {fileListing})");
            NotifyError(string.Format(LocalizationManager.T("Notify.PlatformUpdateError"), message));
            return Task.FromResult<PlatformReleaseFile?>(null);
        }

        if (options.Count == 1 || _chooseDistribution is null)
            return Task.FromResult<PlatformReleaseFile?>(options[0].File);

        var chosen = _chooseDistribution(options);
        if (chosen is null)
        {
            // issue #334 п.2 (0.3.12.2): отмена выбора дистрибутива прерывает операцию —
            // раньше отмена подменялась «рекомендуемым» файлом, поток доходил до диалога
            // сохранения и после «Отмены» спрашивал «куда скачать».
            AppendLog(LocalizationManager.T("Updates.Distribution.Cancelled"));
            _appLogger?.Info(
                $"Обновление платформы {row.Version}: выбор дистрибутива отменён пользователем");
            return Task.FromResult<PlatformReleaseFile?>(null);
        }

        return Task.FromResult<PlatformReleaseFile?>(chosen.File);
    }

    /// <summary>Краткое описание файла дистрибутива для журнала: имя, размер, разрядность
    /// («8.3.27.2214_x64.zip (1,2 ГБ, x64)»).</summary>
    private static string DescribeDistributionFile(PlatformReleaseFile file)
    {
        var parts = new List<string>();
        if (file.SizeBytes > 0)
            parts.Add($"{file.SizeBytes} байт");
        if (!string.IsNullOrWhiteSpace(file.Architecture))
            parts.Add(file.Architecture);
        var suffix = parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
        return file.FileName + suffix;
    }

    /// <summary>Пишет фактический размер скачанного файла в журнал окна и файловый журнал
    /// (issue #334: скачивание должно оставлять след в обоих журналах).</summary>
    private void LogDownloadedSize(string downloadedPath)
    {
        long bytes = 0;
        try
        {
            if (File.Exists(downloadedPath))
                bytes = new FileInfo(downloadedPath).Length;
        }
        catch
        {
            // Файл недоступен для чтения размера — не роняем операцию, пишем 0.
        }

        AppendLog(string.Format(
            LocalizationManager.T("PlatformUpdate.Diag.DownloadedSize"), bytes));
        _appLogger?.Info($"Обновление платформы: размер скачанного файла {downloadedPath} — {bytes} байт");
    }

    /// <summary>Локализованный текст предупреждения удаления спорной версии
    /// (issue #334): новейшая / используется базами (с именами) / процессами 1С.</summary>
    private string FormatCleanupWarning(OldVersionCleanupWarning warning)
    {
        return warning.Kind switch
        {
            OldVersionCleanupRiskKind.Newest => string.Format(
                LocalizationManager.T("PlatformUpdate.Confirm.RemoveRiskNewest"), warning.VersionDisplay),
            OldVersionCleanupRiskKind.UsedByBases => string.Format(
                LocalizationManager.T("PlatformUpdate.Confirm.RemoveRiskBases"),
                warning.VersionDisplay, string.Join(", ", warning.Details)),
            _ => string.Format(
                LocalizationManager.T("PlatformUpdate.Confirm.RemoveRiskProcesses"), warning.VersionDisplay),
        };
    }

    /// <summary>Перестраивает список строк: сопоставление установленных и доступных
    /// версий (<see cref="PlatformUpdateMatcher.Merge"/>) + число совместимых баз.</summary>
    private void RebuildRows(IReadOnlyList<string> installed, IReadOnlyList<PlatformRelease> available)
    {
        IReadOnlyList<Infobase> bases;
        try
        {
            bases = _infobaseRepository.Load() ?? new List<Infobase>();
        }
        catch (Exception ex)
        {
            bases = new List<Infobase>();
            AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.Network")}: {ex.Message}");
        }

        var matches = PlatformUpdateMatcher.Merge(installed, available);
        SelectedRow = null;
        Rows.Clear();
        foreach (var match in matches)
        {
            var release = available.FirstOrDefault(r =>
                string.Equals(r.Version, match.Version, StringComparison.OrdinalIgnoreCase));
            var compatibleNames = PlatformUpdateMatcher.GetCompatibleBaseNames(match.Version, bases);
            var row = new PlatformUpdateRowViewModel(match, release, files => _service.PickDistribution(files))
            {
                CompatibleBases = compatibleNames.Count,
                CompatibleBaseNames = compatibleNames,
            };
            Rows.Add(row);
        }
    }

    /// <summary>
    /// Собирает замечания перед установкой через инжектируемые делегаты
    /// (процессы 1С, права администратора, свободное место на целевом диске,
    /// подпись файла) и формирует список <see cref="PlatformInstallPreflight.Check"/>.
    /// Любой сбой отдельного источника тихо деградирует в «проблем нет» —
    /// проверка не должна ронять установку из-за недоступности WMI/прав.
    /// </summary>
    private IReadOnlyList<PlatformInstallWarning> RunPreflight(string installerPath, string targetDir, long distributionSize)
    {
        IReadOnlyList<string> processes;
        try
        {
            processes = _loadRunningProcesses() ?? Array.Empty<string>();
        }
        catch
        {
            processes = Array.Empty<string>();
        }

        bool isAdmin;
        try
        {
            isAdmin = _isAdministrator();
        }
        catch
        {
            isAdmin = true;
        }

        long? freeBytes;
        try
        {
            freeBytes = _getFreeBytes(targetDir);
        }
        catch
        {
            freeBytes = null;
        }

        bool isSigned;
        try
        {
            isSigned = _hasValidSignature(installerPath);
        }
        catch
        {
            isSigned = true;
        }

        return PlatformInstallPreflight.Check(processes, isAdmin, freeBytes, distributionSize, isSigned);
    }

    /// <summary>Показывает уведомление о результате установки: при частичном успехе
    /// (были предупреждения) — Warning, иначе — Success. Категория события — Update.</summary>
    private void NotifyResult(string summary)
    {
        _notify(
            LocalizationManager.T("PlatformUpdate.WindowTitle"),
            summary,
            _installHadWarnings ? NotificationKind.Warning : NotificationKind.Success,
            NotificationEvent.Update);
    }

    /// <summary>Показывает уведомление об ошибке операции (категория Update, вид Error).</summary>
    private void NotifyError(string summary)
    {
        _notify(
            LocalizationManager.T("PlatformUpdate.WindowTitle"),
            summary,
            NotificationKind.Error,
            NotificationEvent.Update);
    }

    /// <summary>Обновляет доступность всех команд после изменения состояния.</summary>
    private void RefreshCommands()
    {
        CheckCommand.RaiseCanExecuteChanged();
        DownloadAndInstallCommand.RaiseCanExecuteChanged();
        DownloadOnlyCommand.RaiseCanExecuteChanged();
        RemoveOldVersionsCommand.RaiseCanExecuteChanged();
    }
}