#if LINUX
using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Themes;

namespace Configuration_Management
{
    /// <summary>
    /// База модальных диалоговых окон (Avalonia/Linux). Предоставляет синхронный показ
    /// модального окна с эмуляцией <c>DialogResult</c>, как в <see cref="AvaloniaDialogService"/>:
    /// модальный показ крутит вложенный цикл сообщений <see cref="Dispatcher.PushFrame"/>,
    /// пока окно открыто. Это позволяет вызывать диалоги синхронно из команд ViewModel,
    /// не блокируя UI-поток.
    /// </summary>
    public abstract class ModalWindowBase : Window
    {
        /// <summary>
        /// Задаёт диалогу «стеклянный» стиль главного окна одним местом на все окна:
        /// собственная рамка без системных кнопок, прозрачный фон под acrylic/размытие
        /// и полупрозрачная подложка нужного цвета темы вокруг содержимого. Окну,
        /// которому нужен свой фон, присвоения мало: обёртка держит подложку.
        /// </summary>
        protected ModalWindowBase()
        {
            // Декор окна следует настройке «Системный заголовок окна» (issue #152), как
            // и у главного окна: включена — стандартная системная рамка с её кнопками
            // и перетаскиванием; выключена — собственный безрамковый режим с кнопками
            // управления (свернуть/закрыть) и «стеклянным» полупрозрачным фоном, который
            // рисует базовый класс. Это единое место для всех диалогов, а не повторение
            // в каждом (раньше отдельные окна жёстко ставили SystemDecorations.Full).
            var useSystemTitleBar = ResolveUseSystemTitleBar();
            SystemDecorations = useSystemTitleBar ? SystemDecorations.Full : SystemDecorations.None;

            // На X11 без композитора (или в виртуализации на программном рендере) любое
            // «прозрачное» окно заставляет оконный менеджер непрерывно перерисовывать фон,
            // что проявляется как «зависание» и высокая нагрузка CPU (~36%, issue #153).
            // Поэтому в непрозрачном режиме окно делается простым прямоугольником: без
            // запроса прозрачности и без расширения клиентской области (последнее в
            // безрамковом режиме тоже требует прозрачных полей под скругление/тень).
            // Расширение и прозрачность остаются только для «стекла» на Wayland, где
            // композитор обязателен и постоянной перерисовки фона нет.
            var opaque = useSystemTitleBar || ShouldRenderOpaque;

            // Установка «расширения» клиентской области и прозрачности на X11 без
            // композитора способна бросать исключение или ронять отрисовку безрамочного
            // окна (issue #177: «после правки окно вообще не появилось»). Оборачиваем
            // в try/catch с диагностическим логом, чтобы по журналу видеть первопричину,
            // а не молча получать «чёрное окно» или отсутствие окна на превью.
            try
            {
                ExtendClientAreaToDecorationsHint = !opaque;

                if (opaque)
                {
                    // В непрозрачном режиме прозрачность и расширение не запрашиваем вовсе,
                    // чтобы не провоцировать непрерывную перерисовку фона (issue #153).
                    // Сплошной фон задаём явно, чтобы нативное окно было непрозрачным.
                    // Пустой список эквивалентен null по поведению Avalonia (системный
                    // уровень прозрачности по умолчанию), но не провоцирует CS8625.
                    TransparencyLevelHint = Array.Empty<WindowTransparencyLevel>();

                    // Фон берётся из темы, а не фиксированным тёмным цветом. Со включённым
                    // системным заголовком окна (SystemDecorations.Full) «стеклянной»
                    // подложки у диалога нет, см. UseGlassChrome, и фон окна виден
                    // насквозь: в светлой теме диалог выглядел чёрным прямоугольником
                    // с нечитаемым тёмным текстом. Кисть та же, что у подложки и у
                    // MaterialMessageWindow, поэтому смена темы и цветовой схемы
                    // подхватывается сама.
                    ThemeBrushes.Bind(this, TemplatedControl.BackgroundProperty,
                        "ContentBackgroundColorBrush");
                }
                else
                {
                    // Прозрачность — только в безрамковом режиме: со стандартной системной
                    // рамкой прозрачный фон и расширение клиентской области конфликтуют и могут
                    // ронять приложение при открытии диалога на Linux (issue #150). Размытие
                    // не просим: AcrylicBlur/Blur включает непрерывную перерисовку фона
                    // (issues #150, #153).
                    TransparencyLevelHint = new[]
                    {
                        WindowTransparencyLevel.Transparent
                    };
                    Background = Brushes.Transparent;
                }
            }
            catch (Exception ex)
            {
                // Логируем и продолжаем: окно обязано появиться даже в случае сбоя
                // запроса прозрачности/расширения. Иначе безрамочный диалог «не появляется»
                // вовсе, а виноват только конфликт свойств окна (issue #177).
                LogWindowConstructionError(
                    "Не удалось настроить прозрачность/расширение безрамочного окна", ex);
            }

            // Диалоги не показываются в панели задач: в разметке WPF
            // ShowInTaskbar="False" стоит у всех шестнадцати окон, поэтому здесь
            // это общее свойство базового окна, а не повторение в каждом.
            ShowInTaskbar = false;

            // Модальные окна по умолчанию центрируются относительно владельца, а не
            // экрана: иначе окно настроек открывалось бы всегда на первом мониторе,
            // даже когда главное окно на втором (issue #151). CenterOwner при отсутствии
            // владельца даёт центр экрана, так что ухудшения нет.
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        // Кэш настройки «Системный заголовок окна» на время жизни процесса.
        private static bool? _systemTitleBarCache;

        /// <summary>
        /// Рисовать ли диалоги непрозрачными (issue #153): на X11 с программным рендером
        /// или в виртуализации прозрачность заставляет оконный менеджер непрерывно
        /// перерисовывать фон, что даёт высокую нагрузку CPU. Вычисляется один раз общим
        /// детектором Services.LinuxRendering — та же логика, что у главного окна.
        /// </summary>
        private static readonly bool ShouldRenderOpaque = Services.LinuxRendering.OpaqueWindow;

        /// <summary>
        /// Пишет диагностику сбоя построения окна в файловый лог (issue #177). Задача —
        /// чтобы перехваченные исключения построения безрамочного окна не исчезали молча:
        /// по журналу можно найти первопричину «окно не появилось» / «чёрное окно» на X11
        /// без композитора. Логгер берётся из контейнера лениво и безопасно: если сервисы
        /// ещё не подняты, пишем в трассировку.
        /// </summary>
        private static void LogWindowConstructionError(string what, Exception ex)
        {
            try
            {
                var logger = AppServices.GetRequiredService<Configuration_Management.Services.IAppLogger>();
                logger.Error($"{what} ({GetTypeName()}). {ex}");
            }
            catch
            {
                System.Diagnostics.Trace.WriteLine($"[window] {what}: {ex}");
            }

            static string GetTypeName() => "ModalWindowBase";
        }

        /// <summary>
        /// Читает настройку «Системный заголовок окна» из репозитория. Значение кэшируется
        /// на время жизни процесса, как и у главного окна: изменение вступает в силу после
        /// перезапуска. При любой ошибке чтения настроек возвращается false (безрамковый
        /// режим по умолчанию), чтобы диалог гарантированно открылся.
        /// </summary>
        private static bool ResolveUseSystemTitleBar()
        {
            if (_systemTitleBarCache is bool cached)
            {
                return cached;
            }
            var value = false;
            try
            {
                value = AppServices.GetRequiredService<Configuration_Management.Services.IInfobaseRepository>()
                    .LoadSettings().UseSystemTitleBar;
            }
            catch
            {
                value = false;
            }
            _systemTitleBarCache = value;
            return value;
        }

        /// <summary>
        /// Сбрасывает кэш настройки «Системный заголовок окна» (issue #159). Вызывается
        /// из окна настроек после изменения настройки, чтобы уже открываемые далее
        /// модальные окна применили новое значение, не дожидаясь перезапуска.
        /// </summary>
        public static void InvalidateSystemTitleBarCache() => _systemTitleBarCache = null;

        /// <summary>
        /// Источник локализации для привязок XAML: <c>{Binding Loc[Key]}</c>.
        /// При смене языка открытые окна автоматически обновляют текст.
        /// </summary>
        public LocalizationSource Loc => LocalizationManager.Instance.Source;

        // Поля для живого обновления текста кнопок «Отмена»/«ОК» при смене языка.
        private TextBlock? _lastCancelText;
        private TextBlock? _lastOkText;
        private string _lastOkRaw = "";
        // Ключ подписи подтверждения, когда кнопку строили по ключу, а не по
        // готовой строке: только так подпись переводится при смене языка.
        private string? _lastOkKey;
        private bool _languageSubscribed;

        // Поля «стеклянной» обёртки содержимого.
        private Border? _glassRoot;
        private bool _wrappingContent;
        private bool _resizeZonesAdded;
        private IDisposable? _glassStateSub;
        private IDisposable? _titleSub;

        /// <summary>Результат диалога: true — подтверждён (ОК), false — отменён.</summary>
        public bool DialogResult { get; protected set; }

        /// <summary>
        /// Оборачивать ли содержимое диалога в собственную «стеклянную» рамку без системного
        /// заголовка (по умолчанию — да, как у главного окна). Окно автоматически обходится
        /// без неё, если использует стандартный системный заголовок (<see cref="SystemDecorations.Full"/>):
        /// иначе SystemDecorations.None + ExtendClientAreaToDecorationsHint + прозрачный фон
        /// конфликтуют с запрошенной системной рамкой и вызывают падение на Linux (issue #150).
        /// Конкретное окно может отключить обёртку и для безрамкового режима.
        /// </summary>
        protected virtual bool UseGlassChrome => SystemDecorations != SystemDecorations.Full;

        protected override void OnClosed(EventArgs e)
        {
            // Событие живёт у синглтона локализации, а обработчик это метод
            // экземпляра: без отписки закрытое окно оставалось бы достижимым.
            // Кисти темы отписывать не нужно, их привязку держит сам элемент.
            if (_languageSubscribed)
            {
                LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
                _languageSubscribed = false;
            }

            // Подписки обёртки «стекла» на состояние окна и заголовок.
            _glassStateSub?.Dispose();
            _glassStateSub = null;
            _titleSub?.Dispose();
            _titleSub = null;

            base.OnClosed(e);
        }

        /// <summary>
        /// Показывает окно модально (синхронно) относительно владельца и блокирует
        /// вызывающий поток до закрытия окна.
        /// </summary>
        /// <param name="owner">Окно-владелец (например, главное). Может быть null.</param>
        /// <returns>True, если пользователь подтвердил действие (DialogResult == true).</returns>
        public bool ShowDialogSync(Window? owner = null)
        {
            // Повторный вход при уже открытом окне недопустим: вложенный PushFrame
            // никогда бы не завершился, так как Closed не сработает повторно.
            if (IsVisible)
                return DialogResult;

            // Кадр вложенного цикла снимается не только по закрытию окна, но и по
            // его скрытию. Window.Hide() события Closed не даёт, а прячет окно вместе
            // с дочерними, а такое скрытие приходит со стороны, пока диалог открыт:
            // при настройке «после запуска базы уйти в трей» базу можно запустить
            // из меню трея, и главное окно спрячется вместе с диалогом
            // (MainWindow.Avalonia.cs, OnAfterLaunchRequested). Раньше кадр в этом
            // случае оставался крутиться навсегда, и приложение замирало целиком,
            // оставаясь живым процессом. Кадр пересоздаётся для запасного пути показа,
            // поэтому обработчики читают текущий, а не захваченный при подписке.
            // Результат прошлого сеанса не переносится в новый: окно можно показать
            // повторно, и неотвеченный диалог обязан вернуть отказ, а не прежнее «да».
            DialogResult = false;

            DispatcherFrame frame = new();
            var opened = false;
            var hiddenWithoutClose = false;
            var closed = false;

            void StopFrame() => frame.Continue = false;
            void OnOpened(object? sender, EventArgs e) => opened = true;
            void OnClosedHandler(object? sender, EventArgs e)
            {
                closed = true;
                StopFrame();
            }

            void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property != IsVisibleProperty || !Equals(e.NewValue, false) || closed)
                    return;

                // Скрытое окно остаётся открытым для приложения: оно продолжает
                // числиться в списке окон, а при ShutdownMode.OnLastWindowClose это
                // держит процесс живым и не отпускает блокировку единственного
                // экземпляра. Поэтому скрытие доводится до закрытия, но уже после
                // выхода из кадра, чтобы не закрывать окно изнутри его же события.
                hiddenWithoutClose = true;
                StopFrame();
            }

