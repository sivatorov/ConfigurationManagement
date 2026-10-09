#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    public partial class MainWindow
    {

        /// <summary>
        /// Запускает бесконечное «подпрыгивание» индикатора выгрузки .dt/.cf (стрелка вверх).
        /// </summary>
        private void StartExportIndicatorAnimation()
        {
            if (_exportAnimating || ExportIndicatorBounce is null)
                return;
            _exportAnimating = true;
            _exportBounceAnimation = new DoubleAnimation
            {
                From = 0,
                To = -4,
                Duration = TimeSpan.FromSeconds(0.5),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            ExportIndicatorBounce.BeginAnimation(TranslateTransform.YProperty, _exportBounceAnimation);
        }

        /// <summary>
        /// Останавливает анимацию индикатора выгрузки .dt/.cf (по завершении операции).
        /// </summary>
        private void StopExportIndicatorAnimation()
        {
            if (!_exportAnimating)
                return;
            _exportAnimating = false;
            ExportIndicatorBounce?.BeginAnimation(TranslateTransform.YProperty, null);
            if (ExportIndicatorBounce is not null)
                ExportIndicatorBounce.Y = 0;
        }

        /// <summary>
        /// Восстанавливает сохранённые размер, позицию и состояние окна приложения.
        /// </summary>
        private void ApplySavedWindowLayout()
        {
            var width = _viewModel.SavedWindowWidth;
            var height = _viewModel.SavedWindowHeight;

            // Если запоминание окна отключено — не восстанавливаем положение и размер.
            if (!_viewModel.RememberWindowLayout)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }

            if (width > 0 && height > 0)
            {
                var left = _viewModel.SavedWindowLeft;
                var top = _viewModel.SavedWindowTop;
                if (left == 0 && top == 0)
                {
                    // Если позиция не сохранена — центрируем окно.
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
                else
                {
                    // Позиция восстановлена — отключаем авторасположение по центру,
                    // иначе WPF переопределит Left/Top при показе окна (в XAML задан CenterScreen).
                    WindowStartupLocation = WindowStartupLocation.Manual;

                    // Определяем монитор, на котором окно было закрыто, по сохранённой позиции.
                    // Это возвращает окно на тот же экран (в т.ч. при нескольких мониторах).
                    var area = SystemParameters.WorkArea;
                    try
                    {
                        var screen = Forms.Screen.FromPoint(
                            new Drawing.Point((int)Math.Round(left), (int)Math.Round(top)));
                        if (screen != null)
                        {
                            var wa = screen.WorkingArea;
                            area = new System.Windows.Rect(wa.Left, wa.Top, wa.Width, wa.Height);
                        }
                    }
                    catch
                    {
                        // Если экран недоступен — используем рабочую область основного монитора.
                        area = SystemParameters.WorkArea;
                    }

                    // Ограничиваем позицию, чтобы окно оставалось видимым на выбранном мониторе.
                    var safeLeft = Math.Max(area.Left, Math.Min(left, area.Right - Math.Min(width, area.Width)));
                    var safeTop = Math.Max(area.Top, Math.Min(top, area.Bottom - Math.Min(height, area.Height)));
                    Left = safeLeft;
                    Top = safeTop;
                }

                Width = width;
                Height = height;
            }

            // Восстанавливаем развёрнутое состояние окна.
            if (Enum.TryParse<WindowState>(_viewModel.SavedWindowState, out var state) &&
                state != WindowState.Minimized)
            {
                WindowState = state;
            }
        }

        /// <summary>
        /// Сохраняет размер, позицию и состояние окна приложения при закрытии.
        /// При включённой опции «Закрывать в трей» скрывает окно вместо выхода.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Защита от NRE при аварийном закрытии, если окно не было полностью
            // сконструировано (например, сбой в конструкторе): состояние сохранять
            // нечем, просто передаём управление базовой реализации.
            if (_viewModel == null)
            {
                base.OnClosing(e);
                return;
            }

            // Гарантированно сохраняем все настройки (включая компактный режим) при закрытии,
            // даже если переключатель не был задействован через сеттер.
            _viewModel.SaveSettings();

            if (!_viewModel.RememberWindowLayout)
            {
                // Если запоминание окна отключено — сбрасываем сохранённый макет,
                // чтобы при следующем запуске окно не открывалось в старом месте/размере.
                _viewModel.SaveWindowLayout(0, 0, 0, 0, string.Empty);
            }
            else if (WindowState == WindowState.Normal)
            {
                // Сохраняем только в обычном состоянии, чтобы не сохранить развёрнутое окно как размер по умолчанию.
                // Берём фактический размер (ActualWidth/ActualHeight): Width/Height равны NaN,
                // пока размер окна не задан явно (например, на первом запуске нового профиля),
                // а NaN/бесконечность не сериализуются в JSON и роняли сохранение настроек.
                SaveValidatedWindowLayout(ActualWidth, ActualHeight, Left, Top, WindowState.ToString());
            }
            else if (WindowState == WindowState.Maximized)
            {
                SaveValidatedWindowLayout(RestoreBounds.Width, RestoreBounds.Height, RestoreBounds.Left, RestoreBounds.Top, WindowState.ToString());
            }

            if (!_forceClose && _viewModel.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                if (_trayIcon != null)
                    _trayIcon.Visible = true;
                return;
            }

            // Останавливаем автоматическую синхронизацию при закрытии окна.
            _viewModel.StopAutoSync();
            DisposeTrayIcon();

            // Отписываемся от события смены языка, чтобы не держать ссылку на окно
            // (избегаем утечки памяти после полного закрытия приложения).
            LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
            // Отписываем ViewModel от события смены языка, чтобы не осталось дублирующей
            // подписки (VM живёт весь срок приложения, но отписка защищает от утечек).
            _viewModel.UnsubscribeLanguageChanged();

            base.OnClosing(e);
        }

        /// <summary>
        /// Сохраняет геометрию окна, отбрасывая невалидные значения.
        /// Width/Height (и RestoreBounds на первом запуске профиля) могут быть NaN,
        /// нулём или бесконечностью — такие числа не сериализуются в JSON и роняли бы
        /// сохранение настроек. В этом случае раскладку не перезаписываем, оставляя
        /// прежние значения (прочие настройки сохраняются отдельно через SaveSettings).
        /// </summary>
        private void SaveValidatedWindowLayout(double width, double height, double left, double top, string state)
        {
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ||
                double.IsNaN(height) || double.IsInfinity(height) || height <= 0 ||
                double.IsNaN(left) || double.IsInfinity(left) ||
                double.IsNaN(top) || double.IsInfinity(top))
            {
                return;
            }
            _viewModel.SaveWindowLayout(width, height, left, top, state);
        }

        /// <summary>
        /// Обработчик кнопки «Выход» в правой панели.
        /// Всегда полностью завершает работу приложения, игнорируя настройку
        /// «Закрывать в трей» (в отличие от обычного закрытия окна).
        /// </summary>
        private void OnExitApplicationClick(object sender, RoutedEventArgs e)
        {
            _forceClose = true;
            Close();
        }

        private void OnToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ToggleTheme();
            UpdateThemeButton();
        }

        /// <summary>Переключатель компактного режима на верхней панели: применяет сразу и сохраняет.</summary>
        private void OnCompactMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (CompactModeButton is null || _viewModel is null)
                return;
            _viewModel.ApplyCompactMode(CompactModeButton.IsChecked == true);
        }

        /// <summary>
        /// Реакция на изменение компактного режима в модели: пересчитывает выравнивание
        /// колонок заголовка списка баз (см. <see cref="AlignHeaderToData"/>), т.к. компактный
        /// режим масштабирует отступы/шрифты и меняет положение данных относительно заголовков.
        /// Вызов откладывается, чтобы выполняться после применения раскладки компактного режима.
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CompactMode))
            {
                QueueHeaderAlign();
            }
            else if (e.PropertyName == nameof(MainViewModel.SearchText))
            {
                // Изменение текста поиска/очистка через крестик приводит к пересборке дерева
                // (плоский список <-> группы), при которой меняется глубина первой видимой
                // базы. Чтобы компенсатор заголовка не оставался в значении от предыдущего
                // состояния, ставим выравнивание в очередь; окончательную коррекцию выполняет
                // пересчёт по мере материализации новых строк (issue #214).
                QueueHeaderAlign();
            }
            else if (e.PropertyName == nameof(MainViewModel.ColumnOrderKeys))
            {
                // Пользователь поменял порядок колонок в настройках: пересобираем
                // заголовок и все уже созданные строки баз.
                Dispatcher.BeginInvoke(new Action(ApplyColumnOrder), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Выравнивает колонки заголовка по фактическому положению колонки «Название»
        /// первой видимой базы в списке. Это необходимо, потому что при группировке
        /// базы смещаются вправо отступами вложенности дерева, и фиксированный сдвиг
        /// заголовка (рассчитанный для баз верхнего уровня) перестаёт совпадать с данными.
        /// </summary>
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            AttachTreeScrollHandler();
            // Внешний общий ScrollViewer и вынесенная вертикальная полоса (issue #309):
            // подписки на изменение вьюпорта/значения и отступ полосы под заголовком.
            AttachListScrollHandler();
            if (MainTree is not null)
            {
                MainTree.Loaded += (_, __) => AttachTreeScrollHandler();
                // Изменение области списка (например, появление/исчезновение полосы прокрутки
                // или смещение после клика в поле поиска) сдвигает колонки строк относительно
                // заголовка — пересчитываем выравнивание (issue #214).
                MainTree.SizeChanged += (_, _) => QueueHeaderAlign();
            }

            AlignHeaderToData();
            // Пересчёт минимальной ширины списка выполняется отдельно от выравнивания:
            // AlignHeaderToData выходит раньше, если первая строка дерева ещё не
            // материализована, а полоса обязана соответствовать сохранённым колонкам
            // уже с первой раскладки (issue #309).
            UpdateTreeMinWidth();
            // Повторное выравнивание после завершения первичной компоновки: стабилизирующий
            // цикл на ApplicationIdle добирает строки, которые виртуализация создаёт уже после
            // этого события (см. QueueHeaderAlign).
            QueueHeaderAlign();

            // Применяем сохранённый пользователем порядок колонок списка баз.
            Dispatcher.BeginInvoke(new Action(ApplyColumnOrder), System.Windows.Threading.DispatcherPriority.Loaded);

            // Применяем сохранённый компактный режим при старте. Делаем это здесь, на
            // событии Loaded, когда визуальное дерево окна уже построено (ApplyCompact
            // обходит его через VisualTreeHelper; до показа дерево пустое и масштабирование
            // не срабатывает). После применения пересчитываем выравнивание колонок заголовка.
            if (_viewModel.CompactMode)
            {
                ThemeManager.ApplyCompact(true);
                QueueHeaderAlign();
            }

            // Финальная стабилизация после полной материализации строк дерева: ApplicationIdle
            // (и повторные проходы стабилизирующего цикла) гарантируют, что строки уже
            // реализованы и замер выравнивания корректен (тот же приём, что в
            // RevealAndSelectAfterRebuild), а не зафиксирован в промежуточном значении (issue #214).
            QueueHeaderAlign();

            // Запускаем автоматическую синхронизацию с файлом ibases.v8i.
            _viewModel.StartAutoSync();

            // Монитор запущенных баз (индикатор «зелёная точка»): первый опрос
            // сразу, далее раз в 10 секунд, пока окно открыто.
            _viewModel.StartRunningBasesMonitor();
        }

        /// <summary>
        /// Пересчитывает выравнивание заголовка при переключении режима группировки,
        /// когда дерево перестраивается и меняется глубина вложенности баз.
        /// </summary>
        private void OnGroupByToggle_Click(object sender, RoutedEventArgs e)
        {
            QueueHeaderAlign();
        }

        /// <summary>
        /// Верхняя кнопка «теги»: помимо переключения панели быстрого отбора тегов
        /// (привязка ShowTagFilterPanel) синхронно управляет и тегами в списке баз.
        /// При выключении запоминает текущее состояние нижней кнопки тегов, а при
        /// повторном включении восстанавливает его (не включает нижнюю кнопку принудительно).
        /// </summary>
        private void OnTopTagsToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle || DataContext is not MainViewModel vm)
                return;

            if (toggle.IsChecked == true)
            {
                // Включение: возвращаем нижней кнопке состояние, которое было до выключения
                // верхней кнопкой (либо оставляем текущее, если ранее не выключали).
                if (_savedTagsStateBeforeTopOff is bool saved)
                    vm.ShowTags = saved;
                _savedTagsStateBeforeTopOff = null;
            }
            else
            {
                // Выключение: запоминаем состояние нижней кнопки и выключаем теги в списке.
                _savedTagsStateBeforeTopOff = vm.ShowTags;
                vm.ShowTags = false;
            }
        }

        /// <summary>
        /// Открывает выпадающее меню выбора типа клиента при нажатии на стрелку
        /// кнопки запуска 1С:Предприятие.
        /// </summary>
        private void OnLaunchSplitButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.ContextMenu is null)
                return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

            // Открываем меню отложенно, чтобы клик по кнопке не закрыл его сразу.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                button.ContextMenu.IsOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnInfobaseTree_PreviewMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Двойной клик по группе сворачивает/разворачивает её в зависимости от текущего состояния.
            // Двойной клик по ячейке «Версия платформы» — выбор версии без полного окна свойств.
            // Двойной клик по базе по-прежнему запускает 1С.
            var source = e.OriginalSource as DependencyObject;

            if (source is FrameworkElement fe
                && string.Equals(fe.Tag as string, "PlatformVersion", StringComparison.Ordinal)
                && fe.DataContext is Infobase versionIb)
            {
                OpenPlatformVersionPicker(versionIb);
                e.Handled = true;
                return;
            }

            // Клик по дочернему Run/тексту внутри TextBlock с Tag
            var tagged = source is null ? null : FindAncestorWithTag(source, "PlatformVersion");
            if (tagged?.DataContext is Infobase versionIb2)
            {
                OpenPlatformVersionPicker(versionIb2);
                e.Handled = true;
                return;
            }

            // Двойной клик по пустой области колонки «Версия платформы» (issue #250):
            // выбор версии должен срабатывать по всей ширине колонки, а не только по надписи.
            if (source is not null)
            {
                var rowGrid = FindAncestorByName(source, "InfobaseRowGrid");
                if (rowGrid is System.Windows.Controls.Grid ibGrid && ibGrid.DataContext is Infobase rowIb)
                {
                    // Колонку платформы определяем динамически по элементу с Tag="PlatformVersion":
                    // ReorderGridColumns переупорядочивает колонки по настройкам и смещает детей
                    // через Grid.SetColumn, поэтому фиксированный индекс (старый баг) не совпадал
                    // с фактическим положением колонки на экране и двойной клик не срабатывал.
                    var platformCol = FindDescendantWithTag(ibGrid, "PlatformVersion")?
                        .GetValue(System.Windows.Controls.Grid.ColumnProperty);
                    if (platformCol is int pc)
                    {
                        var pos = e.GetPosition(ibGrid);
                        if (GetColumnIndexAt(ibGrid, pos.X) == pc)
                        {
                            OpenPlatformVersionPicker(rowIb);
                            e.Handled = true;
                            return;
                        }
                    }
                }
            }

            // Issue #355: двойной клик по колонкам «Конфигурация»/«№ релиза» открывает
            // свойства базы сразу на вкладке «Платформа» (без запуска 1С).
            if (TryOpenPropertiesFromConfigurationColumn(source, e))
            {
                e.Handled = true;
                return;
            }

            var treeViewItem = source is null ? null : FindAncestor<TreeViewItem>(source);
            if (treeViewItem?.DataContext is GroupNodeViewModel groupNode && groupNode.Group is not null)
            {
                _viewModel.ToggleGroupExpandedCommand.Execute(groupNode);
                return;
            }

            // Двойной клик по базе выполняет настроенное действие (функция №28 StartManager):
            // «1С:Предприятие», «Конфигуратор» или «Ничего». Индивидуальное значение ИБ
            // переопределяет глобальную настройку (см. MainViewModel.ResolveDoubleClickAction).
            // Та же логика используется клавишей Enter (issue #328).
            ActivateInfobaseByDoubleClickAction(_viewModel.SelectedInfobase);
        }

        /// <summary>
        /// Выполняет «двойной клик» по базе (issue #328): действие по настройке
        /// (функция №28 StartManager) — «1С:Предприятие», «Конфигуратор» или
        /// «Ничего». Общая точка для двойного клика мышью и клавиши Enter.
        /// </summary>
        private void ActivateInfobaseByDoubleClickAction(Infobase? infobase)
        {
            if (infobase is null)
                return;

            // Отбор «Только запущенные» (issue #339): вместо глобальной настройки
            // двойной клик/Enter активируют окно уже запущенной базы.
            if (_viewModel.IsListModeRunning && infobase.IsRunning)
            {
                _viewModel.ActivateRunningInfobase(infobase);
                return;
            }

            var dblAction = _viewModel.ResolveDoubleClickAction(infobase);
            if (dblAction == Configuration_Management.Models.DoubleClickAction.None)
                return;
            if (dblAction == Configuration_Management.Models.DoubleClickAction.Configurator
                && _viewModel.LaunchConfiguratorCommand.CanExecute(null))
            {
                _viewModel.LaunchConfiguratorCommand.Execute(null);
            }
            else if (_viewModel.LaunchEnterpriseCommand.CanExecute(null))
            {
                _viewModel.LaunchEnterpriseCommand.Execute(null);
            }
        }

        /// <summary>
        /// Issue #355: распознаёт двойной клик по колонкам «Конфигурация»/«№ релиза»
        /// строки базы и открывает окно свойств сразу на вкладке «Платформа».
        /// Возвращает <c>true</c>, если клик попал в одну из конфигурационных колонок.
        /// </summary>
        private bool TryOpenPropertiesFromConfigurationColumn(DependencyObject? source, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (source is null)
                return false;

            // 1) Клик по ячейке с Tag="Configuration"/"ConfigurationVersion" (или по её потомку).
            foreach (var tag in new[] { "Configuration", "ConfigurationVersion" })
            {
                if (FindAncestorWithTag(source, tag)?.DataContext is Infobase tagged)
                {
                    OpenPropertiesOnPlatformTab(tagged);
                    return true;
                }
            }

            // 2) Клик по пустой области колонки (issue #250-подход): колонку определяем
            //    по позиции курсора и сравниваем с колонкой элемента-эталона с Tag.
            //    ReorderGridColumns смещает детей через Grid.SetColumn, поэтому
            //    фиксированные индексы не совпадают с фактическим положением на экране.
            var rowGrid = FindAncestorByName(source, "InfobaseRowGrid");
            if (rowGrid is System.Windows.Controls.Grid ibGrid && ibGrid.DataContext is Infobase rowIb)
            {
                var pos = e.GetPosition(ibGrid);
                var col = GetColumnIndexAt(ibGrid, pos.X);
                foreach (var tag in new[] { "Configuration", "ConfigurationVersion" })
                {
                    var reference = FindDescendantWithTag(ibGrid, tag);
                    if (reference?.GetValue(System.Windows.Controls.Grid.ColumnProperty) is int c && c == col)
                    {
                        OpenPropertiesOnPlatformTab(rowIb);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Issue #355: открывает окно свойств базы сразу на вкладке «Платформа».
        /// Общая точка для двойного клика по колонкам «Конфигурация»/«№ релиза».
        /// </summary>
        private void OpenPropertiesOnPlatformTab(Infobase infobase)
        {
            _viewModel.SelectedInfobase = infobase;
            _viewModel.OpenPropertiesOnPlatformTab(infobase);
        }

        private static FrameworkElement? FindAncestorWithTag(DependencyObject? current, string tag)
        {
            while (current is not null)
            {
                if (current is FrameworkElement fe && string.Equals(fe.Tag as string, tag, StringComparison.Ordinal))
                    return fe;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>Индекс колонки «Версия платформы» в сетке строки базы <c>InfobaseRowGrid</c>.</summary>
        private const int PlatformVersionColumnIndex = 5;

        /// <summary>Поднимается по визуальному дереву и возвращает элемент с заданным именем.</summary>
        private static FrameworkElement? FindAncestorByName(DependencyObject? current, string name)
        {
            while (current is not null)
            {
                if (current is FrameworkElement fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
                    return fe;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>Ищет первого потомка с заданным тегом в визуальном поддереве (в глубину).</summary>
        private static FrameworkElement? FindDescendantWithTag(DependencyObject root, string tag)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && string.Equals(fe.Tag as string, tag, StringComparison.Ordinal))
                    return fe;
                var found = FindDescendantWithTag(child, tag);
                if (found is not null)
                    return found;
            }
            return null;
        }

        /// <summary>Возвращает индекс колонки сетки, в которую попадает координата <paramref name="x"/>.</summary>
        private static int GetColumnIndexAt(System.Windows.Controls.Grid grid, double x)
        {
            double offset = 0;
            for (var i = 0; i < grid.ColumnDefinitions.Count; i++)
            {
                var w = grid.ColumnDefinitions[i].ActualWidth;
                if (x < offset + w)
                    return i;
                offset += w;
            }
            return grid.ColumnDefinitions.Count - 1;
        }

        private void OpenPlatformVersionPicker(Infobase ib)
        {
            _viewModel.SelectedInfobase = ib;
            var dialog = new PlatformVersionPickerWindow(_viewModel.InstalledPlatformVersions, ib.PlatformVersion)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
                return;

            var selected = dialog.Result?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(selected))
                return;

            // Разбираем выбранный вариант: суффикс разрядности «(32)/(64)» выносим
            // в отдельное поле Architecture, а в PlatformVersion сохраняем чистую версию.
            PlatformVersionService.ParseVariant(selected, out var cleanVersion, out var arch);
            var newVersion = string.IsNullOrWhiteSpace(cleanVersion) ? selected : cleanVersion;
            var versionChanged = !string.Equals(ib.PlatformVersion, newVersion, StringComparison.Ordinal);
            // Раньше здесь был ранний return при совпадении версии — из-за этого
            // нельзя было сменить разрядность (х86 → х64) одной и той же версии (issue #146).
            if (versionChanged)
                ib.PlatformVersion = newVersion;
            // Разрядность записываем, только если она задана в выбранном варианте ЯВНО суффиксом
            // «(32)/(64)». ParseVariant по умолчанию возвращает «32» даже без суффикса, поэтому
            // выбор папки/частичной версии («8.3», «8.3.27») не должен подставлять x86 — разрешение
            // разрядности остаётся за лаунчером (issue #251).
            if ((arch == "32" || arch == "64") && PlatformVersionService.HasExplicitArchitecture(selected))
                ib.Architecture = arch;
            if (versionChanged || arch == "32" || arch == "64")
                _viewModel.PersistInfobasesAfterInlineEdit();
        }

        /// <summary>
        /// Выделяет базу или группу под курсором при правом клике в дереве,
        /// чтобы команды контекстного меню применялись именно к этому элементу.
        /// Мультивыделение (0.3.9.90) правый клик НЕ меняет: набор «для выделенных»
        /// должен дожить до открытия меню нетронутым, иначе пакетный блок «Для
        /// выделенных (N)…» теряет базы (issue #313).
        /// <para>
        /// Явно различаются два состояния (issue #313): «текущая строка» (курсор
        /// без Ctrl) и «строка из мультивыделения». Строка входит в набор ТОЛЬКО
        /// после Ctrl/Shift-клика по ней; правый клик строит набор по фактически
        /// выделенным строкам и не добавляет «бывшую текущую», которая в набор
        /// не входила. Поэтому здесь нет ни ToggleBatchSelection, ни
        /// SelectRange, ни ClearBatchSelection — только основное выделение
        /// под курсором (IsSelected/SelectedInfobase).
        /// </para>
        /// </summary>
        private void OnInfobaseTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var treeView = sender as TreeView;
            if (treeView is null)
            {
                return;
            }

            // Если клик попал по строке базы или группы, выделяем её. Модификаторы
            // Ctrl/Shift намеренно игнорируются: они меняют мультивыделение только
            // при ЛЕВОМ клике (OnInfobaseTree_PreviewMouseLeftButtonDown), а правый
            // клик всегда лишь ставит основное выделение под курсором.
            var source = e.OriginalSource as DependencyObject;
            var treeViewItem = source is null ? null : FindAncestor<TreeViewItem>(source);
            switch (treeViewItem?.DataContext)
            {
                // Закреплённая база в узле «Закреплённые» приходит обёрткой
                // PinnedInfobaseItem (уникальные данные строки, issue #314) —
                // разворачиваем до реальной базы для команд правой панели и меню.
                case Infobase:
                case PinnedInfobaseItem:
                    treeViewItem.IsSelected = true;
                    _viewModel.SelectedInfobase = UnwrapInfobase(treeViewItem.DataContext);
                    break;
                case GroupNodeViewModel groupNode when groupNode.Group is not null:
                    treeViewItem.IsSelected = true;
                    _viewModel.SelectedInfobase = null;
                    _viewModel.SelectedGroupNode = groupNode;
                    break;
            }
        }

        /// <summary>
        /// Выделяет базу или группу под курсором при левом клике в дереве.
        /// Сами устанавливаем выбор и помечаем событие обработанным, чтобы
        /// собственная логика TreeView не сбросила выделение. Клики по
        /// интерактивным элементам строки (кнопки, поле ввода) не
        /// перехватываются, чтобы они продолжали работать.
        /// </summary>
        private void OnInfobaseTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // issue #340 (новая стратегия): клик, которым закрыли контекстное меню, после
            // освобождения захвата попапа WPF повторно доставляет в дерево «хвост» того же
            // MouseDown. Это ШТАТНЫЙ путь выбора строки (клик по ЖИВОМУ контейнеру под
            // Recycling) — гасить его НЕЛЬЗЯ: в пяти прежних попытках (0.3.9.277/291/299/
            // 300/302) гашение оставляло только нестабильное «применение в Closed», и
            // выделение пропадало «через мгновение». Здесь повторная доставка распознаётся
            // снимком и обрабатывается ШТАТНО ниже (обычный клик — ClearBatchSelection +
            // ApplySelection; Ctrl/Shift — ToggleBatchSelection/SelectRange), а флаг
            // _menuClosePendingApply снимается — fallback больше не нужен. Снимок живёт до
            // первого события мыши (MouseUp/следующий клик вне окна), поэтому двойной клик
            // для запуска базы не блокируется.
            var clickPos = e.GetPosition(MainTree);
            // Снимок присутствовал в момент клика (F1): стабилизация выполняется не только
            // при совпавшей повторной доставке (путь A), но и когда снимок был сброшен до
            // доставки (путь C — MouseUp пришёл раньше) — это тот же клик, закрывший меню.
            var menuCloseSnapshotPresent = _menuCloseClickSnapshot is not null;
            var isMenuCloseRedelivery = false;
            if (_menuCloseClickSnapshot is { } menuCloseClick)
            {
                // Время — едиными часами Environment.TickCount (той же шкалой записан
                // снимок в TryApplyTreeClickAfterMenuClosed, issue #340).
                isMenuCloseRedelivery = BatchSelectionHelper.IsSameClick(
                    menuCloseClick, "Left", Environment.TickCount, clickPos.X, clickPos.Y);
                _menuCloseClickSnapshot = null;
                if (isMenuCloseRedelivery)
                {
                    // Повторная доставка того же клика: выбор применит штатная ветка ниже,
                    // fallback отменяется.
                    _menuClosePendingApply = false;
                    _menuCloseTarget = null;
                    _menuCloseTargetIsPinnedSection = false;
                }
            }

            // B-1 (0.3.9.311): БЕЗУСЛОВНАЯ запись MouseDown по дереву — координаты, цель,
            // модификаторы и состояние снимка фиксируются при ЛЮБОМ клике. Прежняя запись
            // писалась только при наличии снимка (путь A/C) и не оставляла следов в
            // «путях без снимка» (обычный клик вне окна стабилизации, Ctrl/Shift-клик) —
            // из-за этого присланный trace.json не содержал ни одного события клика.
            var clickSource = e.OriginalSource as DependencyObject;
            var clickRow = clickSource is null ? null : FindAncestor<TreeViewItem>(clickSource);
            var clickMods = Keyboard.Modifiers;
            MenuCloseTrace.Log(BatchSelectionHelper.BuildClickTraceLine(
                "MouseDown",
                clickPos.X, clickPos.Y,
                BatchSelectionHelper.FormatModifiers(
                    (clickMods & ModifierKeys.Control) == ModifierKeys.Control,
                    (clickMods & ModifierKeys.Shift) == ModifierKeys.Shift,
                    (clickMods & ModifierKeys.Alt) == ModifierKeys.Alt),
                BatchSelectionHelper.Unwrap(clickRow?.DataContext)?.Id,
                menuCloseSnapshotPresent,
                isMenuCloseRedelivery,
                BatchSelectionHelper.IsPinnedSection(clickRow?.DataContext)));

            // Payload DnD фиксируем здесь (не в MouseMove): иначе при сдвиге курсора
            // на дочернюю базу TreeViewItem под курсором меняется и «уезжает» не группа, а базы.
            CaptureDragStart(e);

            var treeView = sender as TreeView;
            if (treeView is null)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            if (source is null)
            {
                return;
            }

            // Ctrl+щелчок по кнопке разворота («плюсику») строки ГРУППЫ — то же,
            // что и Ctrl+щелчок по названию: сворачивается/разворачивается ВСЯ
            // ветка, текущая строка и выделение не меняются (issue #341). Кнопка
            // разворота в шаблоне строки — Button с именем «Expander»
            // (MainWindow.xaml): без перехвата ранняя проверка интерактивных
            // элементов ниже вышла бы раньше, а штатная команда
            // ToggleGroupExpandedCommand переключила бы только сам узел.
            // e.Handled гасит и Click кнопки, и остальные ветки обработчика —
            // команда ветки применяется ровно один раз. Обычный (без Ctrl) клик
            // по плюсику работает как раньше.
            if (FindAncestor<Button>(source) is { Name: "Expander" } &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift &&
                FindAncestor<TreeViewItem>(source)?.DataContext is GroupNodeViewModel expanderGroup &&
                expanderGroup.Group is not null)
            {
                _draggedData = null;
                _viewModel.ToggleGroupBranchCommand.Execute(expanderGroup);
                e.Handled = true;
                return;
            }

            // Если клик пришёлся по интерактивному элементу (кнопка, поле ввода,
            // редактируемый список), не вмешиваемся и не начинаем drag. Стрелка
            // списка — ToggleButton из шаблона ComboBox (не Button), поэтому
            // отдельная проверка на сам ComboBox: иначе клик по списку сбрасывал
            // бы фокус на строку дерева и закрывал поле ввода тега (issue #283).
            if (FindAncestor<Button>(source) is not null ||
                FindAncestor<TextBox>(source) is not null ||
                FindAncestor<ComboBox>(source) is not null)
            {
                _draggedData = null;
                return;
            }

            var treeViewItem = FindAncestor<TreeViewItem>(source);
            if (treeViewItem is null)
            {
                return;
            }

            switch (treeViewItem.DataContext)
            {
                // Закреплённая база в узле «Закреплённые» приходит обёрткой
                // PinnedInfobaseItem (уникальные данные строки, issue #314) —
                // разворачиваем до реальной базы, дальше логика не меняется.
                case Infobase:
                case PinnedInfobaseItem:
                {
                    var infobase = UnwrapInfobase(treeViewItem.DataContext);
                    if (infobase is null)
                        return;

                    // Ctrl+щелчок — точечное переключение мультивыделения (0.3.9.90):
                    // строка помечается вторичным фоном «для выделенных» и попадает
                    // в набор пакетных операций. Закладка (номер Alt+N) ставится
                    // горячей клавишей Ctrl+Shift+P или звёздочкой в строке.
                    // Секция строки (закреплённая vs обычная) передаётся явно: клик
                    // в другой секции не смешивает наборы (issue #326).
                    var isPinnedSection = Services.BatchSelectionHelper.IsPinnedSection(treeViewItem.DataContext);
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                        && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
                    {
                        _draggedData = null;
                        // Правило issue #313: первый Ctrl-клик по строке, отличной от
                        // «текущей», добавляет в набор и «текущую» — но только если она
                        // лежит в той же секции (#326). Секция текущей строки берётся из
                        // данных её контейнера (SelectedItem дерева): закреплённая база
                        // приходит обёрткой PinnedInfobaseItem.
                        _viewModel.ToggleBatchSelection(infobase, "Ctrl", isPinnedSection: isPinnedSection,
                            currentRowSectionIsPinned:
                                Services.BatchSelectionHelper.IsPinnedSection(treeView.SelectedItem));
                        e.Handled = true;
                        return;
                    }
                    // Shift+щелчок — диапазон от «якоря» (последний клик без Ctrl)
                    // до текущей строки по видимому порядку ТОЛЬКО в пределах той же
                    // секции (0.3.9.90, #326): закреплённые строки в диапазон обычного
                    // списка не попадают и наоборот.
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    {
                        _draggedData = null;
                        _viewModel.SelectRange(
                            _viewModel.SelectedInfobase,
                            infobase,
                            VisibleInfobasesInOrder(isPinnedSection),
                            isPinnedSection);
                        e.Handled = true;
                        return;
                    }
                    // Обычный клик — единственный выбор: снимаем мультивыделение,
                    // чтобы пакетный блок меню не «висел» после перехода к одной базе.
                    _draggedData = infobase;
                    _viewModel.ClearBatchSelection();
                    ApplySelection(treeViewItem, infobase);

                    // issue #340 (0.3.9.314): запоминаем «последний обычный клик по строке
                    // дерева» — единые часы Environment.TickCount, целевая база и секция.
                    // Второй реальный trace.json показал: клик по строке может прийти в
                    // дерево ДО закрытия контекстного меню (MouseDown → MenuClosed,
                    // snapshot=False, redelivery=False) — снимок по guard-цепочке
                    // TryApplyTreeClickAfterMenuClosed не записывается (кнопка отпущена
                    // к моменту OnContextMenuClosed), повторной доставки нет, и ни один
                    // штатный путь стабилизацию не запускает. Цель этого клика используется
                    // в OnContextMenuClosed (ShouldStabilizeForClickPrecedingMenuClose):
                    // если клик был ≤500 мс до закрытия меню — стабилизация по нему.
                    _lastPlainTreeClick = (Environment.TickCount, infobase, isPinnedSection);
                    MenuCloseTrace.Log($"LastPlainClick: target={infobase.Id}, " +
                                       $"tick={_lastPlainTreeClick.Value.Tick}, pinned={isPinnedSection}");

                    // issue #340 (F1): клик, которым закрыли контекстное меню, после штатного
                    // применения выбора дополнительно «стабилизируется» — подписка на
                    // LayoutUpdated чинит последствия переработки контейнеров
                    // (VirtualizingStackPanel Recycling), из-за которых IsSelected «уезжал»
                    // и выделение пропадало «через мгновение». Стабилизация вызывается ВО ВСЕХ
                    // путях: и при совпавшей повторной доставке (путь A), и когда снимок был
                    // сброшен до доставки (путь C) — признак один: снимок присутствовал
                    // в момент начала этого клика.
                    //
                    // 0.3.9.308: признак РАСШИРЕН предикатом ShouldStabilizeAfterMenuClose —
                    // стабилизация запускается также для ЛЮБОГО обычного клика без модификаторов
                    // в окне ~1,5 с после закрытия контекстного меню дерева (прежний признак
                    // «снимок присутствовал» зависел от успешной записи снимка в длинной
                    // guard-цепочке TryApplyTreeClickAfterMenuClosed и не срабатывал, когда
                    // меню закрылось по ESC или кликом мимо строки). Здесь мы находимся в
                    // ветке обычного клика (без Ctrl/Shift) — предикату передаётся true.
                    if (BatchSelectionHelper.ShouldStabilizeAfterMenuClose(
                            snapshotPresent: menuCloseSnapshotPresent,
                            isPlainLeftClickWithoutModifiers: true,
                            lastMenuCloseTick: _lastMenuCloseTick,
                            nowTick: Environment.TickCount,
                            windowMs: BatchSelectionHelper.MenuCloseStabilizeWindowMs))
                    {
                        // B-5 (0.3.9.311): причина стабилизации фиксируется в стартовой
                        // записи — снимок клика присутствовал (путь A/C) или меню закрылось
                        // недавно без снимка (ESC/клик мимо строки).
                        EnsureSelectionStable(infobase, isPinnedSection,
                            reason: menuCloseSnapshotPresent ? "snapshot" : "recentMenuClose");
                    }
                    break;
                }
                case GroupNodeViewModel groupNode when groupNode.Group is not null:
                    // Ctrl+щелчок по группе — развернуть/свернуть ветку (группа + все
                    // подгруппы), НЕ меняя текущую строку и выделение (issue #341).
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                        (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
                    {
                        _draggedData = null;
                        _viewModel.ToggleGroupBranchCommand.Execute(groupNode);
                        e.Handled = true;
                        return;
                    }
                    _draggedData = groupNode;
                    ApplyGroupSelection(treeViewItem, groupNode);
                    break;
                default:
                    _draggedData = null;
                    // Служебные узлы («Закреплённые», «Без группы») не имеют модели Group,
                    // поэтому в ветку выше (Group != null) не попадают. Если двойной клик
                    // по ним оставить штатному TreeViewItem, он запишет локальное значение
                    // IsExpanded в контейнер, и узел после этого не будет сворачиваться
                    // (тот же дефект, что и для обычных групп в issue #180). Переключаем
                    // развёрнутость на МОДЕЛИ и помечаем клик обработанным. Переключаем через
                    // ToggleGroupExpandedCommand, чтобы состояние сохранялось через
                    // SetGroupCollapsed по внутреннему маркеру узла (NodeKey), а не только
                    // в контейнере TreeViewItem (issue #180).
                    if (treeViewItem.DataContext is GroupNodeViewModel serviceNode && e.ClickCount >= 2)
                    {
                        _viewModel.ToggleGroupExpandedCommand.Execute(serviceNode);
                        e.Handled = true;
                    }
                    return;
            }

            // Переводим клавиатурный фокус на строку дерева (отложенно, после завершения
            // обработки клика), чтобы последующие нажатия стрелок управляли выделением
            // в дереве, а не «прыгали» по кнопкам внутри строки.
            var focusTarget = treeViewItem;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (focusTarget is not null)
                {
                    focusTarget.Focus();
                    Keyboard.Focus(focusTarget);
                }
            }), System.Windows.Threading.DispatcherPriority.Input);

            // Помечаем клик обработанным, чтобы TreeView не сбросил выбранный элемент.
            e.Handled = true;
        }

        /// <summary>
        /// Первое отпускание левой кнопки завершает обработку клика, которым закрыли
        /// контекстное меню дерева (issue #340). Снимок клика сбрасывается: следующий
        /// MouseDown — уже новое действие пользователя и должен обрабатываться штатно
        /// (в т.ч. двойной клик для запуска базы). Метод остаётся подписанным в XAML.
        /// </summary>
        private void OnInfobaseTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // B-1 (0.3.9.311): БЕЗУСЛОВНАЯ запись MouseUp по дереву — те же поля, что
            // у MouseDown (координаты, цель, модификаторы, состояние снимка). Прежняя
            // запись писалась только при сбросе снимка и не оставляла следа для
            // обычных кликов вне окна стабилизации.
            var upPos = e.GetPosition(MainTree);
            var upSource = e.OriginalSource as DependencyObject;
            var upRow = upSource is null ? null : FindAncestor<TreeViewItem>(upSource);
            var upMods = Keyboard.Modifiers;
            MenuCloseTrace.Log(BatchSelectionHelper.BuildClickTraceLine(
                "MouseUp",
                upPos.X, upPos.Y,
                BatchSelectionHelper.FormatModifiers(
                    (upMods & ModifierKeys.Control) == ModifierKeys.Control,
                    (upMods & ModifierKeys.Shift) == ModifierKeys.Shift,
                    (upMods & ModifierKeys.Alt) == ModifierKeys.Alt),
                BatchSelectionHelper.Unwrap(upRow?.DataContext)?.Id,
                _menuCloseClickSnapshot is not null,
                false,
                BatchSelectionHelper.IsPinnedSection(upRow?.DataContext)));

            // issue #340 (0.3.9.316): «последний обычный клик» фиксируется и на отпускании
            // левой кнопки над строкой базы — отпускание даёт СВЕЖУЮ метку активности мыши
            // для восстановления выделения после закрытия контекстного меню
            // (MenuClosedOverRow), даже если MouseDown этого клика дерево не получил
            // (проглочен попапом/частичная доставка «хвоста»). Только обычный клик без
            // Ctrl/Shift — мультивыделение evidence-предиката не касается.
            if (upRow?.DataContext is Infobase or PinnedInfobaseItem &&
                (upMods & (ModifierKeys.Control | ModifierKeys.Shift)) == 0 &&
                BatchSelectionHelper.Unwrap(upRow.DataContext) is { } upBase)
            {
                var upPinned = BatchSelectionHelper.IsPinnedSection(upRow.DataContext);
                _lastPlainTreeClick = (Environment.TickCount, upBase, upPinned);
                MenuCloseTrace.Log($"LastPlainClick (MouseUp): target={upBase.Id}, " +
                                   $"tick={_lastPlainTreeClick.Value.Tick}, pinned={upPinned}");
            }

            // Снимок сбрасывается: следующий MouseDown — уже новое действие пользователя.
            // Флаг _menuClosePendingApply намеренно НЕ трогаем: если повторная доставка
            // MouseDown не пришла, выбор должен применить fallback (ApplyMenuCloseFallback,
            // запланированный на приоритете Input); отпускание кнопки — не признак того,
            // что выбор применён (issue #340, новая стратегия).
            if (_menuCloseClickSnapshot is not null)
            {
                _menuCloseClickSnapshot = null;
                MenuCloseTrace.Log("MouseUp: snapshotCleared=true");
            }
        }

        private void OnEnterpriseMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.ContextMenu is null)
                return;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.IsOpen = true;
        }

        private void OnConfiguratorMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.ContextMenu is null)
                return;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.IsOpen = true;
        }

        private void OnClearCacheMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.ContextMenu is null)
                return;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.IsOpen = true;
        }

        // ======================= Пакетные операции (0.3.9.90) =======================

        /// <summary>Назначить теги всем базам мультивыделения (issue #315).</summary>
        private void OnBatchAssignTag_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || _viewModel.BatchSelectedCount == 0)
                return;
            var dlg = new TagPickWindow(_viewModel.AvailableTags)
            {
                Owner = this
            };
            // Пустой выбор (ничего не отмечено и новый тег не добавлен)
            // трактуется как отмена: назначение не выполняется.
            if (dlg.ShowDialog() != true || dlg.Result.Count == 0)
                return;
            _viewModel.AssignTagsToBatch(dlg.Result);
            _viewModel.ClearBatchSelection();
        }

        /// <summary>Переместить все базы мультивыделения в выбранную группу.</summary>
        private void OnBatchMoveToGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || _viewModel.BatchSelectedCount == 0)
                return;
            var picker = new GroupPickerWindow(
                _viewModel.Groups,
                allowNone: true)
            {
                Owner = this
            };
            if (picker.ShowDialog() != true)
                return;
            _viewModel.MoveBatchToGroup(picker.ResultFullPath);
            _viewModel.ClearBatchSelection();
        }

        /// <summary>Добавить все базы мультивыделения в избранное.</summary>
        private void OnBatchAddFavorites_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || _viewModel.BatchSelectedCount == 0)
                return;
            _viewModel.AddBatchToFavorites();
            _viewModel.ClearBatchSelection();
        }

        /// <summary>Выполнить сценарий резервирования для всех баз мультивыделения.</summary>
        private async void OnBatchRunBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || _viewModel.BatchSelectedCount == 0)
                return;
            await _viewModel.RunBatchBackupAsync();
            _viewModel.ClearBatchSelection();
        }

        /// <summary>Проверить доступность баз мультивыделения.</summary>
        private void OnBatchCheckAvailability_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || _viewModel.BatchSelectedCount == 0)
                return;
            _viewModel.CheckBatchAvailability();
            _viewModel.ClearBatchSelection();
        }

        /// <summary>Удалить все базы мультивыделения (общее окно подтверждения по каждой).</summary>
        private void OnBatchDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || _viewModel.BatchSelectedCount == 0)
                return;
            _viewModel.DeleteBatch();
            _viewModel.ClearBatchSelection();
        }

        /// <summary>
        /// Массовая замена в строках подключения выделенных баз (0.3.9.191, функция 6):
        /// окно получает кандидатов области BatchSelected и колбэки моста MainViewModel;
        /// после закрытия окна выделение снимается (как у остальных пакетных операций).
        /// </summary>
        private void OnBatchConnectionReplace_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null || _viewModel.BatchSelectedCount == 0)
                return;
            var candidates = _viewModel.GetConnectionReplaceCandidates(ConnectionReplaceScope.BatchSelected);
            if (candidates.Count == 0)
                return;
            var vm = new ConnectionReplaceViewModel(
                candidates,
                ConnectionReplaceScope.BatchSelected,
                onApplied: _viewModel.ApplyConnectionReplace,
                onUndone: _viewModel.UndoLastConnectionReplace);
            var win = new ConnectionReplaceWindow(vm) { Owner = this };
            win.ShowDialog();
            _viewModel.ClearBatchSelection();
        }

        /// <summary>
        /// Массовая замена в строках подключения всех видимых баз («Утилиты»): кандидаты
        /// области AllBases строит MainViewModel (скрытые приватные исключены), в окне
        /// пользователь может сменить область на «Выделенные»/«Текущую группу».
        /// </summary>
        private void OnUtilitiesConnectionReplace_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null)
                return;
            var candidates = _viewModel.GetConnectionReplaceCandidates(ConnectionReplaceScope.AllBases);
            if (candidates.Count == 0)
                return;
            var vm = new ConnectionReplaceViewModel(
                candidates,
                ConnectionReplaceScope.AllBases,
                onApplied: _viewModel.ApplyConnectionReplace,
                onUndone: _viewModel.UndoLastConnectionReplace);
            var win = new ConnectionReplaceWindow(vm) { Owner = this };
            win.ShowDialog();
        }

        /// <summary>
        /// Отменить последнюю замену строк подключения («Утилиты»): восстанавливает прежние
        /// настройки через мост MainViewModel (CanUndoConnectionReplace обновляется VM-событием).
        /// </summary>
        private void OnUtilitiesUndoConnectionReplace_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.UndoLastConnectionReplace();
        }


    }
}
#endif
