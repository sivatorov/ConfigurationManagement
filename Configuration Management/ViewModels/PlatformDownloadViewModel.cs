using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Строка списка версий окна «Скачивание версии платформы 1С»: версия каталога
/// <c>releases.1c.ru/project/Platform83</c> (файлы дистрибутива подгружаются лениво).
/// </summary>
public sealed class PlatformDownloadRowViewModel
{
    /// <summary>Релиз каталога (версия + список файлов).</summary>
    public PlatformRelease Release { get; }

    /// <summary>Номер версии платформы.</summary>
    public string Version => Release.Version;

    /// <param name="release">Релиз каталога платформы.</param>
    public PlatformDownloadRowViewModel(PlatformRelease release)
    {
        Release = release ?? throw new ArgumentNullException(nameof(release));
    }
}

/// <summary>
/// ViewModel окна «Скачивание версии платформы 1С» (issue #330): выбор версии из
/// дерева каталога <c>releases.1c.ru</c> (поиск и «свернуть/развернуть все» —
/// комментарий 7OH от 2026-10-08), разрядности (32/64) и типа дистрибутива, скачивание
/// файла с прогрессом в выбранную папку. Установщика в окне нет: файл скачивается
/// архивом, после скачивания пользователь сам открывает папку с файлом.
/// Авторизация портала — через учётную запись ИТС из справочника (#333, выбранная/
/// основная), которую резолвит инжектируемый делегат. Сетевые операции выполняются
/// через <see cref="IPlatformUpdateService"/> и делегат загрузки — класс остаётся
/// чистым и покрывается тестами на fake-сервисах без сети и UI.
/// </summary>
public sealed class PlatformDownloadViewModel : ViewModelBase
{
    private readonly IPlatformUpdateService _service;
    private readonly Func<ItsAccount?> _resolveAccount;
    private readonly Func<string, string, IProgress<double>?, CancellationToken, Task<string?>> _downloadDistribution;
    private readonly Func<string, bool> _openFolder;
    private readonly Func<string?>? _chooseDirectory;
    private readonly Action<string, string, NotificationKind, NotificationEvent>? _notify;
    private readonly Services.IAppLogger? _appLogger;
    private readonly bool _isWindows;

    /// <summary>
    /// Маршаллер изменения UI-состояния в поток Dispatcher (issues #330/#334): обновление
    /// коллекций (<see cref="Releases"/>) и связанных свойств выполняется ТОЛЬКО в UI-потоке,
    /// иначе WPF CollectionView бросает NotSupportedException. null — прямой вызов
    /// (юнит-тесты); окна передают платформенный маршаллер через <see cref="UiDispatch"/>.
    /// </summary>
    private readonly Action<Action>? _dispatchToUi;

    private readonly StringBuilder _log = new();
    private IReadOnlyList<PlatformCatalogNode> _versionTreeRoots = Array.Empty<PlatformCatalogNode>();
    private string _versionSearchQuery = string.Empty;
    private PlatformDownloadRowViewModel? _selectedRelease;
    private bool _isBusy;
    private double _progress;
    private bool _is64Bit;
    private PlatformDownloadType _downloadType;
    private IReadOnlyList<PlatformDownloadType> _availableDownloadTypes = new[] { PlatformDownloadType.Auto };
    private PlatformCatalogNode? _selectedVersionNode;
    private PlatformDistributionOption? _selectedDistribution;
    private IReadOnlyList<PlatformDistributionOption> _distributionOptions = Array.Empty<PlatformDistributionOption>();
    private string _targetDirectory = string.Empty;
    private string _accountName = string.Empty;
    private bool _hasAccount;
    private PlatformReleaseFile? _pickedFile;
    private string _downloadedPath = string.Empty;
    private string _resultText = string.Empty;

    /// <summary>Список версий каталога платформы (по убыванию).</summary>
    public ObservableCollection<PlatformDownloadRowViewModel> Releases { get; } = new();

    /// <summary>
    /// Дерево версий каталога (issue #330): линии «8.3/8.5» → группы сборок «8.3.27»
    /// → полные версии «8.3.27.2214», сортировка по убыванию. Строится из объединённого
    /// каталога Platform83 + Platform85.
    /// </summary>
    public ObservableCollection<PlatformCatalogNode> VersionTree { get; } = new();

