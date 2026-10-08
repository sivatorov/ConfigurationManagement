using System;
using System.Collections.ObjectModel;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Строка окна проверки обновлений конфигурации для выбранной ИБ (<c>UpdateCheckWindow</c>, F9).
/// Привязана к <see cref="Infobase"/>: текущая версия читается/пишется в модель базы,
/// последняя версия и статус — результат сетевой проверки. Содержит команду загрузки.
/// </summary>
public class UpdateCheckRowViewModel : ViewModelBase
{
    /// <summary>Информационная база, для которой выполняется проверка.</summary>
    public Infobase Infobase { get; }

    /// <summary>Имя базы (неизменяемо в рамках окна).</summary>
    public string Name => Infobase.Name;

    /// <summary>Текущая версия конфигурации базы.</summary>
    public string CurrentVersion
    {
        get => Infobase.ConfigurationVersion ?? string.Empty;
        set
        {
            if (Infobase.ConfigurationVersion == value)
                return;
            Infobase.ConfigurationVersion = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    private string _latestVersion = string.Empty;

    /// <summary>Последняя версия, обнаруженная в каталоге релизов.</summary>
    public string LatestVersion
    {
        get => _latestVersion;
        set => SetProperty(ref _latestVersion, value ?? string.Empty);
    }

    private ConfigUpdateStatus _status;

    /// <summary>Итоговый статус проверки.</summary>
    public ConfigUpdateStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasNewer));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(CanDownload));
            }
        }
    }

    private string _url = string.Empty;

    /// <summary>Адрес каталога релизов, по которому выполнялась проверка.</summary>
    public string Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value ?? string.Empty))
                OnPropertyChanged(nameof(CanDownload));
        }
    }

    private string _error = string.Empty;

    /// <summary>Текст ошибки (ключ локализации либо свободный текст).</summary>
    public string Error
    {
        get => _error;
        set => SetProperty(ref _error, value ?? string.Empty);
    }

    private bool _isChecking;

    /// <summary>Выполняется ли сетевая проверка.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (SetProperty(ref _isChecking, value))
            {
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(CanDownloadChain));
            }
        }
    }

    private bool _isDownloading;

    /// <summary>Идёт ли загрузка дистрибутива.</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(CanDownloadChain));
            }
        }
    }

    private double _progress;

    /// <summary>Прогресс загрузки (0..1).</summary>
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    /// <summary>Варианты цепочки обновлений (issue #352); заполняются после проверки,
    /// если последняя версия не обновляется напрямую с текущей.</summary>
    public ObservableCollection<UpdateChainVariantViewModel> ChainVariants { get; } = new();

    private bool _hasChain;

    /// <summary>True — в таблице есть варианты цепочки (видна таблица и кнопка
    /// «Скачать цепочку»).</summary>
    public bool HasChain
    {
        get => _hasChain;
        private set
        {
            if (SetProperty(ref _hasChain, value))
                OnPropertyChanged(nameof(CanDownloadChain));
        }
    }

    private bool _isDirectUpdate;

    /// <summary>True — текущую версию можно обновить напрямую до последней.</summary>
    public bool IsDirectUpdate
    {
        get => _isDirectUpdate;
        private set
        {
            if (SetProperty(ref _isDirectUpdate, value))
                OnPropertyChanged(nameof(ChainStatusText));
        }
    }

    private bool _hasSourceData = true;

    private bool _currentVersionMissing;

    /// <summary>True — в каталоге есть данные «Список версий»; false — совместимость
    /// неизвестна, доступна только загрузка последней версии.</summary>
    public bool HasSourceData
    {
        get => _hasSourceData;
        private set
        {
            if (SetProperty(ref _hasSourceData, value))
                OnPropertyChanged(nameof(ChainStatusText));
        }
    }

    private string _chainStatusText = string.Empty;

    /// <summary>Локализованный статус цепочки: прямое обновление / требуется цепочка /
    /// цепочка не строится / нет данных о совместимости.</summary>
    public string ChainStatusText
    {
        get => _chainStatusText;
        private set => SetProperty(ref _chainStatusText, value);
    }

    private UpdateChainVariantViewModel? _selectedVariant;

    /// <summary>Выбранный вариант цепочки (для кнопки «Скачать цепочку»).</summary>
    public UpdateChainVariantViewModel? SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (SetProperty(ref _selectedVariant, value))
                OnPropertyChanged(nameof(CanDownloadChain));
        }
    }

    private bool _isChainDownloading;

    /// <summary>Идёт ли загрузка цепочки.</summary>
    public bool IsChainDownloading
    {
        get => _isChainDownloading;
        set
        {
            if (SetProperty(ref _isChainDownloading, value))
                OnPropertyChanged(nameof(CanDownloadChain));
        }
    }

    private double _chainProgress;

    /// <summary>Общий прогресс загрузки цепочки (0..1).</summary>
    public double ChainProgress
    {
        get => _chainProgress;
        set => SetProperty(ref _chainProgress, Math.Clamp(value, 0, 1));
    }

    private string _chainProgressText = string.Empty;

    /// <summary>Текст прогресса загрузки цепочки: какой файл скачивается и сколько осталось
    /// (например, «Скачивается 2 из 4: версия 3.0.158.71 … Осталось файлов: 2»).</summary>
    public string ChainProgressText
    {
        get => _chainProgressText;
        set => SetProperty(ref _chainProgressText, value);
    }

    /// <summary>Доступна ли загрузка цепочки: есть варианты и нет активных операций.</summary>
    public bool CanDownloadChain => !_isChecking && !_isDownloading && !_isChainDownloading && HasChain;

    /// <summary>True — обнаружен релиз новее текущей версии.</summary>
    public bool HasNewer => _status == ConfigUpdateStatus.NewerAvailable;

    /// <summary>True — проверка завершена (не Unknown).</summary>
    public bool IsFinished => _status != ConfigUpdateStatus.Unknown;

    /// <summary>Доступна ли загрузка: найден новый релиз, нет активных операций и есть ссылка.</summary>
    public bool CanDownload => !_isChecking && !_isDownloading && HasNewer && !string.IsNullOrWhiteSpace(_url);

    /// <summary>Команда загрузки дистрибутива (реализация — в окне).</summary>
    public RelayCommand DownloadCommand { get; }

    /// <param name="infobase">Информационная база.</param>
    /// <param name="download">Действие «начать загрузку».</param>
    public UpdateCheckRowViewModel(Infobase infobase, Action<UpdateCheckRowViewModel> download)
    {
        Infobase = infobase ?? throw new ArgumentNullException(nameof(infobase));
        DownloadCommand = new RelayCommand(() => download(this), () => CanDownload);
    }

    /// <summary>Применяет результат сетевой проверки к строке.</summary>
    public void ApplyResult(ConfigUpdateCheckResult result)
    {
        LatestVersion = result.LatestVersion;
        Status = result.Status;
        Url = result.Url;
        Error = result.Error;
    }

    /// <summary>Текущая версия отсутствует в каталоге (issue #352: релиз отозван 1С).</summary>
    public bool CurrentVersionMissing
    {
        get => _currentVersionMissing;
        private set
        {
            if (SetProperty(ref _currentVersionMissing, value))
                OnPropertyChanged(nameof(ChainStatusText));
        }
    }

    /// <summary>Применяет результат построения цепочек обновлений (issue #352):
    /// заполняет таблицу вариантов и статус цепочки. Null — сброс (пустой результат).</summary>
    public void SetChains(UpdateChainSet? set)
    {
        if (set is null)
        {
            ResetChains();
            return;
        }

        ChainVariants.Clear();
        foreach (var variant in set.Variants)
            ChainVariants.Add(new UpdateChainVariantViewModel(variant));

        HasSourceData = set.HasSourceData;
        IsDirectUpdate = set.IsDirectUpdate;
        CurrentVersionMissing = set.IsCurrentVersionMissing;
        HasChain = ChainVariants.Count > 0;
        SelectedVariant = HasChain ? ChainVariants[0] : null;

        if (!HasSourceData)
            ChainStatusText = LocalizationManager.T("Updates.Chain.NoData");
        else if (CurrentVersionMissing)
            ChainStatusText = LocalizationManager.T("Updates.Chain.VersionMissing");
        else if (IsDirectUpdate)
            ChainStatusText = LocalizationManager.T("Updates.Chain.Direct");
        else if (HasChain)
            ChainStatusText = LocalizationManager.T("Updates.Chain.Available");
        else
            ChainStatusText = LocalizationManager.T("Updates.Chain.Impossible");
    }

    /// <summary>Сбрасывает состояние цепочки (новая проверка или её начало).</summary>
    public void ResetChains()
    {
        ChainVariants.Clear();
        HasSourceData = true;
        IsDirectUpdate = false;
        CurrentVersionMissing = false;
        HasChain = false;
        SelectedVariant = null;
        ChainStatusText = string.Empty;
        IsChainDownloading = false;
        ChainProgress = 0;
        ChainProgressText = string.Empty;
    }

    /// <summary>Обновляет привязку текущей версии после изменения модели базы.</summary>
    public void RefreshInfobase()
    {
        OnPropertyChanged(nameof(CurrentVersion));
        OnPropertyChanged(nameof(Name));
    }
}