#if WINDOWS
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management;

/// <summary>
/// Окно проверки обновлений конфигурации для выбранной информационной базы (горячая клавиша F9).
/// Показывает имя базы, текущую версию конфигурации, последнюю версию из каталога релизов 1С,
/// адрес каталога и статус проверки; подсвечивает наличие нового релиза и позволяет скачать
/// дистрибутив с индикатором прогресса. Сетевые операции выполняются в фоновом потоке.
/// </summary>
public partial class UpdateCheckWindow : Window
{
    private readonly IOneCUpdatesService _updates = AppServices.GetRequiredService<IOneCUpdatesService>();
    private readonly IInfobaseRepository _repository = AppServices.GetRequiredService<IInfobaseRepository>();
    private readonly ICustomConfigTypesStore _store = AppServices.GetRequiredService<ICustomConfigTypesStore>();
    private readonly IItsAccountsStore _itsAccounts = AppServices.GetRequiredService<IItsAccountsStore>();
    private readonly IAppLogger _logger = AppServices.GetRequiredService<IAppLogger>();
    private readonly IDialogService _dialogs = AppServices.GetRequiredService<IDialogService>();

    private readonly Infobase _infobase;
    private readonly UpdateCheckRowViewModel _row;
    private CancellationTokenSource? _cts;

    /// <summary>Активна ли ссылка каталога релизов (валидный http/https-адрес, issue #323).</summary>
    private bool _urlLinkEnabled;