    /// <summary>Поисковый запрос для дерева версий (issue #330, комментарий 7OH): по мере
    /// ввода дерево фильтруется — остаются версии, содержащие подстроку (без учёта
    /// регистра), и линии/группы, в которых они есть. Пустой запрос возвращает полное
    /// дерево. Аналог поиска в обозревателе метаданных, но без debounce: дерево в памяти
    /// и фильтрация мгновенна.</summary>
    public string VersionSearchQuery
    {
        get => _versionSearchQuery;
        set
        {
            if (!SetProperty(ref _versionSearchQuery, value ?? string.Empty))
                return;
            ApplyVersionFilter();
        }
    }

    /// <summary>Выбранный узел дерева версий (лист). При выборе подгружает файлы релиза.</summary>
    public PlatformCatalogNode? SelectedVersionNode
    {
        get => _selectedVersionNode;
        set
        {
            if (!SetProperty(ref _selectedVersionNode, value))
                return;
            var release = value?.Release;
            SelectedRelease = release is null
                ? null
                : Releases.FirstOrDefault(r =>
                    string.Equals(r.Version, release.Version, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Выбранная версия. При выборе лениво подгружаются файлы релиза и
    /// пересчитывается файл дистрибутива под разрядность/тип.</summary>
    public PlatformDownloadRowViewModel? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (SetProperty(ref _selectedRelease, value))
            {
                PickedFile = null;
                RefreshCommands();
                _ = LoadReleaseFilesAsync(value);
            }
        }
    }

    /// <summary>Варианты дистрибутива выбранной версии для целевой ОС (issue #330):
    /// полный/тонкий клиент Windows x64/x86 или пакеты/архив Linux; рекомендуемый помечен.</summary>
    public IReadOnlyList<PlatformDistributionOption> DistributionOptions
    {
        get => _distributionOptions;
        private set => SetProperty(ref _distributionOptions, value ?? Array.Empty<PlatformDistributionOption>());
    }

    /// <summary>Выбранный пользователем вариант дистрибутива (определяет
    /// <see cref="PickedFile"/> и доступность команды «Скачать»).</summary>
    public PlatformDistributionOption? SelectedDistribution
    {
        get => _selectedDistribution;
        set
        {
            if (SetProperty(ref _selectedDistribution, value))
                PickedFile = value?.File;
        }
    }

    /// <summary>Выполняется ли сетевая операция (блокирует команды).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                RefreshCommands();
        }
    }

