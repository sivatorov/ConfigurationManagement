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
            if (MainTree is not null)
            {
                MainTree.Loaded += (_, __) => AttachTreeScrollHandler();
                // Изменение области списка (например, появление/исчезновение полосы прокрутки
                // или смещение после клика в поле поиска) сдвигает колонки строк относительно
                // заголовка — пересчитываем выравнивание (issue #214).
                MainTree.SizeChanged += (_, _) => QueueHeaderAlign();
            }

            AlignHeaderToData();
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

            var treeViewItem = source is null ? null : FindAncestor<TreeViewItem>(source);
            if (treeViewItem?.DataContext is GroupNodeViewModel groupNode && groupNode.Group is not null)
            {
                _viewModel.ToggleGroupExpandedCommand.Execute(groupNode);
                return;
            }

            // Двойной клик по базе выполняет настроенное действие (функция №28 StartManager):
            // «1С:Предприятие», «Конфигуратор» или «Ничего». Индивидуальное значение ИБ
            // переопределяет глобальную настройку (см. MainViewModel.ResolveDoubleClickAction).
            var dblAction = _viewModel.ResolveDoubleClickAction(_viewModel.SelectedInfobase);
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
        /// </summary>
        private void OnInfobaseTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var treeView = sender as TreeView;
            if (treeView is null)
            {
                return;
            }

            // Если клик попал по строке базы или группы, выделяем её.
            var source = e.OriginalSource as DependencyObject;
            var treeViewItem = source is null ? null : FindAncestor<TreeViewItem>(source);
            switch (treeViewItem?.DataContext)
            {
                case Infobase infobase:
                    treeViewItem.IsSelected = true;
                    _viewModel.SelectedInfobase = infobase;
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
                case Infobase infobase:
                    // Ctrl+щелчок по строке базы — поставить/снять закладку (номер).
                    // Не конфликтует с множественным выделением: выбор здесь и так
                    // полностью управляется методом ApplySelection.
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        _draggedData = null;
                        _viewModel.ToggleBookmark(infobase);
                        e.Handled = true;
                        return;
                    }
                    _draggedData = infobase;
                    ApplySelection(treeViewItem, infobase);
                    break;
                case GroupNodeViewModel groupNode when groupNode.Group is not null:
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


    }
}
#endif