    /// <param name="infobase">Информационная база, для которой выполняется проверка обновлений.</param>
    public UpdateCheckWindow(Infobase infobase)
    {
        _infobase = infobase ?? throw new ArgumentNullException(nameof(infobase));
        _row = new UpdateCheckRowViewModel(infobase, OnDownloadRow);

        InitializeComponent();
        DataContext = _row;

        BaseNameText.Text = _row.Name;
        CurrentVersionText.Text = string.IsNullOrWhiteSpace(_row.CurrentVersion)
            ? "—"
            : _row.CurrentVersion;

        _row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_row.Progress))
                UpdateProgressDisplay();
        };

        // Папка цепочки (issue #352.2): строка с путём и кнопкой «Открыть» над таблицей.
        UpdateChainFolderDisplay();

        Loaded += async (_, _) => await RunCheckAsync();
    }

    /// <summary>Запускает сетевую проверку наличия обновлений для связанной конфигурации.</summary>
    private async Task RunCheckAsync()
    {
        if (_row.IsChecking)
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _row.IsChecking = true;
        CheckButton.IsEnabled = false;
        ErrorText.Text = string.Empty;

        try
        {
            var config = FindLinkedConfig();
            // Явное связывание (UpdateConfigCode) — приоритет. Если связи нет, но свойства
            // конфигурации базы определены (вкладка «Платформа»), строим каталог релизов из них:
            // типовая конфигурация подбирается автоматически по имени (issue #323).
            if (config is null && !string.IsNullOrWhiteSpace(_infobase.ConfigurationName))
                config = ConfigTypeMatcher.FindByInfobaseName(_store.LoadAll(), _infobase.ConfigurationName);

            // Редакция по версии базы («3.1.38.92» → «3.1»), иначе первая редакция по умолчанию
            // (issue #346: в F9 раньше всегда бралась DefaultEdition, версия не учитывалась).
            var edition = ConfigTypeMatcher.FindEditionByVersion(config, _infobase.ConfigurationVersion)
                ?? config?.DefaultEdition;
            var url = _updates.BuildUpdateUrl(config, edition, _infobase.UpdateUrlOverride,
                _infobase.UpdateUrlSegment);
            _row.Url = url;

            if (string.IsNullOrWhiteSpace(url))
            {
                // Понятное пояснение вместо сухого «не задан адрес»: база не связана с типовой
                // конфигурацией либо у конфигурации нет сегмента/ника каталога релизов (issue #323).
                _row.Status = ConfigUpdateStatus.Failed;
                _row.Error = EmptyUrlMessage();
            }
            else
            {
                var configName = config?.Name ?? _row.Name;
                var currentVersion = _row.CurrentVersion;
                var result = await Task.Run(
                    () => _updates.CheckForUpdatesAsync(configName, currentVersion, url, token), token);
                _row.ApplyResult(result);
                SaveUpdateCache(result);
                // Цепочка обновлений (issue #352): при наличии нового релиза строим
                // варианты от текущей версии до последней и показываем их в таблице.
                await BuildChainsAsync(url, token);
            }
        }
        catch (OperationCanceledException)
        {
            _row.Status = ConfigUpdateStatus.Failed;
            _row.Error = LocalizationManager.T("Updates.Cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error($"Ошибка проверки обновлений конфигурации базы «{_row.Name}»", ex);
            _row.Status = ConfigUpdateStatus.Failed;
            _row.Error = LocalizationManager.T("Updates.NetworkError");
        }
        finally
        {
            _row.IsChecking = false;
            CheckButton.IsEnabled = true;
            UpdateStatusDisplay();
        }
    }

    /// <summary>Находит типовую конфигурацию, связанную с базой (по коду связи).
    /// Общий список типовых = встроенные + пользовательские из файла custom_config_types.json
    /// (единый загрузчик <see cref="ICustomConfigTypesStore"/> — issue #321).</summary>
    private OneCConfigType? FindLinkedConfig()
    {
        var code = _infobase.UpdateConfigCode;
        if (string.IsNullOrWhiteSpace(code))
            return null;

        return _store.LoadAll().FirstOrDefault(c =>
            string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Обновляет блок статуса, ошибок и деталей (версия/URL/кнопка «Скачать») после проверки.</summary>
    private void UpdateStatusDisplay()
    {
        StatusText.Text = StatusTextLocalized(_row.Status);

        var brush = FindResource("TextPrimaryBrush") as Brush ?? Brushes.Gray;
        if (_row.HasNewer)
            brush = FindResource("AccentBrush") as Brush ?? Brushes.Green;
        else if (_row.Status == ConfigUpdateStatus.Failed)
            brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
        StatusText.Foreground = brush;

        // Раньше «Последняя версия» и «Ссылка на каталог релизов» не обновлялись после проверки,
        // поэтому пользователь не видел результат работы окна (issue #323).
        LatestVersionText.Text = string.IsNullOrWhiteSpace(_row.LatestVersion)
            ? "—"
            : _row.LatestVersion;
        UpdateUrlLinkDisplay();
        DownloadButton.IsEnabled = _row.CanDownload;

        // Error может быть ключом локализации (Updates.*) либо свободным текстом («HTTP 500»):
        // ключ переводим, свободный текст LocalizationManager.T() вернёт как есть.
        ErrorText.Text = _row.Status == ConfigUpdateStatus.Failed ? LocalizeError(_row.Error) : string.Empty;

        // При ошибке авторизации показываем панель действий: имя учётной записи ИТС,
        // «Открыть login.1c.ru в браузере», «Учётные данные ИТС…» (issue #323/#330/#334).
        ShowAuthActions(_row.Status == ConfigUpdateStatus.Failed && IsAuthErrorKey(_row.Error));

        // Блок цепочки обновлений (issue #352): таблица вариантов и кнопка «Скачать цепочку».
        UpdateChainDisplay();
    }

    /// <summary>Показывает/скрывает панель действий при ошибке авторизации и заполняет имя
    /// используемой учётной записи ИТС (анонимизированно — без логина, issue #323/#330/#334).</summary>
    private void ShowAuthActions(bool visible)
    {
        AuthActionsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;

        var settings = _repository.LoadSettings();
        var account = _itsAccounts.Resolve(settings.ItsAccountId);
        var accountName = account is not null && !string.IsNullOrWhiteSpace(account.Name)
            ? account.Name!
            : LocalizationManager.T("Updates.AccountPrimary");
        AuthAccountText.Text = string.Format(LocalizationManager.T("Updates.AccountUsed"), accountName);
    }

    /// <summary>True — ключ ошибки относится к авторизации на portal.1c.ru (для панели действий).</summary>
    private static bool IsAuthErrorKey(string error)
        => error is "Updates.AuthRequired" or "Updates.AuthFailed"
            or "Updates.FormUnavailable" or "Updates.LoginLimitReached";

    /// <summary>Открывает login.1c.ru в браузере (issue #323/#330/#334): пользователь выполняет
    /// вход вручную, после чего возвращается в окно и повторяет проверку.</summary>
    private void OnOpenLoginClick(object sender, RoutedEventArgs e)
    {
        if (!OneCLauncher.OpenUrl("https://login.1c.ru/login"))
            ErrorText.Text = LocalizationManager.T("Settings.About.LinkOpenFailed");
    }

    /// <summary>Открывает справочник учётных записей ИТС (issue #323/#330/#334): после правки
    /// данных повторный вход использует обновлённую запись.</summary>
    private void OnItsAccountsClick(object sender, RoutedEventArgs e)
    {
        var win = new ItsAccountsWindow { Owner = this };
        win.ShowDialog();
    }

    /// <summary>Обновляет ссылку каталога релизов: текст, активность и вид (issue #323).
    /// Валидный http/https-адрес — кликабельная ссылка (AccentBrush, подчёркивание, ToolTip);
    /// пустой/невалидный адрес («—») — обычный вторичный текст без перехода.</summary>
    private void UpdateUrlLinkDisplay()
    {
        var url = string.IsNullOrWhiteSpace(_row.Url) ? null : _row.Url;
        UrlRun.Text = url ?? "—";

        if (url is not null
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            _urlLinkEnabled = true;
            UrlLink.NavigateUri = uri;
            UrlLink.IsEnabled = true;
            UrlLink.Foreground = FindResource("AccentBrush") as Brush ?? Brushes.Blue;
            UrlLink.TextDecorations = TextDecorations.Underline;
            System.Windows.Controls.ToolTipService.SetToolTip(UrlLink, LocalizationManager.T("Updates.OpenCatalog"));
        }
        else
        {
            _urlLinkEnabled = false;
            UrlLink.NavigateUri = null;
            UrlLink.IsEnabled = false;
            UrlLink.Foreground = FindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            UrlLink.TextDecorations = null;
            System.Windows.Controls.ToolTipService.SetToolTip(UrlLink, null);
        }
    }

    /// <summary>Открывает каталог релизов в браузере через <see cref="OneCLauncher.OpenUrl"/>
    /// (не собственным Process.Start, issue #323).</summary>
    private void UrlLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (!_urlLinkEnabled)
            return;

        var url = e.Uri?.AbsoluteUri ?? _row.Url;
        if (!OneCLauncher.OpenUrl(url))
        {
            ErrorText.Text = LocalizationManager.T("Settings.About.LinkOpenFailed");
            ErrorText.Foreground = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
        }
    }

    /// <summary>Локализует текст ошибки: ключи «Updates.*» переводит, остальное возвращает без изменений.</summary>
    private static string LocalizeError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return string.Empty;
        return LocalizationManager.T(error);
    }

    /// <summary>
    /// Пояснение при пустом адресе каталога релизов: различает «база не связана», «у конфигурации
    /// нет ника на releases.1c.ru» и «нет адреса» (issue #323).
    /// </summary>
    private string EmptyUrlMessage()
    {
        if (!string.IsNullOrWhiteSpace(_infobase.UpdateConfigCode))
        {
            var config = FindLinkedConfig();
            if (config is not null && string.IsNullOrWhiteSpace(config.Nick))
                return string.Format(LocalizationManager.T("Updates.NoNick"), config.Name);
            return LocalizationManager.T("Updates.NoUrl");
        }

        if (!string.IsNullOrWhiteSpace(_infobase.ConfigurationName))
        {
            var config = ConfigTypeMatcher.FindByInfobaseName(_store.LoadAll(), _infobase.ConfigurationName);
            if (config is not null && string.IsNullOrWhiteSpace(config.Nick))
                return string.Format(LocalizationManager.T("Updates.NoNick"), config.Name);
            // Имя конфигурации базы не сопоставилось ни с одной типовой (issue #346):
            // сообщаем конкретную причину вместо общего «База не связана с типовой».
            if (config is null)
                return string.Format(LocalizationManager.T("Updates.NoMatchFound"), _infobase.ConfigurationName);
        }
        return LocalizationManager.T("Updates.NoLink");
    }

    /// <summary>
    /// Сохраняет результат успешной проверки в <see cref="AppSettings.UpdateCheckCache"/>
    /// (по коду связи базы), чтобы колонка «Обновление» в Центре обслуживания показывала
    /// последний результат. Ошибки/отмена в кэш не пишутся.
    /// </summary>
    private void SaveUpdateCache(ConfigUpdateCheckResult result)
    {
        if (!result.Succeeded || string.IsNullOrWhiteSpace(_infobase.UpdateConfigCode))
            return;
        try
        {
            var settings = _repository.LoadSettings();
            settings.UpdateCheckCache ??= new System.Collections.Generic.Dictionary<string, Models.ConfigUpdateCheckResult>();
            settings.UpdateCheckCache[_infobase.UpdateConfigCode] = result;
            _repository.SaveSettings(settings);
        }
        catch (Exception ex)
        {
            _logger.Warn("Не удалось сохранить результат проверки обновлений в кэш: " + ex.Message);
        }
    }

    private void UpdateProgressDisplay()
    {
        if (_row.IsDownloading)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressText.Text = $"{Math.Round(_row.Progress * 100, 0):0} %";
        }
        else
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnCheckClick(object sender, RoutedEventArgs e)
    {
        await RunCheckAsync();
    }

    /// <summary>Загружает дистрибутив обновления по ссылке каталога релизов (кнопка «Скачать»).
    /// С 0.3.11 (issue #352.1): сначала запрашиваются варианты файлов релиза со страницы
    /// файлов версии («Дистрибутив обновления» / «Полный дистрибутив»). Один вариант —
    /// скачивается сразу; несколько — диалог выбора; ничего — прежний путь через
    /// DownloadUpdateAsync (fallback).</summary>
    private async void OnDownloadRow(UpdateCheckRowViewModel row)
    {
        if (!row.CanDownload || string.IsNullOrWhiteSpace(row.Url))
            return;

        row.IsDownloading = true;
        row.Progress = 0;
        UpdateProgressDisplay();
        _cts?.Cancel();

        try
        {
            // issue #352.1: страница файлов версии → варианты «Дистрибутив обновления»/
            // «Полный дистрибутив». Имя сохранения — с расширением выбранного файла.
            IReadOnlyList<UpdateFileChoice> choices = Array.Empty<UpdateFileChoice>();
            try
            {
                choices = await Task.Run(() =>
                    _updates.GetReleaseFileChoicesAsync(row.Url, CancellationToken.None));
            }
            catch (Exception ex)
            {
                _logger.Warn($"Не удалось получить варианты файлов релиза: {ex.Message}");
            }

            var choice = choices.Count switch
            {
                1 => choices[0],
                > 1 => ShowFileChoiceDialog(choices),
                _ => null,
            };

            if (choice is not null)
            {
                await DownloadUpdateChoiceAsync(row, choice);
            }
            else if (choices.Count == 0)
            {
                // Fallback — прежний путь: страница скачивания по цепочке, имя «.zip».
                var targetPath = AskSavePath(BuildDownloadFileName(row, null));
                if (string.IsNullOrWhiteSpace(targetPath))
                    return;
                await DownloadUpdateFileAsync(row, row.Url, targetPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Ошибка загрузки обновления конфигурации «{row.Name}»", ex);
            _dialogs.ShowError(LocalizationManager.T("Updates.NetworkError"),
                LocalizationManager.T("Updates.CheckTitle"));
        }
        finally
        {
            row.IsDownloading = false;
            UpdateProgressDisplay();
        }
    }

    /// <summary>Диалог выбора файла релиза (2+ варианта); null — пользователь отменил.</summary>
    private UpdateFileChoice? ShowFileChoiceDialog(IReadOnlyList<UpdateFileChoice> choices)
    {
        try
        {
            return UpdateFileChoiceWindow.Pick(choices,
                string.Format(LocalizationManager.T("Updates.FileChoice.Heading"), _row.Name));
        }
        catch (Exception ex)
        {
            // Диалог не должен ронять скачивание — берём первый (приоритетный) вариант.
            _logger.Warn("Не удалось показать выбор файла релиза: " + ex.Message);
            return choices[0];
        }
    }

    /// <summary>Скачивает выбранный вариант файла релиза (issue #352.1).</summary>
    private async Task DownloadUpdateChoiceAsync(UpdateCheckRowViewModel row, UpdateFileChoice choice)
    {
        var targetPath = AskSavePath(BuildDownloadFileName(row, choice));
        if (string.IsNullOrWhiteSpace(targetPath))
            return;
        await DownloadUpdateFileAsync(row, choice.Url, targetPath);
    }

    /// <summary>Диалог сохранения с фильтром известных расширений дистрибутивов.</summary>
    private string? AskSavePath(string fileName)
    {
        return _dialogs.SaveFileDialog(
            LocalizationManager.T("Updates.Download"),
            fileName,
            "Архивы (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|Файлы 1С (*.cf;*.cfu)|*.cf;*.cfu|Все файлы (*.*)|*.*",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Общая загрузка файла обновления с прогрессом и отчётом о результате.</summary>
    private async Task DownloadUpdateFileAsync(UpdateCheckRowViewModel row, string url, string targetPath)
    {
        var progress = new Progress<double>(v => row.Progress = Math.Clamp(v, 0, 1));
        var savedPath = await Task.Run(() =>
            _updates.DownloadUpdateAsync(url, targetPath, progress, CancellationToken.None));

        if (string.IsNullOrWhiteSpace(savedPath))
        {
            _dialogs.ShowWarning(LocalizationManager.T("Updates.NetworkError"),
                LocalizationManager.T("Updates.CheckTitle"));
        }
        else
        {
            row.Progress = 1;
            UpdateProgressDisplay();
            _dialogs.ShowInfo(string.Format(LocalizationManager.T("Updates.Loaded"), savedPath),
                LocalizationManager.T("Updates.CheckTitle"));
        }
    }

    /// <summary>
    /// Строит цепочку обновлений (issue #352): получает полный каталог версий конфигурации
    /// через <see cref="IOneCUpdatesService.GetUpdateCatalogAsync"/> и вычисляет варианты
    /// от текущей версии до последней (<see cref="UpdateChainBuilder"/>). Ошибки каталога
    /// не роняют результат основной проверки: состояние цепочки сбрасывается и остаётся
    /// прежнее поведение — «Скачать» только последнюю версию.
    /// </summary>
    private async Task BuildChainsAsync(string url, CancellationToken token)
    {
        _row.ResetChains();
        if (!_row.HasNewer || string.IsNullOrWhiteSpace(_row.CurrentVersion)
            || string.IsNullOrWhiteSpace(_row.LatestVersion))
        {
            UpdateChainDisplay();
            return;
        }

        try
        {
            // issue #352: каталог запрашивается с allUpdates=true — без параметра портал
            // отдаёт только последние релизы, и цепочка не строится.
            var catalogUrl = OneCUpdatesService.BuildAllUpdatesCatalogUrl(url);
            var catalog = await Task.Run(() => _updates.GetUpdateCatalogAsync(catalogUrl, token), token);
            if (catalog.Status == PortalFetchStatus.Ok)
            {
                var set = UpdateChainBuilder.Build(_row.CurrentVersion, _row.LatestVersion, catalog.Releases);
                _row.SetChains(set);
            }
            else
            {
                _logger.Warn($"Не удалось получить каталог версий для цепочки обновлений ({catalog.ErrorKey}).");
            }
        }
        catch (OperationCanceledException)
        {
            // Проверка отменена — цепочка не строится (прежнее поведение).
        }
        catch (Exception ex)
        {
            _logger.Error($"Ошибка построения цепочки обновлений для «{_row.Name}»", ex);
        }

        UpdateChainDisplay();
    }

    /// <summary>Обновляет видимость и состояние блока цепочки обновлений (issue #352):
    /// таблица вариантов, кнопка «Скачать цепочку» и панель прогресса цепочки.</summary>
    private void UpdateChainDisplay()
    {
        var hasChain = _row.HasChain;
        ChainPanel.Visibility = hasChain ? Visibility.Visible : Visibility.Collapsed;
        DownloadChainButton.Visibility = hasChain ? Visibility.Visible : Visibility.Collapsed;
        DownloadChainButton.IsEnabled = _row.CanDownloadChain;
        ChainProgressPanel.Visibility = _row.IsChainDownloading ? Visibility.Visible : Visibility.Collapsed;

        // issue #352: «Версии нет на сайте» — красным под текущей версией.
        CurrentVersionMissingText.Visibility = _row.CurrentVersionMissing
            ? Visibility.Visible
            : Visibility.Collapsed;

        // issue #352: при появлении таблицы вариантов окно поднимается так, чтобы
        // 2–3 строки таблицы были видны без прокрутки.
        if (hasChain)
            EnsureWindowHeightForChain();
    }

    /// <summary>Поднимает высоту окна при появлении таблицы вариантов цепочки (issue #352):
    /// 2–3 строки таблицы должны быть видны без прокрутки. Ограничение — рабочая область
    /// экрана (окно не должно становиться выше неё).</summary>
    private void EnsureWindowHeightForChain()
    {
        var target = Math.Min(
            760d,
            Math.Max(SystemParameters.WorkArea.Height - 40d, MinHeight));
        if (Height < target)
            Height = target;
    }

    /// <summary>Кнопка «Скачать цепочку» (issue #352).</summary>
    private async void OnDownloadChainClick(object sender, RoutedEventArgs e)
    {
        await DownloadChainAsync();
    }

    /// <summary>
    /// Скачивает выбранную цепочку обновлений в указанный каталог (issue #352):
    /// последовательно все версии варианта (от текущей к последней) с прогрессом —
    /// какой файл скачивается и сколько ещё осталось.
    /// </summary>
    private async Task DownloadChainAsync()
    {
        if (!_row.CanDownloadChain || string.IsNullOrWhiteSpace(_row.Url))
            return;

        var variant = _row.SelectedVariant ?? _row.ChainVariants.FirstOrDefault();
        if (variant is null || variant.Steps.Count == 0)
            return;

        var settingsFolder = _repository.LoadSettings().UpdateChainFolder;
        var initialFolder = !string.IsNullOrWhiteSpace(settingsFolder) && Directory.Exists(settingsFolder)
            ? settingsFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = _dialogs.OpenFolderDialog(
            LocalizationManager.T("Updates.Chain.ChooseFolder"),
            initialFolder);
        if (string.IsNullOrWhiteSpace(folder))
            return;

        // issue #352.2: выбранная папка запоминается — следующий диалог и кнопка «Открыть».
        SaveChainFolder(folder);
        UpdateChainFolderDisplay();

        _row.IsChainDownloading = true;
        _row.ChainProgress = 0;
        UpdateChainDisplay();

        try
        {
            // issue #352 (комментарий 7OH от 2026-10-09): докачка цепочки — файлы,
            // уже скачанные ранее (существуют и непусты), пропускаются ещё на старте;
            // счётчик «Скачивается X из Y» считается только по требующим скачивания.
            var baseName = SanitizeFileName(_row.Name);
            var targetPaths = variant.Steps
                .Select(step => Path.Combine(folder, $"{baseName}_{step.Version}.zip"))
                .ToList();
            var pending = UpdateChainDownloadPlanner.SelectPendingSteps(targetPaths);
            var skipped = variant.Steps.Count - pending.Count;
            if (skipped > 0)
                _logger.Info($"Пропущено уже скачанных файлов цепочки «{_row.Name}»: {skipped} из {variant.Steps.Count}.");

            if (pending.Count == 0)
            {
                _dialogs.ShowInfo(string.Format(
                        LocalizationManager.T("Updates.Chain.AllDownloaded"), variant.Steps.Count, folder),
                    LocalizationManager.T("Updates.CheckTitle"));
                return;
            }

            var total = pending.Count;
            var ok = 0;
            var failed = 0;

            for (var n = 0; n < total; n++)
            {
                var i = pending[n];
                var step = variant.Steps[i];
                var url = OneCUpdatesService.ToAbsoluteVersionFilesUrl(step.VersionFilesUrl, _row.Url);
                var targetPath = targetPaths[i];

                _row.ChainProgressText = string.Format(
                    LocalizationManager.T("Updates.Chain.DownloadProgress"), n + 1, total, step.Version);
                _row.ChainProgress = (double)n / total;

                var progress = new Progress<double>(p =>
                    _row.ChainProgress = Math.Clamp((n + p) / total, 0, 1));
                var saved = await Task.Run(() =>
                    _updates.DownloadUpdateAsync(url, targetPath, progress, CancellationToken.None));

                if (!string.IsNullOrWhiteSpace(saved))
                {
                    ok++;
                    _logger.Info($"Скачана версия {step.Version} цепочки: {saved}");
                }
                else
                {
                    failed++;
                    _logger.Warn($"Не удалось скачать версию {step.Version} цепочки ({url}).");
                }

                _row.ChainProgress = (double)(n + 1) / total;
                _row.ChainProgressText = string.Format(
                        LocalizationManager.T("Updates.Chain.DownloadProgress"), n + 1, total, step.Version)
                    + " " + string.Format(LocalizationManager.T("Updates.Chain.Remaining"), total - n - 1);
            }

            if (failed > 0)
            {
                // issue #352.3: вместо предупреждения — диалог «Попробовать ещё раз?»
                // с обратным отсчётом; «Да» повторяет загрузку (планировщик сам пропустит
                // скачанное и покажет корректный «Скачивается X из Y»).
                var message = string.Format(
                    LocalizationManager.T("Updates.Chain.LoadedFailedDetailed"), failed, total);
                if (ChainRetryWindow.Ask(this, message))
                {
                    await DownloadChainAsync();
                    return;
                }
            }
            else if (skipped > 0)
            {
                _dialogs.ShowInfo(string.Format(
                    LocalizationManager.T("Updates.Chain.LoadedOkSkipped"), ok, total, skipped, folder),
                    LocalizationManager.T("Updates.CheckTitle"));
            }
            else
            {
                _dialogs.ShowInfo(string.Format(
                    LocalizationManager.T("Updates.Chain.LoadedOk"), ok, total, folder),
                    LocalizationManager.T("Updates.CheckTitle"));
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Ошибка загрузки цепочки обновлений «{_row.Name}»", ex);
            _dialogs.ShowError(LocalizationManager.T("Updates.NetworkError"),
                LocalizationManager.T("Updates.CheckTitle"));
        }
        finally
        {
            _row.IsChainDownloading = false;
            UpdateChainDisplay();
        }
    }

    /// <summary>Имя сохранения: «<Имя>_<версия><расширение>»; расширение берётся из
    /// выбранного файла релиза (issue #352.1: .cf вместо фиксированного .zip).</summary>
    private static string BuildDownloadFileName(UpdateCheckRowViewModel row, UpdateFileChoice? choice)
    {
        var baseName = SanitizeFileName(row.Name);
        var latest = string.IsNullOrWhiteSpace(row.LatestVersion) ? "update" : row.LatestVersion;
        var extension = choice is not null && !string.IsNullOrWhiteSpace(choice.FileName)
            ? Path.GetExtension(choice.FileName)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".zip";
        return $"{baseName}_{latest}{extension}";
    }

    // ===================== Папка цепочки обновлений (issue #352.2) =====================

    /// <summary>Сохраняет папку цепочки в настройки (пустая папка не пишется).</summary>
    private void SaveChainFolder(string folder)
    {
        try
        {
            var settings = _repository.LoadSettings();
            settings.UpdateChainFolder = folder;
            _repository.SaveSettings(settings);
        }
        catch (Exception ex)
        {
            _logger.Warn("Не удалось сохранить папку цепочки обновлений: " + ex.Message);
        }
    }

    /// <summary>Обновляет строку «Папка: …» над таблицей цепочки: путь либо подсказка
    /// «выберите папку при скачивании» (issue #352.2).</summary>
    private void UpdateChainFolderDisplay()
    {
        var folder = _repository.LoadSettings().UpdateChainFolder;
        var hasFolder = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        ChainFolderText.Text = hasFolder
            ? folder
            : LocalizationManager.T("Updates.Chain.FolderHint");
        ChainOpenFolderButton.IsEnabled = hasFolder;
    }

    /// <summary>Кнопка «Открыть»: показывает сохранённую папку цепочки в проводнике.</summary>
    private void OnOpenChainFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = _repository.LoadSettings().UpdateChainFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            };
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.Warn("Не удалось открыть папку цепочки обновлений: " + ex.Message);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private static string StatusTextLocalized(ConfigUpdateStatus status)
    {
        return status switch
        {
            ConfigUpdateStatus.UpToDate => LocalizationManager.T("Updates.UpToDate"),
            ConfigUpdateStatus.NewerAvailable => LocalizationManager.T("Updates.NewerAvailable"),
            ConfigUpdateStatus.Unavailable => LocalizationManager.T("Updates.Unavailable"),
            ConfigUpdateStatus.Failed => LocalizationManager.T("Updates.CheckFailed"),
            _ => LocalizationManager.T("Updates.Status"),
        };
    }

    private void OnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
#endif