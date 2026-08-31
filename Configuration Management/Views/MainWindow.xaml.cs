#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.IO;
using MaterialDesignThemes.Wpf;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;

namespace Configuration_Management
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private Point _dragStartPoint;
        private object? _draggedData;
        private bool _isDragging;
        private Forms.NotifyIcon? _trayIcon;
        private bool _forceClose;

        /// <summary>Информационная версия сборки для заголовка окна (например «0.3.3.41»).</summary>
        private readonly string? _infoVersion;

        /// <summary>
        /// Состояние нижней кнопки тегов (<see cref="MainViewModel.ShowTags"/>), запомненное
        /// в момент выключения верхней кнопки «теги», чтобы восстановить его при повторном
        /// включении (вместо принудительного включения нижней кнопки).
        /// </summary>
        private bool? _savedTagsStateBeforeTopOff;

        public MainWindow(ViewModels.MainViewModel? viewModel = null)
        {
            InitializeComponent();

            // «Стеклянный» полупрозрачный фон окна: подложка берётся из текущего цвета
            // темы (светлая/тёмная и любые схемы) и пересчитывается при смене темы.
            // ThemeManager.ApplyScheme заменяет словарь темы в Application.Resources,
            // поэтому слушаем коллекцию MergedDictionaries и обновляем подложку заново.
            if (Application.Current?.Resources is { } resources)
            {
                ((System.Collections.Specialized.INotifyCollectionChanged)resources.MergedDictionaries)
                    .CollectionChanged += (_, _) =>
                    {
                        try { Dispatcher.BeginInvoke(new Action(ApplyGlassBackground)); }
                        catch { /* не блокируем смену темы */ }
                    };
            }

            // Выводим версию программы в заголовок окна (информационная версия,
            // чтобы показать точное значение «0.3.3.41»).
            // Из InformationalVersion отбрасываем возможный суффикс «+<sha>».
            _infoVersion = VersionInfo.Display();
            // Заголовок собираем через общий метод: это защищает от повторного
            // добавления суффикса версии при повторном применении XAML-привязки.
            UpdateWindowTitle();

            // Смена языка интерфейса: заголовок окна, подсказки и меню трея, которые
            // задаются в code-behind, обновляются вручную (LocExtension-привязки XAML
            // обновляются сами через LocalizationManager.Source.NotifyAll()).
            LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;

            _viewModel = viewModel ?? new ViewModels.MainViewModel();
            DataContext = _viewModel;

            // Действие «после запуска базы/конфигуратора» согласно глобальной настройке.
            _viewModel.AfterLaunchRequested += OnAfterLaunchRequested;

            // После пересборки дерева (например, сохранения настроек базы) возвращаем
            // клавиатурный фокус на выбранную строку — прежний контейнер уничтожен.
            _viewModel.TreeRebuilt += RestoreTreeKeyboardFocus;

            // Пересчитываем выравнивание колонок заголовка после переключения компактного
            // режима: ApplyCompact масштабирует отступы/шрифты/компенсатор заголовка,
            // поэтому старое значение HeaderOffsetColumn становится неактуальным и данные
            // разъезжаются относительно заголовков. Пересчёт откладывается до Loaded-приоритета,
            // чтобы он выполнился уже после того, как ApplyCompact завершит изменения раскладки.
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // После завершения фоновой инициализации (дерево уже построено) восстанавливаем
            // последнее выделение и пересчитываем раскладку — раньше дерево ещё пустое.
            _viewModel.StartupInitializationCompleted += (_, _) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        RestoreLastSelection();
                        AlignHeaderToData();
                    }
                    catch { /* не блокируем запуск из-за восстановления выделения */ }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            };

            // Применяем сохранённую цветовую схему (тему оформления) при запуске.
            _viewModel.ApplyActiveColorSchemeToUi();

            UpdateThemeButton();

            // Компактный режим: синхронизируем состояние кнопки на верхней панели.
            if (CompactModeButton != null)
                CompactModeButton.IsChecked = _viewModel.CompactMode;

            // Применяем сохранённые ширины колонок списка баз.
            ApplySavedColumnWidths();

            // Применяем сохранённые размер, позицию и состояние окна.
            ApplySavedWindowLayout();

            // Трей и хоткеи — после загрузки окна (STA/иконка безопаснее на Loaded).
            Loaded += (_, _) =>
            {
                try
                {
                    InitializeTrayIcon();
                    RegisterLaunchHotkeys();
                    RegisterFavoriteHotkeys();
                    RestoreLastSelection();
                }
                catch
                {
                    // не блокируем запуск из‑за трея/хоткеев
                }
            };
            _viewModel.FavoriteHotkeysChanged += (_, _) =>
            {
                try { RegisterFavoriteHotkeys(); }
                catch { /* ignore */ }
            };
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.ShowTrayIcon))
                {
                    try { UpdateTrayVisibility(); } catch { /* ignore */ }
                    return;
                }

                // Индикатор выгрузки .dt/.cf: запускаем/останавливаем анимацию.
                if (e.PropertyName is nameof(MainViewModel.IsExporting))
                {
                    if (_viewModel.IsExporting) StartExportIndicatorAnimation();
                    else StopExportIndicatorAnimation();
                    return;
                }

                // При изменении видимости колонок/кнопок пересчитываем выравнивание
                // заголовка с данными, чтобы колонки не разъезжались.
                if (e.PropertyName is nameof(MainViewModel.ShowVersionColumn)
                    or nameof(MainViewModel.ShowLaunchModeColumn)
                    or nameof(MainViewModel.ShowServerColumn)
                    or nameof(MainViewModel.ShowLastLaunchColumn)
                    or nameof(MainViewModel.ShowSizeColumn)
                    or nameof(MainViewModel.ShowActionsColumn)
                    or nameof(MainViewModel.ShowFavoritesButton)
                    or nameof(MainViewModel.ShowPinnedButton))
                {
                    Dispatcher.BeginInvoke(new Action(AlignHeaderToData), System.Windows.Threading.DispatcherPriority.Loaded);
                }

                if (e.PropertyName is nameof(MainViewModel.HotkeyEnterprise)
                    or nameof(MainViewModel.HotkeyConfigurator)
                    or nameof(MainViewModel.HotkeyFavorite)
                    or nameof(MainViewModel.HotkeyEdit)
                    or nameof(MainViewModel.HotkeyDelete)
                    or nameof(MainViewModel.HotkeyClearCache)
                    or nameof(MainViewModel.HotkeyAdd)
                    or nameof(MainViewModel.HotkeyPin)
                    or nameof(MainViewModel.HotkeyShowAll)
                    or nameof(MainViewModel.HotkeyShowFavorites)
                    or nameof(MainViewModel.HotkeyShowRecent))
                {
                    try { RegisterLaunchHotkeys(); } catch { /* ignore */ }
                }
            };
        }

        private DoubleAnimation? _exportBounceAnimation;
        private bool _exportAnimating;












        private enum TrayIconKind
        {
            Open, Database, Enterprise, Configurator, Sync, Settings, Exit
        }



        private static readonly Dictionary<TrayIconKind, Drawing.Image> TrayIconCache = new();



        /// <summary>Современный рендерер меню трея (скругление, hover, светлый фон).</summary>
        private sealed class ModernTrayMenuRenderer : Forms.ToolStripProfessionalRenderer
        {
            public ModernTrayMenuRenderer() : base(new ModernTrayColorTable())
            {
                RoundedEdges = true;
            }

            protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected && !e.Item.Pressed)
                {
                    base.OnRenderMenuItemBackground(e);
                    return;
                }

                var rect = new Drawing.Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
                using var b = new Drawing.SolidBrush(Drawing.Color.FromArgb(239, 246, 255));
                e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = RoundedRect(rect, 6);
                e.Graphics.FillPath(b, path);
            }

            protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item is Forms.ToolStripLabel
                    ? Drawing.Color.FromArgb(100, 116, 139)
                    : Drawing.Color.FromArgb(30, 41, 59);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
            {
                var y = e.Item.Height / 2;
                using var pen = new Drawing.Pen(Drawing.Color.FromArgb(226, 232, 240));
                e.Graphics.DrawLine(pen, 28, y, e.Item.Width - 8, y);
            }

            private static Drawing.Drawing2D.GraphicsPath RoundedRect(Drawing.Rectangle bounds, int radius)
            {
                var path = new Drawing.Drawing2D.GraphicsPath();
                int d = radius * 2;
                path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
                path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
                path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
                path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        private sealed class ModernTrayColorTable : Forms.ProfessionalColorTable
        {
            public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(226, 232, 240);
            public override Drawing.Color MenuItemBorder => Drawing.Color.Transparent;
            public override Drawing.Color MenuItemSelected => Drawing.Color.FromArgb(239, 246, 255);
            public override Drawing.Color MenuItemSelectedGradientBegin => Drawing.Color.FromArgb(239, 246, 255);
            public override Drawing.Color MenuItemSelectedGradientEnd => Drawing.Color.FromArgb(239, 246, 255);
            public override Drawing.Color MenuItemPressedGradientBegin => Drawing.Color.FromArgb(219, 234, 254);
            public override Drawing.Color MenuItemPressedGradientEnd => Drawing.Color.FromArgb(219, 234, 254);
            public override Drawing.Color ImageMarginGradientBegin => Drawing.Color.FromArgb(248, 250, 252);
            public override Drawing.Color ImageMarginGradientMiddle => Drawing.Color.FromArgb(248, 250, 252);
            public override Drawing.Color ImageMarginGradientEnd => Drawing.Color.FromArgb(248, 250, 252);
            public override Drawing.Color ToolStripDropDownBackground => Drawing.Color.FromArgb(255, 255, 255);
            public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(226, 232, 240);
            public override Drawing.Color SeparatorLight => Drawing.Color.FromArgb(241, 245, 249);
        }





























        // ---- Динамический порядок колонок списка баз ----

        // Статический порядок колонок данных в сетке (заголовке и строке базы) после
        // фиксированных колонок слева (кнопки групп, компенсатор, избранное, закрепление,
        // название). Совпадает с порядком по умолчанию: «Действия» сразу после «Режим запуска».
        private static readonly string[] StaticDataColumnKeys =
            { "Version", "LaunchMode", "Actions", "ServerBase", "LastLaunch", "Size", "Configuration" };

        // Индекс первой колонки данных в сетке заголовка / строки базы.
        // Строка базы и заголовок имеют одинаковый набор ведущих колонок
        // (кнопки групп + компенсатор + избранное + закрепление + название),
        // поэтому данные строк точно совпадают по горизонтали с заголовками.
        private const int HeaderFirstDataColumn = 5; // после «Названия» заголовка
        private const int RowFirstDataColumn = 5;    // после «Названия» строки базы (как у заголовка)

        // Метка сетки строки базы: по ней находим созданные строки при обходе дерева.
        private static readonly object RowGridMarker = new();
        // Метка сетки заголовка группы: строки групп тоже перестраиваются по порядку колонок,
        // чтобы команды группы оставались в колонке «Действия» на уровне строк баз.
        private static readonly object GroupGridMarker = new();

        /// <summary>
        /// Ключ логической колонки элемента сетки (для динамического порядка колонок).
        /// Используется вместо Tag, т.к. Tag занят другими целями (сортировка, двойной клик).
        /// </summary>
        public static readonly DependencyProperty ColumnKeyProperty =
            DependencyProperty.RegisterAttached(
                "ColumnKey", typeof(string), typeof(MainWindow), new PropertyMetadata(null));

        public static void SetColumnKey(DependencyObject obj, string? value) =>
            obj.SetValue(ColumnKeyProperty, value);

        public static string? GetColumnKey(DependencyObject obj) =>
            (string?)obj.GetValue(ColumnKeyProperty);























        /// <summary>
        /// Блокирует автоматическую прокрутку TreeView к выделенному элементу
        /// (по умолчанию WPF вызывает BringIntoView при IsSelected/Focus — список «прыгает» вверх).
        /// </summary>



















        // Поля для ручного перетаскивания разделителя колонок.
        private ColumnDefinition? _resizeColumn;
        private double _resizeStartWidth;
        private Point _resizeStartMouse;

















        // ===================== Drag & Drop баз и групп =====================
        //
        // Модель WPF DnD (кратко):
        // 1) Источник: после порога смещения мыши вызывается DragDrop.DoDragDrop — синхронный
        //    цикл до Drop/Cancel. Пока он идёт, приходят DragOver/Drop на цели.
        // 2) Цель: AllowDrop=True; в DragOver обязательно задать e.Effects и e.Handled=true,
        //    иначе курсор «запрещено» и Drop не придёт.
        // 3) Данные: лучше свой payload с MouseDown (не с MouseMove) — иначе под курсором
        //    уже другой TreeViewItem (дочерняя база вместо группы).
        // 4) DoDragDrop возвращается после Drop → finally очищает _draggedData; во время Drop
        //    поле ещё валидно. Дополнительно кладём объект в DataObject по имени формата.

        private const string DragFormatInfobase = "Configuration_Management.Infobase";
        private const string DragFormatGroup = "Configuration_Management.GroupNode";


        /// <summary>
        /// Шаг накопительного отступа вложенности дерева (см. Margin у ItemsHost
        /// в ControlTemplate TreeViewItem: "18,0,0,0" на каждый уровень).
        /// Базы внутри групп смещаются вправо на этот шаг, чтобы была видна
        /// иерархия «группа в группе».
        /// </summary>
        private const double GroupTreeIndentStep = 18.0;

        /// <summary>Ширина кнопки разворота группы (px). Синхронизирована с Expander Width в XAML.</summary>
        private const double GroupTreeExpanderWidth = 26.0;












        // ===================== Стеклянный фон окна (acrylic/mica через DWM) =====================
        //
        // Повторяем визуальный эффект Avalonia-версии («прозрачное стекло») средствами WPF.
        // Механика: расширенная системная стеклянная рамка DWM (GlassFrameThickness=-1)
        // + полупрозрачная подложка из цвета темы (~0xE8) + системный acrylic/mica backdrop
        // (Windows 11) либо blur-behind (старые Windows). Если DWM недоступен — остаётся
        // просто полупрозрачный фон без размытия, окно остаётся рабочим.

        /// <summary>Альфа полупрозрачной подложки «стекла» — 0xE8 (~91% непрозрачности).</summary>
        private const byte GlassBackgroundAlpha = 0xE8;

        // DWMWA_SYSTEMBACKDROP_TYPE (38): 2 = Mica, 3 = Acrylic.
        private const int DwmSystemBackdropType = 38;
        private const int DwmBackdropAcrylic = 3;
        private const int DwmBackdropMica = 2;

        // DWMWA_WINDOW_CORNER_PREFERENCE (33): 1 = не скруглять, 2 = скруглять.
        private const int DwmWindowCornerPreference = 33;
        private const int DwmCornerRound = 2;
        private const int DwmCornerDoNotRound = 1;

        // DWM_BB_ENABLE для DwmEnableBlurBehindWindow.
        private const int DwmBbEnable = 0x00000001;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind pBlurBehind);

        [StructLayout(LayoutKind.Sequential)]
        private struct DwmBlurBehind
        {
            public int dwFlags;
            public int fEnable;
            public IntPtr hRgnBlur;
            public int fTransitionOnMaximized;
        }

        /// <summary>
        /// Применяет стеклянное оформление, когда у окна появился HWND (SourceInitialized):
        /// полупрозрачная подложка темы, системный acrylic/mica (или blur-behind) и
        /// скруглённые углы. Никакие сбои DWM не должны блокировать запуск окна.
        /// </summary>
        private void OnWindowSourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                ApplyGlassBackground();
                ApplySystemBackdrop(hwnd);
                ApplyCornerPreference();
            }
            catch
            {
                // Не блокируем запуск из-за недоступности эффекта стекла.
            }
        }

        /// <summary>
        /// Полупрозрачная подложка окна: берём текущий цвет темы (ContentBackgroundBrush,
        /// обновляется для светлой/тёмной темы и любой цветовой схемы) и пересчитываем его
        /// с альфой ~0xE8. Именно эта подложка остаётся основным фоном, а размытие DWM
        /// лишь добавляет эффект «стекла» сквозь прозрачные области.
        /// </summary>
        private void ApplyGlassBackground()
        {
            if (TryFindResource("ContentBackgroundBrush") is SolidColorBrush brush)
            {
                var c = brush.Color;
                Background = new SolidColorBrush(Color.FromArgb(GlassBackgroundAlpha, c.R, c.G, c.B));
            }
        }

        /// <summary>
        /// Включает системный размытый фон: на Windows 11 — acrylic (DWMSBT_TRANSIENTWINDOW),
        /// при недоступности — mica (DWMSBT_MAINWINDOW); на старых Windows — blur-behind.
        /// Если ничего не удалось, откатываемся на полупрозрачный фон без размытия.
        /// </summary>
        private void ApplySystemBackdrop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            try
            {
                // Windows 11 (build >= 22000): системный acrylic backdrop,
                // при недоступности — mica.
                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    int backdrop = DwmBackdropAcrylic;
                    if (DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref backdrop, sizeof(int)) == 0)
                        return;

                    backdrop = DwmBackdropMica;
                    if (DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref backdrop, sizeof(int)) == 0)
                        return;
                }

                // Старые Windows: классический blur-behind.
                var bb = new DwmBlurBehind { dwFlags = DwmBbEnable, fEnable = 1 };
                DwmEnableBlurBehindWindow(hwnd, ref bb);
            }
            catch
            {
                // Не блокируем запуск: останется полупрозрачный фон без размытия.
            }
        }

        /// <summary>
        /// Скруглённые углы окна в стиле glass на уровне DWM (Windows 11). В развёрнутом
        /// состоянии углы обнуляются, чтобы в углах окна не просвечивал рабочий стол.
        /// </summary>
        private void ApplyCornerPreference()
        {
            try
            {
                if (Environment.OSVersion.Version.Build < 22000)
                    return;

                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                int pref = WindowState == WindowState.Maximized ? DwmCornerDoNotRound : DwmCornerRound;
                DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref pref, sizeof(int));
            }
            catch
            {
                // Игнорируем: скругление — некритичное улучшение.
            }
        }

        /// <summary>
        /// При максимизации возвращаем толщину стеклянной рамки к 0: это известное
        /// обходное решение, иначе окно с WindowChrome и расширенной стеклянной рамкой
        /// (GlassFrameThickness=-1) при развороте перекрывает панель задач Windows 11.
        /// </summary>
        private void UpdateGlassFrameForMaximize()
        {
            try
            {
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (chrome is null)
                    return;
                chrome.GlassFrameThickness = WindowState == WindowState.Maximized
                    ? new Thickness(0)
                    : new Thickness(-1);
            }
            catch
            {
                // Игнорируем: если рамку не удалось поправить, окно всё равно работает.
            }
        }

        // ===================== Собственные кнопки управления окном (без системной рамки) =====================

        /// <summary>
        /// Перетаскивание окна за фон верхней панели (окно без системной рамки, WindowChrome
        /// с <c>CaptionHeight=0</c>). Двойной клик по пустой области переключает разворот.
        /// Нажатия на интерактивных элементах (кнопки, поля, вкладки) перехватываются ими
        /// самими и сюда не доходят, поэтому случайного перетаскивания при кликах нет.
        /// </summary>
        private void OnTopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ClickCount == 2)
                {
                    ToggleMaximize();
                    return;
                }

                // Развёрнутое окно не перетаскиваем: возврат к «плавающему» виду делается
                // кнопкой разворота, а DragMove по развёрнутому окну ведёт себя непредсказуемо.
                if (WindowState == WindowState.Maximized)
                    return;

                DragMove();
            }
            catch
            {
                // Игнорируем: DragMove может выбросить при клике, ушедшем в дочерний элемент.
            }
        }

        private void OnMinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        /// <summary>
        /// Закрытие через штатный <see cref="Window.Close"/>: уважает настройку
        /// «свернуть в трей» (<c>CloseToTray</c>), обрабатываемую в <c>OnClosing</c>.
        /// </summary>
        private void OnCloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        /// <summary>
        /// Переключает значок кнопки «развернуть/восстановить» в зависимости от состояния окна
        /// (одинарный квадрат — в обычном состоянии, два квадрата — в развёрнутом).
        /// </summary>
        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            if (MaximizeGlyphPath == null || RestoreGlyphPath == null)
                return;

            bool maximized = WindowState == WindowState.Maximized;
            MaximizeGlyphPath.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
            RestoreGlyphPath.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;

            // При развороте обнуляем скругление углов и толщину стеклянной рамки,
            // чтобы окно корректно прилегало к краям экрана и панели задач.
            ApplyCornerPreference();
            UpdateGlassFrameForMaximize();
        }
    }
}
#endif