            Opened += OnOpened;
            Closed += OnClosedHandler;
            PropertyChanged += OnVisibilityChanged;

            // Владелец пригоден только видимый и с измеренной геометрией. На Linux/X11
            // модальный показ относительно неотрисованного владельца (нулевая геометрия)
            // и центрирование по нему способны вызывать нативный abort при открытии
            // диалога (issue #168). С непригодным владельцем окно открываем по центру
            // экрана и без привязки модальности к окну.
            var validOwner = owner is { IsVisible: true } o && HasUsableBounds(o);

            try
            {
                if (validOwner)
                {
                    // validOwner гарантирует ненулевого владельца; оператор ! — только
                    // для анализатора nullability, чтобы не давать ложное предупреждение.
                    WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    _ = ShowDialog(owner!);
                }
                else
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    Show();
                }

                Dispatcher.UIThread.PushFrame(frame);
            }
            catch (Exception ex)
            {
                // Первый способ показа сорвался (нативный сбой Avalonia при открытии
                // диалога, раньше ронявший процесс abort-ом). Не даём окну молча
                // исчезнуть (issue #168: «не падает, но и не открывается»): пробуем
                // запасной путь — немодальный показ по центру экрана. Если и он падает,
                // снимаем кадр и прячем неоткрытое окно. Исключение логируем, чтобы по
                // журналу было видно, на чём именно рвётся показ безрамочного окна
                // (issue #177).
                LogWindowConstructionError("Не удалось открыть модальное окно первым способом", ex);
                frame.Continue = false;
                try
                {
                    // Запасному показу нужен свой кадр: прежний уже остановлен, и
                    // PushFrame на нём возвращается сразу, то есть окно закрывалось бы,
                    // едва открывшись, а результат диалога возвращался бы неспрошенным.
                    frame = new DispatcherFrame();
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    if (!IsVisible)
                        Show();

                    // Ждать имеет смысл только когда окно действительно открылось.
                    // IsVisible этого не доказывает: показ выставляет его до разметки
                    // содержимого, и при исключении в разметке окно остаётся «видимым»,
                    // ни разу не появившись. Ожидание такого окна не закончится никогда.
                    if (opened)
                        Dispatcher.UIThread.PushFrame(frame);
                }
                catch (Exception fallbackEx)
                {
                    LogWindowConstructionError("Запасной показ модального окна тоже не удался", fallbackEx);
                    frame.Continue = false;
                    try { if (IsVisible) Hide(); } catch { /* ignore */ }
                }
            }
            finally
            {
                // Снимаем кадр при любом исходе: если ShowDialog/Show бросили исключение
                // до входа в цикл сообщений, кадр не должен зависнуть, а неоткрытое окно
                // прячем, чтобы повторное открытие не копило висящие окна.
                frame.Continue = false;
                try { if (IsVisible) Hide(); } catch { /* ignore */ }

                // Окно, спрятанное со стороны, закрываем: иначе оно остаётся
                // в списке окон приложения и держит процесс.
                if (hiddenWithoutClose && !closed)
                {
                    try { Close(); } catch { /* ignore */ }
                }

                // Подписки этого сеанса снимаются: окно может показываться повторно,
                // а обработчики держат кадр уже закончившегося показа.
                Opened -= OnOpened;
                Closed -= OnClosedHandler;
                PropertyChanged -= OnVisibilityChanged;
            }

