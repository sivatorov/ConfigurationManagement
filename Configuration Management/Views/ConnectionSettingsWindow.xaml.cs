#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог настройки подключения к информационной базе.
    /// </summary>
    public partial class ConnectionSettingsWindow : Window
    {
        private readonly ConnectionSettingsViewModel _viewModel;
        private readonly IDialogService _dialogs =
            AppServices.GetRequiredService<IDialogService>();
        /// <summary>Пользовательские параметры запуска для справочника (issue #141).</summary>
        private readonly IReadOnlyList<string> _customLaunchParameters;
        /// <summary>Обратный вызов сохранения пользовательских параметров запуска (issue #141).</summary>
        private readonly Action<IReadOnlyList<string>>? _onCustomLaunchParametersChanged;

        /// <summary>
        /// Создаёт диалог настройки подключения.
        /// </summary>
        /// <param name="infobase">База для редактирования. Если null — создаётся новая база.</param>
        /// <param name="groups">Список доступных групп для выбора.</param>
        /// <param name="installedPlatformVersions">Список установленных версий платформы 1С.</param>
        /// <param name="defaultGroupPath">Путь группы по умолчанию для новой базы.</param>
        /// <param name="availableServers">Список серверов 1С из других баз списка для выпадающего списка.</param>
        /// <param name="availablePorts">Список портов серверов 1С из других баз списка для выпадающего списка.</param>
        /// <param name="customLaunchParameters">Пользовательские параметры запуска для справочника (необязательно).</param>
        /// <param name="onCustomLaunchParametersChanged">Обратный вызов сохранения пользовательских параметров (необязательно).</param>
        /// <param name="availableRepositoryServers">Список доступных серверов хранилища конфигурации (необязательно).</param>
        public ConnectionSettingsWindow(Infobase? infobase = null, IEnumerable<Group>? groups = null,
            IEnumerable<string>? installedPlatformVersions = null, string? defaultGroupPath = null,
            IEnumerable<string>? availableServers = null, IEnumerable<int>? availablePorts = null,
            IReadOnlyList<string>? customLaunchParameters = null,
            Action<IReadOnlyList<string>>? onCustomLaunchParametersChanged = null,
            IEnumerable<string>? availableRepositoryServers = null,
            IEnumerable<string>? availableTags = null)
        {
            _customLaunchParameters = customLaunchParameters ?? Array.Empty<string>();
            _onCustomLaunchParametersChanged = onCustomLaunchParametersChanged;
            InitializeComponent();

            // ESC сначала закрывает открытые всплывающие подсказки, а только потом окно (issue #270).
            ToolTipCloser.Register();
            PreviewKeyDown += OnToolTipEscPreviewKeyDown;

            // Подсказки скрываются при потере фокуса окна (issue #275).
            Deactivated += (_, _) => ToolTipCloser.CloseAll();

            Loaded += (_, _) =>
            {
                SyncPasswordBoxFromViewModel();
                SyncRepositoryPasswordBoxFromViewModel();
                SyncConfiguratorPasswordBoxFromViewModel();
            };
            _viewModel = new ConnectionSettingsViewModel(groups);
            _viewModel.SetInstalledPlatformVersions(installedPlatformVersions ?? new List<string>());
            _viewModel.SetAvailableServers(availableServers);
            _viewModel.SetAvailablePorts(availablePorts);
            _viewModel.SetAvailableRepositoryServers(availableRepositoryServers);
            // Существующие теги всех баз — для автодополнения при добавлении (issue #283).
            _viewModel.SetAvailableTags(availableTags);
            if (infobase != null)
            {
                _viewModel.LoadFrom(infobase);

                // Сохраняем служебные поля существующей базы, чтобы они не сбрасывались
                // при редактировании (избранное, закрепление, теги, дата последнего запуска, метаданные).
                Result.IsFavorite = infobase.IsFavorite;
                Result.IsPinned = infobase.IsPinned;
                Result.Tags = new List<string>(infobase.Tags);
                Result.LastLaunchDate = infobase.LastLaunchDate;
                Result.MetadataRoot = infobase.MetadataRoot;
            }
            else if (!string.IsNullOrWhiteSpace(defaultGroupPath))
            {
                // Новая база: подставляем группу, в которой сейчас находится курсор.
                _viewModel.Group = defaultGroupPath;
                _viewModel.SelectedGroup = GroupHierarchyHelper.FindByFullPath(defaultGroupPath, _viewModel.Groups);
            }
            DataContext = _viewModel;
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConnectionSettingsViewModel.Password))
                    SyncPasswordBoxFromViewModel();
                else if (e.PropertyName == nameof(ConnectionSettingsViewModel.RepositoryPassword))
                    SyncRepositoryPasswordBoxFromViewModel();
                else if (e.PropertyName == nameof(ConnectionSettingsViewModel.ConfiguratorPassword))
                    SyncConfiguratorPasswordBoxFromViewModel();
                else if (e.PropertyName == nameof(ConnectionSettingsViewModel.IsOs64Bit))
                    UpdateOsArchitectureHint();
            };
            LocalizationManager.Instance.LanguageChanged += (_, _) => UpdateOsArchitectureHint();
            UpdateOsArchitectureHint();
            // #268: две настройки запуска («Режим запуска по умолчанию» и «Действие по двойному
            // клику») сведены к одной — «Действие по двойному клику». Режим запуска по умолчанию
            // на запуск не влиял (двойной клик использует ResolveDoubleClickAction), поэтому его
            // комбобокс убран из окна свойств базы.
            InitDoubleClickActionCombo();
        }

        /// <summary>Добавляет тег из поля ввода (кнопка «Добавить») — issue #283.</summary>
        private void OnAddTag_Click(object sender, RoutedEventArgs e) => _viewModel.AddTag();

        /// <summary>Добавляет тег по нажатию Enter в поле ввода — issue #283.</summary>
        private void OnTagInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _viewModel.AddTag();
                e.Handled = true;
            }
        }

        /// <summary>Удаляет тег по клику на «×» в чипе — issue #283.</summary>
        private void OnRemoveTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag)
                _viewModel.RemoveTag(tag);
        }

        /// <summary>
        /// Preview-обработчик ESC (issue #270): первый ESC закрывает открытые всплывающие
        /// подсказки и помечает событие обработанным, повторный ESC закрывает окно как обычно.
        /// </summary>
        private void OnToolTipEscPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape &&
                Keyboard.Modifiers == ModifierKeys.None &&
                ToolTipCloser.CloseAll())
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// Заполняет комбобокс «Действие по двойному щелчку» (функция №28 StartManager)
        /// и выставляет текущее значение базы. Пустая строка — использовать глобальную настройку.
        /// </summary>
        private void InitDoubleClickActionCombo()
        {
            if (DoubleClickActionCombo is null || _viewModel is null) return;
            DoubleClickActionCombo.ItemsSource = new[]
            {
                LocalizationManager.T("Connection.DblClickGlobal"),
                LocalizationManager.T("Connection.DefaultLaunchEnterprise"),
                LocalizationManager.T("Connection.DefaultLaunchConfigurator"),
                LocalizationManager.T("Connection.DblClickNone")
            };
            DoubleClickActionCombo.SelectedIndex = (_viewModel.DoubleClickAction ?? string.Empty).Trim() switch
            {
                Models.DoubleClickAction.Enterprise => 1,
                Models.DoubleClickAction.Configurator => 2,
                Models.DoubleClickAction.None => 3,
                _ => 0
            };
        }

        /// <summary>Обработчик смены «действия по двойному щелчку»: пишет каноническое значение в ViewModel.</summary>
        private void OnDoubleClickActionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_viewModel is null || sender is not System.Windows.Controls.ComboBox combo)
                return;
            _viewModel.DoubleClickAction = combo.SelectedIndex switch
            {
                1 => Models.DoubleClickAction.Enterprise,
                2 => Models.DoubleClickAction.Configurator,
                3 => Models.DoubleClickAction.None,
                _ => Models.DoubleClickAction.Default
            };
        }

        /// <summary>Открывает диалог выбора внешней обработки (.epf/.erf) (функция №25 StartManager).</summary>
        private void OnBrowseExternalProcessing_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = LocalizationManager.T("Connection.ExternalProcessingPickTitle"),
                Filter = LocalizationManager.T("Connection.ExternalProcessingFilter"),
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) == true && ExternalProcessingPathBox is not null)
            {
                ExternalProcessingPathBox.Text = dialog.FileName;
                _viewModel.ExternalProcessingPath = dialog.FileName;
            }
        }

        /// <summary>
        /// Обновляет текст подсказки о разрядности ОС. Управляется в коде,
        /// т.к. внутри Style.Triggers нельзя использовать привязки ({loc:Loc}).
        /// </summary>
        private void UpdateOsArchitectureHint()
        {
            if (OsArchitectureHintText is null || _viewModel is null) return;
            OsArchitectureHintText.Text = _viewModel.IsOs64Bit
                ? LocalizationManager.T("Connection.Os64Text")
                : LocalizationManager.T("Connection.Os32Text");
        }

        /// <summary>
        /// Открывает дерево групп для выбора.
        /// </summary>
        private void OnSelectGroup_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new GroupPickerWindow(
                _viewModel.Groups,
                currentGroupId: _viewModel.SelectedGroup?.Id,
                allowNone: true,
                noneLabel: LocalizationManager.T("Connection.NoGroup"),
                kind: GroupPickerObjectKind.Infobase)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                _viewModel.SelectedGroup = dialog.ResultGroup;
                if (dialog.ResultGroup is null)
                    _viewModel.Group = string.Empty;
            }
        }

        /// <summary>
        /// Открывает диалог выбора каталога для файловой базы и подставляет путь в поле.
        /// </summary>
        private void OnBrowseFilePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = LocalizationManager.T("Connection.ChooseFolderTitle"),
                Multiselect = false
            };
            var current = _viewModel.FilePath;
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog(this) == true)
                _viewModel.FilePath = dialog.FolderName;
        }

        /// <summary>
        /// Копирует ID базы в буфер обмена.
        /// </summary>
        private void OnCopyId_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_viewModel.Id))
            {
                Clipboard.SetText(_viewModel.Id);
            }
        }

        /// <summary>
        /// Открывает окно ввода строки подключения 1С. Если в буфере обмена лежит
        /// строка, удовлетворяющая критериям ссылки на информационную базу, она
        /// сразу подставляется в поле ввода. После подтверждения строка разбивается
        /// по полям настроек базы.
        /// </summary>
        private void OnPasteConnectionString_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ConnectionStringInputWindow(_viewModel.ConnectionString)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return;

            // Применяем разобранную строку к полям ViewModel.
            _viewModel.ApplyConnectionString(dialog.Result);

            // Обновляем значение поля строки подключения во ViewModel,
            // чтобы оно совпадало с применённым значением.
            _viewModel.ConnectionString = dialog.Result ?? string.Empty;

            _dialogs.ShowInfo(LocalizationManager.T("Connection.PasteSuccess"),
                LocalizationManager.T("Connection.PasteSuccessTitle"));
        }

        /// <summary>
        /// Генерирует новый идентификатор базы в формате, совместимом с ibases.v8i (GUID без скобок).
        /// </summary>
        private void OnGenerateId_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Id = Guid.NewGuid().ToString("D");
        }

        /// <summary>
        /// Возвращает отредактированную информационную базу.
        /// </summary>
        public Infobase Result { get; private set; } = new();

        private void OnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OnSave_Click(object sender, RoutedEventArgs e)
        {
            // Не допускаем значений, которые не могут быть переданы в командную строку 1С
            // (двойная кавычка / управляющий символ) — иначе запуск молча пропускал бы аргумент
            // (issue #205).
            var validationError = _viewModel.ValidateCliArgs();
            if (validationError is not null)
            {
                _dialogs.ShowWarning(validationError,
                    LocalizationManager.T("Connection.InvalidCliCharTitle"));
                return;
            }

            // Применяем значения из ViewModel к результату.
            _viewModel.ApplyTo(Result);

            // Если ID базы 1С не задан — пытаемся найти его в файле ibases.v8i
            // по имени базы или строке подключения. Это позволяет корректно
            // очищать кеш 1С точечно по ID даже для баз, созданных вручную.
            if (string.IsNullOrWhiteSpace(Result.Id))
            {
                var id = IbasesV8iImporter.FindId(Result.Name, Result.Connection.ToConnectionString());
                if (!string.IsNullOrWhiteSpace(id))
                {
                    Result.Id = id;
                }
                else
                {
                    // ID не найден ни во ViewModel, ни в ibases.v8i —
                    // назначаем новый идентификатор, чтобы у базы он был всегда
                    // (нужен для точечной очистки кеша и экспорта в ibases.v8i).
                    Result.Id = Guid.NewGuid().ToString("D");
                }
            }

            DialogResult = true;
        }

        /// <summary>
        /// Вставляет скопированное из 1С единое поле подключения к хранилищу
        /// (например «tcp://server:1542/ИмяХранилища») и разделяет его на
        /// адрес сервера и имя хранилища (issue #140).
        /// </summary>
        private void OnPasteRepositorySplit_Click(object sender, RoutedEventArgs e)
        {
            var text = System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText().Trim()
                : string.Empty;
            if (string.IsNullOrEmpty(text))
                return;
            _viewModel.SplitRepositoryConnectionString(text);
        }

        private void OnLaunchParameters_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new LaunchParametersWindow(
                _viewModel.LaunchParameters,
                _customLaunchParameters,
                _onCustomLaunchParametersChanged)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                _viewModel.LaunchParameters = dialog.Result;
            }
        }

        /// <summary>
        /// Открывает окно выбора версии платформы 1С со сгруппированными версиями.
        /// Выбранный вариант вида «8.3.25.1234 (64)» разбирается на чистую версию
        /// и разрядность, которые сохраняются в соответствующие свойства.
        /// </summary>
        private void OnPlatformSettings_Click(object sender, RoutedEventArgs e)
        {
            // Композицию варианта «версия (разрядность)» строит общий парсер (ПЗ-5).
            var current = OneCLaunchArgumentParser.ComposePlatformVersionDisplay(
                _viewModel.PlatformVersion, _viewModel.Architecture);

            var dialog = new PlatformVersionPickerWindow(_viewModel.InstalledPlatformVersions, current)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
                return;

            var result = (dialog.Result ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(result))
                return;

            PlatformVersionService.ParseVariant(result, out var version, out var architecture);
            // Подставляем чистую версию; если разбор не удался — всю строку выбора.
            _viewModel.PlatformVersion = string.IsNullOrWhiteSpace(version) ? result : version;
            // Разрядность меняем только если в выбранной строке она явно указана «(32)/(64)».
            if (result.Contains('(') && (architecture == "32" || architecture == "64"))
                _viewModel.Architecture = architecture;
        }

        /// <summary>
        /// Определяет имя и версию конфигурации по настройкам подключения
        /// (COM-коннектор на Windows, эвристика по файлу базы на Linux)
        /// и заполняет поля (issue #174). Чтение выполняется в фоновом потоке
        /// с модальным диалогом прогресса, чтобы недоступный сервер не «замораживал»
        /// окно настроек на весь таймаут (~8 с).
        /// </summary>
        private async void OnDetectConfiguration_Click(object sender, RoutedEventArgs e)
        {
            // Снимаем оба вердикта о недоступности COM (кэш реестра и сессионную защёлку
            // процесса-агента): причина сбоя могла быть разовой или уже устранённой, а иначе
            // кнопка «Определить» до перезапуска приложения молча отвечала бы отказом (issue #174).
            OneCComConnector.ResetComVerdicts();

            var progress = new DetectConfigProgressWindow { Owner = this };
            progress.SetStage(BuildDetectConnectStageMessage());
            progress.Show();
            IsEnabled = false;
            try
            {
                OneCConfigInfo? info;
                try
                {
                    info = await Task.Run(() => _viewModel.ReadConfiguration(progress.SetStage));
                }
                catch
                {
                    info = null;
                }

                if (_viewModel.ApplyConfiguration(info)) return;

                ShowDetectConfigurationFailure();
            }
            finally
            {
                IsEnabled = true;
                progress.Close();
            }
        }

        /// <summary>
        /// Строит текст этапа «создание COM-подключения» для диалога прогресса (issue #174):
        /// с фактическим ProgID (например, «V83.COMConnector») и версией платформы базы,
        /// чтобы было видно, какой именно COM-коннектор создаётся.
        /// </summary>
        private string BuildDetectConnectStageMessage()
        {
            var version = _viewModel.PlatformVersion;
            var progId = ConfigurationInfoService.LastUsedProgId;
            var hasProgId = !string.IsNullOrWhiteSpace(progId);
            var hasVersion = !string.IsNullOrWhiteSpace(version);

            if (hasProgId && hasVersion)
                return string.Format(LocalizationManager.T("Connection.DetectStageConnectWithProgIdFormat"), progId, version);
            if (hasProgId)
                return string.Format(LocalizationManager.T("Connection.DetectStageConnectWithProgIdNoVersion"), progId);
            if (hasVersion)
                return string.Format(LocalizationManager.T("Connection.DetectStageConnectFormat"), version);
            return LocalizationManager.T("Connection.DetectStageConnectNoVersion");
        }

        /// <summary>
        /// Детальная диагностика неудачи определения свойств конфигурации (issue #174):
        /// помимо общей фразы показывает текст последней ошибки COM и фактически
        /// использованный ProgID/версию платформы, чтобы было видно, какой именно
        /// COM-коннектор пробовался (например, шаблон имени дал неправильный ProgID).
        /// </summary>
        private void ShowDetectConfigurationFailure()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(LocalizationManager.T("Connection.DetectConfigFailed"));

            var comError = ConfigurationInfoService.LastComError;
            if (!string.IsNullOrWhiteSpace(comError))
                sb.AppendLine().AppendLine(comError);

            var progId = ConfigurationInfoService.LastUsedProgId;
            if (!string.IsNullOrWhiteSpace(progId))
                sb.AppendLine().AppendLine($"Использован COM-коннектор: {progId}");

            var platformVersion = ConfigurationInfoService.LastUsedPlatformVersion;
            if (!string.IsNullOrWhiteSpace(platformVersion))
                sb.AppendLine().AppendLine($"Версия платформы базы: {platformVersion}");

            // Если имя коннектора не соответствует версии платформы базы (например, V85
            // для базы 8.3.x), поясняем причину и подсказываем решение (issue #174).
            if (!ConfigurationInfoService.ProgIdMatchesPlatform(progId, platformVersion))
            {
                sb.AppendLine().AppendLine(
                    "Внимание: имя COM-коннектора не соответствует версии платформы базы. " +
                    "Если определение свойств даёт неверный результат, задайте шаблон имени " +
                    "COM-коннектора в настройках приложения.");
            }

            _dialogs.ShowInfo(
                sb.ToString().TrimEnd(),
                LocalizationManager.T("Connection.DetectConfigTitle"));
        }

        /// <summary>Синхронизация PasswordBox → ViewModel (пароль не биндится напрямую).</summary>
        private void OnPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || sender is not PasswordBox pb) return;
            if (_isSyncingPassword) return;
            _viewModel.Password = pb.Password;
        }

        private bool _isSyncingPassword;
        private bool _isSyncingRepositoryPassword;
        private bool _isSyncingConfiguratorPassword;

        // Флаги показа пароля «глазом» (issue #169). WPF-PasswordBox не умеет снимать
        // маску напрямую, поэтому при показе поверх скрываем PasswordBox и показываем
        // редактируемое текстовое поле; правки в нём синхронизируются обратно (issue #211).
        private bool _isPasswordRevealed;
        private bool _isRepositoryPasswordRevealed;
        private bool _isConfiguratorPasswordRevealed;

        /// <summary>
        /// Переключает видимость пароля между PasswordBox и редактируемым полем показа (issue #211).
        /// При показе копируем текущее значение в поле показа; копирование выполняется под флагом
        /// синхронизации, чтобы не спровоцировать рекурсию из TextChanged-обработчика.
        /// </summary>
        private void ApplyReveal(PasswordBox box, TextBox reveal, bool show, ref bool syncingFlag)
        {
            box.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            reveal.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            syncingFlag = true;
            try
            {
                reveal.Text = box.Password;
            }
            finally
            {
                syncingFlag = false;
            }
        }

        /// <summary>Копирует пароль в буфер обмена.</summary>
        private static void CopyPassword(PasswordBox box)
        {
            if (!string.IsNullOrEmpty(box.Password))
                System.Windows.Clipboard.SetText(box.Password);
        }

        /// <summary>Заполняет PasswordBox из ViewModel без рекурсии событий.</summary>
        private void SyncPasswordBoxFromViewModel()
        {
            if (PasswordBox is null || _viewModel is null) return;
            _isSyncingPassword = true;
            try
            {
                if (PasswordBox.Password != (_viewModel.Password ?? string.Empty))
                    PasswordBox.Password = _viewModel.Password ?? string.Empty;
            }
            finally
            {
                _isSyncingPassword = false;
            }
        }

        /// <summary>Синхронизация PasswordBox хранилища → ViewModel (пароль не биндится напрямую).</summary>
        private void OnRepositoryPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || sender is not PasswordBox pb) return;
            if (_isSyncingRepositoryPassword) return;
            _viewModel.RepositoryPassword = pb.Password;
        }

        /// <summary>Заполняет PasswordBox хранилища из ViewModel без рекурсии событий.</summary>
        private void SyncRepositoryPasswordBoxFromViewModel()
        {
            if (RepositoryPasswordBox is null || _viewModel is null) return;
            _isSyncingRepositoryPassword = true;
            try
            {
                if (RepositoryPasswordBox.Password != (_viewModel.RepositoryPassword ?? string.Empty))
                    RepositoryPasswordBox.Password = _viewModel.RepositoryPassword ?? string.Empty;
            }
            finally
            {
                _isSyncingRepositoryPassword = false;
            }
        }

        /// <summary>Синхронизация PasswordBox Конфигуратора → ViewModel (пароль не биндится напрямую).</summary>
        private void OnConfiguratorPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || sender is not PasswordBox pb) return;
            if (_isSyncingConfiguratorPassword) return;
            _viewModel.ConfiguratorPassword = pb.Password;
        }

        /// <summary>Заполняет PasswordBox Конфигуратора из ViewModel без рекурсии событий.</summary>
        private void SyncConfiguratorPasswordBoxFromViewModel()
        {
            if (ConfiguratorPasswordBox is null || _viewModel is null) return;
            _isSyncingConfiguratorPassword = true;
            try
            {
                if (ConfiguratorPasswordBox.Password != (_viewModel.ConfiguratorPassword ?? string.Empty))
                    ConfiguratorPasswordBox.Password = _viewModel.ConfiguratorPassword ?? string.Empty;
            }
            finally
            {
                _isSyncingConfiguratorPassword = false;
            }
        }

        // ===================== Показ и копирование пароля (issue #169) =====================

        private void OnPasswordReveal_Click(object sender, RoutedEventArgs e)
        {
            _isPasswordRevealed = !_isPasswordRevealed;
            ApplyReveal(PasswordBox, PasswordRevealTextBox, _isPasswordRevealed, ref _isSyncingPassword);
        }

        private void OnPasswordCopy_Click(object sender, RoutedEventArgs e) => CopyPassword(PasswordBox);

        private void OnRepositoryPasswordReveal_Click(object sender, RoutedEventArgs e)
        {
            _isRepositoryPasswordRevealed = !_isRepositoryPasswordRevealed;
            ApplyReveal(RepositoryPasswordBox, RepositoryPasswordRevealTextBox, _isRepositoryPasswordRevealed, ref _isSyncingRepositoryPassword);
        }

        private void OnRepositoryPasswordCopy_Click(object sender, RoutedEventArgs e) => CopyPassword(RepositoryPasswordBox);

        private void OnConfiguratorPasswordReveal_Click(object sender, RoutedEventArgs e)
        {
            _isConfiguratorPasswordRevealed = !_isConfiguratorPasswordRevealed;
            ApplyReveal(ConfiguratorPasswordBox, ConfiguratorPasswordRevealTextBox, _isConfiguratorPasswordRevealed, ref _isSyncingConfiguratorPassword);
        }

        private void OnConfiguratorPasswordCopy_Click(object sender, RoutedEventArgs e) => CopyPassword(ConfiguratorPasswordBox);

        // ============ Синхронизация правок из поля показа пароля (issue #211) ============
        // Поле показа редактируемо, поэтому изменения в нём должны попадать и в скрытый
        // PasswordBox (чтобы значение не терялось при скрытии) и во ViewModel. Выполняется
        // под флагом _isSyncing*, чтобы исключить рекурсию событий.

        /// <summary>Синхронизация правок в поле показа пароля → PasswordBox и ViewModel (issue #211).</summary>
        private void OnPasswordReveal_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewModel is null || sender is not TextBox tb) return;
            if (_isSyncingPassword) return;
            _isSyncingPassword = true;
            try
            {
                var text = tb.Text ?? string.Empty;
                if (PasswordBox.Password != text)
                    PasswordBox.Password = text;
                if (_viewModel.Password != text)
                    _viewModel.Password = text;
            }
            finally
            {
                _isSyncingPassword = false;
            }
        }

        /// <summary>Синхронизация правок в поле показа пароля хранилища (issue #211).</summary>
        private void OnRepositoryPasswordReveal_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewModel is null || sender is not TextBox tb) return;
            if (_isSyncingRepositoryPassword) return;
            _isSyncingRepositoryPassword = true;
            try
            {
                var text = tb.Text ?? string.Empty;
                if (RepositoryPasswordBox.Password != text)
                    RepositoryPasswordBox.Password = text;
                if (_viewModel.RepositoryPassword != text)
                    _viewModel.RepositoryPassword = text;
            }
            finally
            {
                _isSyncingRepositoryPassword = false;
            }
        }

        /// <summary>Синхронизация правок в поле показа пароля конфигуратора (issue #211).</summary>
        private void OnConfiguratorPasswordReveal_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewModel is null || sender is not TextBox tb) return;
            if (_isSyncingConfiguratorPassword) return;
            _isSyncingConfiguratorPassword = true;
            try
            {
                var text = tb.Text ?? string.Empty;
                if (ConfiguratorPasswordBox.Password != text)
                    ConfiguratorPasswordBox.Password = text;
                if (_viewModel.ConfiguratorPassword != text)
                    _viewModel.ConfiguratorPassword = text;
            }
            finally
            {
                _isSyncingConfiguratorPassword = false;
            }
        }
    }
}
#endif