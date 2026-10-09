using System;
using System.Windows;
using Configuration_Management.Localization;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Окно «Серверы 1С» (0.3.9.124, Windows/WPF): встроенный монитор серверов 1С
    /// через утилиту rac — панель подключения (адрес/порт/логин/пароль), выбор
    /// кластера, вкладки «Рабочие процессы / Сеансы / Соединения / Блокировки /
    /// Информация о кластере» и статус-строка. Вся логика — в чистой ViewModel
    /// <see cref="ServerMonitorViewModel"/>. Пароль не сохраняется на диск
    /// (решение планирования, раздел 5 плана 0.3.9.123–126): PasswordBox не
    /// биндится, значение передаётся в VM при подключении.
    /// </summary>
    public partial class ServerMonitorWindow : Window
    {
        private readonly ServerMonitorViewModel _vm;

        public ServerMonitorWindow()
        {
            InitializeComponent();

            var rac = AppServices.GetRequiredService<IRacClient>();
            var dialogs = AppServices.GetRequiredService<IDialogService>();

            _vm = new ServerMonitorViewModel(
                rac,
                dialogs,
                action => Application.Current?.Dispatcher.BeginInvoke(action));

            // Начальные адрес/порт/логин — из настроек приложения (пароль НЕ сохраняется).
            LoadSavedConnectionSettings();

            DataContext = _vm;
            Title = LocalizationManager.T("ServerMonitor.Title");
            ProcessesGrid.ItemsSource = _vm.Processes;
            SessionsGrid.ItemsSource = _vm.Sessions;
            ConnectionsGrid.ItemsSource = _vm.Connections;
            LocksGrid.ItemsSource = _vm.Locks;
            JobsGrid.ItemsSource = _vm.FilteredJobs;

            // Таймер автообновления останавливается при закрытии окна (без утечки).
            Closed += (_, _) => _vm.Dispose();
        }

        private async void OnConnect_Click(object sender, RoutedEventArgs e)
        {
            // PasswordBox не биндится (пароль живёт только в памяти окна) — передаём вручную.
            _vm.Password = PasswordBox.Password;
            await _vm.ConnectAsync();
            if (_vm.HasConnected)
                SaveRacSettings();
        }

        private void OnRefresh_Click(object sender, RoutedEventArgs e) => _vm.Refresh();

        private void OnTerminateSession_Click(object sender, RoutedEventArgs e) =>
            _vm.TerminateSessionCommand.Execute(null);

        private void OnDisconnectConnection_Click(object sender, RoutedEventArgs e) =>
            _vm.DisconnectConnectionCommand.Execute(null);

        private void OnPauseJob_Click(object sender, RoutedEventArgs e) =>
            _vm.PauseJobCommand.Execute(null);

        private void OnResumeJob_Click(object sender, RoutedEventArgs e) =>
            _vm.ResumeJobCommand.Execute(null);

        private void OnJobDetails_Click(object sender, RoutedEventArgs e)
        {
            var row = _vm.SelectedJob;
            if (row is null)
                return;
            new JobDetailsWindow(row.DetailsText) { Owner = this }.ShowDialog();
        }

        /// <summary>
        /// «Сохранить изменения» на вкладке «Информация о кластере» (issue #324, C3):
        /// rac «cluster update» по изменённым параметрам, перечитывание данных.
        /// </summary>
        private void OnSaveClusterInfo_Click(object sender, RoutedEventArgs e) =>
            _vm.SaveClusterPropertiesCommand.Execute(null);

        private void OnClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// «Диагностика сети…» (0.3.9.232, функция 12): открывает окно диагностики
        /// с текущими адресом/портом монитора. Не требует успешного подключения rac —
        /// диагностика нужна именно до подключения.
        /// </summary>
        private void OnDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var target = NetworkDiagnosticsTargets.FromServerMonitor(_vm.ServerAddress, _vm.ServerPort);
            var vm = new NetworkDiagnosticsViewModel(
                AppServices.GetRequiredService<INetworkDiagnosticsService>(),
                target,
                action => Application.Current?.Dispatcher.BeginInvoke(action));
            new NetworkDiagnosticsWindow(vm) { Owner = this }.ShowDialog();
        }

        /// <summary>
        /// Сохраняет адрес/порт/логин после успешного подключения (без пароля —
        /// см. решения планирования; пароль rac необратим в PBKDF2 и не хранится).
        /// </summary>
        private void SaveRacSettings()
        {
            try
            {
                var repository = AppServices.GetRequiredService<IInfobaseRepository>();
                var settings = repository.LoadSettings();
                settings.RacServerAddress = _vm.ServerAddress;
                settings.RacServerPort = _vm.ServerPort;
                settings.RacUserName = _vm.UserName;
                repository.SaveSettings(settings);
            }
            catch
            {
                // Сохранение настроек не критично для работы окна.
            }
        }

        private void LoadSavedConnectionSettings()
        {
            try
            {
                var settings = AppServices.GetRequiredService<IInfobaseRepository>().LoadSettings();
                _vm.ServerAddress = string.IsNullOrWhiteSpace(settings.RacServerAddress)
                    ? "localhost"
                    : settings.RacServerAddress;
                if (settings.RacServerPort > 0)
                    _vm.ServerPort = settings.RacServerPort;
                _vm.UserName = settings.RacUserName ?? string.Empty;
            }
            catch
            {
                // Без сохранённых настроек остаются дефолты VM.
            }
        }
    }
}