            return DialogResult;
        }

        /// <summary>
        /// Признак того, что окно имеет измеренную ненулевую геометрию и пригодно
        /// в качестве владельца модального диалога. На Linux/X11 центрирование по
        /// владельцу с нулевым размером и показ диалога поверх него могут давать
        /// нативный abort при открытии окна (issue #168).
        /// </summary>
        private static bool HasUsableBounds(Window w)
        {
            try
            {
                if (w.Bounds.Width > 0 && w.Bounds.Height > 0)
                    return true;
            }
            catch
            {
                // Bounds может быть недоступен у ещё не показанного окна.
            }
            // Явно заданная ширина/высота тоже считается пригодной геометрией.
            return w.Width > 0 && w.Height > 0;
        }

        /// <summary>
        /// Показывает окно модально (синхронно) без владельца.
        /// </summary>
        public bool ShowDialogSync() => ShowDialogSync(null);

        // ======================= «Стеклянная» обёртка содержимого =======================

        /// <summary>
        /// Каждый диалог строит своё содержимое, не задумываясь о рамке окна. Чтобы
        /// добавить «стеклянную» подложку, собственные кнопки окна и перетаскивание
        /// без правки восемнадцати окон, содержимое оборачивается здесь, в базе:
        /// первый установленный Control становится телом «стеклянного» контейнера
        /// с полосой заголовка (перетаскивание + свернуть/закрыть) поверх.
        /// </summary>
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property != ContentProperty || _wrappingContent)
            {
                return;
            }
            // Окна со стандартной системной рамкой обходятся без «стеклянной» обёртки:
            // прозрачная подложка и ExtendClientAreaToDecorationsHint в сочетании с ней
            // на Linux роняют приложение при открытии диалога (issue #150).
            if (!UseGlassChrome)
            {
                return;
            }
            if (change.GetNewValue<object?>() is Control inner)
            {
                _wrappingContent = true;
                try
                {
                    // Обёртка ставит Content второй раз, пока inner уже числится
                    // логическим ребёнком окна: при замене ContentControl снимает
                    // у него логического родителя, а inner к этому моменту лежит
                    // внутри нового Grid, и обход дерева обёртки падает с
                    // AttachedToLogicalTreeCore ... has no logical parent.
                    // Сброс в null отпускает inner заранее.
                    Content = null;
                    Content = BuildChrome(inner);
                }
                finally
                {
                    _wrappingContent = false;
                }
            }
        }

        /// <summary>
        /// Собирает «стеклянный» контейнер: скруглённый полупрозрачный корень, внутри —
        /// полоса заголовка с кнопками окна и тело диалога.
        /// </summary>
        private Control BuildChrome(Control inner)
        {
            var glass = new Border
            {
                CornerRadius = new CornerRadius(UiMetrics.RadiusLg),
                ClipToBounds = true
            };
            ApplyGlassBackground(glass);
            _glassRoot = glass;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Для окон с SizeToContent по высоте строка содержимого тоже Auto: звёздная
            // строка в окне, меряющемся по содержимому, могла бы схлопнуться или уехать.
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = SizeToContent == SizeToContent.Height || SizeToContent == SizeToContent.WidthAndHeight
                    ? GridLength.Auto
                    : GridLength.Star
            });

            var strip = BuildTitleStrip();
            Grid.SetRow(strip, 0);
            Grid.SetRow(inner, 1);

            grid.Children.Add(strip);
            grid.Children.Add(inner);

            glass.Child = grid;

            // В развёрнутом виде скругление убираем: в углах окна не должно
            // просвечивать содержимое рабочего стола под рамкой соседних окон.
            _glassStateSub = this.GetObservable(WindowStateProperty)
                .Subscribe(new WindowStateObserver(() =>
                    glass.CornerRadius = WindowState == WindowState.Maximized
                        ? new CornerRadius(0)
                        : new CornerRadius(UiMetrics.RadiusLg)));

            return glass;
        }

        /// <summary>
        /// Альфа полупрозрачной «стеклянной» подложки: ~91% непрозрачности сохраняет
        /// контраст текста, но при этом сквозь неё проступает acrylic/размытие фона.
        /// </summary>
        private const byte GlassBackgroundAlpha = 0xE8;

        /// <summary>
        /// Подписка стеклянного контейнера на цвет фона темы: берём текущий
        /// <c>ContentBackgroundColorBrush</c> и делаем из него (полу)прозрачную версию,
        /// чтобы обе темы и все цветовые схемы выглядели как «стекло» своего цвета.
        /// В непрозрачном режиме (X11 без композитора/виртуализация, issue #153)
        /// подложка рисуется полностью непрозрачной: полупрозрачный слой поверх окна
        /// в этих окружениях тоже способен включать лишнюю компоновку кадра.
        /// </summary>
        private void ApplyGlassBackground(Border glass)
        {
            var alpha = ShouldRenderOpaque ? (byte)0xFF : GlassBackgroundAlpha;
            ThemeBrushes.Observe(glass, "ContentBackgroundColorBrush",
                brush => glass.Background = ThemeBrushes.WithAlpha(brush, alpha));
        }

        /// <summary>
        /// Полоса заголовка диалога: слева заголовок окна, справа собственные кнопки
        /// управления (свернуть, закрыть с красным выделением). Пустое место полосы
        /// таскает окно за собой (<see cref="BeginMoveDrag"/>).
        /// </summary>
        private Control BuildTitleStrip()
        {
            // Прозрачная заливка обязательна: Border без Background не участвует
            // в проверке попадания, нажатие уходит мимо и окно не таскается
            // (issue #177). Внешний вид от неё не меняется.
            var strip = new Border { Background = Brushes.Transparent };
            strip.PointerPressed += OnTitleStripPointerPressed;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(UiMetrics.Scaled(14), 0, 0, 0),
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Height = UiMetrics.Scaled(30)
            };
            ThemeBrushes.Bind(title, TextBlock.ForegroundProperty, "TextPrimaryColorBrush");
            // Заголовок следует за свойством окна (в т.ч. за привязкой {loc:Loc ...}).
            _titleSub = title.Bind(TextBlock.TextProperty, this.GetObservable(TitleProperty));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            var minimize = new DialogWindowControlButton(DialogWindowControlKind.Minimize);
            ToolTip.SetTip(minimize, LocalizationManager.T("Window.Minimize"));
            minimize.Click += (_, _) => WindowState = WindowState.Minimized;
            buttons.Children.Add(minimize);

            var close = new DialogWindowControlButton(DialogWindowControlKind.Close);
            ToolTip.SetTip(close, LocalizationManager.T("Common.Close"));
            close.Click += (_, _) => Close();
            buttons.Children.Add(close);

            Grid.SetColumn(title, 0);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(title);
            grid.Children.Add(buttons);

            strip.Child = grid;
            return strip;
        }

        /// <summary>
        /// Перетаскивание окна за полосу заголовка. Кнопки, поля и прочие
        /// интерактивные элементы движение не начинают — только пустое место полосы.
        /// </summary>
        private void OnTitleStripPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            if (WindowState == WindowState.Maximized)
                return;
            if (IsInteractiveSource(e.Source))
                return;
            BeginMoveDrag(e);
        }

        /// <summary>true, если источник нажатия — интерактивный элемент полосы заголовка.</summary>
        private static bool IsInteractiveSource(object? source)
        {
            var node = source as Visual;
            while (node is not null)
            {
                if (node is Button or ToggleButton or TextBox or HelpLink)
                    return true;
                node = node.GetVisualParent();
            }
            return false;
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            // Без системной рамки изменение размера рисуем сами — невидимые зоны по
            // краям и углам. Только для изменяемых окон: у фиксированных ресайза нет.
            if (CanResize && !_resizeZonesAdded && _glassRoot?.Child is Grid grid)
            {
                AddResizeZones(grid);
                _resizeZonesAdded = true;
            }

            // На Linux/X11 окно после показа не всегда сразу получает клавиатурный
            // фокус, пока пользователь не кликнул по какому-нибудь элементу (issue #226):
            // Esc, нажатый сразу после открытия, уходил в главное окно или в никуда,
            // а кнопка «Отмена» (IsCancel) срабатывала только после получения фокуса.
            // Запрашиваем активацию/фокус окна при каждом открытии, чтобы Esc
            // обрабатывался самим диалогом (см. OnKeyDown) с первого нажатия и при
            // повторном открытии, а не был «одноразовым». Активация выставляется через
            // Dispatcher, чтобы не делать её изнутри события показа окна. Флаг IsActive
            // при этом становится истинным, и главное окно по Esc не уходит в трей
            // (MainWindow.Avalonia.cs, HasOpenModalDialog).
            Dispatcher.UIThread.Post(Activate, DispatcherPriority.Input);
        }

        /// <summary>
        /// Esc закрывает активный модальный диалог (issue #226). Обработка живёт на
        /// уровне самой базы, чтобы диалог закрывался сам, а не полагался на главное
        /// окно (которое при открытом диалоге по Esc лишь уходит в трей, см.
        /// MainWindow.Avalonia.cs). Не перехватываем Esc, если его уже обработал
        /// вложенный элемент и пометил событие обработанным: редактируемый ComboBox
        /// с раскрытым списком и HotkeyBox отменяют по Esc свой ввод — такое закрывать
        /// диалог не должно. Закрытие равносильно нажатию «Отмена»: положительный
        /// результат (<see cref="DialogResult"/>) не выставляется.
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && !e.Handled)
            {
                CloseAsCancel();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        /// <summary>
        /// Закрывает диалог так же, как кнопка «Отмена»: без положительного
        /// результата. Вызывается и из <see cref="OnKeyDown"/>, и из главного окна,
        /// когда Esc пришёл туда (см. MainWindow.Avalonia.cs, OnWindowKeyDown).
        /// </summary>
        internal void CloseAsCancel()
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Невидимые зоны изменения размера по краям и углам окна (системной рамки
        /// больше нет): нажатие в такой зоне вызывает BeginResizeDrag нужного края.
        /// </summary>
        private void AddResizeZones(Grid root)
        {
            const double edgeThickness = 6;
            const double cornerSize = 12;

            var overlay = new Grid();
            Grid.SetRowSpan(overlay, root.RowDefinitions.Count);
            overlay.ZIndex = 2000;

            AddResizeZone(overlay, WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top,
                cornerSize, cornerSize, StandardCursorType.TopLeftCorner);
            AddResizeZone(overlay, WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top,
                cornerSize, cornerSize, StandardCursorType.TopRightCorner);
            AddResizeZone(overlay, WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom,
                cornerSize, cornerSize, StandardCursorType.BottomLeftCorner);
            AddResizeZone(overlay, WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom,
                cornerSize, cornerSize, StandardCursorType.BottomRightCorner);
            AddResizeZone(overlay, WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top,
                0, edgeThickness, StandardCursorType.SizeNorthSouth);
            AddResizeZone(overlay, WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
                0, edgeThickness, StandardCursorType.SizeNorthSouth);
            AddResizeZone(overlay, WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch,
                edgeThickness, 0, StandardCursorType.SizeWestEast);
            AddResizeZone(overlay, WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch,
                edgeThickness, 0, StandardCursorType.SizeWestEast);

            root.Children.Add(overlay);
        }

        private void AddResizeZone(Grid host, WindowEdge edge, HorizontalAlignment ha, VerticalAlignment va,
            double width, double height, StandardCursorType cursor)
        {
            var zone = new Border
            {
                HorizontalAlignment = ha,
                VerticalAlignment = va,
                Width = width > 0 ? width : double.NaN,
                Height = height > 0 ? height : double.NaN,
                // Прозрачная, но не null кисть: по ней всё равно идёт hit-test.
                Background = Brushes.Transparent,
                Cursor = new Cursor(cursor)
            };
            zone.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginResizeDrag(edge, e);
            };
            // host это overlay, созданный как new Grid() без строк и колонок,
            // поэтому span из его счётчиков равен нулю, а Avalonia такой span
            // не принимает: ArgumentException прямо в OnOpened. Зона и без
            // привязок занимает единственную ячейку целиком.
            host.Children.Add(zone);
        }

        // ======================= Панель кнопок «Отмена»/«ОК» =======================

        /// <summary>
        /// Строит стандартный ряд кнопок «Отмена»/«ОК» с иконками и обработчиками.
        /// При нажатии «ОК» сначала выполняется <paramref name="onOk"/> (если задан),
        /// затем устанавливается <see cref="DialogResult"/> и окно закрывается.
        /// </summary>
        /// <param name="okText">Текст кнопки подтверждения. Если пуст/null — используется локализованный текст <c>Common.Ok</c>.</param>
        /// <param name="okWidth">Ширина кнопки подтверждения.</param>
        /// <param name="onOk">Необязательный обратный вызов при подтверждении (например, сохранить результат).</param>

        /// <summary>
        /// Строит правую панель кнопок «Отмена» и подтверждение, как в разметке WPF:
        /// отмена мягкой заливкой, подтверждение акцентом, зазор 8.
        /// </summary>
        /// <param name="okText">Подпись кнопки подтверждения; пусто означает «ОК».</param>
        /// <param name="okWidth">Ширина кнопки подтверждения.</param>
        /// <param name="onOk">Что выполнить перед закрытием с положительным результатом.</param>
        /// <param name="cancelWidth">Ширина кнопки отмены.</param>
        /// <param name="okIconKey">Ключ значка кнопки подтверждения.</param>
        /// <param name="okTextKey">
        /// Ключ подписи подтверждения вместо готовой строки: только так подпись
        /// переводится при смене языка.
        /// </param>
        protected StackPanel BuildButtons(string? okText = null, double okWidth = 130, Action? onOk = null,
            double cancelWidth = 110, string okIconKey = "IconOk", string? okTextKey = null)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };

            panel.Children.Add(BuildCancelButton(cancelWidth));
            panel.Children.Add(BuildConfirmButton(okText, okWidth, onOk, okIconKey: okIconKey, okTextKey: okTextKey));
            return panel;
        }

        /// <summary>
        /// Кнопка отмены. По умолчанию это основная кнопка с мягкой заливкой и
        /// основным цветом текста, как её задаёт разметка WPF в диалогах ввода;
        /// <paramref name="secondary"/> переключает её на стиль вторичной кнопки.
        /// </summary>
        /// <param name="width">Ширина кнопки.</param>
        /// <param name="secondary">Оформлять стилем вторичной кнопки.</param>
        /// <param name="minimumWidth">Ширина задаётся как минимальная, а не жёсткая.</param>
        protected Button BuildCancelButton(double width = 110, bool secondary = false, bool minimumWidth = false)
        {
            var caption = new TextBlock
            {
                Text = LocalizationManager.T("Common.Cancel"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        IconHelper.MakeIcon("IconClose", 14, secondary ? "SecondaryButtonTextBrush" : "TextPrimaryBrush"),
                        caption
                    }
                },
                IsCancel = true
            };

            button.Styled(secondary ? ControlThemes.SecondaryButton : ControlThemes.ModernButton);
            if (!secondary)
            {
                Themes.ThemeBrushes.Bind(button, BackgroundProperty, "ItemHoverBrush");
                Themes.ThemeBrushes.Bind(button, ForegroundProperty, "TextPrimaryBrush");
            }

            SetButtonWidth(button, width, minimumWidth);
            button.Click += (_, _) => Close();

            _lastCancelText = caption;
            EnsureLanguageSubscription();
            return button;
        }

        /// <summary>
        /// Кнопка подтверждения: акцентная заливка, значок и подпись, закрытие
        /// с положительным результатом.
        /// </summary>
        /// <param name="okText">Подпись; пусто означает «ОК».</param>
        /// <param name="width">Ширина кнопки.</param>
        /// <param name="onOk">Что выполнить перед закрытием.</param>
        /// <param name="minimumWidth">Ширина задаётся как минимальная, а не жёсткая.</param>
        /// <param name="okIconKey">Ключ значка.</param>
        /// <param name="okTextKey">Ключ подписи вместо готовой строки.</param>
        protected Button BuildConfirmButton(string? okText, double width = 130, Action? onOk = null,
            bool minimumWidth = false, string okIconKey = "IconOk", string? okTextKey = null)
        {
            var caption = new TextBlock
            {
                Text = okTextKey is { Length: > 0 } captionKey
                    ? LocalizationManager.T(captionKey)
                    : ResolveOkText(okText),
                VerticalAlignment = VerticalAlignment.Center
            };

            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        IconHelper.MakeIcon(okIconKey, 14, "ButtonTextBrush"),
                        caption
                    }
                },
                IsDefault = true
            };

            button.Styled(ControlThemes.ModernButton);
            SetButtonWidth(button, width, minimumWidth);
            button.Click += (_, _) =>
            {
                onOk?.Invoke();
                DialogResult = true;
                Close();
            };

            _lastOkText = caption;
            _lastOkRaw = okText ?? "";
            _lastOkKey = okTextKey;
            EnsureLanguageSubscription();
            return button;
        }

        /// <summary>
        /// Кнопка подтверждения нижней панели диалога: зелёная заливка, белые
        /// значок и подпись. Так эта кнопка задана в разметке WPF у десяти окон.
        /// </summary>
        /// <param name="textKey">Ключ локализации подписи.</param>
        /// <param name="iconKey">Ключ значка.</param>
        /// <param name="width">Ширина кнопки.</param>
        /// <param name="onOk">Что выполнить перед закрытием с положительным результатом.</param>
        /// <param name="height">Высота кнопки.</param>
        /// <param name="iconSize">Размер значка.</param>
        /// <param name="iconGap">Зазор между значком и подписью.</param>
        /// <param name="closeOnClick">
        /// Закрывать окно по нажатию. Ложь нужна окнам, где кнопка сначала
        /// проверяет введённое и закрывает окно сама.
        /// </param>
        protected Button BuildConfirmActionButton(string textKey, string iconKey, double width,
            Action? onOk = null, double height = 36, double iconSize = 16, double iconGap = 6,
            bool closeOnClick = true)
        {
            var caption = new TextBlock
            {
                Text = LocalizationManager.T(textKey),
                VerticalAlignment = VerticalAlignment.Center
            };

            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = iconGap,
                    Children =
                    {
                        IconHelper.MakeIcon(iconKey, iconSize, Brushes.White),
                        caption
                    }
                },
                Width = width,
                Height = height,
                IsDefault = true
            };

            button.Styled(ControlThemes.DialogConfirmButton);
            if (closeOnClick)
            {
                button.Click += (_, _) =>
                {
                    onOk?.Invoke();
                    DialogResult = true;
                    Close();
                };
            }
            else if (onOk is not null)
            {
                button.Click += (_, _) => onOk();
            }

            _lastOkText = caption;
            _lastOkKey = textKey;
            EnsureLanguageSubscription();
            return button;
        }

        /// <summary>
        /// Кнопка отмены нижней панели диалога: прозрачная заливка, красный контур,
        /// красные значок и подпись, как в разметке WPF.
        /// </summary>
        /// <param name="width">Ширина кнопки.</param>
        /// <param name="height">Высота кнопки.</param>
        /// <param name="iconSize">Размер значка.</param>
        /// <param name="iconGap">Зазор между значком и подписью.</param>
        protected Button BuildCancelActionButton(double width = 140, double height = 36,
            double iconSize = 16, double iconGap = 6)
        {
            var caption = new TextBlock
            {
                Text = LocalizationManager.T("Common.Cancel"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = iconGap,
                    Children =
                    {
                        IconHelper.MakeIcon("IconClose", iconSize, DangerBrush),
                        caption
                    }
                },
                Width = width,
                Height = height,
                IsCancel = true
            };

            button.Styled(ControlThemes.DialogCancelButton);
            button.Click += (_, _) => Close();

            _lastCancelText = caption;
            EnsureLanguageSubscription();
            return button;
        }

        /// <summary>Красный цвет отмены и удаления: в разметке WPF он задан числом.</summary>
        protected static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#EF4444"));

        /// <summary>
        /// Берёт на себя перевод подписи кнопки, которую окно собрало само.
        /// Без этого при смене языка переводится только «Отмена», и панель
        /// остаётся двуязычной.
        /// </summary>
        /// <param name="caption">Подпись кнопки подтверждения.</param>
        /// <param name="textKey">Ключ локализации этой подписи.</param>
        protected void RegisterConfirmCaption(TextBlock caption, string textKey)
        {
            _lastOkText = caption;
            _lastOkKey = textKey;
            EnsureLanguageSubscription();
        }

        private static void SetButtonWidth(Button button, double width, bool minimumWidth)
        {
            if (minimumWidth)
                button.MinWidth = width;
            else
                button.Width = width;
        }

        /// <summary>
        /// Возвращает локализованный текст кнопки подтверждения.
        /// Пустое значение (null/пустая строка) интерпретируется как <c>Common.Ok</c>.
        /// </summary>
        private static string ResolveOkText(string? okText) =>
            string.IsNullOrEmpty(okText) ? LocalizationManager.T("Common.Ok") : okText;

        /// <summary>
        /// Включает подписку на смену языка. Вызывается сама для окон
        /// со стандартной панелью кнопок; окну, которое строит кнопки само,
        /// нужно вызвать её явно.
        /// </summary>
        protected void EnsureLanguageSubscription()
        {
            if (_languageSubscribed)
                return;
            _languageSubscribed = true;
            LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
        }

        protected virtual void OnLanguageChanged(object? sender, EventArgs e)
        {
            if (_lastCancelText is not null)
                _lastCancelText.Text = LocalizationManager.T("Common.Cancel");
            if (_lastOkText is not null)
                _lastOkText.Text = _lastOkKey is { Length: > 0 } key
                    ? LocalizationManager.T(key)
                    : ResolveOkText(_lastOkRaw);
        }

        /// <summary>Простой наблюдатель bool (для IsEnabled кнопки управления окном).</summary>
        private sealed class BoolObserver : IObserver<bool>
        {
            private readonly Action<bool> _action;
            public BoolObserver(Action<bool> action) => _action = action;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(bool value) => _action(value);
        }

        /// <summary>Простой наблюдатель WindowState (для скругления при развороте).</summary>
        private sealed class WindowStateObserver : IObserver<WindowState>
        {
            private readonly Action _action;
            public WindowStateObserver(Action action) => _action = action;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(WindowState value) => _action();
        }

        /// <summary>Вид кнопки управления окном диалога: свернуть или закрыть.</summary>
        private enum DialogWindowControlKind { Minimize, Close }

        /// <summary>
        /// Собственная кнопка управления окном диалога (свернуть/закрыть). Значок
        /// строится из StreamGeometry; цвет значка и hover-подложка следуют теме через
        /// ThemeBrushes. У кнопки «закрыть» красная подложка при наведении/нажатии
        /// и белый значок, как у главного окна.
        /// </summary>
        private sealed class DialogWindowControlButton : Button
        {
            // Контуры в координатном поле 24 на 24 (как ресурсы Icons.axaml).
            private const string MinimizeData = "M6,11.5H18V13H6Z";
            private const string CloseData =
                "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z";

            private readonly DialogWindowControlKind _kind;
            private readonly Avalonia.Controls.Shapes.Path _glyph;

            private IBrush _hoverBg = Brushes.Transparent;
            private IBrush _pressedBg = Brushes.Transparent;
            private IBrush _baseGlyphBrush = Brushes.Transparent;
            private bool _hovered;
            private bool _pressed;

            // Красная подложка кнопки «закрыть» (классический алый), не зависит от темы:
            // наведение — алый, нажатие — чуть темнее. Значок на ней всегда белый.
            private static readonly IBrush CloseHoverBrush = new SolidColorBrush(Color.Parse("#E81123"));
            private static readonly IBrush ClosePressedBrush = new SolidColorBrush(Color.Parse("#C50F1F"));

            public DialogWindowControlButton(DialogWindowControlKind kind)
            {
                _kind = kind;

                Width = UiMetrics.Scaled(40);
                Height = UiMetrics.Scaled(30);
                Padding = new Thickness(0);
                HorizontalContentAlignment = HorizontalAlignment.Center;
                VerticalContentAlignment = VerticalAlignment.Center;
                Cursor = new Cursor(StandardCursorType.Hand);

                // Кастомный шаблон: скруглённый Border + ContentPresenter (без Fluent-хрома).
                Theme = new ControlTheme(typeof(Button))
                {
                    Setters =
                    {
                        new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<DialogWindowControlButton>((_, _) =>
                        {
                            var border = new Border { CornerRadius = new CornerRadius(UiMetrics.RadiusSm) };
                            border[!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty);
                            border[!Border.BorderBrushProperty] = new TemplateBinding(TemplatedControl.BorderBrushProperty);
                            var presenter = new ContentPresenter();
                            presenter[!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty);
                            presenter[!ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty);
                            presenter[!ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(ContentControl.VerticalContentAlignmentProperty);
                            border.Child = presenter;
                            return border;
                        }))
                    }
                };

                _glyph = new Avalonia.Controls.Shapes.Path
                {
                    Width = UiMetrics.Scaled(16),
                    Height = UiMetrics.Scaled(16),
                    Stretch = Stretch.Uniform,
                    Data = StreamGeometry.Parse(_kind == DialogWindowControlKind.Close ? CloseData : MinimizeData)
                };
                Content = _glyph;

                // Цвет значка и hover-подложка следуют теме. У кнопки «закрыть» цвет значка
                // храним отдельно: при красной подложке он перекрашивается в белый, а при
                // выходе курсора возвращается к теме (см. ApplyState).
                if (_kind == DialogWindowControlKind.Close)
                    ThemeBrushes.Observe(this, "TextPrimaryColorBrush", b => { _baseGlyphBrush = b; ApplyState(); });
                else
                    ThemeBrushes.Bind(_glyph, Avalonia.Controls.Shapes.Path.FillProperty, "TextPrimaryColorBrush");
                ThemeBrushes.Observe(this, "ItemHoverBrush", b => { _hoverBg = b; ApplyState(); });
                ThemeBrushes.Observe(this, "AccentPressedBrush", b => { _pressedBg = b; ApplyState(); });

                PointerEntered += (_, _) => { _hovered = true; ApplyState(); };
                PointerExited += (_, _) => { _hovered = false; _pressed = false; ApplyState(); };
                PointerPressed += (_, _) => { _pressed = true; ApplyState(); };
                PointerReleased += (_, _) => { _pressed = false; ApplyState(); };
                PointerCaptureLost += (_, _) => { _pressed = false; ApplyState(); };
                this.GetObservable(IsEnabledProperty).Subscribe(new BoolObserver(_ => ApplyState()));

                ApplyState();
            }

            private void ApplyState()
            {
                if (!IsEnabled)
                {
                    Opacity = 0.55;
                    Background = Brushes.Transparent;
                    BorderBrush = Brushes.Transparent;
                    return;
                }

                Opacity = 1.0;
                BorderBrush = Brushes.Transparent;

                if (_kind == DialogWindowControlKind.Close)
                {
                    // Кнопка «закрыть»: красная подложка при наведении/нажатии, значок — белый.
                    var redActive = _pressed || _hovered;
                    Background = _pressed ? ClosePressedBrush : (_hovered ? CloseHoverBrush : Brushes.Transparent);
                    _glyph.Fill = redActive ? Brushes.White : _baseGlyphBrush;
                }
                else
                {
                    Background = _pressed ? _pressedBg : (_hovered ? _hoverBg : Brushes.Transparent);
                }
            }
        }
    }
}
#endif