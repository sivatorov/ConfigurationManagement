#if LINUX
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Окно проверки обновлений конфигурации для выбранной информационной базы (горячая клавиша F9).
    /// Показывает имя базы, текущую версию конфигурации, последнюю версию из каталога релизов 1С,
    /// адрес каталога и статус проверки; подсвечивает наличие нового релиза и позволяет скачать
    /// дистрибутив с индикатором прогресса. Сетевые операции выполняются в фоновом потоке.
    /// Avalonia/Linux-версия WPF-окна <see cref="UpdateCheckWindow"/>.
    /// </summary>
    public sealed class UpdateCheckWindow : ModalWindowBase
    {
        private readonly Services.IOneCUpdatesService _updates = AppServices.GetRequiredService<Services.IOneCUpdatesService>();
        private readonly Services.IInfobaseRepository _repository = AppServices.GetRequiredService<Services.IInfobaseRepository>();
        private readonly Services.ICustomConfigTypesStore _store = AppServices.GetRequiredService<Services.ICustomConfigTypesStore>();
        private readonly Services.IItsAccountsStore _itsAccounts = AppServices.GetRequiredService<Services.IItsAccountsStore>();
        private readonly Services.IAppLogger _logger = AppServices.GetRequiredService<Services.IAppLogger>();
        private readonly IDialogService _dialogs = AppServices.GetRequiredService<IDialogService>();

        private readonly Infobase _infobase;
        private readonly UpdateCheckRowViewModel _row;
        private CancellationTokenSource? _cts;

        /// <summary>Активна ли ссылка каталога релизов (валидный http/https-адрес, issue #323).</summary>
        private bool _urlLinkEnabled;

        private readonly TextBlock _baseNameText = new();
        private readonly TextBlock _currentVersionText = new();

        /// <summary>Красное предупреждение «Версии нет на сайте» под текущей версией (issue #352).</summary>
        private readonly TextBlock _currentVersionMissingText = new() { IsVisible = false };

        private readonly TextBlock _latestVersionText = new();
        private readonly TextBlock _urlText = new();
        private readonly TextBlock _statusText = new();
        private readonly TextBlock _errorText = new();
        private readonly StackPanel _authActionsPanel = new() { IsVisible = false };
        private readonly TextBlock _authAccountText = new();
        private readonly StackPanel _progressPanel = new() { IsVisible = false };
        private readonly ProgressBar _progressBar = new() { Minimum = 0, Maximum = 1 };
        private readonly TextBlock _progressText = new();

        // Блок цепочки обновлений (issue #352): секция с таблицей вариантов и прогрессом.
        private readonly StackPanel _chainSection = new() { IsVisible = false };
        private readonly TextBlock _chainHintText = new();
        private readonly TextBlock _chainStatusValue = new();

        // Папка сохранения цепочки (issue #352.2): «Папка: <путь>» + кнопка «Открыть».
        private readonly TextBlock _chainFolderValue = new();
        private readonly Button _chainOpenFolderButton = new();
        private readonly StackPanel _chainFolderPanel = new() { IsVisible = false };

        private readonly StackPanel _chainRowsPanel = new();
        private readonly StackPanel _chainProgressPanel = new() { IsVisible = false };
        private readonly ProgressBar _chainProgressBar = new() { Minimum = 0, Maximum = 1 };
        private readonly TextBlock _chainProgressText = new();
        private Button _checkButton = new();
        private Button _downloadButton = new();
        private Button _downloadChainButton = new();

        /// <param name="infobase">Информационная база, для которой выполняется проверка обновлений.</param>
        public UpdateCheckWindow(Infobase infobase)
        {
            _infobase = infobase ?? throw new ArgumentNullException(nameof(infobase));
            _row = new UpdateCheckRowViewModel(infobase, OnDownloadRow);

            Title = LocalizationManager.T("Updates.CheckTitle");
            Width = 720;
            Height = 660;
            MinWidth = 640;
            MinHeight = 500;
            FontSize = 13;
            CanResize = true;

            _baseNameText.Text = _row.Name;
            _currentVersionText.Text = string.IsNullOrWhiteSpace(_row.CurrentVersion) ? "—" : _row.CurrentVersion;

            _row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(_row.Progress))
                    UpdateProgressDisplay();
                else if (e.PropertyName == nameof(_row.ChainProgress)
                         || e.PropertyName == nameof(_row.ChainProgressText)
                         || e.PropertyName == nameof(_row.IsChainDownloading))
                    UpdateChainProgressDisplay();
                else if (e.PropertyName == nameof(_row.LatestVersion)
                         || e.PropertyName == nameof(_row.Url)
                         || e.PropertyName == nameof(_row.Status)
                         || e.PropertyName == nameof(_row.Error)
                         || e.PropertyName == nameof(_row.CanDownload))
                    RefreshDetailDisplay();
            };

            // Клик по адресу каталога релизов открывает браузер (issue #323).
            _urlText.PointerReleased += OnUrlTextPointerReleased;

            Content = BuildRoot();
            // Папка цепочки (issue #352.2): строка с путём и кнопкой «Открыть».
            RefreshChainFolderDisplay();
            Opened += async (_, _) => await RunCheckAsync();
        }

        /// <summary>
        /// Показывает окно модально (синхронно). Открытая публичная обёртка над
        /// <see cref="ModalWindowBase.ShowDialogSync(Window?)"/>, чтобы диалог можно было
        /// вызывать из ViewModel (не наследника окна).
        /// </summary>
        public bool ShowSync(Window? owner = null) => ShowDialogSync(owner);

        private static string T(string key) => LocalizationManager.T(key);

        /// <summary>Запускает сетевую проверку наличия обновлений для связанной конфигурации.</summary>
        private async Task RunCheckAsync()
        {
            if (_row.IsChecking)
                return;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _row.IsChecking = true;
            _checkButton.IsEnabled = false;
            _errorText.Text = string.Empty;

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
                _row.Error = T("Updates.Cancelled");
            }
            catch (Exception ex)
            {
                _logger.Error($"Ошибка проверки обновлений конфигурации базы «{_row.Name}»", ex);
                _row.Status = ConfigUpdateStatus.Failed;
                _row.Error = T("Updates.NetworkError");
            }
            finally
            {
                _row.IsChecking = false;
                _checkButton.IsEnabled = true;
                RefreshDetailDisplay();
            }
        }

        /// <summary>Находит типовую конфигурацию, связанную с базой (по коду связи).
        /// Общий список типовых = встроенные + пользовательские из файла custom_config_types.json
        /// (единый загрузчик <see cref="Services.ICustomConfigTypesStore"/> — issue #321).</summary>
        private OneCConfigType? FindLinkedConfig()
        {
            var code = _infobase.UpdateConfigCode;
            if (string.IsNullOrWhiteSpace(code))
                return null;

            return _store.LoadAll().FirstOrDefault(c =>
                string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Обновляет текстовые ячейки деталей и состояние кнопок из модели строки.</summary>
        private void RefreshDetailDisplay()
        {
            _latestVersionText.Text = string.IsNullOrWhiteSpace(_row.LatestVersion) ? "—" : _row.LatestVersion;
            UpdateUrlLinkDisplay();
            _statusText.Text = StatusTextLocalized(_row.Status);

            IBrush? brush = new SolidColorBrush(Colors.Gray);
            var primary = TryGetBrush("TextPrimaryBrush");
            if (primary is not null) brush = primary;
            if (_row.HasNewer)
            {
                brush = TryGetBrush("AccentBrush") ?? new SolidColorBrush(Color.Parse("#16A34A"));
            }
            else if (_row.Status == ConfigUpdateStatus.Failed)
            {
                brush = new SolidColorBrush(Color.Parse("#EF4444"));
            }
            _statusText.Foreground = brush;

            // Error может быть ключом локализации (Updates.*) либо свободным текстом («HTTP 500»):
            // ключ переводим, свободный текст LocalizationManager.T() вернёт как есть.
            _errorText.Text = _row.Status == ConfigUpdateStatus.Failed ? LocalizeError(_row.Error) : string.Empty;
            _downloadButton.IsEnabled = _row.CanDownload;

            // При ошибке авторизации показываем панель действий: имя учётной записи ИТС,
            // «Открыть login.1c.ru в браузере», «Учётные данные ИТС…» (issue #323/#330/#334).
            ShowAuthActions(_row.Status == ConfigUpdateStatus.Failed && IsAuthErrorKey(_row.Error));

            // Блок цепочки обновлений (issue #352): таблица вариантов и кнопка «Скачать цепочку».
            RefreshChainDisplay();
        }

        /// <summary>Показывает/скрывает панель действий при ошибке авторизации и заполняет имя
        /// используемой учётной записи ИТС (анонимизированно — без логина, issue #323/#330/#334).</summary>
        private void ShowAuthActions(bool visible)
        {
            _authActionsPanel.IsVisible = visible;
            if (!visible)
                return;

            var settings = _repository.LoadSettings();
            var account = _itsAccounts.Resolve(settings.ItsAccountId);
            var accountName = account is not null && !string.IsNullOrWhiteSpace(account.Name)
                ? account.Name!
                : T("Updates.AccountPrimary");
            _authAccountText.Text = string.Format(T("Updates.AccountUsed"), accountName);
        }

        /// <summary>True — ключ ошибки относится к авторизации на portal.1c.ru (для панели действий).</summary>
        private static bool IsAuthErrorKey(string error)
            => error is "Updates.AuthRequired" or "Updates.AuthFailed"
                or "Updates.FormUnavailable" or "Updates.LoginLimitReached";

        /// <summary>Открывает login.1c.ru в браузере (issue #323/#330/#334): пользователь выполняет
        /// вход вручную, после чего возвращается в окно и повторяет проверку.</summary>
        private void OnOpenLoginClick()
        {
            if (!OneCLauncher.OpenUrl("https://login.1c.ru/login"))
                _errorText.Text = T("Settings.About.LinkOpenFailed");
        }

        /// <summary>Открывает справочник учётных записей ИТС (issue #323/#330/#334): после правки
        /// данных повторный вход использует обновлённую запись.</summary>
        private void OnItsAccountsClick()
        {
            var win = new ItsAccountsWindow();
            win.ShowSync(this);
        }

        /// <summary>Обновляет ссылку каталога релизов: текст, активность и вид (issue #323).
        /// Валидный http/https-адрес — кликабельная ссылка (AccentBrush, подчёркивание, курсор Hand,
        /// ToolTip); пустой/невалидный адрес («—») — обычный вторичный текст без перехода.</summary>
        private void UpdateUrlLinkDisplay()
        {
            var url = string.IsNullOrWhiteSpace(_row.Url) ? null : _row.Url;
            _urlText.Text = url ?? "—";

            var valid = url is not null
                        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            _urlLinkEnabled = valid;

            if (valid)
            {
                _urlText.TextDecorations = TextDecorations.Underline;
                _urlText.Cursor = new Cursor(StandardCursorType.Hand);
                Themes.ThemeBrushes.Bind(_urlText, TextBlock.ForegroundProperty, "AccentBrush");
                ToolTip.SetTip(_urlText, T("Updates.OpenCatalog"));
            }
            else
            {
                _urlText.TextDecorations = null;
                _urlText.Cursor = Cursor.Default;
                Themes.ThemeBrushes.Bind(_urlText, TextBlock.ForegroundProperty, "TextSecondaryBrush");
                ToolTip.SetTip(_urlText, null);
            }
        }

        /// <summary>Клик по адресу каталога релизов открывает браузер через
        /// <see cref="OneCLauncher.OpenUrl"/> (не собственным Process.Start, issue #323).
        /// Проверка попадания по Bounds — как в ссылке окна «Ручное обновление».</summary>
        private void OnUrlTextPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_urlLinkEnabled || e.InitialPressMouseButton != MouseButton.Left)
                return;

            var point = e.GetPosition(_urlText);
            if (point.X < 0 || point.Y < 0
                || point.X > _urlText.Bounds.Width || point.Y > _urlText.Bounds.Height)
                return;

            if (!OneCLauncher.OpenUrl(_row.Url))
            {
                _errorText.Text = T("Settings.About.LinkOpenFailed");
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
                    return string.Format(T("Updates.NoNick"), config.Name);
                return T("Updates.NoUrl");
            }

            if (!string.IsNullOrWhiteSpace(_infobase.ConfigurationName))
            {
                var config = ConfigTypeMatcher.FindByInfobaseName(_store.LoadAll(), _infobase.ConfigurationName);
                if (config is not null && string.IsNullOrWhiteSpace(config.Nick))
                    return string.Format(T("Updates.NoNick"), config.Name);
                // Имя конфигурации базы не сопоставилось ни с одной типовой (issue #346):
                // сообщаем конкретную причину вместо общего «База не связана с типовой».
                if (config is null)
                    return string.Format(T("Updates.NoMatchFound"), _infobase.ConfigurationName);
            }
            return T("Updates.NoLink");
        }

        /// <summary>
        /// Сохраняет результат успешной проверки в <see cref="Models.AppSettings.UpdateCheckCache"/>
        /// (по коду связи базы), чтобы колонка «Обновление» в Центре обслуживания показывала
        /// последний результат. Ошибки/отмена в кэш не пишутся.
        /// </summary>
        private void SaveUpdateCache(Models.ConfigUpdateCheckResult result)
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

        private IBrush? TryGetBrush(string key)
        {
            if (Application.Current is not { } app || !app.TryFindResource(key, out var found))
                return null;
            return found as IBrush;
        }

        private void UpdateProgressDisplay()
        {
            // Progress<double> приходит из фонового потока — маршализуем обновление в UI-поток.
            Dispatcher.UIThread.Post(() =>
            {
                if (_row.IsDownloading)
                {
                    _progressPanel.IsVisible = true;
                    _progressBar.Value = Math.Clamp(_row.Progress, 0, 1);
                    _progressText.Text = $"{Math.Round(_row.Progress * 100, 0):0} %";
                }
                else
                {
                    _progressPanel.IsVisible = false;
                }
            });
        }

        private async void OnCheckClick()
        {
            await RunCheckAsync();
        }

        /// <summary>Загружает дистрибутив обновления по ссылке каталога релизов (кнопка «Скачать»).
        /// С 0.3.11 (issue #352.1): варианты файлов релиза («Дистрибутив обновления» /
        /// «Полный дистрибутив»), 1 вариант — сразу, 2+ — диалог выбора, 0 — прежний путь.</summary>
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
                _dialogs.ShowError(T("Updates.NetworkError"), T("Updates.CheckTitle"));
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
                    string.Format(T("Updates.FileChoice.Heading"), _row.Name), this);
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
                T("Updates.Download"),
                fileName,
                "Архивы (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|Файлы 1С (*.cf;*.cfu)|*.cf;*.cfu|Все файлы (*.*)|*.*",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        /// <summary>Общая загрузка файла обновления с прогрессом и отчётом о результате.</summary>
        private async Task DownloadUpdateFileAsync(UpdateCheckRowViewModel row, string url, string targetPath)
        {
            var progress = new Progress<double>(v =>
                Dispatcher.UIThread.Post(() => row.Progress = Math.Clamp(v, 0, 1)));
            var savedPath = await Task.Run(() =>
                _updates.DownloadUpdateAsync(url, targetPath, progress, CancellationToken.None));

            if (string.IsNullOrWhiteSpace(savedPath))
            {
                _dialogs.ShowWarning(T("Updates.NetworkError"), T("Updates.CheckTitle"));
            }
            else
            {
                row.Progress = 1;
                UpdateProgressDisplay();
                _dialogs.ShowInfo(string.Format(T("Updates.Loaded"), savedPath), T("Updates.CheckTitle"));
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

        /// <summary>
        /// Строит цепочку обновлений (issue #352): получает полный каталог версий конфигурации
        /// через <see cref="Services.IOneCUpdatesService.GetUpdateCatalogAsync"/> и вычисляет
        /// варианты от текущей версии до последней (<see cref="UpdateChainBuilder"/>). Ошибки
        /// каталога не роняют результат основной проверки: состояние цепочки сбрасывается,
        /// остаётся прежнее поведение — «Скачать» только последнюю версию.
        /// </summary>
        private async Task BuildChainsAsync(string url, CancellationToken token)
        {
            _row.ResetChains();
            if (!_row.HasNewer || string.IsNullOrWhiteSpace(_row.CurrentVersion)
                || string.IsNullOrWhiteSpace(_row.LatestVersion))
            {
                RefreshChainDisplay();
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

            RefreshChainDisplay();
        }

        /// <summary>Обновляет блок цепочки обновлений: видимость секции и кнопки «Скачать цепочку»,
        /// статус и состав таблицы вариантов (issue #352).</summary>
        private void RefreshChainDisplay()
        {
            Dispatcher.UIThread.Post(() =>
            {
                var hasChain = _row.HasChain;
                _chainSection.IsVisible = hasChain;
                _downloadChainButton.IsVisible = hasChain;
                _downloadChainButton.IsEnabled = _row.CanDownloadChain;

                if (!hasChain)
                {
                    _chainRowsPanel.Children.Clear();
                    return;
                }

                // issue #352: при появлении таблицы вариантов окно поднимается так, чтобы
                // 2–3 строки таблицы были видны без прокрутки.
                EnsureWindowHeightForChain();

                _chainStatusValue.Text = _row.ChainStatusText;
                // issue #352: красное предупреждение под текущей версией.
                _currentVersionMissingText.IsVisible = _row.CurrentVersionMissing;
                RebuildChainRows();
            });
        }

        /// <summary>Поднимает высоту окна при появлении таблицы вариантов цепочки (issue #352):
        /// 2–3 строки таблицы должны быть видны без прокрутки. Ограничение — рабочая область
        /// экрана (окно не должно становиться выше неё).</summary>
        private void EnsureWindowHeightForChain()
        {
            try
            {
                var target = 760d;
                var screen = Screens.Primary;
                if (screen is not null && screen.Scaling > 0)
                {
                    var workHeight = screen.WorkingArea.Height / screen.Scaling;
                    target = Math.Min(target, Math.Max(workHeight - 40d, MinHeight));
                }

                if (Height < target)
                    Height = target;
            }
            catch
            {
                // Определение экрана не должно мешать показу таблицы цепочки.
            }
        }

        /// <summary>Перестраивает строки таблицы вариантов цепочки («№» и «Список версий»).</summary>
        private void RebuildChainRows()
        {
            _chainRowsPanel.Children.Clear();
            foreach (var variant in _row.ChainVariants)
                _chainRowsPanel.Children.Add(MakeChainRow(variant));
        }

        /// <summary>Строка таблицы вариантов: номер и список версий (issue #352);
        /// клик по строке выбирает вариант для «Скачать цепочку», подсказка — тип варианта.</summary>
        private Control MakeChainRow(UpdateChainVariantViewModel variant)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(36)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            var number = new TextBlock
            {
                Text = variant.Number.ToString(),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Themes.ThemeBrushes.Bind(number, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(number, 0);
            grid.Children.Add(number);

            var versions = new TextBlock
            {
                Text = variant.VersionsText,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Themes.ThemeBrushes.Bind(versions, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            ToolTip.SetTip(versions, variant.KindText);
            Grid.SetColumn(versions, 1);
            grid.Children.Add(versions);

            grid.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                    _row.SelectedVariant = variant;
            };

            return grid;
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
        private void RefreshChainFolderDisplay()
        {
            var folder = _repository.LoadSettings().UpdateChainFolder;
            var hasFolder = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
            _chainFolderValue.Text = hasFolder ? folder : T("Updates.Chain.FolderHint");
            _chainOpenFolderButton.IsEnabled = hasFolder;
        }

        /// <summary>Кнопка «Открыть»: показывает сохранённую папку цепочки в файловом менеджере.</summary>
        private void OnOpenChainFolderClick()
        {
            try
            {
                var folder = _repository.LoadSettings().UpdateChainFolder;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    return;

                using var process = new System.Diagnostics.Process();
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xdg-open",
                    UseShellExecute = false,
                    ArgumentList = { folder },
                };
                process.Start();
            }
            catch (Exception ex)
            {
                _logger.Warn("Не удалось открыть папку цепочки обновлений: " + ex.Message);
            }
        }

        /// <summary>Обновляет панель прогресса цепочки: какой файл скачивается и сколько осталось.</summary>
        private void UpdateChainProgressDisplay()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _chainProgressPanel.IsVisible = _row.IsChainDownloading;
                _chainProgressBar.Value = Math.Clamp(_row.ChainProgress, 0, 1);
                _chainProgressText.Text = _row.ChainProgressText;
                _downloadChainButton.IsEnabled = _row.CanDownloadChain;
            });
        }

        /// <summary>Кнопка «Скачать цепочку» (issue #352).</summary>
        private async void OnDownloadChainClick()
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
                T("Updates.Chain.ChooseFolder"),
                initialFolder);
            if (string.IsNullOrWhiteSpace(folder))
                return;

            // issue #352.2: выбранная папка запоминается — следующий диалог и кнопка «Открыть».
            SaveChainFolder(folder);
            RefreshChainFolderDisplay();

            _row.IsChainDownloading = true;
            _row.ChainProgress = 0;
            UpdateChainProgressDisplay();

            // issue #334 п.1: загрузка цепочки регистрируется в менеджере фоновых
            // загрузок — продолжается после закрытия окна и видна в индикаторе.
            var chainId = $"chain:{_row.Name}";
            var backgroundEntry = Services.BackgroundDownloadManager.Default.Start(chainId, _row.Name);

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
                    // Цепочка уже полностью скачана — фоновая запись сразу завершается.
                    Services.BackgroundDownloadManager.Default.Complete(chainId);
                    _dialogs.ShowInfo(string.Format(
                            T("Updates.Chain.AllDownloaded"), variant.Steps.Count, folder),
                        T("Updates.CheckTitle"));
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
                        T("Updates.Chain.DownloadProgress"), n + 1, total, step.Version);
                    _row.ChainProgress = (double)n / total;

                    var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() =>
                    {
                        _row.ChainProgress = Math.Clamp((n + p) / total, 0, 1);
                        Services.BackgroundDownloadManager.Default.ReportProgress(chainId, _row.ChainProgress);
                    }));
                    var saved = await Task.Run(() =>
                        _updates.DownloadUpdateAsync(url, targetPath, progress, backgroundEntry.Cancellation.Token));

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
                            T("Updates.Chain.DownloadProgress"), n + 1, total, step.Version)
                        + " " + string.Format(T("Updates.Chain.Remaining"), total - n - 1);
                }

                if (failed > 0)
                {
                    // issue #352.3: вместо предупреждения — диалог «Попробовать ещё раз?»
                    // с обратным отсчётом; «Да» повторяет загрузку (планировщик сам пропустит
                    // скачанное и покажет корректный «Скачивается X из Y»).
                    var message = string.Format(T("Updates.Chain.LoadedFailedDetailed"), failed, total);
                    if (ChainRetryWindow.Ask(this, message))
                    {
                        await DownloadChainAsync();
                        return;
                    }
                }
                else if (skipped > 0)
                {
                    _dialogs.ShowInfo(string.Format(
                        T("Updates.Chain.LoadedOkSkipped"), ok, total, skipped, folder),
                        T("Updates.CheckTitle"));
                }
                else
                {
                    _dialogs.ShowInfo(string.Format(T("Updates.Chain.LoadedOk"), ok, total, folder),
                        T("Updates.CheckTitle"));
                }
            }
            catch (OperationCanceledException)
            {
                // Отмена из индикатора главного окна (issue #334 п.1): состояние Cancelled
                // уже выставлено менеджером; здесь только журнал.
                _logger.Info($"Загрузка цепочки обновлений «{_row.Name}» отменена пользователем.");
            }
            catch (Exception ex)
            {
                Services.BackgroundDownloadManager.Default.Fail(chainId);
                _logger.Error($"Ошибка загрузки цепочки обновлений «{_row.Name}»", ex);
                _dialogs.ShowError(T("Updates.NetworkError"), T("Updates.CheckTitle"));
            }
            finally
            {
                if (backgroundEntry.IsActive)
                    Services.BackgroundDownloadManager.Default.Complete(chainId);
                _row.IsChainDownloading = false;
                UpdateChainProgressDisplay();
            }
        }

        /// <summary>Колонка сводного ряда «Текущая / Последняя / Статус» (issue #352).
        /// <paramref name="extra"/> — необязательная третья строка (предупреждение).</summary>
        private static Grid MakeSummaryColumn(string labelKey, TextBlock value, int column, TextBlock? extra = null)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, column < 2 ? 14 : 0, 0) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (extra is not null)
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock
            {
                Text = labelKey,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Themes.ThemeBrushes.Bind(label, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            value.VerticalAlignment = VerticalAlignment.Center;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(value, 1);
            grid.Children.Add(value);

            if (extra is not null)
            {
                extra.FontSize = 12;
                extra.FontWeight = FontWeight.SemiBold;
                extra.TextWrapping = TextWrapping.Wrap;
                extra.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#DC2626"));
                Grid.SetRow(extra, 2);
                grid.Children.Add(extra);
            }

            return grid;
        }

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var title = new TextBlock
            {
                Text = T("Updates.CheckTitle"),
                FontSize = 15,
                FontWeight = FontWeight.SemiBold
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            // Карточка сведений о базе и результате проверки.
            var card = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(14, 12),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            Themes.ThemeBrushes.Bind(card, Border.BackgroundProperty, "CardBackgroundColorBrush");
            Themes.ThemeBrushes.Bind(card, Border.BorderBrushProperty, "BorderColorBrush");

            var fields = new StackPanel { Spacing = 8 };

            // Сводка в один ряд: Текущая версия | Последняя версия | Статус (issue #352).
            var summary = new Grid();
            summary.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)));
            summary.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)));
            summary.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            _currentVersionText.FontSize = 14;
            _currentVersionText.FontWeight = FontWeight.SemiBold;
            _latestVersionText.FontSize = 14;
            _latestVersionText.FontWeight = FontWeight.SemiBold;
            _statusText.FontSize = 14;
            _statusText.FontWeight = FontWeight.SemiBold;
            // issue #352: предупреждение «Версии нет на сайте» — третьей строкой в колонке
            // «Текущая версия»; видимость переключается в RefreshChainDisplay.
            _currentVersionMissingText.Text = T("Updates.Chain.VersionMissing");
            summary.Children.Add(MakeSummaryColumn(T("Updates.CurrentVersion"), _currentVersionText, 0,
                _currentVersionMissingText));
            summary.Children.Add(MakeSummaryColumn(T("Updates.LatestVersion"), _latestVersionText, 1));
            summary.Children.Add(MakeSummaryColumn(T("Updates.Status"), _statusText, 2));
            fields.Children.Add(summary);

            _baseNameText.FontWeight = FontWeight.SemiBold;
            _baseNameText.FontSize = 14;
            fields.Children.Add(MakeFieldRow(T("Updates.Name"), _baseNameText));

            fields.Children.Add(MakeFieldRow(T("Updates.Url"), _urlText));

            _errorText.TextWrapping = TextWrapping.Wrap;
            _errorText.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            fields.Children.Add(MakeFieldRow(string.Empty, _errorText));

            // Панель действий при ошибке авторизации (issue #323/#330/#334): имя учётной
            // записи ИТС, открыть login.1c.ru в браузере, справочник учётных данных ИТС.
            _authAccountText.FontSize = 12;
            _authAccountText.TextWrapping = TextWrapping.Wrap;
            Themes.ThemeBrushes.Bind(_authAccountText, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var openLoginButton = new Button { Content = T("Updates.OpenLoginPage"), MinWidth = 150, Height = 32 };
            openLoginButton.Styled(ControlThemes.SecondaryButton);
            openLoginButton.Click += (_, _) => OnOpenLoginClick();

            var itsAccountsButton = new Button { Content = T("Updates.OpenItsAccounts"), MinWidth = 150, Height = 32 };
            itsAccountsButton.Styled(ControlThemes.SecondaryButton);
            itsAccountsButton.Click += (_, _) => OnItsAccountsClick();

            var authButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 6, 0, 0)
            };
            authButtons.Children.Add(openLoginButton);
            authButtons.Children.Add(itsAccountsButton);
            _authActionsPanel.Children.Add(_authAccountText);
            _authActionsPanel.Children.Add(authButtons);
            fields.Children.Add(_authActionsPanel);

            // Пояснение пользователю: откуда берётся адрес каталога и где задать логин/пароль (issue #323).
            var helpText = new TextBlock
            {
                Text = T("Updates.HelpText"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            Themes.ThemeBrushes.Bind(helpText, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            fields.Children.Add(helpText);

            // Индикатор прогресса загрузки.
            _progressBar.Height = 6;
            _progressText.FontSize = 12;
            Themes.ThemeBrushes.Bind(_progressText, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var progressStack = new StackPanel { Spacing = 4 };
            progressStack.Children.Add(_progressBar);
            progressStack.Children.Add(_progressText);
            _progressPanel.Children.Add(progressStack);
            fields.Children.Add(_progressPanel);

            // Блок цепочки обновлений (issue #352): видим при наличии вариантов.
            var chainTitle = new TextBlock
            {
                Text = T("Updates.Chain.Title"),
                FontSize = 13,
                FontWeight = FontWeight.SemiBold
            };
            _chainSection.Children.Add(chainTitle);

            _chainHintText.Text = T("Updates.Chain.Hint");
            _chainHintText.FontSize = 12;
            _chainHintText.TextWrapping = TextWrapping.Wrap;
            Themes.ThemeBrushes.Bind(_chainHintText, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            _chainSection.Children.Add(_chainHintText);

            _chainStatusValue.FontSize = 12;
            _chainStatusValue.TextWrapping = TextWrapping.Wrap;
            Themes.ThemeBrushes.Bind(_chainStatusValue, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            _chainSection.Children.Add(_chainStatusValue);

            // Строка «Папка: <путь>» + кнопка «Открыть» (issue #352.2).
            var folderLabel = new TextBlock
            {
                Text = T("Updates.Chain.Folder"),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Themes.ThemeBrushes.Bind(folderLabel, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            _chainFolderValue.FontSize = 12;
            _chainFolderValue.VerticalAlignment = VerticalAlignment.Center;
            _chainFolderValue.TextTrimming = TextTrimming.CharacterEllipsis;
            Themes.ThemeBrushes.Bind(_chainFolderValue, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            _chainOpenFolderButton.Content = T("Updates.Chain.OpenFolder");
            _chainOpenFolderButton.MinWidth = 110;
            _chainOpenFolderButton.Height = 28;
            _chainOpenFolderButton.Margin = new Thickness(8, 0, 0, 0);
            _chainOpenFolderButton.Styled(ControlThemes.SecondaryButton);
            _chainOpenFolderButton.Click += (_, _) => OnOpenChainFolderClick();

            var folderGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            folderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            folderGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            folderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(folderLabel, 0);
            Grid.SetColumn(_chainFolderValue, 1);
            Grid.SetColumn(_chainOpenFolderButton, 2);
            folderGrid.Children.Add(folderLabel);
            folderGrid.Children.Add(_chainFolderValue);
            folderGrid.Children.Add(_chainOpenFolderButton);
            _chainFolderPanel.Children.Add(folderGrid);
            _chainSection.Children.Add(_chainFolderPanel);

            _chainSection.Children.Add(_chainRowsPanel);

            _chainProgressBar.Height = 6;
            _chainProgressText.FontSize = 12;
            _chainProgressText.TextWrapping = TextWrapping.Wrap;
            Themes.ThemeBrushes.Bind(_chainProgressText, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var chainProgressStack = new StackPanel { Spacing = 4 };
            chainProgressStack.Children.Add(_chainProgressBar);
            chainProgressStack.Children.Add(_chainProgressText);
            _chainProgressPanel.Children.Add(chainProgressStack);
            _chainSection.Children.Add(_chainProgressPanel);

            fields.Children.Add(_chainSection);

            card.Child = new ScrollViewer
            {
                Content = fields,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Grid.SetRow(card, 1);
            grid.Children.Add(card);

            // Нижняя панель: «Проверить», «Скачать», закрыть.
            var bottom = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            bottom.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10
            };
            // Ширины кнопок не фиксированы (только минимальные): при любом шрифте и масштабе
            // кнопка растягивается под текст и не обрезается (issue #323).
            _checkButton = new Button { Content = T("Updates.Check"), MinWidth = 160, Height = 40 };
            _checkButton.Styled(ControlThemes.SecondaryButton);
            _checkButton.Click += (_, _) => OnCheckClick();

            _downloadChainButton = new Button
            {
                Content = T("Updates.Chain.Download"),
                MinWidth = 170,
                Height = 40,
                IsVisible = false,
                IsEnabled = false,
            };
            _downloadChainButton.Styled(ControlThemes.DialogConfirmButton);
            _downloadChainButton.Click += (_, _) => OnDownloadChainClick();

            _downloadButton = new Button { Content = T("Updates.Download"), MinWidth = 150, Height = 40, IsEnabled = false };
            _downloadButton.Styled(ControlThemes.DialogConfirmButton);
            _downloadButton.Click += (_, _) => OnDownloadRow(_row);

            var close = BuildCancelActionButton(150, 40);
            close.Click += (_, _) => Close();

            buttons.Children.Add(_checkButton);
            buttons.Children.Add(_downloadChainButton);
            buttons.Children.Add(_downloadButton);
            buttons.Children.Add(close);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 2);
            grid.Children.Add(bottom);

            return grid;
        }

        /// <summary>Строка «подпись / значение» карточки.</summary>
        private static Grid MakeFieldRow(string labelKey, TextBlock value)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            var label = new TextBlock
            {
                Text = labelKey,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Themes.ThemeBrushes.Bind(label, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            value.VerticalAlignment = VerticalAlignment.Center;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            return grid;
        }
    }
}
#endif