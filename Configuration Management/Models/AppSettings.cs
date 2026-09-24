namespace Configuration_Management.Models;

/// <summary>
/// Настройки интерфейса приложения, сохраняемые между запусками.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Версия схемы файла <c>settings.json</c> (см. <see cref="Configuration_Management.Services.InfobaseRepository.ConfigSchemaVersion"/>).
    /// Используется для обратной совместимости и безопасной миграции: если файл создан более
    /// новой версией приложения, чем текущая, он откладывается в резервную копию, а приложение
    /// стартует с чистыми настройками вместо того, чтобы зависнуть на незнакомых данных.
    /// Значение 0 означает легаси-файл, созданный до введения версии схемы.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>Показывать только избранные базы.</summary>
    public bool ShowFavoritesOnly { get; set; }

    /// <summary>Группировать базы по группам.</summary>
    public bool GroupByGroup { get; set; } = true;

    /// <summary>Показывать пустые группы (без информационных баз) в дереве.</summary>
    public bool ShowEmptyGroups { get; set; } = false;

    /// <summary>
    /// Цвет фона заголовка узла «Без группы» (в формате #RRGGBB).
    /// По умолчанию серый, чтобы отличаться от обычных групп (синий #2D6CDF).
    /// </summary>
    public string NoGroupColor { get; set; } = "#6B7280";

    /// <summary>Цвет иконки узла «Без группы» (в формате #RRGGBB).</summary>
    public string NoGroupIconColor { get; set; } = "#FFFFFF";

    /// <summary>Ключ иконки узла «Без группы» (имя Geometry из Icons.xaml, пусто — по умолчанию).</summary>
    public string NoGroupIcon { get; set; } = string.Empty;

    /// <summary>
    /// Цвет фона заголовка узла «Закреплённые» (в формате #RRGGBB).
    /// По умолчанию фиолетовый, чтобы отличаться от обычных групп (синий #2D6CDF).
    /// </summary>
    public string PinnedColor { get; set; } = "#8B5CF6";

    /// <summary>Цвет иконки узла «Закреплённые» (в формате #RRGGBB).</summary>
    public string PinnedIconColor { get; set; } = "#FFFFFF";

    /// <summary>Ключ иконки узла «Закреплённые» (имя Geometry из Icons.xaml, пусто — по умолчанию).</summary>
    public string PinnedIcon { get; set; } = string.Empty;

    /// <summary>Название выбранной темы оформления.</summary>
    public string Theme { get; set; } = string.Empty;

    /// <summary>
    /// Код языка интерфейса, например "ru", "en", "de". Пусто — язык определяется
    /// автоматически (по языку операционной системы, если он доступен, иначе русский).
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Активная цветовая схема (тема оформления): именованный набор из двух палитр —
    /// для светлого и тёмного режима. Если задана — применяется при запуске (палитра по
    /// свойству <see cref="Theme"/>). Если отсутствует — используется встроенная схема.
    /// </summary>
    public ColorScheme? ActiveColorScheme { get; set; }

    /// <summary>
    /// Устаревшее поле для обратной совместимости: пользовательская схема светлой базовой темы
    /// из старых версий. Читается только для миграции в единую схему <see cref="ActiveColorScheme"/>
    /// (в новые файлы не записывается).
    /// </summary>
    public ColorScheme? LightColorScheme { get; set; }

    /// <summary>
    /// Устаревшее поле для обратной совместимости: пользовательская схема тёмной базовой темы
    /// из старых версий. Читается только для миграции в единую схему <see cref="ActiveColorScheme"/>
    /// (в новые файлы не записывается).
    /// </summary>
    public ColorScheme? DarkColorScheme { get; set; }

    /// <summary>Имена групп, свёрнутых в списке баз.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    /// <summary>
    /// Автосохранять состояние списка баз (раскрытые группы) по таймеру (Этап 13,
    /// функция №15 StartManager). false — состояние сохраняется только штатно
    /// (при завершении работы и по событиям изменения раскрытия).
    /// </summary>
    public bool AutoSaveListState { get; set; } = true;

    /// <summary>
    /// Периодичность автосохранения состояния списка (раскрытые группы) в секундах.
    /// По умолчанию — 10. Применяется, когда включено <see cref="AutoSaveListState"/>.
    /// </summary>
    public int ListStateAutoSaveIntervalSeconds { get; set; } = 10;

    /// <summary>Список установленных версий платформы 1С.</summary>
    public List<string> InstalledPlatformVersions { get; set; } = new();

    /// <summary>
    /// Пользовательские параметры запуска, добавленные в справочник параметров
    /// (issue #141). Дополняют встроенный список ключей командной строки 1С
    /// в окне «Конфигуратор параметров запуска».
    /// </summary>
    public List<string> CustomLaunchParameters { get; set; } = new();

    /// <summary>
    /// Дополнительные пути к каталогам установки платформы 1С
    /// (помимо стандартных Program Files и Program Files (x86)).
    /// Пользователь может указать нестандартные/портативные установки.
    /// </summary>
    public List<string> AdditionalPlatformSearchPaths { get; set; } = new();

    /// <summary>
    /// Последняя успешно использованная версия платформы при создании файловой ИБ.
    /// Подставляется по умолчанию в поле «Версия» окна создания (если версия ещё установлена).
    /// </summary>
    public string LastFileCreatePlatformVersion { get; set; } = "";

    /// <summary>
    /// Последняя успешно использованная версия платформы при создании клиент-серверной ИБ.
    /// Для клиент-серверной базы версия должна совпадать с версией сервера 1С,
    /// поэтому сохраняется отдельно от файловых баз.
    /// </summary>
    public string LastClientServerCreatePlatformVersion { get; set; } = "";

    /// <summary>
    /// Настраиваемый шаблон имени COM-коннектора 1С (issue #175).
    /// Пустая строка — использовать стандартные ProgID (<c>V85/V83/V82/V81.COMConnector</c>).
    /// Если шаблон задан, он разворачивается по версии платформы каждой базы и пробуется
    /// первым в переборе ProgID. Плейсхолдеры:
    /// <list type="bullet">
    /// <item><c>%V12%</c> — первые две цифры версии (например <c>83</c> для 8.3.x);</item>
    /// <item><c>%V3%</c> — третья цифра версии (например <c>27</c> для 8.3.27.x);</item>
    /// <item><c>%V4%</c> — четвёртая цифра версии (например <c>1644</c> для 8.3.27.1644).</item>
    /// </list>
    /// Пример: <c>V%V12%.ComConnector</c>.
    /// </summary>
    public string ComConnectorNameTemplate { get; set; } = "";

    /// <summary>
    /// Таймаут определения свойств конфигурации через COM-коннектор (issue #174), миллисекунды.
    /// Первое COM-подключение к клиент-серверной базе (особенно localhost с холодным стартом
    /// сервера, обращением к лицензиям HASP и первичным созданием сеанса) часто превышает 8 секунд.
    /// Значение по умолчанию 30000 мс — чтение выполняется только по явной команде, поэтому
    /// длинный таймаут не мешает старту. Минимально допустимое значение — 1000.
    /// </summary>
    public int ComDetectTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Глобальная настройка глубины истории запусков (issue #246): максимальное количество
    /// записей истории запусков, которое запоминается для одной информационной базы. При
    /// превышении этого количества из конца списка удаляются самые старые записи.
    /// По умолчанию — 30 (как было зашито жёстко до версии 0.3.7.34).
    /// </summary>
    public int MaxLaunchHistoryPerBase { get; set; } = 30;

    /// <summary>
    /// Глобальная настройка действия по двойному щелчку на информационной базе
    /// (функция №28 StartManager). Каноническое строковое значение из
    /// <see cref="DoubleClickAction"/>: "Enterprise" (по умолчанию), "Configurator"
    /// или "None". Индивидуальное значение конкретной ИБ (Infobase.DoubleClickAction)
    /// переопределяет эту настройку.
    /// </summary>
    public string DefaultDoubleClickAction { get; set; } = DoubleClickAction.GlobalDefault;

    /// <summary>Режим синхронизации с файлом ibases.v8i.</summary>
    public IbasesSyncMode IbasesSyncMode { get; set; } = IbasesSyncMode.None;

    /// <summary>Путь к файлу ibases.v8i для синхронизации (пусто — стандартный путь).</summary>
    public string IbasesSyncFilePath { get; set; } = string.Empty;

    /// <summary>Момент запуска автоматической синхронизации (по умолчанию — при запуске).</summary>
    public IbasesSyncTrigger IbasesSyncTrigger { get; set; } = IbasesSyncTrigger.OnStartup;

    /// <summary>Интервал автоматической синхронизации в минутах (для режима Interval).</summary>
    public int IbasesSyncIntervalMinutes { get; set; } = 30;

    /// <summary>Время автоматической синхронизации по расписанию в формате "HH:mm" (для режима Schedule).</summary>
    public string IbasesSyncScheduleTime { get; set; } = "09:00";

    /// <summary>
    /// «Сохранять после правки» (issue #269): записывать изменения свойств базы в ibases.v8i
    /// сразу после правки, если режим синхронизации подразумевает сохранение (Export/Both).
    /// Опция доступна только при Export/Both и не влияет на «Момент синхронизации».
    /// </summary>
    public bool IbasesSaveAfterEdit { get; set; } = true;

    /// <summary>
    /// Создавать резервную копию файла ibases.v8i перед синхронизацией (экспортом/записью).
    /// </summary>
    public bool IbasesBackupEnabled { get; set; } = true;

    /// <summary>
    /// Сколько последних резервных копий ibases.v8i хранить (старые удаляются).
    /// </summary>
    public int IbasesBackupKeepCount { get; set; } = 5;

    /// <summary>
    /// Момент последней успешной выгрузки приложения в ibases.v8i (UTC, issue #278).
    /// В двустороннем режиме синхронизации (Both) по этой метке определяется внешнее
    /// изменение файла: если файл менялся позже нашей последней выгрузки (например,
    /// ручное восстановление), сначала выполняется загрузка из файла, затем выгрузка.
    /// По умолчанию default(DateTime) — выгрузка ещё не выполнялась (первый запуск).
    /// </summary>
    public DateTime IbasesLastSyncExportUtc { get; set; }

    /// <summary>Ширина колонки «Название» в списке баз (0 — по умолчанию).</summary>
    public double NameColumnWidth { get; set; }

    /// <summary>Ширина колонки «Версия платформы» в списке баз (0 — по умолчанию).</summary>
    public double VersionColumnWidth { get; set; }

    /// <summary>Ширина колонки «Режим запуска» в списке баз (0 — по умолчанию).</summary>
    public double LaunchModeColumnWidth { get; set; }

    /// <summary>Ширина колонки «Сервер/База» в списке баз (0 — по умолчанию).</summary>
    public double ServerColumnWidth { get; set; }

    /// <summary>Ширина колонки «Последний запуск» в списке баз (0 — по умолчанию).</summary>
    public double LastLaunchColumnWidth { get; set; }

    /// <summary>Показывать колонку-кнопку «Избранное» (★) в списке баз.</summary>
    public bool ShowFavoritesButton { get; set; } = true;

    /// <summary>Показывать колонку-кнопку «Закрепить» (📌) в списке баз.</summary>
    public bool ShowPinnedButton { get; set; } = true;

    /// <summary>Показывать теги баз в списке.</summary>
    public bool ShowTags { get; set; } = true;

    /// <summary>Показывать панель быстрого отбора по тегам над списком баз.</summary>
    public bool ShowTagFilterPanel { get; set; } = true;

    /// <summary>
    /// Разрешить запуск нескольких экземпляров приложения.
    /// false — при повторном запуске активируется уже открытое окно.
    /// </summary>
    public bool AllowMultipleInstances { get; set; }

    /// <summary>
    /// Проверять наличие обновлений приложения при запуске (GitHub Releases).
    /// </summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>Автоматически устанавливать новые версии без подтверждения (self-update при запуске).</summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>Показывать колонку «Версия платформы» в списке баз.</summary>
    public bool ShowVersionColumn { get; set; } = true;

    /// <summary>Показывать колонку «Конфигурация» (только название) в списке баз.</summary>
    public bool ShowConfigurationColumn { get; set; } = true;

    /// <summary>Ширина колонки «Конфигурация» (0 — по умолчанию).</summary>
    public double ConfigurationColumnWidth { get; set; }

    /// <summary>Показывать колонку «№ релиза» (версия конфигурации) в списке баз.</summary>
    public bool ShowConfigurationVersionColumn { get; set; } = true;

    /// <summary>Ширина колонки «№ релиза» (0 — по умолчанию).</summary>
    public double ConfigurationVersionColumnWidth { get; set; }

    /// <summary>Показывать колонку «Действия» (кнопки запуска/конфигуратора/очистки кеша) в списке баз.</summary>
    public bool ShowActionsColumn { get; set; } = true;

    /// <summary>Ширина колонки «Действия» в списке баз (0 — по умолчанию).</summary>
    public double ActionsColumnWidth { get; set; }

    /// <summary>Показывать колонку «Режим запуска» в списке баз.</summary>
    public bool ShowLaunchModeColumn { get; set; } = true;

    /// <summary>Показывать колонку «Сервер/База» в списке баз.</summary>
    public bool ShowServerColumn { get; set; } = true;

    /// <summary>Показывать колонку «Последний запуск» в списке баз.</summary>
    public bool ShowLastLaunchColumn { get; set; } = true;

    /// <summary>Показывать колонку «Размер» (файловые ИБ) в списке баз.</summary>
    public bool ShowSizeColumn { get; set; } = true;

    /// <summary>Ширина колонки «Размер» (0 — по умолчанию).</summary>
    public double SizeColumnWidth { get; set; }

    /// <summary>Показывать колонку «Дата изменений» файла ИБ в списке баз (Этап 13).</summary>
    public bool ShowModifiedColumn { get; set; } = true;

    /// <summary>Ширина колонки «Дата изменений» (0 — по умолчанию).</summary>
    public double ModifiedColumnWidth { get; set; }

    /// <summary>
    /// Порядок колонок списка баз слева направо (кроме фиксированных колонок
    /// «Название» и «Действия»). Пустой список — порядок по умолчанию
    /// (колонка «Конфигурация» в самом конце).
    /// </summary>
    public List<string> ColumnOrder { get; set; } = new();

    /// <summary>Ширина колонки «База» в окне «Очистка кэша» (0 — растягивается).</summary>
    public double CacheCleanBaseColumnWidth { get; set; }

    /// <summary>Ширина колонки «Программный» в окне «Очистка кэша» (0 — по умолчанию).</summary>
    public double CacheCleanProgramColumnWidth { get; set; }

    /// <summary>Ширина колонки «Пользовательский» в окне «Очистка кэша» (0 — по умолчанию).</summary>
    public double CacheCleanUserColumnWidth { get; set; }

    /// <summary>Сохранённая ширина окна приложения (0 — по умолчанию).</summary>
    public double WindowWidth { get; set; }

    /// <summary>Сохранённая высота окна приложения (0 — по умолчанию).</summary>
    public double WindowHeight { get; set; }

    /// <summary>Сохранённая позиция окна по горизонтали (0 — по центру экрана).</summary>
    public double WindowLeft { get; set; }

    /// <summary>Сохранённая позиция окна по вертикали (0 — по центру экрана).</summary>
    public double WindowTop { get; set; }

    /// <summary>Состояние окна приложения (Normal, Maximized, Minimized).</summary>
    public string WindowState { get; set; } = string.Empty;

    /// <summary>
    /// Показывать стандартный системный заголовок окна (с кнопками закрытия/сворачивания).
    /// false (по умолчанию) — Linux/Avalonia рисует собственный безрамковый заголовок.
    /// true — используется системная рамка, как в Windows (issue #152). На Windows
    /// признак не применяется: WPF-версия всегда использует системный заголовок.
    /// </summary>
    public bool UseSystemTitleBar { get; set; }

    /// <summary>
    /// Запоминать размер, позицию, состояние окна и монитор, на котором оно было
    /// закрыто, и восстанавливать их при следующем запуске.
    /// </summary>
    public bool RememberWindowLayout { get; set; } = true;

    /// <summary>
    /// При закрытии окна сворачивать приложение в системный трей вместо выхода.
    /// </summary>
    public bool CloseToTray { get; set; }

    /// <summary>
    /// Действие после успешного запуска информационной базы или конфигуратора 1С:
    /// "None" (ничего), "Minimize" (просто свернуть), "MinimizeToTray" (свернуть в трей)
    /// или "Close" (закрыть/увести в трей). Хранится строкой для обратной совместимости.
    /// </summary>
    public string AfterLaunchAction { get; set; } = "None";

    /// <summary>Показывать значок приложения в системном трее.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Горячая клавиша запуска «1С:Предприятие» (например F3). Пусто — не назначена.</summary>
    public string HotkeyEnterprise { get; set; } = "F3";

    /// <summary>Горячая клавиша запуска «Конфигуратор» (например F4).</summary>
    public string HotkeyConfigurator { get; set; } = "F4";

    /// <summary>Горячая клавиша «Избранное» (например F8).</summary>
    public string HotkeyFavorite { get; set; } = "F8";

    /// <summary>Горячая клавиша «Изменить» (например F2).</summary>
    public string HotkeyEdit { get; set; } = "F2";

    /// <summary>Горячая клавиша «Удалить» (например Delete).</summary>
    public string HotkeyDelete { get; set; } = "Delete";

    /// <summary>Горячая клавиша «Очистить кэш».</summary>
    public string HotkeyClearCache { get; set; } = "";

    /// <summary>Горячая клавиша «Добавить базу» (например Insert).</summary>
    public string HotkeyAdd { get; set; } = "Insert";

    /// <summary>Горячая клавиша «Закрепить».</summary>
    public string HotkeyPin { get; set; } = "";

    /// <summary>Горячая клавиша показа вкладки «Все базы». Пусто — не назначена.</summary>
    public string HotkeyShowAll { get; set; } = "";

    /// <summary>Горячая клавиша показа вкладки «Избранное». Пусто — не назначена.</summary>
    public string HotkeyShowFavorites { get; set; } = "";

    /// <summary>Горячая клавиша показа вкладки «Недавние». Пусто — не назначена.</summary>
    public string HotkeyShowRecent { get; set; } = "";

    /// <summary>Горячая клавиша очистки строки поиска. Пусто — не назначена (issue #160).</summary>
    public string HotkeyClearSearch { get; set; } = "Ctrl+Shift+C";

    /// <summary>Горячая клавиша сброса фильтра по тегам. Пусто — не назначена (issue #160).</summary>
    public string HotkeyClearTags { get; set; } = "Ctrl+Shift+T";

    /// <summary>Горячая клавиша переключения подробностей правой панели информации (issue #172). Пусто — не назначена.</summary>
    public string HotkeyRightPanelDetails { get; set; } = "Ctrl+D";

    /// <summary>Горячая клавиша «Найти в списке» — переход к базе в общем списке (issue #285). По умолчанию Ctrl+T.</summary>
    public string HotkeyFindInList { get; set; } = "Ctrl+T";

    /// <summary>Горячая клавиша «Смена пользователя» (issue #200). Пусто — не назначена.</summary>
    public string HotkeySwitchUser { get; set; } = "";

    /// <summary>
    /// Поле сортировки списка баз: Name (по умолчанию), LastLaunchDate, SortOrder.
    /// </summary>
    public string SortField { get; set; } = "Name";

    /// <summary>Направление сортировки: true — по возрастанию, false — по убыванию.</summary>
    public bool SortAscending { get; set; } = true;

    /// <summary>
    /// Идентификатор последней выбранной информационной базы (восстанавливается при запуске).
    /// Пусто — база не была выбрана.
    /// </summary>
    public string LastSelectedInfobaseId { get; set; } = string.Empty;

    /// <summary>
    /// Полный путь последней выбранной группы (восстанавливается при запуске).
    /// Пусто — группа не была выбрана.
    /// </summary>
    public string LastSelectedGroupPath { get; set; } = string.Empty;

    /// <summary>
    /// Упорядоченный список идентификаторов избранных баз для горячих клавиш Alt+1…Alt+9.
    /// Индекс 0 → Alt+1, индекс 1 → Alt+2 и т.д. (максимум 9).
    /// </summary>
    public List<string> FavoriteHotkeyIds { get; set; } = new();

    /// <summary>
    /// Показывать подробности в правой панели (имя, подключение, теги).
    /// false — компактный режим: только кнопки действий.
    /// </summary>
    public bool ShowRightPanelDetails { get; set; } = true;

    /// <summary>
    /// Показывать блок «Текущая сессия» (режим клиента и разрядность) в правой панели.
    /// Работает и в полном, и в компактном режиме панели.
    /// </summary>
    public bool ShowSessionLaunchPanel { get; set; } = true;

    /// <summary>Сохранённый режим клиента «текущей сессии» (Auto / Ordinary / Thick / Thin).</summary>
    public string SessionClientMode { get; set; } = "Auto";

    /// <summary>Сохранённая разрядность «текущей сессии» (Auto / X86 / X64).</summary>
    public string SessionArchitecture { get; set; } = "Auto";

    /// <summary>
    /// Режим глобальной «Разрядности по умолчанию»: "X86" — всегда 32-бит,
    /// "X64" — всегда 64-бит, либо "Priority" («Использовать приоритет базы») —
    /// брать явную настройку разрядности информационной базы (вкладка «Разрядность»).
    /// Используется при запуске, когда у базы не задана собственная разрядность.
    /// </summary>
    public string DefaultArchitecture { get; set; } = "X64";

    /// <summary>
    /// Каталоги шаблонов конфигураций (как в стартере 1С).
    /// Пустой список — использовать пути, настроенные в 1С / по умолчанию.
    /// </summary>
    public List<string> TemplateCatalogPaths { get; set; } = new();

    /// <summary>
    /// При Esc сворачивать главное окно в трей (нужен включённый значок в трее).
    /// </summary>
    public bool EscapeToTray { get; set; } = true;

    /// <summary>В нижней панели показывать путь / строку подключения.</summary>
    public bool StatusShowConnectionPath { get; set; } = true;

    /// <summary>В нижней панели показывать разрядность (32/64).</summary>
    public bool StatusShowArchitecture { get; set; } = true;

    /// <summary>В нижней панели показывать режим запуска.</summary>
    public bool StatusShowLaunchMode { get; set; } = true;

    /// <summary>В нижней панели показывать порт сервера.</summary>
    public bool StatusShowPort { get; set; } = true;

    /// <summary>В нижней панели показывать версию платформы.</summary>
    public bool StatusShowPlatformVersion { get; set; } = true;

    /// <summary>В нижней панели показывать тип клиента.</summary>
    public bool StatusShowClientType { get; set; }

    /// <summary>В нижней панели показывать тип подключения.</summary>
    public bool StatusShowConnectionType { get; set; }

    /// <summary>В нижней панели показывать имя пользователя подключения.</summary>
    public bool StatusShowUser { get; set; }

    /// <summary>В нижней панели показывать ID информационной базы.</summary>
    public bool StatusShowId { get; set; }

    /// <summary>
    /// Добавлять дату-время к имени файла при выгрузке (экспорт списка баз в JSON,
    /// выгрузка ИБ в .dt, выгрузка конфигурации в .cf). По умолчанию — включено.
    /// </summary>
    public bool AddTimestampToExportFileName { get; set; } = true;

    /// <summary>
    /// Шаблон (формат) отметки даты и времени для имени файла при выгрузке.
    /// По умолчанию — «yyyyMMdd_HHmmss» (например «20260819_074312»).
    /// Применяется только когда <see cref="AddTimestampToExportFileName"/> включён.
    /// </summary>
    public string ExportTimestampFormat { get; set; } = "yyyyMMdd_HHmmss";

    /// <summary>Семейство шрифта интерфейса (например «Segoe UI»).</summary>
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Размер шрифта интерфейса (в логических единицах WPF, по умолчанию 13).</summary>
    public double FontSize { get; set; } = 13;

    /// <summary>Начертание шрифта интерфейса: «Normal» или «Bold».</summary>
    public string FontWeight { get; set; } = "Normal";

    /// <summary>Стиль шрифта интерфейса: «Normal» или «Italic».</summary>
    public string FontStyle { get; set; } = "Normal";

    /// <summary>
    /// Компактный режим интерфейса: уменьшает размеры иконок, шрифтов, отступов и
    /// расстояний между элементами, убирая лишнее пустое пространство.
    /// </summary>
    public bool CompactMode { get; set; }

    /// <summary>
    /// Индивидуальные настройки шрифта для отдельных областей интерфейса
    /// (список баз, заголовки, правая панель, строка состояния, вкладки, кнопки, поля ввода).
    /// Ключи — из <see cref="Themes.ThemeManager.FontDefault"/>, <see cref="Themes.ThemeManager.FontList"/> и т.д.
    /// </summary>
    public Dictionary<string, ElementFontSettings> ElementFonts { get; set; } = new();

    /// <summary>
    /// Каталог резервного копирования «профиля» приложения: сюда сохраняются настройки,
    /// список баз (включая пользователей и пароли запуска), группы и файл ibases.v8i.
    /// Пусто — резервное копирование не настроено. После переустановки системы достаточно
    /// указать этот каталог, чтобы восстановить привычное состояние приложения.
    /// </summary>
    public string ProfileBackupDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Восстанавливать профиль из каталога <see cref="ProfileBackupDirectory"/> при каждом
    /// запуске приложения. Включается после переустановки системы, чтобы сразу получить
    /// привычно настроенное приложение без ручных действий.
    /// </summary>
    public bool ProfileRestoreOnStartup { get; set; }

    /// <summary>
    /// Кеш размеров файловых ИБ (ключ — нормализованный путь в верхнем регистре,
    /// значение — размер и время последней записи). Используется при запуске, чтобы
    /// не сканировать диски заново для каждой файловой базы при большом списке
    /// (<see cref="Configuration_Management.ViewModels.MainViewModel"/>.
    /// </summary>
    public Dictionary<string, FileSizeCacheEntry> FileSizeCache { get; set; } = new();

    /// <summary>
    /// Пользовательские типовые конфигурации 1С для проверки обновлений (функции №21/№22).
    /// Дополняются предопределённым набором <c>Services.BuiltInConfigTypes</c> при показе.
    /// </summary>
    public List<OneCConfigType> CustomConfigTypes { get; set; } = new();

    /// <summary>Горячая клавиша «Проверить обновления» для выбранной ИБ (по умолчанию F9).</summary>
    public string HotkeyCheckUpdate { get; set; } = "F9";

    /// <summary>Горячая клавиша окна «Актуальные релизы» (по умолчанию Alt+F9).</summary>
    public string HotkeyActualReleases { get; set; } = "Alt+F9";

    /// <summary>Логин учётной записи сайта 1С для авторизации (HTTP Basic Auth) при проверке обновлений конфигураций.</summary>
    public string UpdatesLogin { get; set; } = "";

    /// <summary>Пароль учётной записи сайта 1С для авторизации (HTTP Basic Auth) при проверке обновлений конфигураций.</summary>
    public string UpdatesPassword { get; set; } = "";

    /// <summary>Горячая клавиша «Выполнить сценарий резервирования» для выбранной ИБ (по умолчанию Ctrl+Shift+F5).</summary>
    public string HotkeyRunBackup { get; set; } = "Ctrl+Shift+F5";

    /// <summary>Горячая клавиша окна «Список выгрузок» (по умолчанию Ctrl+Shift+F7).</summary>
    public string HotkeyExportsList { get; set; } = "Ctrl+Shift+F7";

    /// <summary>Горячая клавиша «Блокировка сеансов ИБ» (функция №20, по умолчанию Ctrl+Alt+L).</summary>
    public string HotkeySessionLock { get; set; } = "Ctrl+Alt+L";

    /// <summary>Горячая клавиша «Временная блокировка приложения паролем» (функция №19, по умолчанию не задана).</summary>
    public string HotkeyLockApp { get; set; } = "";

    /// <summary>
    /// Горячая клавиша «Проверка целостности файловой ИБ (chdbfl)» (функция №29,
    /// по умолчанию Ctrl+Alt+Q).
    /// </summary>
    public string HotkeyCheckIntegrity { get; set; } = "Ctrl+Alt+Q";

    /// <summary>
    /// Горячая клавиша «Консоль администрирования серверов 1С» (Этап 6,
    /// по умолчанию Ctrl+Alt+S).
    /// </summary>
    public string HotkeyServerConsole { get; set; } = "Ctrl+Alt+S";

    /// <summary>Пароль временной блокировки приложения (функция №19) в виде PBKDF2-хэша.</summary>
    public string AppLockPasswordHash { get; set; } = "";

    /// <summary>Путь к исполняемому файлу внешнего архиватора RAR (winrar.exe/rar), если он не найден в PATH.</summary>
    public string RarExecutablePath { get; set; } = "";

    /// <summary>
    /// Интеграция с проводником Windows (функция №12 дорожной карты): регистрация
    /// ассоциации <c>.1CD</c> и команд контекстного меню «Зарегистрировать в списке баз» /
    /// «Запустить 1С:Предприятие» / «Запустить Конфигуратор». На Linux не применяется
    /// (функция недоступна — пункт настройки скрыт/заблокирован).
    /// </summary>
    public bool ExplorerIntegrationEnabled { get; set; }

    /// <summary>
    /// Автозапуск при старте ОС (функция №31 StartManager). На Windows — ключ реестра
    /// <c>HKCU\...\Run</c>; на Linux — файл автозапуска десктоп-окружения
    /// (<c>~/.config/autostart/*.desktop</c>).
    /// </summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>
    /// Горячая клавиша сохранения копии экрана (функция №30 StartManager).
    /// По умолчанию <c>Ctrl+F12</c>. Пусто — не назначена.
    /// </summary>
    public string ScreenshotHotkey { get; set; } = "Ctrl+F12";

    /// <summary>
    /// Каталог сохранения копий экрана (функция №30 StartManager). Пусто — каталог
    /// «Изображения» по умолчанию.
    /// </summary>
    public string ScreenshotSaveDirectory { get; set; } = "";

    /// <summary>Дополнительные каталоги, сканируемые «Списком выгрузок» наряду с каталогами сценариев.</summary>
    public List<string> BackupTargetDirectories { get; set; } = new();

    /// <summary>
    /// Режим функциональности приложения (Этап 10 дорожной карты StartManager):
    /// «Пользователь» / «Специалист» / «Разработчик». Хранится канонической строкой
    /// <see cref="FunctionalModes"/> (не зависит от локали). В режиме «Пользователь»
    /// системное меню (редактирование/удаление баз, выгрузки, администрирование) скрыто.
    /// По умолчанию — «Специалист».
    /// </summary>
    public string FunctionalMode { get; set; } = Models.FunctionalModes.Default;

    /// <summary>
    /// Параметры по умолчанию для запуска 1С из файла <c>1CLaunch.cfg</c> (Этап 10):
    /// значения ключей <c>configpath</c>/<c>configdir</c>/<c>appmode</c>/<c>selectmodeoff</c>,
    /// считанные при последнем запуске. Используются как начальные значения в окне
    /// «Параметры» и лаунчером, если не переопределены командной строкой/параметрами базы.
    /// Пустой объект — файл не найден или не содержит настроек.
    /// </summary>
    public LaunchConfigDefaults LaunchConfigDefaults { get; set; } = new();

    /// <summary>
    /// Кеш последних результатов проверки обновлений по коду конфигурации (необязательно,
    /// заполняется сервисом проверки для быстрого отображения предыдущего результата).
    /// </summary>
    public Dictionary<string, ConfigUpdateCheckResult> UpdateCheckCache { get; set; } = new();

    /// <summary>
    /// Приводит настройки, загруженные из файла, к безопасному состоянию (issue #64).
    /// В легаси-файлах, созданных более ранними версиями приложения, поля-коллекции
    /// могли отсутствовать либо явно содержать <c>null</c>. Десериализация в таком случае
    /// перезаписывает инициализированные значения по умолчанию на <c>null</c>, а потребители
    /// настроек (конструктор <see cref="Configuration_Management.ViewModels.MainViewModel"/>
    /// и его Avalonia-версия) итерируют эти коллекции без проверки — это вызывало
    /// <c>NullReferenceException</c> при старте поверх старых конфигов и «зависание»
    /// (процесс запущен, но главное окно не появляется).
    /// </summary>
    public void NormalizeForLoad()
    {
        // Восстанавливаем непустые коллекции, которые могли прийти как null.
        CollapsedGroups ??= new List<string>();
        InstalledPlatformVersions ??= new List<string>();
        AdditionalPlatformSearchPaths ??= new List<string>();
        ColumnOrder ??= new List<string>();
        FavoriteHotkeyIds ??= new List<string>();
        TemplateCatalogPaths ??= new List<string>();
        ElementFonts ??= new Dictionary<string, ElementFontSettings>();
        FileSizeCache ??= new Dictionary<string, FileSizeCacheEntry>();
        CustomConfigTypes ??= new List<OneCConfigType>();
        UpdateCheckCache ??= new Dictionary<string, ConfigUpdateCheckResult>();
        BackupTargetDirectories ??= new List<string>();
        LaunchConfigDefaults ??= new LaunchConfigDefaults();

        // Нормализуем строковые поля, чтобы избежать null-значений у потребителей.
        NoGroupIcon ??= string.Empty;
        PinnedIcon ??= string.Empty;
        AfterLaunchAction = string.IsNullOrWhiteSpace(AfterLaunchAction) ? "None" : AfterLaunchAction;
        FunctionalMode = string.IsNullOrWhiteSpace(FunctionalMode)
            ? Models.FunctionalModes.Default
            : FunctionalMode;
    }

}