    /// <summary>Прогресс загрузки (0..1).</summary>
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, Math.Clamp(value, 0, 1));
    }

    /// <summary>True — целевая разрядность x64; false — x86.</summary>
    public bool Is64Bit
    {
        get => _is64Bit;
        set
        {
            if (SetProperty(ref _is64Bit, value))
                RepickFile();
        }
    }

    /// <summary>Тип дистрибутива (полный/тонкий клиент, пакет, архив, авто).</summary>
    public PlatformDownloadType DownloadType
    {
        get => _downloadType;
        set
        {
            if (SetProperty(ref _downloadType, value))
                RepickFile();
        }
    }

    /// <summary>Доступные типы дистрибутива для выбранной версии (зависит от ОС и файлов).</summary>
    public IReadOnlyList<PlatformDownloadType> AvailableDownloadTypes
    {
        get => _availableDownloadTypes;
        private set => SetProperty(ref _availableDownloadTypes, value);
    }

    /// <summary>Варианты типов дистрибутива («локализованное имя → тип») для комбобокса.</summary>
    public IReadOnlyList<DownloadTypeOption> DownloadTypeOptions { get; private set; }
        = new[] { new DownloadTypeOption(
            LocalizationManager.T(PlatformDistributionPicker.TypeLocalizationKey(PlatformDownloadType.Auto)),
            PlatformDownloadType.Auto) };

    /// <summary>Каталог сохранения дистрибутива (создаётся при скачивании).</summary>
    public string TargetDirectory
    {
        get => _targetDirectory;
        set => SetProperty(ref _targetDirectory, value ?? string.Empty);
    }

    /// <summary>Отображаемое имя учётной записи ИТС, которой будет выполняться авторизация.</summary>
    public string AccountName
    {
        get => _accountName;
        private set => SetProperty(ref _accountName, value ?? string.Empty);
    }

    /// <summary>True — задана учётная запись ИТС (иначе запросы уйдут без авторизации).</summary>
    public bool HasAccount
    {
        get => _hasAccount;
        private set => SetProperty(ref _hasAccount, value);
    }

    /// <summary>Выбранный файл дистрибутива (после ленивой подгрузки файлов версии).</summary>
    public PlatformReleaseFile? PickedFile
    {
        get => _pickedFile;
        private set
        {
            if (SetProperty(ref _pickedFile, value))
            {
                OnPropertyChanged(nameof(FileInfoText));
                RefreshCommands();
            }
        }
    }

    /// <summary>Описание выбранного файла («имя (размер)») либо ключ «файл не выбран».</summary>
    public string FileInfoText => PickedFile is null
        ? LocalizationManager.T("PlatformDownload.NoFile")
        : $"{PickedFile.FileName} ({FormatSize(PickedFile.SizeBytes)})";

    /// <summary>Полный путь скачанного дистрибутива (пусто — ещё не скачано).</summary>
    public string DownloadedPath
    {
        get => _downloadedPath;
        private set
        {
            if (SetProperty(ref _downloadedPath, value))
                OnPropertyChanged(nameof(HasDownloaded));
        }
    }

    /// <summary>True — скачивание завершено (доступна кнопка «Открыть папку»).</summary>
    public bool HasDownloaded => !string.IsNullOrWhiteSpace(DownloadedPath);

    /// <summary>Итоговое сообщение после скачивания: что скачано, куда, что делать дальше.</summary>
    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value ?? string.Empty);
    }

    /// <summary>Текст журнала окна.</summary>
    public string LogText => _log.ToString();

    /// <summary>Команда «Проверить каталог» (получение списка версий с портала).</summary>
    public RelayCommand LoadCatalogCommand { get; }

    /// <summary>Команда «Скачать».</summary>
    public RelayCommand DownloadCommand { get; }

    /// <summary>Команда «Открыть папку».</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Команда «Выбрать папку…».</summary>
    public RelayCommand ChooseDirectoryCommand { get; }

    /// <summary>Команда «Развернуть все» для дерева версий (issue #330, комментарий 7OH):
    /// раскрывает все видимые узлы дерева.</summary>
    public RelayCommand ExpandAllCommand { get; }

    /// <summary>Команда «Свернуть все» для дерева версий (issue #330, комментарий 7OH):
    /// сворачивает все видимые узлы дерева.</summary>
    public RelayCommand CollapseAllCommand { get; }

    /// <param name="service">Сервис каталога версий платформы (портал releases.1c.ru).</param>
    /// <param name="resolveAccount">Резолвит учётную запись ИТС для авторизации (выбранная/основная,
    /// issue #333); null — без авторизации.</param>
    /// <param name="downloadDistribution">Загружает файл дистрибутива по прямой ссылке с прогрессом;
    /// возвращает путь сохранённого файла или null при ошибке/отмене.</param>
    /// <param name="openFolder">Открывает папку с файлом (Windows — проводник с выделением, Linux —
    /// файловый менеджер); true — действие инициировано.</param>
    /// <param name="chooseDirectory">Диалог выбора папки сохранения (null при отмене); опционально.</param>
    /// <param name="is64Bit">Целевая разрядность по умолчанию (обычно — разрядность ОС).</param>
    /// <param name="defaultDirectory">Каталог сохранения по умолчанию.</param>
    /// <param name="isWindows">True — целевая ОС Windows (выбор типа дистрибутива по умолчанию).</param>
    /// <param name="notify">Уведомление о результате (опционально).</param>
    /// <param name="appLogger">Журнал приложения (опционально).</param>
    public PlatformDownloadViewModel(
        IPlatformUpdateService service,
        Func<ItsAccount?> resolveAccount,
        Func<string, string, IProgress<double>?, CancellationToken, Task<string?>> downloadDistribution,
        Func<string, bool> openFolder,
        Func<string?>? chooseDirectory = null,
        bool is64Bit = true,
        string? defaultDirectory = null,
        bool isWindows = true,
        Action<string, string, NotificationKind, NotificationEvent>? notify = null,
        Services.IAppLogger? appLogger = null,
        Action<Action>? dispatchToUi = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatchToUi = dispatchToUi;
        _resolveAccount = resolveAccount ?? (() => null);
        _downloadDistribution = downloadDistribution ?? throw new ArgumentNullException(nameof(downloadDistribution));
        _openFolder = openFolder ?? (_ => false);
        _chooseDirectory = chooseDirectory;
        _notify = notify;
        _appLogger = appLogger;
        _isWindows = isWindows;
        _is64Bit = is64Bit;
        _downloadType = PlatformDownloadType.Auto;
        _targetDirectory = string.IsNullOrWhiteSpace(defaultDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : defaultDirectory.Trim();

        LoadCatalogCommand = new RelayCommand(async () => await LoadCatalogAsync(), () => !IsBusy);
        DownloadCommand = new RelayCommand(async () => await DownloadAsync(), () => CanDownload());
        OpenFolderCommand = new RelayCommand(OpenFolder, () => HasDownloaded);
        ChooseDirectoryCommand = new RelayCommand(ChooseDirectory, () => !IsBusy);
        ExpandAllCommand = new RelayCommand(() => SetAllNodesExpanded(true), () => VersionTree.Count > 0);
        CollapseAllCommand = new RelayCommand(() => SetAllNodesExpanded(false), () => VersionTree.Count > 0);

        RefreshAccount();
        AppendLog(string.Format(LocalizationManager.T("PlatformDownload.Status.Directory"), TargetDirectory));
    }

    /// <summary>Доступна ли загрузка (нет активной операции, выбраны версия и файл).</summary>
    private bool CanDownload()
        => !IsBusy && SelectedRelease is not null && PickedFile is not null;

    /// <summary>Обновляет доступность команд после изменения состояния.</summary>
    private void RefreshCommands()
    {
        DownloadCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        LoadCatalogCommand.RaiseCanExecuteChanged();
        ChooseDirectoryCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Добавляет строку в журнал и уведомляет UI.</summary>
    public void AppendLog(string message)
    {
        _log.AppendLine(message ?? string.Empty);
        OnPropertyChanged(nameof(LogText));
    }

    /// <summary>Показывает учётную запись ИТС, которой будет выполняться авторизация
    /// (выбранная/основная из справочника #333).</summary>
    private void RefreshAccount()
    {
        var account = _resolveAccount();
        if (account is not null && !string.IsNullOrWhiteSpace(account.Login))
        {
            AccountName = account.ToString()!;
            HasAccount = true;
        }
        else
        {
            AccountName = string.Empty;
            HasAccount = false;
        }
    }

    /// <summary>True — статус ошибки связан с авторизацией на портале 1С (требуется вход,
    /// вход не подтверждён, исчерпан лимит попыток, форма входа изменилась): для таких
    /// ошибок в журнал окна добавляется расширенный совет <c>PlatformUpdate.AuthAdvice</c>
    /// (issue #334/#330/#323; FormUnavailable — issue #323/#330/#334, третья итерация).</summary>
    private static bool IsAuthIssue(PortalFetchStatus status)
        => status is PortalFetchStatus.AuthRequired or PortalFetchStatus.AuthFailed
            or PortalFetchStatus.LoginLimitReached
            or PortalFetchStatus.FormUnavailable;

    /// <summary>Получает список версий платформы с портала и заполняет список. Ошибки
    /// каталога (авторизация/сеть/404) пишутся в журнал ключом локализации.</summary>
    public async Task LoadCatalogAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Progress = 0;
        DownloadedPath = string.Empty;
        ResultText = string.Empty;
        AppendLog(LocalizationManager.T("PlatformDownload.Status.Checking"));
        _appLogger?.Info("Скачивание платформы: получение каталога версий с портала 1С");
        try
        {
            // Объединённый каталог всех линий платформы (Platform83 + Platform85)
            // с allUpdates=true — полный список версий (issue #330).
            var result = await _service.GetAllAvailableReleasesAsync().ConfigureAwait(false);
            if (result.Status != PortalFetchStatus.Ok)
            {
                var errorKey = string.IsNullOrWhiteSpace(result.ErrorKey)
                    ? PlatformUpdateService.ErrorNetwork
                    : result.ErrorKey;
                AppendLog(LocalizationManager.T(errorKey));
                // Расширенный совет при проблемах авторизации портала: что проверить и когда
                // повторить (issue #334/#330/#323). Базовые тексты ошибок — краткие, детали здесь.
                if (IsAuthIssue(result.Status))
                    AppendLog(LocalizationManager.T("PlatformUpdate.AuthAdvice"));
                _appLogger?.Warn($"Скачивание платформы: каталог не получен — {errorKey}");
                return;
            }

            // issue #330: продолжение после ConfigureAwait(false) идёт на пуле потоков,
            // а заполнение ObservableCollection Releases (WPF CollectionView DataGrid)
            // бросает NotSupportedException. Все изменения коллекции и зависимых свойств
            // (SelectedRelease, AvailableDownloadTypes и пр.) — только в UI-потоке.
            UiDispatch.Run(_dispatchToUi, () =>
            {
                Releases.Clear();
                foreach (var release in result.Releases)
                    Releases.Add(new PlatformDownloadRowViewModel(release));

                // Дерево версий «8.x \ 8.x.yy \ полная версия» (паттерн выбора платформы).
                // Полное дерево хранится отдельно; в VersionTree — результат фильтра поиска
                // (пустой запрос — полное дерево, issue #330).
                _versionTreeRoots = PlatformVersionTreeBuilder.BuildFromCatalog(result.Releases);
                ApplyVersionFilter();

                if (Releases.Count == 0)
                {
                    AppendLog(LocalizationManager.T("PlatformUpdate.Error.NotFound"));
                    return;
                }

                AppendLog(string.Format(
                    LocalizationManager.T("PlatformDownload.Status.Releases"), Releases.Count));
                SelectedRelease = Releases[0];
            });
        }
        catch (Exception ex)
        {
            AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.NetworkError")}: {ex.Message}");
            _appLogger?.Error($"Скачивание платформы: исключение при получении каталога: {ex.GetType().Name}: {ex.Message}", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Лениво подгружает файлы дистрибутива выбранной версии и пересчитывает
    /// выбранный файл под разрядность/тип.</summary>
    private async Task LoadReleaseFilesAsync(PlatformDownloadRowViewModel? row)
    {
        if (row is null)
            return;

        try
        {
            var result = await _service.LoadReleaseFilesAsync(row.Release).ConfigureAwait(false);
            if (result.Status != PortalFetchStatus.Ok)
            {
                var errorKey = string.IsNullOrWhiteSpace(result.ErrorKey)
                    ? PlatformUpdateService.ErrorNetwork
                    : result.ErrorKey;
                AppendLog(LocalizationManager.T(errorKey));
                if (IsAuthIssue(result.Status))
                    AppendLog(LocalizationManager.T("PlatformUpdate.AuthAdvice"));
                return;
            }

            // issue #330 (комментарий 7OH): страница version_files получена, но файлов
            // не распознано — молча пустой список недопустим. В журнал окна выводится
            // диагностика запроса (URL, длина ответа, число распознанных файлов), чтобы
            // пользователь мог прислать её с обратной связью.
            if (row.Release.Files.Count == 0)
            {
                AppendLog(string.Format(
                    LocalizationManager.T("PlatformDownload.Error.NoFiles"), row.Version));
                AppendLog(BuildFilesDiagnostics(result));
                _appLogger?.Warn($"Скачивание платформы: страница файлов версии {row.Version} не содержит распознанных дистрибутивов; {BuildFilesDiagnosticsRaw(result)}");
                return;
            }

            // Подтверждение загрузки в журнале окна (issue #330): пользователь видит,
            // сколько файлов распознано, даже если варианты дистрибутива не поместились
            // в комбобокс для его ОС.
            AppendLog(string.Format(
                LocalizationManager.T("PlatformDownload.Status.Files"), row.Release.Files.Count));
            _appLogger?.Info($"Скачивание платформы: файлы версии {row.Version}; {BuildFilesDiagnosticsRaw(result)}");

            // RepickFile меняет свойства, связанные с UI (AvailableDownloadTypes,
            // DownloadTypeOptions, PickedFile) — также строго в UI-потоке (issue #330).
            UiDispatch.Run(_dispatchToUi, RepickFile);
        }
        catch (Exception ex)
        {
            AppendLog($"{LocalizationManager.T("PlatformUpdate.Error.NetworkError")}: {ex.Message}");
            _appLogger?.Error($"Скачивание платформы: исключение при подгрузке файлов {row.Version}: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>Строка диагностики загрузки страницы файлов версии для журнала окна
    /// (issue #330, локализованный ключ PlatformDownload.Status.FilesDiag): URL запроса,
    /// длина ответа, число распознанных файлов. Пробел в URL заменяется на «%20»,
    /// чтобы адрес можно было скопировать целиком.</summary>
    private static string BuildFilesDiagnostics(PlatformCatalogResult result)
    {
        var url = (result.FetchedUrl ?? "—").Replace(" ", "%20");
        return string.Format(
            LocalizationManager.T("PlatformDownload.Status.FilesDiag"),
            url,
            result.BodyLength,
            result.ParsedFileCount);
    }

    /// <summary>Нелокализованная строка диагностики для журнала приложения (issue #330).</summary>
    private static string BuildFilesDiagnosticsRaw(PlatformCatalogResult result)
        => $"url={result.FetchedUrl ?? "-"}; bodyLength={result.BodyLength}; parsedFiles={result.ParsedFileCount}";

    /// <summary>Перестраивает дерево версий из сохранённого полного дерева по текущему
    /// поисковому запросу (issue #330, комментарий 7OH).</summary>
    private void ApplyVersionFilter()
    {
        VersionTree.Clear();
        foreach (var node in PlatformVersionTreeBuilder.Filter(_versionTreeRoots, VersionSearchQuery))
            VersionTree.Add(node);
        ExpandAllCommand.RaiseCanExecuteChanged();
        CollapseAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Устанавливает <see cref="PlatformCatalogNode.IsExpanded"/> у всех видимых
    /// узлов дерева (команды «Развернуть все»/«Свернуть все», issue #330).</summary>
    private void SetAllNodesExpanded(bool isExpanded)
    {
        foreach (var root in VersionTree)
            SetNodeExpanded(root, isExpanded);
    }

    private static void SetNodeExpanded(PlatformCatalogNode node, bool isExpanded)
    {
        node.IsExpanded = isExpanded;
        foreach (var child in node.Children)
            SetNodeExpanded(child, isExpanded);
    }

    /// <summary>Пересчитывает варианты дистрибутива и выбранный файл
    /// (версия + разрядность + тип → файл, issue #330).</summary>
    private void RepickFile()
    {
        var files = SelectedRelease?.Release.Files ?? (IReadOnlyList<PlatformReleaseFile>)Array.Empty<PlatformReleaseFile>();

        // Варианты для целевой ОС: реальный выбор (полный/тонкий клиент, x64/x86, пакеты)
        // вместо единственного «Авто». Рекомендуемый помечен и предвыбран по умолчанию.
        var options = PlatformDistributionPicker.BuildOptions(files, _isWindows, Is64Bit);
        DistributionOptions = options;

        var types = PlatformDistributionPicker.AvailableTypes(files, _isWindows);
        AvailableDownloadTypes = types.Count > 0 ? types : new[] { PlatformDownloadType.Auto };

        if (!types.Contains(DownloadType))
            DownloadType = PlatformDownloadType.Auto;

        DownloadTypeOptions = types
            .Select(t => new DownloadTypeOption(
                LocalizationManager.T(PlatformDistributionPicker.TypeLocalizationKey(t)), t))
            .ToList();

        // Явный тип дистрибутива (не «Авто», issue #330): файл выбирается по типу,
        // иначе комбинированный выбор типа не влиял на итоговый файл (тест
        // ThinClientType_PicksThinZip).
        if (DownloadType != PlatformDownloadType.Auto)
        {
            PickedFile = PlatformDistributionPicker.PickFile(files, Is64Bit, DownloadType, _isWindows)
                ?? PlatformDistributionPicker.PickFile(files, Is64Bit, PlatformDownloadType.Auto, _isWindows);
            SelectedDistribution = options.FirstOrDefault(o => ReferenceEquals(o.File, PickedFile))
                ?? SelectedDistribution;
            return;
        }

        // Сохраняем выбор пользователя; при смене версии/разрядности выбираем рекомендуемый.
        if (SelectedDistribution is null || !options.Any(o => ReferenceEquals(o, SelectedDistribution)))
        {
            SelectedDistribution = options.FirstOrDefault(o => o.IsRecommended) ?? options.FirstOrDefault();
        }

        PickedFile = SelectedDistribution?.File
            ?? PlatformDistributionPicker.PickFile(files, Is64Bit, DownloadType, _isWindows);
    }

    /// <summary>Скачивает выбранный дистрибутив (архив) в <see cref="TargetDirectory"/> с прогрессом.
    /// Установщика в окне нет — после скачивания пользователь сам открывает папку с файлом
    /// (требование issue #330, комментарий 7OH от 2026-10-08).</summary>
    public async Task DownloadAsync()
    {
        if (!CanDownload())
            return;

        IsBusy = true;
        Progress = 0;
        DownloadedPath = string.Empty;
        ResultText = string.Empty;
        var picked = PickedFile!;
        var version = SelectedRelease!.Version;

        if (!HasAccount)
        {
            AppendLog(LocalizationManager.T("PlatformDownload.WarnNoAccount"));
            _appLogger?.Warn("Скачивание платформы: учётная запись ИТС не задана — авторизация портала не будет выполнена");
        }

        try
        {
            var targetDir = string.IsNullOrWhiteSpace(TargetDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : TargetDirectory.Trim();
            Directory.CreateDirectory(targetDir);

            var targetPath = Path.Combine(targetDir,
                OneCUpdatesService.BuildTargetFileName(version, picked.FileName));

            AppendLog(string.Format(LocalizationManager.T("PlatformDownload.Progress.Download"),
                picked.FileName, targetDir));
            _appLogger?.Info($"Скачивание платформы: загрузка версии {version} в «{targetPath}»");

            var progress = new Progress<double>(v =>
            {
                Progress = v;
                OnPropertyChanged(nameof(LogText));
            });
            var downloaded = await _downloadDistribution(picked.Url, targetPath, progress, CancellationToken.None)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(downloaded))
            {
                AppendLog(LocalizationManager.T("PlatformUpdate.Error.NetworkError"));
                _notify?.Invoke(
                    LocalizationManager.T("PlatformDownload.WindowTitle"),
                    LocalizationManager.T("PlatformUpdate.Error.NetworkError"),
                    NotificationKind.Error,
                    NotificationEvent.Update);
                return;
            }

            Progress = 1;
            DownloadedPath = downloaded;
            ResultText = BuildResultText(downloaded);
            AppendLog(ResultText);
            _appLogger?.Info($"Скачивание платформы: дистрибутив сохранён в «{downloaded}»");
            _notify?.Invoke(
                LocalizationManager.T("PlatformDownload.WindowTitle"),
                ResultText,
                NotificationKind.Success,
                NotificationEvent.Update);
        }
        catch (Exception ex)
        {
            var errorText = $"{LocalizationManager.T("PlatformUpdate.Error.NetworkError")}: {ex.Message}";
            AppendLog(errorText);
            _appLogger?.Error($"Скачивание платформы: исключение при загрузке {version}: {ex.GetType().Name}: {ex.Message}", ex);
            _notify?.Invoke(
                LocalizationManager.T("PlatformDownload.WindowTitle"),
                errorText,
                NotificationKind.Error,
                NotificationEvent.Update);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Открывает папку со скачанным файлом (проводник/файловый менеджер).</summary>
    public void OpenFolder()
    {
        if (!HasDownloaded)
            return;

        if (_openFolder(DownloadedPath))
        {
            AppendLog(string.Format(LocalizationManager.T("PlatformDownload.Status.FolderOpened"), DownloadedPath));
        }
        else
        {
            AppendLog(string.Format(LocalizationManager.T("PlatformDownload.Error.OpenFolder"), DownloadedPath));
        }
    }

    /// <summary>Выбирает папку сохранения через инжектируемый диалог (реализация — в окне).</summary>
    public void ChooseDirectory()
    {
        var directory = _chooseDirectory?.Invoke();
        if (string.IsNullOrWhiteSpace(directory))
            return;

        TargetDirectory = directory.Trim();
        AppendLog(string.Format(LocalizationManager.T("PlatformDownload.Status.Directory"), TargetDirectory));
    }

    /// <summary>Формирует итоговое сообщение после скачивания: что скачано, куда, что делать дальше.</summary>
    private static string BuildResultText(string downloadedPath)
    {
        var fileName = Path.GetFileName(downloadedPath);
        var directory = Path.GetDirectoryName(downloadedPath) ?? string.Empty;
        return string.Format(LocalizationManager.T("PlatformDownload.Result.Summary"), fileName, directory);
    }

    /// <summary>Форматирует размер файла человекочитаемо («123,4 МБ»).</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "—";
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        if (bytes >= gb)
            return $"{bytes / (double)gb:0.#} ГБ";
        if (bytes >= mb)
            return $"{bytes / (double)mb:0.#} МБ";
        return $"{bytes / (double)kb:0.#} КБ";
    }
}

/// <summary>Вариант типа дистрибутива для комбобокса: локализованное имя + тип.</summary>
public sealed record DownloadTypeOption(string Name, PlatformDownloadType Type)
{
    /// <summary>Отображаемое имя варианта.</summary>
    public override string ToString() => Name;
}