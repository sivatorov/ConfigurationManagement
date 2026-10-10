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
        /// Регистрирует настраиваемые горячие клавиши действий (запуск, правка, удаление и т.д.).
        /// </summary>
        private void RegisterLaunchHotkeys()
        {
            // Удаляем ранее зарегистрированные «пользовательские» биндинги (кроме Alt+1…9).
            var toRemove = InputBindings
                .OfType<KeyBinding>()
                .Where(kb => kb.Command is not null &&
                             kb.Modifiers != ModifierKeys.Alt)
                .ToList();
            foreach (var kb in toRemove)
                InputBindings.Remove(kb);

            void Add(string? gesture, ICommand? command)
            {
                if (command is null) return;
                if (!TryParseKeyGesture(gesture, out var key, out var mods)) return;
                try
                {
                    InputBindings.Add(new KeyBinding(command, key, mods));
                }
                catch
                {
                    // Одно неверное значение (например, записанное до правки issue #204
                    // сочетание без модификатора) не должно обрывать регистрацию остальных.
                }
            }

            Add(_viewModel.HotkeyEnterprise, _viewModel.LaunchEnterpriseCommand);
            Add(_viewModel.HotkeyConfigurator, _viewModel.LaunchConfiguratorCommand);
            Add(_viewModel.HotkeyFavorite, _viewModel.ToggleFavoriteCommand);
            Add(_viewModel.HotkeyEdit, _viewModel.EditInfobaseCommand);
            Add(_viewModel.HotkeyDelete, _viewModel.DeleteInfobaseCommand);
            Add(_viewModel.HotkeyClearCache, _viewModel.ClearCacheCommand);
            Add(_viewModel.HotkeyAdd, _viewModel.AddInfobaseCommand);
            Add(_viewModel.HotkeyPin, _viewModel.TogglePinCommand);
            // Переключение вкладок списка баз: Все / Избранное / Недавние / Запущенные.
            Add(_viewModel.HotkeyShowAll, _viewModel.ShowAllCommand);
            Add(_viewModel.HotkeyShowFavorites, _viewModel.ShowFavoritesCommand);
            Add(_viewModel.HotkeyShowRecent, _viewModel.ShowRecentCommand);
            // Отбор «Только запущенные» (issue #339): настраиваемый хоткей.
            Add(_viewModel.HotkeyShowRunning, _viewModel.ShowRunningCommand);

            // Очистка строки поиска и сброс фильтра тегов — настраиваемые хоткеи (issue #160),
            // значения по умолчанию Ctrl+Shift+C / Ctrl+Shift+T задаются в настройках.
            Add(_viewModel.HotkeyClearSearch, _viewModel.ClearSearchCommand);
            Add(_viewModel.HotkeyClearTags, _viewModel.ClearTagFiltersCommand);

            // Переключение подробностей правой панели информации — настраиваемый
            // хоткей (issue #172); значение по умолчанию Ctrl+D задаётся в настройках.
            Add(_viewModel.HotkeyRightPanelDetails, _viewModel.ToggleRightPanelDetailsCommand);

            // «Найти в списке» — переход к базе в общем списке (issue #285);
            // значение по умолчанию Ctrl+T задаётся в настройках.
            Add(_viewModel.HotkeyFindInList, _viewModel.FindInListCommand);

            // Командная палитра (Ctrl+K): быстрый поиск баз и команд интерфейса.
            Add(_viewModel.HotkeyCommandPalette, _viewModel.CommandPaletteCommand);

            // Смена пользователя — настраиваемый хоткей (issue #200);
            // значение по умолчанию не задано.
            Add(_viewModel.HotkeySwitchUser, _viewModel.SwitchUserCommand);

            // Проверка обновлений конфигураций 1С (функции №21/№22): F9 — для выбранной
            // ИБ, ALT+F9 — окно «Актуальные релизы». Сочетания настраиваются в настройках.
            Add(_viewModel.HotkeyCheckUpdate, _viewModel.CheckUpdateCommand);
            Add(_viewModel.HotkeyActualReleases, _viewModel.ShowActualReleasesCommand);
            // Автообновление платформы 1С (функция 9, этап 0.3.9.214): Ctrl+F9.
            Add(_viewModel.HotkeyPlatformUpdate, _viewModel.ShowPlatformUpdateCommand);

            // Сценарии резервирования и «Список выгрузок» (функции №16/№18):
            // Ctrl+Shift+F5 — выполнить сценарий, Ctrl+Shift+F7 — список выгрузок.
            Add(_viewModel.HotkeyRunBackup, _viewModel.RunBackupScenarioCommand);
            Add(_viewModel.HotkeyExportsList, _viewModel.ShowExportsListCommand);
            // «Выполнить скрипт» для выбранной базы (issue #308): F5.
            Add(_viewModel.HotkeyRunScript, _viewModel.RunScriptForSelectedCommand);

            // Блокировка сеансов ИБ (функция №20, Ctrl+Alt+L) и временная блокировка
            // приложения паролем (функция №19). Сочетания настраиваются в настройках.
            Add(_viewModel.HotkeySessionLock, _viewModel.ShowSessionLockCommand);
            Add(_viewModel.HotkeyLockApp, _viewModel.LockAppCommand);

            // Масштаб строк списка (issue #303): Ctrl++ / Ctrl+- / Ctrl+0, как в
            // редакторах. Сочетания настраиваются в «Настройки → Клавиши».
            Add(_viewModel.HotkeyZoomIn, _viewModel.ZoomInCommand);
            Add(_viewModel.HotkeyZoomOut, _viewModel.ZoomOutCommand);
            Add(_viewModel.HotkeyZoomReset, _viewModel.ZoomResetCommand);

            // Администрирование ИБ (Этап 6, функция №29 + консоль серверов):
            // проверка целостности файловой ИБ (chdbfl) и консоль администрирования серверов 1С.
            Add(_viewModel.HotkeyCheckIntegrity, _viewModel.CheckIntegrityCommand);
            Add(_viewModel.HotkeyServerConsole, _viewModel.OpenServerConsoleCommand);
            // Сохранение копии экрана (функция №30 StartManager): сочетание настраивается в настройках.
            Add(_viewModel.ScreenshotHotkey, _viewModel.TakeScreenshotCommand);

            // Ctrl+Shift+Plus / Ctrl+Shift+Minus — развернуть/свернуть все узлы дерева.
            // Регистрируются обе раскладки (основная Oem* и цифровой блок Add/Subtract).
            InputBindings.Add(new KeyBinding(_viewModel.ExpandAllGroupsCommand, Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift));
            InputBindings.Add(new KeyBinding(_viewModel.ExpandAllGroupsCommand, Key.Add, ModifierKeys.Control | ModifierKeys.Shift));
            InputBindings.Add(new KeyBinding(_viewModel.CollapseAllGroupsCommand, Key.OemMinus, ModifierKeys.Control | ModifierKeys.Shift));
            InputBindings.Add(new KeyBinding(_viewModel.CollapseAllGroupsCommand, Key.Subtract, ModifierKeys.Control | ModifierKeys.Shift));

            // Ctrl+Alt+Plus / Ctrl+Alt+Minus — развернуть/свернуть ТОЛЬКО ветку под курсором
            // (issue #341): Ctrl+Plus/Minus заняты масштабом строк (#303), Ctrl+Shift+Plus/Minus —
            // «развернуть/свернуть всё» (#160). Обе раскладки: основная и цифровой блок.
            InputBindings.Add(new KeyBinding(_viewModel.ExpandBranchCommand, Key.OemPlus, ModifierKeys.Control | ModifierKeys.Alt));
            InputBindings.Add(new KeyBinding(_viewModel.ExpandBranchCommand, Key.Add, ModifierKeys.Control | ModifierKeys.Alt));
            InputBindings.Add(new KeyBinding(_viewModel.CollapseBranchCommand, Key.OemMinus, ModifierKeys.Control | ModifierKeys.Alt));
            InputBindings.Add(new KeyBinding(_viewModel.CollapseBranchCommand, Key.Subtract, ModifierKeys.Control | ModifierKeys.Alt));
        }

        /// <summary>
        /// Разбирает жест вида «F3», «Delete», «Ctrl+F2», «Shift+Insert».
        /// </summary>
        internal static bool TryParseKeyGesture(string? text, out Key key, out ModifierKeys modifiers)
        {
            key = Key.None;
            modifiers = ModifierKeys.None;
            if (string.IsNullOrWhiteSpace(text) ||
                string.Equals(text.Trim(), "—", StringComparison.Ordinal) ||
                string.Equals(text.Trim(), "-", StringComparison.Ordinal) ||
                string.Equals(text.Trim(), "Нет", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text.Trim(), "None", StringComparison.OrdinalIgnoreCase))
                return false;

            var parts = text.Trim().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;

            for (var i = 0; i < parts.Length - 1; i++)
            {
                var p = parts[i];
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModifierKeys.Control;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModifierKeys.Shift;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModifierKeys.Alt;
                else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                         p.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModifierKeys.Windows;
                else
                    return false;
            }

            var keyPart = parts[^1];
            // Синонимы
            if (keyPart.Equals("Del", StringComparison.OrdinalIgnoreCase))
                keyPart = "Delete";
            if (keyPart.Equals("Ins", StringComparison.OrdinalIgnoreCase))
                keyPart = "Insert";
            if (keyPart.Equals("Esc", StringComparison.OrdinalIgnoreCase))
                keyPart = "Escape";
            // «Отображаемые» имена клавиш из HotkeyBox (KeyToDisplay): без обратных
            // синонимов сохранённые сочетания вида Ctrl+0 / Ctrl++ / Ctrl+- не читались
            // бы из настроек после перезапуска (issue #303 — хоткеи масштаба строк).
            if (keyPart.Length == 1 && keyPart[0] >= '0' && keyPart[0] <= '9')
                keyPart = "D" + keyPart;
            else if (keyPart is "+" or "=")
                keyPart = "OemPlus";
            else if (keyPart == "-")
                keyPart = "OemMinus";
            else if (keyPart == "NumPad+")
                keyPart = "Add";
            else if (keyPart == "NumPad-")
                keyPart = "Subtract";

            if (!Enum.TryParse<Key>(keyPart, true, out var parsed) || parsed == Key.None)
                return false;

            // Сочетание без модификатора WPF не принимает в KeyBinding для букв/цифр
            // (NotSupportedException). Такие значения могли сохраниться в settings.json
            // до правки поля ввода (issue #204) — отбраковываем их при чтении, чтобы
            // они не ломали регистрацию остальных горячих клавиш. Без модификатора
            // допустимы только функциональные клавиши и Delete/Insert — как в HotkeyBox.
            if (modifiers == ModifierKeys.None && !IsAllowedWithoutModifier(parsed))
                return false;

            key = parsed;
            return true;
        }

        /// <summary>
        /// Допустима ли клавиша в сочетании без модификатора: функциональные
        /// клавиши F1…F24, а также Delete и Insert. Буквы и цифры без модификатора
        /// WPF не принимает в KeyBinding, поэтому требуют хотя бы одного модификатора.
        /// Набор совпадает с реализацией в Controls/HotkeyBox.cs.
        /// </summary>
        private static bool IsAllowedWithoutModifier(Key key) =>
            (key >= Key.F1 && key <= Key.F24)
            || key == Key.Delete
            || key == Key.Insert;

        /// <summary>
        /// Регистрирует системные биндинги закладок: Alt+1…Alt+9 (запуск Предприятия),
        /// а также сочетания установки/навигации/очистки/запуска закладок.
        /// Перед добавлением удаляются ВСЕ прежние биндинги этих же жестов (включая
        /// пользовательские), чтобы системное сочетание гарантированно выигрывало.
        /// </summary>
        private void RegisterFavoriteHotkeys()
        {
            // Удаляем предыдущие системные биндинги закладок (и пользовательские,
            // занявшие эти же жесты): см. IsBookmarkSystemBinding.
            var toRemove = InputBindings
                .OfType<KeyBinding>()
                .Where(kb => IsBookmarkSystemBinding(kb.Modifiers, kb.Key))
                .ToList();
            foreach (var kb in toRemove)
                InputBindings.Remove(kb);

            // Alt+1…Alt+9 — запуск Предприятия избранных баз.
            for (int i = 1; i <= 9; i++)
            {
                int index = i;
                InputBindings.Add(new KeyBinding(
                    new ViewModels.RelayCommand(_ => _viewModel.LaunchFavoriteByHotkey(index)),
                    (Key)((int)Key.D0 + i),
                    ModifierKeys.Alt));
            }

            // Ctrl+Shift+P — поставить/снять закладку выбранной базы.
            InputBindings.Add(new KeyBinding(
                new ViewModels.RelayCommand(_ => _viewModel.ToggleBookmarkForCurrent()),
                Key.P, ModifierKeys.Control | ModifierKeys.Shift));

            // Ctrl+Shift+D1..D9 — назначить явный номер закладки.
            for (int i = 1; i <= 9; i++)
            {
                int number = i;
                InputBindings.Add(new KeyBinding(
                    new ViewModels.RelayCommand(_ => _viewModel.AssignBookmarkSlot(_viewModel.SelectedInfobase, number)),
                    (Key)((int)Key.D0 + i),
                    ModifierKeys.Control | ModifierKeys.Shift));
            }

            // Ctrl+D1..D9 — перейти к закладке (раскрыть свёрнутую группу).
            for (int i = 1; i <= 9; i++)
            {
                int number = i;
                InputBindings.Add(new KeyBinding(
                    new ViewModels.RelayCommand(_ => _viewModel.NavigateToBookmark(number)),
                    (Key)((int)Key.D0 + i),
                    ModifierKeys.Control));
            }

            // Ctrl+Alt+X — очистить все закладки.
            InputBindings.Add(new KeyBinding(
                new ViewModels.RelayCommand(_ => _viewModel.ClearAllBookmarks()),
                Key.X, ModifierKeys.Control | ModifierKeys.Alt));

            // Ctrl+Alt+D1..D9 — запустить Конфигуратор по закладке.
            for (int i = 1; i <= 9; i++)
            {
                int number = i;
                InputBindings.Add(new KeyBinding(
                    new ViewModels.RelayCommand(_ => _viewModel.LaunchBookmark(number, true)),
                    (Key)((int)Key.D0 + i),
                    ModifierKeys.Control | ModifierKeys.Alt));
            }

            // Alt+E — запустить все закладки.
            InputBindings.Add(new KeyBinding(
                new ViewModels.RelayCommand(_ => _viewModel.LaunchAllBookmarks()),
                Key.E, ModifierKeys.Alt));

            // Меню закладок (issue #356): настраиваемый хоткей, по умолчанию Ctrl+B.
            // Ранее сочетание было зашито, и назначение Ctrl+B другому действию
            // (например, «Показать избранное») молча не работало.
            if (TryParseKeyGesture(_viewModel.HotkeyBookmarksMenu, out var bookmarksKey, out var bookmarksMods))
                InputBindings.Add(new KeyBinding(
                    new ViewModels.RelayCommand(_ => ShowBookmarksMenu()),
                    bookmarksKey, bookmarksMods));
        }

        /// <summary>
        /// Признак того, что жесты (модификаторы + клавиша) относятся к системным
        /// сочетаниям закладок. Такие привязки удаляются перед повторной регистрацией,
        /// чтобы пользовательские хоткеи не перебивали их. Сочетание меню закладок —
        /// настраиваемое (issue #356, по умолчанию Ctrl+B).
        /// </summary>
        private bool IsBookmarkSystemBinding(ModifierKeys mods, Key key)
        {
            if (key >= Key.D1 && key <= Key.D9)
            {
                return mods == ModifierKeys.Alt
                    || mods == (ModifierKeys.Control | ModifierKeys.Shift)
                    || mods == ModifierKeys.Control
                    || mods == (ModifierKeys.Control | ModifierKeys.Alt);
            }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.P)
                return true;
            if (mods == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.X)
                return true;
            if (mods == ModifierKeys.Alt && key == Key.E)
                return true;
            // Меню закладок (issue #356): настраиваемое сочетание, по умолчанию Ctrl+B.
            if (TryParseKeyGesture(_viewModel?.HotkeyBookmarksMenu, out var bmKey, out var bmMods) &&
                mods == bmMods && key == bmKey)
                return true;
            return false;
        }

        /// <summary>
        /// Совпадает ли нажатие с настроенным сочетанием меню закладок (issue #356,
        /// по умолчанию Ctrl+B). Используется надёжным fallback-обработчиком клавиатуры.
        /// </summary>
        private bool IsBookmarksMenuGesture(Key key, ModifierKeys mods) =>
            TryParseKeyGesture(_viewModel.HotkeyBookmarksMenu, out var gestureKey, out var gestureMods)
            && key == gestureKey && mods == gestureMods;

        /// <summary>
        /// Участвует ли меню в стабилизации выделения после закрытия (механизм issue #340):
        /// контекстное меню дерева и меню закладок, открытое по хоткею (issue #356).
        /// </summary>
        private bool IsTreeLikeMenu(ContextMenu menu) =>
            ReferenceEquals(menu, MainTree?.ContextMenu)
            || string.Equals(menu.Tag as string, BatchSelectionHelper.BookmarksMenuTag, StringComparison.Ordinal);

        /// <summary>
        /// Показывает контекстное меню закладок (Ctrl+B) относительно позиции курсора.
        /// Для каждой закладки — запуск Предприятия/Конфигуратора, переход и снятие;
        /// внизу — «Очистить все».
        /// </summary>
        private void ShowBookmarksMenu()
        {
            var menu = new ContextMenu();
            var bookmarks = _viewModel.GetBookmarks();

            if (bookmarks.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = LocalizationManager.T("Main.BookmarksNone"), IsEnabled = false });
            }
            else
            {
                foreach (var (number, ib) in bookmarks)
                {
                    var sub = new MenuItem { Header = string.Format(LocalizationManager.T("Main.BookmarksItem"), number, ib.Name) };
                    sub.Items.Add(new MenuItem
                    {
                        Header = string.Format(LocalizationManager.T("Main.BookmarksEnterprise"), number),
                        Command = new ViewModels.RelayCommand(_ => _viewModel.LaunchBookmark(number, false))
                    });
                    sub.Items.Add(new MenuItem
                    {
                        Header = string.Format(LocalizationManager.T("Main.BookmarksConfigurator"), number),
                        Command = new ViewModels.RelayCommand(_ => _viewModel.LaunchBookmark(number, true))
                    });
                    sub.Items.Add(new MenuItem
                    {
                        Header = string.Format(LocalizationManager.T("Main.BookmarksNavigate"), number),
                        Command = new ViewModels.RelayCommand(_ => _viewModel.NavigateToBookmark(number))
                    });
                    sub.Items.Add(new Separator());
                    sub.Items.Add(new MenuItem
                    {
                        Header = LocalizationManager.T("Main.BookmarksRemove"),
                        Command = new ViewModels.RelayCommand(_ => _viewModel.RemoveBookmark(ib))
                    });
                    menu.Items.Add(sub);
                }
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(new MenuItem
            {
                Header = LocalizationManager.T("Main.BookmarksClearAll"),
                Command = new ViewModels.RelayCommand(_ => _viewModel.ClearAllBookmarks())
            });

            // issue #356 (комментарий 2): меню закладок участвует в стабилизации
            // выделения после закрытия (механизм issue #340) наравне с контекстным
            // меню дерева — клик по строке, закрывший меню, должен выбирать строку.
            menu.Tag = BatchSelectionHelper.BookmarksMenuTag;
            menu.Opened += OnContextMenuOpened;
            menu.Closed += OnContextMenuClosed;

            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Надёжный обработчик Alt+1…9 (KeyBinding с Alt иногда перехватывается системой).
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Блокировка приложения (issue #294): пока активна, клавиатура главного окна
            // не работает — любое нажатие лишь открывает окно ввода пароля. Окно блокировки
            // ввода — отдельное окно, его клавиатура сюда не попадает.
            if (_viewModel.IsAppLocked)
            {
                _viewModel.ShowAppUnlockDialog();
                e.Handled = true;
                return;
            }

            // Ctrl+Alt++ / Ctrl+Alt+- — развернуть/свернуть ВЕТКУ под курсором (issue #341).
            // Проверяем РАНЬШЕ ветки Ctrl+Shift: физическое нажатие «+» на основной
            // клавиатуре требует Shift (реальные модификаторы Ctrl+Alt+Shift+OemPlus),
            // и такая комбинация должна трактоваться как Ctrl+Alt+«+» (ветка), а не как
            // Ctrl+Shift+«+» («развернуть всё»). Явный разбор на этапе Preview, как и для
            // Ctrl+Shift ниже: KeyBinding/KeyGesture на части раскладок и при разном
            // состоянии фокуса срабатывают не всегда, а прямой вызов команды детерминирован.
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                if (key is Key.OemPlus or Key.Add)
                {
                    _viewModel.ExpandBranchCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (key is Key.OemMinus or Key.Subtract)
                {
                    _viewModel.CollapseBranchCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl+Shift++ / Ctrl+Shift+- — «развернуть все» / «свернуть все» (issue #160).
            // Обрабатываем на этапе Preview (туннелирование): событие доходит сюда раньше,
            // чем до вложенных элементов и чем оцениваются InputBindings (фаза всплытия),
            // и не зависит от фокуса/времени регистрации привязок. Поэтому хоткей
            // гарантированно срабатывает с первого нажатия. Вызываются те же команды,
            // что и у кнопок верхней панели (ExpandAllGroupsCommand/CollapseAllGroupsCommand),
            // которые, по отзывам, работают сразу. Установка e.Handled = true отменяет
            // всплытие KeyDown, так что дублирующие InputBindings не сработают повторно.
            // Alt исключается: Ctrl+Alt+Shift+OemPlus («Ctrl+Alt+плюс» физически) уже
            // обработан выше как ветка (issue #341).
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
                (Keyboard.Modifiers & ModifierKeys.Alt) != ModifierKeys.Alt)
            {
                if (key is Key.OemPlus or Key.Add)
                {
                    _viewModel.ExpandAllGroupsCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (key is Key.OemMinus or Key.Subtract)
                {
                    _viewModel.CollapseAllGroupsCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // Стрелки ↑/↓/←/→ управляют выделением в списке баз, только если
            // фокус находится в пределах дерева и не в поле ввода текста.
            // Это гарантирует, что стрелки всегда перемещают выделение по дереву,
            // а не «прыгают» по кнопкам внутри строки (избранное, закрепление, теги).
            if (key is Key.Up or Key.Down or Key.Left or Key.Right &&
                Keyboard.Modifiers == ModifierKeys.None &&
                Keyboard.FocusedElement is not TextBox &&
                !IsFocusInsideTagEditor() &&
                IsFocusInsideMainTree())
            {
                if (HandleArrowNavigation(key))
                {
                    e.Handled = true;
                    return;
                }
            }

            // Enter на строке списка = двойной клик (issue #328): база → запуск
            // «1С:Предприятие»/«Конфигуратор» (ResolveDoubleClickAction), группа →
            // свернуть/развернуть. Не срабатывает, когда фокус вне дерева, в
            // текстовом вводе (инлайн-редактор тега, поиск, палитра команд) или
            // открыт модальный диалог — там Enter работает как обычно. В HotkeyBox
            // Enter намеренно не назначается (клавиша ввода/навигации), поэтому
            // конфликта с пользовательскими горячими нет.
            if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
                && IsFocusInsideMainTree()
                && Keyboard.FocusedElement is not Button and not ToggleButton
                && Services.EnterActivationHelper.CanHandleEnter(
                    Keyboard.FocusedElement is TextBox or PasswordBox || IsFocusInsideTagEditor(),
                    HasOpenModalDialog())
                && HandleRowEnterActivation())
            {
                e.Handled = true;
                return;
            }

            // Esc → в трей (если включено в настройках)
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                // Не перехватываем, если фокус в поле ввода тега — там свой обработчик
                if (IsFocusInsideTagEditor())
                    return;

                // Сначала закрываем открытую подсказку, открытые контекстные меню и пользовательские
                // Popup/оверлеи (issue #261): первый ESC прячет элемент, а не сворачивает/закрывает
                // окно. Иначе главное окно уходит в трей, а элемент остаётся «висеть». После закрытия
                // меню события сюда не доходят (меню обрабатывает ESC само класс-обработчиком
                // OnContextMenuPreviewKeyDown), поэтому повторный ESC уже уводит окно в трей.
                if (ToolTipCloser.CloseAll() || CloseOpenContextMenus() || CloseOpenPopups())
                {
                    e.Handled = true;
                    return;
                }

                if (_viewModel.EscapeToTray && _viewModel.ShowTrayIcon)
                {
                    MinimizeToTray();
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl+F → фокус в поле поиска (в том числе когда фокус в другом поле ввода)
            if (key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (SearchTextBox is not null)
                {
                    SearchTextBox.Focus();
                    SearchTextBox.SelectAll();
                    e.Handled = true;
                    return;
                }
            }

            // Закладки: установка, навигация, очистка и запуск Конфигуратора.
            // Надёжный fallback для наборов цифр с Ctrl/Ctrl+Alt, которые могут
            // перехватываться фокусом или системой.
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) == ModifierKeys.Control)
            {
                var shift = (mods & ModifierKeys.Shift) == ModifierKeys.Shift;
                var alt = (mods & ModifierKeys.Alt) == ModifierKeys.Alt;

                // Ctrl+Shift+P — поставить/снять закладку выбранной базы.
                if (key == Key.P && shift && !alt)
                {
                    _viewModel.ToggleBookmarkForCurrent();
                    e.Handled = true;
                    return;
                }

                // Меню закладок (issue #356): настраиваемый хоткей, по умолчанию Ctrl+B.
                if (IsBookmarksMenuGesture(key, mods))
                {
                    ShowBookmarksMenu();
                    e.Handled = true;
                    return;
                }

                // Ctrl+Alt+X — очистить все закладки.
                if (key == Key.X && alt && !shift)
                {
                    _viewModel.ClearAllBookmarks();
                    e.Handled = true;
                    return;
                }

                bool isDigit = (key >= Key.D1 && key <= Key.D9)
                    || (key >= Key.NumPad1 && key <= Key.NumPad9);
                if (isDigit)
                {
                    int num = key >= Key.NumPad1 && key <= Key.NumPad9
                        ? key - Key.NumPad0
                        : key - Key.D0;
                    if (shift && !alt)
                    {
                        _viewModel.AssignBookmarkSlot(_viewModel.SelectedInfobase, num);
                        e.Handled = true;
                        return;
                    }
                    if (alt && !shift)
                    {
                        _viewModel.LaunchBookmark(num, true);
                        e.Handled = true;
                        return;
                    }
                    if (!shift && !alt)
                    {
                        _viewModel.NavigateToBookmark(num);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Alt+E — запустить все закладки.
            if (key == Key.E && mods == ModifierKeys.Alt)
            {
                _viewModel.LaunchAllBookmarks();
                e.Handled = true;
                return;
            }

            // Alt+1…Alt+9 — запуск Предприятия избранных баз (надёжный fallback
            // KeyBinding, см. RegisterFavoriteHotkeys). Обрабатываются РАНЬШЕ хоткеев
            // действий: системные сочетания закладок имеют приоритет (0.3.9.198).
            if (mods == ModifierKeys.Alt && key >= Key.D1 && key <= Key.D9)
            {
                _viewModel.LaunchFavoriteByHotkey(key - Key.D0);
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Alt && key >= Key.NumPad1 && key <= Key.NumPad9)
            {
                _viewModel.LaunchFavoriteByHotkey(key - Key.NumPad0);
                e.Handled = true;
                return;
            }

            // Пользовательские действия по горячей клавише (0.3.9.198): в самом конце
            // цепочки — системные хоткеи (InputBindings) и закладки имеют приоритет.
            // Не срабатываем, пока вводится текст (поле поиска, инлайн-правка тега).
            if (HandleCustomActionHotkey(key))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// Выполняет пользовательское действие по совпавшей горячей клавише (0.3.9.198):
        /// ключ <see cref="MainViewModel.HotkeyCustomActions"/> разбирается TryParseKeyGesture
        /// и сравнивается с текущим нажатием; контекст определяется как в подменю
        /// (мультивыделение → база → группа, <see cref="CustomActionExecutionPlan.DetermineMenuContext"/>).
        /// Не срабатывает во время выполнения другого действия и при вводе текста.
        /// Возвращает true, если сочетание распознано как хоткей действия.
        /// </summary>
        private bool HandleCustomActionHotkey(Key key)
        {
            if (_viewModel is null || _viewModel.IsCustomActionRunning || _viewModel.HotkeyCustomActions.Count == 0)
                return false;
            // Не срабатываем, пока вводится текст: поле поиска, пароль/другие TextBox-поля,
            // инлайн-правка тега строки базы (issue #283).
            if (Keyboard.FocusedElement is TextBox or PasswordBox || IsFocusInsideTagEditor())
                return false;

            var mods = Keyboard.Modifiers;
            foreach (var pair in _viewModel.HotkeyCustomActions)
            {
                if (!TryParseKeyGesture(pair.Key, out var parsedKey, out var parsedMods))
                    continue;
                if (parsedKey != key || parsedMods != mods)
                    continue;

                var context = CustomActionExecutionPlan.DetermineMenuContext(
                    _viewModel.BatchSelectedCount,
                    _viewModel.SelectedInfobase is not null,
                    _viewModel.SelectedGroupNode is not null);
                if (context is null)
                    return false;
                _ = ExecuteCustomActionHotkeyAsync(pair.Value, context.Value);
                return true;
            }
            return false;
        }

        /// <summary>Выполняет действие по горячей клавише через общий мост (с тем же подтверждением и индикацией).</summary>
        private async Task ExecuteCustomActionHotkeyAsync(CustomAction action, CustomActionContext context)
        {
            if (_viewModel is null || _viewModel.IsCustomActionRunning)
                return;
            await _viewModel.ExecuteCustomActionAsync(action, context);
        }

        /// <summary>
        /// True, если фокус ввода находится внутри inline-поля правки тега строки базы
        /// (редактируемый ComboBox InlineTagBox или его внутреннее поле ввода). В этом
        /// случае клавиши остаются полю: там свой обработчик Enter/Esc (issue #283).
        /// </summary>
        private bool IsFocusInsideTagEditor()
        {
            if (Keyboard.FocusedElement is not DependencyObject focused)
                return false;

            return focused is ComboBox { Name: "InlineTagBox" }
                || FindAncestor<ComboBox>(focused) is { Name: "InlineTagBox" };
        }

        /// <summary>Открытые контекстные меню главного окна (issue #261).</summary>
        private readonly HashSet<ContextMenu> _openContextMenus = new();

        /// <summary>
        /// Снимок клика, которым закрыли контекстное меню дерева (issue #340, новая стратегия).
        /// Записывается в момент закрытия меню (простой левый клик по строке БЕЗ модификаторов);
        /// используется для распознавания ПОВТОРНОЙ доставки того же MouseDown в дерево (WPF
        /// освобождает захват попапа асинхронно) и для fallback, если повторной доставки не будет.
        /// Сбрасывается при первом же событии мыши после закрытия меню.
        /// </summary>
        private BatchSelectionHelper.MenuCloseClickSnapshot? _menuCloseClickSnapshot;

        /// <summary>
        /// Флаг «клик, закрывший меню, ещё не обработан» (issue #340, новая стратегия):
        /// взводится в <see cref="TryApplyTreeClickAfterMenuClosed"/>, снимается штатной
        /// повторной доставкой клика (OnInfobaseTree_PreviewMouseLeftButtonDown) или
        /// fallback-обработчиком <see cref="ApplyMenuCloseFallback"/>. Fallback срабатывает
        /// только пока флаг взведён — применение выбора однократно и идемпотентно.
        /// </summary>
        private bool _menuClosePendingApply;

        /// <summary>Целевая база клика, закрывшего меню (для fallback, issue #340).</summary>
        private Infobase? _menuCloseTarget;

        /// <summary>Секция целевой строки: true — «Закреплённые» (для fallback, issue #340).</summary>
        private bool _menuCloseTargetIsPinnedSection;

        /// <summary>
        /// Метка последнего закрытия контекстного меню ДЕРЕВА (issue #340, 0.3.9.308):
        /// единые часы <see cref="Environment.TickCount"/>. Фиксируется БЕЗУСЛОВНО в
        /// <see cref="OnContextMenuClosed"/> (в отличие от снимка клика, который писался
        /// только после длинной guard-цепочки <see cref="TryApplyTreeClickAfterMenuClosed"/>).
        /// Используется как расширенный признак запуска стабилизации выделения
        /// (BatchSelectionHelper.ShouldStabilizeAfterMenuClose) для любого обычного клика
        /// без модификаторов в окне ~1,5 с после закрытия меню.
        /// </summary>
        private long _lastMenuCloseTick;

        /// <summary>
        /// Последний обычный клик по строке дерева (issue #340, 0.3.9.314): единые часы
        /// <see cref="Environment.TickCount"/>, целевая база и секция строки. Записывается
        /// в ветке обычного клика <c>OnInfobaseTree_PreviewMouseLeftButtonDown</c>
        /// (MainWindow.Events.cs, после ApplySelection) и используется при закрытии меню
        /// дерева в <see cref="OnContextMenuClosed"/>: клик мог прийти в дерево ДО закрытия
        /// меню (второй реальный trace.json: MouseDown → MenuClosed, snapshot=False,
        /// redelivery=False) — снимок не записывается (кнопка отпущена к моменту закрытия),
        /// повторной доставки нет, и стабилизацию нужно запускать по цели этого клика
        /// (BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose, причина
        /// "clickBeforeMenuClose"). Очищается после использования либо перезаписывается
        /// следующим кликом.
        /// </summary>
        private (long Tick, Infobase Base, bool IsPinnedSection)? _lastPlainTreeClick;

        /// <summary>
        /// Метка открытия контекстного меню ДЕРЕВА (issue #340, 11-я итерация): единые часы
        /// <see cref="Environment.TickCount"/>. Устанавливается в <see cref="OnContextMenuOpened"/>;
        /// используется как граница «клик во время открытого меню» (см.
        /// <see cref="_treeMenuOpenClickTick"/>).
        /// </summary>
        private long _treeMenuOpenedTick;

        /// <summary>
        /// Метка последнего левого клика по ПОПАПУ контекстного меню дерева (issue #340,
        /// 11-я итерация): 0 — клика не было. Записывается в
        /// <see cref="OnTreeMenuPopupMouseLeftButtonDown"/>, сбрасывается при открытии меню.
        /// Надёжный сигнал «меню закрыто кликом» БЕЗ привязки к давности: любой клик, пока
        /// меню открыто, попадает в попап ДО того, как будет проглочен/доставлен дальше —
        /// в отличие от окон 500/2000 мс, которые не видели полностью проглоченный клик
        /// (лог 0.3.9.316, 7OH).
        /// </summary>
        private long _treeMenuOpenClickTick;

        /// <summary>
        /// Был ли клавиатурный фокус в дереве ДО открытия контекстного меню дерева (issue #340,
        /// 11-я итерация): после закрытия меню фокус возвращается дереву, если он был там до
        /// открытия (комментарий 7OH 28/28: стрелки не работают, TAB уходит на кнопку
        /// сворачивания). Фиксируется в <see cref="OnContextMenuOpened"/>.
        /// </summary>
        private bool _keyboardFocusWasInTreeBeforeMenuOpen;

        /// <summary>
        /// Детерминированный признак «меню закрыто КЛИКОМ ПО ПУНКТУ» (issue #356, 0.3.11):
        /// фиксируется В МОМЕНТ клика по попапу (<see cref="OnTreeMenuPopupMouseLeftButtonDown"/>,
        /// OriginalSource — внутри MenuItem), сбрасывается при открытии меню. Старый признак
        /// через Mouse.DirectlyOver в MenuClosed нестабилен — попап к этому моменту уже
        /// закрыт, hit-test проходит «через раз».
        /// </summary>
        private bool _menuClosedByItemClick;

        private void OnContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
            {
                _openContextMenus.Add(menu);
                // issue #340 (0.3.9.308): безусловная запись открытия меню — диагностика
                // не должна зависеть от guard-цепочки TryApplyTreeClickAfterMenuClosed.
                var isTreeMenu = IsTreeLikeMenu(menu);
                MenuCloseTrace.Log($"MenuOpened: menuId={GetContextMenuId(menu)}, isTreeMenu={isTreeMenu}");
                // issue #340 (0.3.9.317, 11-я итерация): для меню ДЕРЕВА фиксируем метку
                // открытия, состояние клавиатурного фокуса до открытия и подписываемся на
                // левый клик по ПОПАПУ меню (надёжный сигнал «меню закрыто кликом»).
                if (isTreeMenu && MainTree is not null)
                {
                    _treeMenuOpenedTick = Environment.TickCount;
                    _treeMenuOpenClickTick = 0;
                    // issue #356 (0.3.11): сброс детерминированного признака «закрыто
                    // кликом по пункту» на каждое открытие меню.
                    _menuClosedByItemClick = false;
                    _keyboardFocusWasInTreeBeforeMenuOpen = IsFocusInsideMainTree();
                    menu.PreviewMouseLeftButtonDown += OnTreeMenuPopupMouseLeftButtonDown;
                    MenuCloseTrace.Log($"MenuOpenedFocus: wasInTree={_keyboardFocusWasInTreeBeforeMenuOpen}, " +
                                       $"tick={_treeMenuOpenedTick}");
                }
            }
        }

        /// <summary>
        /// Левый клик по ПОПАПУ контекстного меню дерева (issue #340, 11-я итерация): обычный
        /// клик во время открытого меню попадает в попап ДО того, как будет проглочен или
        /// доставлен дальше (в дерево он не доходит вовсе). Метка — единые часы
        /// <see cref="Environment.TickCount"/>. Используется как надёжный сигнал
        /// «меню закрыто кликом» — независимо от давности клика.
        /// </summary>
        private void OnTreeMenuPopupMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _treeMenuOpenClickTick = Environment.TickCount;
            // issue #356 (0.3.11): детерминированная фиксация «клик пришёл по ПУНКТУ
            // меню» — в момент клика, когда попап ещё открыт и hit-test стабилен.
            var overMenuItemNow = FindAncestor<MenuItem>(e.OriginalSource as DependencyObject) is not null;
            if (overMenuItemNow)
                _menuClosedByItemClick = true;
            var pos = e.GetPosition(MainTree);
            MenuCloseTrace.Log($"MenuClickDuringOpen: tick={_treeMenuOpenClickTick}, " +
                               $"x={pos.X:0.#}, y={pos.Y:0.#}, source=popup, overMenuItem={overMenuItemNow}");
        }

        private void OnContextMenuClosed(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
            {
                _openContextMenus.Remove(menu);
                // issue #340 (0.3.9.317): отписка от кликов по попапу меню (см. OnContextMenuOpened).
                menu.PreviewMouseLeftButtonDown -= OnTreeMenuPopupMouseLeftButtonDown;
                // issue #340 (0.3.9.308): безусловная запись закрытия меню. Для меню дерева
                // дополнительно фиксируется метка закрытия — расширенный признак запуска
                // стабилизации IsSelected (меню могло закрыться ESC/кликом мимо строки,
                // когда снимок клика не записывался вовсе).
                var isTreeMenu = IsTreeLikeMenu(menu);
                MenuCloseTrace.Log($"MenuClosed: menuId={GetContextMenuId(menu)}, isTreeMenu={isTreeMenu}");
                if (isTreeMenu)
                    _lastMenuCloseTick = Environment.TickCount;

                // Строка базы ПОД КУРСОРОМ на момент закрытия меню дерева (issue #340,
                // 0.3.9.316): цель восстановления выбором (MenuClosedOverRow), если меню
                // закрылось кликом, который попап проглотил и не доставил в дерево.
                Infobase? cursorInfobase = null;
                var cursorPinnedSection = false;
                var overTreeRow = false;
                var overMenuItem = false;

                // B-4 (0.3.9.311): для меню ДЕРЕВА дополнительно записываются координаты
                // курсора и признаки «курсор над строкой дерева» / «над пунктом меню» /
                // фокус окна — по логу видно, ЧЕМ именно закрыто меню (кликом по строке /
                // кликом мимо / выбором пункта / ESC). Hit-test выполняется по дереву даже
                // если guard-цепочка TryApplyTreeClickAfterMenuClosed не прошла (снимок
                // клика не записан) — запись о положении мыши остаётся безусловной.
                if (isTreeMenu && MainTree is not null)
                {
                    var cursorPos = Mouse.GetPosition(MainTree);
                    var hitOver = MainTree.InputHitTest(cursorPos) as DependencyObject;
                    var cursorRow = hitOver is null ? null : FindAncestor<TreeViewItem>(hitOver);
                    overTreeRow = cursorRow is not null
                        && cursorRow.DataContext is Infobase or PinnedInfobaseItem or GroupNodeViewModel;
                    // issue #356 (0.3.11): признак «меню закрыто выбором пункта» —
                    // детерминированный _menuClosedByItemClick (записан в момент клика
                    // по попапу) ИЛИ прежняя эвристика Mouse.DirectlyOver (запасной путь:
                    // hit-test в MenuClosed нестабилен, попап уже закрыт).
                    overMenuItem = _menuClosedByItemClick ||
                        (Mouse.DirectlyOver is { } directlyOver &&
                         FindAncestor<MenuItem>(directlyOver as DependencyObject) is not null);
                    if (cursorRow?.DataContext is Infobase or PinnedInfobaseItem)
                    {
                        cursorInfobase = UnwrapInfobase(cursorRow.DataContext);
                        cursorPinnedSection = BatchSelectionHelper.IsPinnedSection(cursorRow.DataContext);
                    }
                    MenuCloseTrace.Log($"MenuClosedCursor: x={cursorPos.X:0.#}, y={cursorPos.Y:0.#}, " +
                                       $"overTreeRow={overTreeRow}, overMenuItem={overMenuItem}, " +
                                       $"closedByItemClick={_menuClosedByItemClick}, " +
                                       $"keyboardFocusWithin={IsKeyboardFocusWithin}");
                }

                // issue #340: клик по строке дерева, закрывший контекстное меню,
                // перехватывается попапом меню и «проглатывается» — выбор строки и
                // снятие мультивыделения не выполняются. Повторяем обработку клика.
                TryApplyTreeClickAfterMenuClosed(menu);

                // issue #340 (0.3.9.314): клик по строке мог прийти в дерево ДО закрытия
                // меню (второй реальный trace.json: MouseDown → MenuClosed, snapshot=False,
                // redelivery=False) — снимок по guard-цепочке TryApplyTreeClickAfterMenuClosed
                // не записывается (левая кнопка к моменту закрытия уже отпущена), повторной
                // доставки «хвоста» нет, и ни один штатный путь стабилизацию не запускает.
                // Если последний обычный клик по строке был непосредственно (≤500 мс, окно
                // MenuClosePrecedingClickWindowMs) перед закрытием меню ДЕРЕВА — стабилизируем
                // выбор по цели этого клика. Отложенный запуск (приоритет Input): компоновка
                // после закрытия попапа устаканится; сама стабилизация идемпотентна, доводит
                // выбор до сходимости (15 проходов / 1,5 с) и не трогает мультивыделение.
                if (isTreeMenu)
                {
                    var precedingClick = _lastPlainTreeClick;
                    _lastPlainTreeClick = null;
                    // issue #340 (0.3.9.317, 11-я итерация): «клик по попапу меню» — надёжный
                    // сигнал того, что меню закрыто КЛИКОМ (а не ESC/программно), без привязки
                    // к давности клика (метка из OnTreeMenuPopupMouseLeftButtonDown).
                    var clickDuringMenuOpen = _treeMenuOpenClickTick > _treeMenuOpenedTick;
                    var restoreScheduled = false;
                    var restoreReason = "none";
                    if (_menuCloseClickSnapshot is null &&
                        precedingClick is { } lastPlainClick &&
                        BatchSelectionHelper.ShouldStabilizeForClickPrecedingMenuClose(
                            lastPlainClick.Tick,
                            _lastMenuCloseTick,
                            BatchSelectionHelper.MenuClosePrecedingClickWindowMs))
                    {
                        restoreReason = "clickBeforeMenuClose";
                        var stabilizeTarget = lastPlainClick.Base;
                        var stabilizePinned = lastPlainClick.IsPinnedSection;
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Input,
                            new Action(() => EnsureSelectionStable(stabilizeTarget, stabilizePinned,
                                reason: "clickBeforeMenuClose")));
                    }
                    // issue #340 (0.3.9.316, 10-я итерация): третий реальный trace.json
                    // (0.3.9.315) показал сценарий, где НИ один штатный путь не запускает
                    // стабилизацию: меню закрылось с курсором над строкой дерева
                    // (overTreeRow=true), снимок клика не записан (guard-цепочка
                    // TryApplyTreeClickAfterMenuClosed не прошла — левая кнопка отпущена),
                    // а последний обычный клик был за пределами окна "clickBeforeMenuClose"
                    // (500 мс) — например ~1,8 с (клик, начавший цепочку действий, был ещё
                    // ДО открытия меню). Клик, которым пользователь ЗАКРЫЛ меню, вероятно,
                    // проглочен попапом и не дошёл до дерева — его цель это строка ПОД
                    // КУРСОРОМ: восстанавливаем выбор по ней (reason "menuClosedOverRow").
                    // Исключения — в предикате ShouldRestoreSelectionForRowUnderCursor
                    // (overMenuItem, наличие снимка, ESC/программно без недавнего клика,
                    // мультивыделение).
                    else if (precedingClick is { } activityEvidence &&
                             BatchSelectionHelper.ShouldRestoreSelectionForRowUnderCursor(
                                 overTreeRow: overTreeRow,
                                 overMenuItem: overMenuItem,
                                 snapshotPresent: _menuCloseClickSnapshot is not null,
                                 recentClickWasPlainLeftWithoutModifiers: true,
                                 lastPlainClickTick: activityEvidence.Tick,
                                 menuCloseTick: _lastMenuCloseTick,
                                 nowTick: Environment.TickCount,
                                 windowMs: BatchSelectionHelper.MenuCloseRecentMouseActivityWindowMs) &&
                             cursorInfobase is not null)
                    {
                        restoreScheduled = true;
                        restoreReason = "menuClosedOverRow";
                        var restoreTarget = cursorInfobase;
                        var restorePinned = cursorPinnedSection;
                        var evidenceTick = activityEvidence.Tick;
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Input,
                            new Action(() => ApplyRowUnderCursorRestore(restoreTarget, restorePinned, evidenceTick)));
                    }
                    // issue #340 (0.3.9.317, 11-я итерация): четвёртый реальный лог (0.3.9.316)
                    // показал, что клик, закрывший меню, попап глотает ПОЛНОСТЬЮ, и НИ ОДИН путь
                    // с окнами давности (500/2000 мс) не может его увидеть — последний обычный
                    // клик был ~2,9 с назад (за пределами MenuCloseRecentMouseActivityWindowMs).
                    // Сигнал «клик по попапу меню» (OnTreeMenuPopupMouseLeftButtonDown) фиксирует
                    // факт независимо от давности. Восстановление по строке ПОД КУРСОРОМ при
                    // overTreeRow && !overMenuItem && !snapshot && clickDuringMenuOpen. ESC/
                    // программное закрытие попап-клик не производят; выбор пункта меню исключён
                    // overMenuItem.
                    else if (cursorInfobase is not null &&
                             BatchSelectionHelper.ShouldRestoreSelectionAfterMenuClose(
                                 overTreeRow: overTreeRow,
                                 overMenuItem: overMenuItem,
                                 snapshotPresent: _menuCloseClickSnapshot is not null,
                                 clickDuringMenuOpen: clickDuringMenuOpen))
                    {
                        restoreScheduled = true;
                        restoreReason = "rowUnderCursor";
                        var restoreTarget = cursorInfobase;
                        var restorePinned = cursorPinnedSection;
                        var evidenceTick = _treeMenuOpenClickTick;
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Input,
                            new Action(() => ApplyRowUnderCursorRestore(restoreTarget, restorePinned, evidenceTick)));
                    }
                    // issue #356: меню закрыто ВЫБОРОМ ПУНКТА (меню закладок открывается
                    // хоткеем и закрывается пунктом — клика по дереву нет вовсе, все пути
                    // «строка под курсором» отсечены overMenuItem). Переработка контейнеров
                    // (Recycling) после закрытия попапа сбрасывает подсветку текущей строки —
                    // возвращаем выбор по ТЕКУЩЕЙ базе модели (идемпотентно, без переноса
                    // выбора и без вмешательства в мультивыделение).
                    else if (_viewModel?.SelectedInfobase is { } currentSelected &&
                             BatchSelectionHelper.ShouldRestoreCurrentSelectionAfterMenuCloseEx(
                                 isTreeLikeMenuClosed: true,
                                 closedByItemClick: _menuClosedByItemClick,
                                 overMenuItemHeuristic: overMenuItem,
                                 focusStillWithinWindow: IsKeyboardFocusWithin,
                                 modalDialogOpen: HasOpenModalDialog(),
                                 hasCurrentSelection: true))
                    {
                        restoreScheduled = true;
                        restoreReason = "menuItem";
                        var stableTarget = currentSelected;
                        var stablePinned = currentSelected.IsPinned;
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Input,
                            new Action(() => EnsureSelectionStable(stableTarget, stablePinned, reason: "menuItem")));
                        // issue #356 (0.3.11): контрольный второй проход на Background —
                        // контейнеры после Recycling могут переработаться ПОСЛЕ первого
                        // прохода; EnsureSelectionStable идемпотентен, доводит выбор до
                        // сходимости и мультивыделение не трогает.
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Background,
                            new Action(() => EnsureSelectionStable(stableTarget, stablePinned, reason: "menuItem")));
                    }

                    // Возврат клавиатурного фокуса дереву после закрытия меню (issue #340,
                    // 11-я итерация, комментарий 7OH 28/28): после пропажи выделения дерево
                    // теряет фокус — стрелки не работают, TAB уходит на кнопку сворачивания.
                    var focusRestore = BatchSelectionHelper.ShouldReturnKeyboardFocusToTree(
                        isTreeMenuClosed: true,
                        focusWasInTreeBeforeMenuOpen: _keyboardFocusWasInTreeBeforeMenuOpen,
                        focusStillWithinWindow: IsKeyboardFocusWithin,
                        modalDialogOpen: HasOpenModalDialog(),
                        clickDuringMenuOpen: clickDuringMenuOpen,
                        overTreeRow: overTreeRow);
                    if (focusRestore)
                    {
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Input,
                            new Action(FocusTreeAfterMenuClose));
                    }

                    MenuCloseTrace.Log(BatchSelectionHelper.BuildMenuCloseDecisionLine(
                        restore: restoreScheduled,
                        reason: restoreReason,
                        clickDuringOpen: clickDuringMenuOpen,
                        overTreeRow: overTreeRow,
                        overMenuItem: overMenuItem,
                        focusRestore: focusRestore,
                        closedByItemClick: _menuClosedByItemClick));
                }

                // issue #356 (0.3.11): признак отработан — сброс до следующего открытия меню.
                _menuClosedByItemClick = false;
            }
        }

        /// <summary>
        /// Короткий идентификатор меню для диагностики (issue #340, 0.3.9.308): имя
        /// элемента или тип (контекстное меню дерева может быть без Name). Чистая
        /// строка, безопасная для JSON-сообщения.
        /// </summary>
        private static string GetContextMenuId(ContextMenu menu)
        {
            var name = menu.Name;
            return string.IsNullOrEmpty(name) ? menu.GetType().Name : name;
        }

        /// <summary>
        /// Фиксирует клик по строке дерева, которым пользователь закрыл контекстное меню
        /// (issue #340, новая стратегия). Пока меню открыто, WPF держит захват мыши в попапе:
        /// событие клика по строке уходит в попап и только закрывает меню — ни выбор
        /// строки, ни снятие мультивыделения (OnInfobaseTree_PreviewMouseLeftButtonDown)
        /// при этом не выполняются. После фактического закрытия меню WPF освобождает захват
        /// и ПОВТОРНО доставляет «хвост» того же MouseDown в дерево — это штатный, самый
        /// устойчивый путь выбора (клик по ЖИВОМУ контейнеру под Recycling).
        /// <para>
        /// Пять прежних попыток (0.3.9.277/291/299/300/302) применяли выбор синхронно/
        /// отложенно в момент закрытия меню (по данным InputHitTest) и гасили повторную
        /// доставку — но в состоянии Closed контейнеры ещё перерабатываются виртуализацией
        /// (VirtualizingStackPanel, Recycling), IsSelected «уезжает», а гашение оставляло
        /// систему без единственного устойчивого пути выбора. Новая стратегия: здесь выбор
        /// НЕ применяется — только запоминается клик (снимок + флаг _menuClosePendingApply)
        /// и планируется FALLBACK (<see cref="ApplyMenuCloseFallback"/>) на случай, если
        /// WPF повторную доставку не выполнит. Сам выбор применит штатная повторная
        /// доставка в OnInfobaseTree_PreviewMouseLeftButtonDown; после него запускается
        /// стабилизация IsSelected (<see cref="EnsureSelectionStable"/>).
        /// </para>
        /// </summary>
        private void TryApplyTreeClickAfterMenuClosed(ContextMenu menu)
        {
            // Меню закладок (issue #356) обрабатывается наравне с контекстным меню
            // дерева: клик по строке, закрывший меню, должен выбирать строку.
            if (!IsTreeLikeMenu(menu))
                return;
            if (_viewModel is null || !IsVisible)
                return;

            // Отсекаем закрытие выбором пункта меню (мышь в этот момент над пунктом меню)
            // и закрытие по ESC / программно (кнопка мыши не нажата).
            if (Mouse.DirectlyOver is { } over && FindAncestor<MenuItem>(over as DependencyObject) is not null)
                return;
            if (Mouse.LeftButton != MouseButtonState.Pressed)
                return;

            var pos = Mouse.GetPosition(MainTree);
            if (pos.X < 0 || pos.Y < 0 ||
                pos.X > MainTree.ActualWidth || pos.Y > MainTree.ActualHeight)
                return;

            var hit = MainTree.InputHitTest(pos) as DependencyObject;
            var treeViewItem = hit is null ? null : FindAncestor<TreeViewItem>(hit);
            if (treeViewItem?.DataContext is not Infobase and not PinnedInfobaseItem)
                return;
            var infobase = UnwrapInfobase(treeViewItem.DataContext);
            if (infobase is null || infobase.Id is not { Length: > 0 })
                return;

            // Снимок записывается ТОЛЬКО для простого левого клика БЕЗ модификаторов:
            // Ctrl/Shift-клики при открытом меню должны уйти штатной логике мультивыделения
            // (ToggleBatchSelection/SelectRange) — снимок не должен их дедуплицировать или
            // «перевыбирать» (иначе мультивыделение подавлялось бы вместе с повторной
            // доставкой).
            var mods = Keyboard.Modifiers;
            if (!BatchSelectionHelper.ShouldRecordMenuCloseSnapshot(
                    "Left",
                    (mods & ModifierKeys.Control) == ModifierKeys.Control,
                    (mods & ModifierKeys.Shift) == ModifierKeys.Shift))
            {
                return;
            }

            // Запоминаем клик (снимок + флаг + цель). Время — едиными часами
            // Environment.TickCount (та же шкала, что и в сравнении
            // OnInfobaseTree_PreviewMouseLeftButtonDown).
            _menuCloseClickSnapshot = new BatchSelectionHelper.MenuCloseClickSnapshot(
                "Left", Environment.TickCount, pos.X, pos.Y);
            _menuClosePendingApply = true;
            _menuCloseTarget = infobase;
            _menuCloseTargetIsPinnedSection = BatchSelectionHelper.IsPinnedSection(treeViewItem.DataContext);

            // Диагностика (issue #340, F-поля): активность/видимость окна и число
            // открытых контекстных меню — для проверки гипотезы S4 (деактивация окна
            // закрытием попапа меню и сброс состояния до повторной доставки клика).
            MenuCloseTrace.Log($"TryApply: snapshot=(Left,t={Environment.TickCount},x={pos.X:0.#},y={pos.Y:0.#}), " +
                               $"target={infobase.Id}, pending=true, pinned={_menuCloseTargetIsPinnedSection}, " +
                               $"IsVisible={IsVisible}, IsActive={IsActive}, openMenusCount={_openContextMenus.Count}");

            // Fallback срабатывает на приоритете Input ПОСЛЕ возможной повторной доставки
            // клика: если штатный PreviewMouseLeftButtonDown уже обработал клик, он снял
            // _menuClosePendingApply, и fallback ничего не делает (идемпотентность). Если
            // повторной доставки не было — выбор ставится по данным (SelectTreeRowByData)
            // и запускается стабилизация IsSelected.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(ApplyMenuCloseFallback));

            // Контрольный дамп через 500 мс после клика (issue #340, диагностика): итоговое
            // состояние выделения — SelectedItem дерева, модель SelectedInfobase, подсветка
            // контейнера и размер набора мультивыделения. Позволяет точно определить, какое
            // звено рвётся, если баг сохраняется.
            var dumpTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            dumpTimer.Tick += (_, _) =>
            {
                dumpTimer.Stop();
                var selectedItem = UnwrapInfobase(MainTree?.SelectedItem);
                var selectedModel = _viewModel.SelectedInfobase;
                MenuCloseTrace.Log(
                    $"Dump500ms: target={infobase.Id}, SelectedItem={(selectedItem?.Id ?? "null")}, " +
                    $"SelectedInfobase={(selectedModel?.Id ?? "null")}, " +
                    $"container.IsSelected={treeViewItem.IsSelected}, batch.Count={_viewModel.BatchSelectedCount}");
            };
            dumpTimer.Start();
        }

        /// <summary>
        /// Fallback-применение выбора клика, которым закрыли контекстное меню (issue #340,
        /// новая стратегия). Штатный путь — ПОВТОРНАЯ доставка MouseDown в дерево (WPF
        /// освобождает захват попапа асинхронно) — обрабатывает клик по живому контейнеру
        /// и сам снимает флаг <see cref="_menuClosePendingApply"/>. Fallback нужен на случай,
        /// если повторной доставки не произошло: применяет выбор по ДАННЫМ
        /// (<see cref="SelectTreeRowByData"/>) и запускает стабилизацию
        /// (<see cref="EnsureSelectionStable"/>). Идемпотентен: срабатывает только пока флаг
        /// взведён и пользователь не перевыбрал другую строку.
        /// </summary>
        private void ApplyMenuCloseFallback()
        {
            if (!_menuClosePendingApply)
            {
                MenuCloseTrace.Log("Fallback: ran=false (флаг уже снят штатной доставкой)");
                return;
            }
            _menuClosePendingApply = false;

            var target = _menuCloseTarget;
            var isPinnedSection = _menuCloseTargetIsPinnedSection;
            _menuCloseTarget = null;
            _menuCloseTargetIsPinnedSection = false;
            if (target is null || _viewModel is null || MainTree is null)
                return;

            // Пользователь успел перевыбрать другую строку — не вмешиваемся.
            if (_viewModel.SelectedInfobase is { } current && !ReferenceEquals(current, target))
            {
                MenuCloseTrace.Log($"Fallback: ran=true, target={target.Id}, userReselected=true");
                return;
            }

            // Клик был без модификаторов — семантика обычного клика: единственный выбор
            // (мультивыделение снимается, выбирается целевая строка).
            var containerFound = isPinnedSection
                ? FindPinnedTreeViewItemForData(target) is not null
                : FindRegularTreeViewItemForData(target) is not null;
            _viewModel.ClearBatchSelection();
            SelectTreeRowByData(target, null, isPinnedSection);
            MenuCloseTrace.Log($"Fallback: ran=true, target={target.Id}, containerFound={containerFound}, " +
                               $"selectedByData=true, pinned={isPinnedSection}");
            // B-5 (0.3.9.311): fallback работает по снимку клика — причина "snapshot".
            EnsureSelectionStable(target, isPinnedSection, reason: "snapshot");
        }

        /// <summary>
        /// Восстанавливает выбор строки ПОД КУРСОРОМ после закрытия контекстного меню
        /// (issue #340, 10-я итерация): меню закрылось с курсором над строкой дерева,
        /// снимок клика не записан, а последний обычный клик мыши был в окне
        /// <see cref="BatchSelectionHelper.MenuCloseRecentMouseActivityWindowMs"/> (клик,
        /// закрывший меню, проглочен попапом и не дошёл до дерева — строка под курсором
        /// и есть его цель). Семантика обычного клика без модификаторов: единственный
        /// выбор; мультивыделение снимается ТОЛЬКО если строка под курсором отличается
        /// от текущего выбора (строка уже выбранная — только стабилизация, набор «для
        /// выделенных» не трогаем). Если пользователь успел перевыбрать другую строку —
        /// не вмешиваемся. Выбор применяется по данным и дополнительно стабилизируется
        /// (<see cref="EnsureSelectionStable"/>, причина "menuClosedOverRow").
        /// </summary>
        /// <param name="target">Строка базы под курсором в момент закрытия меню.</param>
        /// <param name="isPinnedSection">Секция строки: true — «Закреплённые» (issue #326).</param>
        /// <param name="evidenceTick">Метка последнего обычного клика (для диагностики).</param>
        private void ApplyRowUnderCursorRestore(Infobase target, bool isPinnedSection, long evidenceTick)
        {
            if (_viewModel is null || MainTree is null || target is null)
                return;

            // Пользователь успел перевыбрать другую строку — не вмешиваемся.
            if (_viewModel.SelectedInfobase is { } current && !ReferenceEquals(current, target))
            {
                MenuCloseTrace.Log($"MenuClosedOverRow: ran=false, target={target.Id}, userReselected=true");
                return;
            }

            var selectionChanged = !ReferenceEquals(_viewModel.SelectedInfobase, target);
            if (selectionChanged)
            {
                // Семантика обычного клика: единственный выбор строки под курсором.
                _viewModel.ClearBatchSelection();
                SelectTreeRowByData(target, null, isPinnedSection);
            }
            MenuCloseTrace.Log($"MenuClosedOverRow: ran=true, target={target.Id}, pinned={isPinnedSection}, " +
                               $"selected={(selectionChanged ? "applied" : "same")}, " +
                               $"precedingClickTick={evidenceTick}");
            EnsureSelectionStable(target, isPinnedSection, reason: "menuClosedOverRow");
        }

        /// <summary>
        /// Возвращает клавиатурный фокус дереву после закрытия контекстного меню (issue #340,
        /// 11-я итерация): комментарий 7OH 28/28 — после пропажи выделения стрелки перестают
        /// работать, TAB уходит на кнопку сворачивания (дерево теряет клавиатурный фокус).
        /// Фокус ставится на контейнер ТЕКУЩЕГО выбора (если реализован), иначе — на само
        /// дерево. В отличие от <see cref="MainWindow.Tree.RestoreTreeKeyboardFocus"/> (пересборка
        /// списка, RevealAndSelectAfterRebuild со скроллом) здесь НЕ пересобираем и НЕ прокручиваем —
        /// только фокус, чтобы не дёргать вид после правого клика. Идемпотентно; вызывается
        /// только при взведённом предикате
        /// <see cref="BatchSelectionHelper.ShouldReturnKeyboardFocusToTree"/>.
        /// </summary>
        private void FocusTreeAfterMenuClose()
        {
            if (MainTree is null || _viewModel is null)
                return;

            if (_viewModel.SelectedInfobase is { } selected)
            {
                var item = FindRegularTreeViewItemForData(selected)
                           ?? FindPinnedTreeViewItemForData(selected);
                if (item is not null)
                {
                    item.Focus();
                    Keyboard.Focus(item);
                    MenuCloseTrace.Log($"MenuFocusRestore: restored=true, target={selected.Id}, " +
                                       $"focusedElement={Keyboard.FocusedElement?.GetType().Name ?? "null"}");
                    return;
                }
            }

            MainTree.Focus();
            Keyboard.Focus(MainTree);
            MenuCloseTrace.Log($"MenuFocusRestore: restored=true, target=MainTree, " +
                               $"focusedElement={Keyboard.FocusedElement?.GetType().Name ?? "null"}");
        }

        /// <summary>
        /// Закрывает контекстное меню по ESC (issue #261). Класс-обработчик Preview на тип
        /// ContextMenu: срабатывает, когда фокус ввода находится внутри открытого меню (попап меню
        /// живёт во внешнем HWND/Popup, куда Window_PreviewKeyDown главного окна не доходит). Прячем
        /// меню и помечаем событие обработанным, чтобы тот же ESC не увёл окно в трей.
        /// </summary>
        private void OnContextMenuPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None
                && sender is ContextMenu { IsOpen: true } menu)
            {
                menu.IsOpen = false;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Закрывает все открытые контекстные меню главного окна (issue #261). Используется при
        /// нажатии ESC: первый ESC должен закрыть открытое меню (меню запуска/выбора клиента, меню
        /// «Утилиты», контекстные меню строк и заголовков), а не сворачивать окно в трей. Возвращает
        /// true, если было закрыто хотя бы одно меню. Закрытие через владельца детерминированно —
        /// меню записываются класс-обработчиками Opened/Closed (см. MainWindow.xaml.cs) независимо
        /// от того, как они были показаны.
        /// </summary>
        private bool CloseOpenContextMenus()
        {
            var closed = false;
            foreach (var menu in _openContextMenus.ToArray())
            {
                if (menu.IsOpen)
                {
                    menu.IsOpen = false;
                    closed = true;
                }
            }
            if (closed)
                _openContextMenus.Clear();
            return closed;
        }

        /// <summary>
        /// Закрывает пользовательские всплывающие элементы (Popup) главного окна, которые не являются
        /// ни стандартными <see cref="System.Windows.Controls.ToolTip"/>, ни <see cref="ContextMenu"/>
        /// (issue #261). Именно такие пользовательские Popup-контейнеры/оверлеи (на скриншотах 7OH —
        /// два всплывающих элемента) оставались открытыми по ESC после фикса контекстных меню в
        /// 0.3.9.17: окно сворачивалось в трей, а попап «висел». Возвращает true, если был закрыт
        /// хотя бы один открытый Popup. Обход ведём по всем окнам приложения, чтобы не зависеть от
        /// того, где физически размещён Popup (в визуальном дереве окна или во внешнем HWND/Popup).
        /// </summary>
        private bool CloseOpenPopups()
        {
            var closed = false;
            foreach (Window window in Application.Current.Windows)
            {
                if (ClosePopupsIn(window))
                    closed = true;
            }
            return closed;
        }

        /// <summary>Закрывает все открытые <see cref="System.Windows.Controls.Primitives.Popup"/> в поддереве.</summary>
        private static bool ClosePopupsIn(DependencyObject root)
        {
            var any = false;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node is System.Windows.Controls.Primitives.Popup { IsOpen: true } popup)
                {
                    popup.IsOpen = false;
                    any = true;
                }
                for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                    queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
            return any;
        }


        /// <summary>
        /// Определяет, находится ли клавиатурный фокус внутри дерева баз.
        /// Возвращает false, если фокус вне дерева (поле поиска, кнопка верхней панели и т.п.).
        /// </summary>
        private bool IsFocusInsideMainTree()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            return focused is not null && MainTree is not null &&
                   IsDescendantOf(focused, MainTree);
        }

        /// <summary>
        /// Проверяет, является ли <paramref name="candidate"/> потомком <paramref name="root"/> в визуальном дереве.
        /// </summary>
        private static bool IsDescendantOf(DependencyObject candidate, DependencyObject root)
        {
            for (var current = candidate; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (ReferenceEquals(current, root))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Обрабатывает нажатие стрелки для навигации по дереву баз (issue #331).
        /// Навигация идёт по фактической иерархии произвольной глубины, а не по
        /// двухуровневой схеме:
        /// ↑/↓ — предыдущий/следующий ВИДИМЫЙ узел в порядке обхода (без перескоков
        /// между секциями «Закреплённые» и обычным списком вопреки порядку);
        /// → — раскрыть свёрнутую папку или перейти к первому потомку;
        /// ← — свернуть развёрнутую папку (ТОЛЬКО её — корневые секции не
        /// затрагиваются) или перейти к родительской строке.
        /// Возвращает true, если событие обработано.
        /// </summary>
        private bool HandleArrowNavigation(Key key)
        {
            if (MainTree is null || _viewModel.GroupNodes.Count == 0)
                return false;

            var rows = GetVisibleTreeViewItems();
            if (rows.Count == 0)
                return false;

            var currentIndex = FindCurrentRowIndex(rows);

            // ↑/↓ — следующий/предыдущий видимый узел. Навигация идёт по контейнерам
            // строк, а не по объектам данных: закреплённая база присутствует в дереве
            // дважды (узел «Закреплённые» и собственная группа), и работа с данными
            // всякий раз находила бы первое (верхнее) вхождение, «перепрыгивая»
            // выделение в начало списка.
            if (key is Key.Up or Key.Down)
            {
                var targetIndex = key == Key.Down
                    ? Services.TreeNavigationHelper.NextVisible(rows.Count, currentIndex)
                    : Services.TreeNavigationHelper.PreviousVisible(currentIndex);
                if (targetIndex < 0 || targetIndex == currentIndex)
                    return false;

                // issue #350: движение курсором стрелками снимает мультивыделение, как
                // обычный клик мышью без модификаторов (Ctrl/Shift сюда не попадают —
                // обработчик требует ModifierKeys.None). Набор не трогается, если целевая
                // строка та же или пометок нет.
                if (currentIndex >= 0
                    && Services.BatchSelectionHelper.ShouldClearBatchOnKeyboardNavigation(
                        _viewModel.SelectedInfobaseIds,
                        UnwrapInfobase(rows[currentIndex].DataContext)?.Id,
                        UnwrapInfobase(rows[targetIndex].DataContext)?.Id))
                {
                    _viewModel.ClearBatchSelection();
                }

                SelectRowItem(rows[targetIndex]);
                return true;
            }

            // ←/→ — по фактической вложенности (произвольная глубина, issue #331).
            // Решение принимает чистый хелпер на основе описания строки; Expand и
            // Collapse оставляют выделение на строке, GoToParent/GoToFirstChild
            // переносят его на строку-цель по контейнерам.
            if (key is Key.Left or Key.Right)
            {
                if (currentIndex < 0)
                    return false;

                var row = rows[currentIndex];
                var info = BuildRowInfoForNavigation(rows, currentIndex);
                var action = key == Key.Right
                    ? Services.TreeNavigationHelper.DecideRight(info)
                    : Services.TreeNavigationHelper.DecideLeft(info);

                switch (action)
                {
                    case Services.TreeNavigationHelper.LateralAction.Expand:
                    case Services.TreeNavigationHelper.LateralAction.Collapse:
                        ToggleGroupExpanded(row);
                        return true;

                    case Services.TreeNavigationHelper.LateralAction.GoToFirstChild:
                    case Services.TreeNavigationHelper.LateralAction.GoToParent:
                    {
                        var target = Services.TreeNavigationHelper.TargetIndex(action, info, currentIndex);
                        if (target >= 0 && target < rows.Count && target != currentIndex)
                            SelectRowItem(rows[target]);
                        return true;
                    }

                    default:
                        // База без детей (или корень без родителя): стрелка ничего не
                        // делает. Помечаем событие обработанным, чтобы штатная логика
                        // TreeView не «сворачивала» соседние секции (кейс 1 issue #331).
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Описание строки для навигации влево/вправо: принадлежность группе,
        /// развёрнутость, наличие потомков и индексы родителя/первого потомка в
        /// видимом порядке. Индексы считаются по контейнерам, чтобы закреплённая
        /// копия базы не «перепрыгивала» выделение в начало списка.
        /// </summary>
        private Services.TreeNavigationHelper.RowInfo BuildRowInfoForNavigation(List<TreeViewItem> rows, int index)
        {
            var row = rows[index];
            var isGroup = row.DataContext is GroupNodeViewModel;
            var hasChildren = row.DataContext is GroupNodeViewModel groupNode && groupNode.Items.Count > 0;
            var isExpanded = row.IsExpanded;

            // Родительская строка — ItemsControl, которому принадлежит контейнер;
            // для вложенной строки это TreeViewItem-предок из того же обхода.
            int? parentIndex = null;
            if (ItemsControl.ItemsControlFromItemContainer(row) is TreeViewItem parentTvi)
            {
                var pi = rows.IndexOf(parentTvi);
                if (pi >= 0)
                    parentIndex = pi;
            }

            int? firstChildIndex = null;
            if (isGroup && isExpanded && hasChildren)
                firstChildIndex = index + 1; // первый потомок идёт сразу после группы

            return new Services.TreeNavigationHelper.RowInfo(
                isGroup, isExpanded, hasChildren, parentIndex, firstChildIndex);
        }

        /// <summary>Раскрывает/сворачивает группу через модель (сохраняя состояние).</summary>
        private void ToggleGroupExpanded(TreeViewItem row)
        {
            if (row.DataContext is GroupNodeViewModel groupNode)
                _viewModel.ToggleGroupExpandedCommand.Execute(groupNode);
        }

        /// <summary>
        /// Выполняет «Enter = двойной клик» по текущей строке списка (issue #328):
        /// группа (включая служебные узлы) сворачивается/разворачивается, база
        /// запускается действием по настройке (как при двойном клике). Возвращает
        /// true, если строка под курсором есть и действие выполнено.
        /// </summary>
        private bool HandleRowEnterActivation()
        {
            if (_viewModel is null)
                return false;

            if (_viewModel.SelectedGroupNode is { } groupNode)
            {
                _viewModel.ToggleGroupExpandedCommand.Execute(groupNode);
                return true;
            }

            if (_viewModel.SelectedInfobase is { } infobase)
            {
                ActivateInfobaseByDoubleClickAction(infobase);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Открыт ли какой-либо другой видимый модальный диалог (свойства базы,
        /// настройки и т.п.). При открытом диалоге Enter не перехватывается —
        /// он работает в самом диалоге как обычно (issue #328).
        /// </summary>
        private bool HasOpenModalDialog()
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (!ReferenceEquals(window, this) && window.IsVisible)
                    return true;
            }
            return false;
        }

    }
}
#endif
