#if LINUX
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Горячие клавиши главного окна (Avalonia/Linux): регистрация привязок,
    /// обработка нажатий на клавиатуре и вспомогательные проверки открытых диалогов.
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Горячие клавиши действий. Сочетания берутся из вьюмодели, оттуда же
        /// их показывают подсказки и контекстное меню, поэтому список и подписи
        /// не расходятся.
        /// Важно про порядок: в Avalonia привязки окна проверяются раньше, чем
        /// клавишу получит элемент с фокусом, в отличие от WPF. Ни одно из этих
        /// сочетаний не совпадает с правкой текста, поэтому ввод в поле поиска
        /// они не задевают, но добавлять сюда Ctrl+C, Ctrl+V и подобное нельзя:
        /// они отберут клавишу у поля ввода. Delete по этой же причине живёт
        /// в отдельном обработчике с проверкой фокуса, а не здесь.
        /// </summary>
        private void RegisterHotkeys()
        {
            if (_vm is null)
                return;

            KeyBindings.Clear();

            // Alt+1…Alt+9 запускают избранные базы по порядку слотов и ставятся
            // ПЕРЕД пользовательскими: Avalonia перебирает привязки по порядку
            // списка и останавливается на первой подошедшей, поэтому иначе
            // назначенный пользователем Alt+1 перебивал бы избранное. Версия
            // для Windows добивается того же с другого конца: там
            // RegisterFavoriteHotkeys сперва удаляет из InputBindings все
            // Alt+1…9, включая пользовательские (MainWindow.Hotkeys.cs:118).
            // Незанятый слот привязку всё равно имеет и клавишу поглощает,
            // но действия не выполняет: так же ведёт себя и версия для Windows.
            for (var number = 1; number <= 9; number++)
            {
                var slot = number;
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture((Key)((int)Key.D0 + slot), KeyModifiers.Alt),
                    Command = new ViewModels.RelayCommand(_ => _vm.LaunchFavoriteByHotkey(slot))
                });
            }

            // Системные сочетания закладок ставятся ПЕРЕД пользовательскими, чтобы
            // те их не перебивали (Avalonia останавливается на первой подошедшей привязке).

            // Ctrl+Shift+P — поставить/снять закладку выбранной базы.
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift),
                Command = new ViewModels.RelayCommand(_ => _vm.ToggleBookmarkForCurrent())
            });

            // Ctrl+Shift+D1..D9 — назначить явный номер закладки.
            for (var number = 1; number <= 9; number++)
            {
                var slot = number;
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture((Key)((int)Key.D0 + slot), KeyModifiers.Control | KeyModifiers.Shift),
                    Command = new ViewModels.RelayCommand(_ => _vm.AssignBookmarkSlot(_vm.SelectedInfobase, slot))
                });
            }

            // Ctrl+D1..D9 — перейти к закладке (раскрыть свёрнутую группу).
            for (var number = 1; number <= 9; number++)
            {
                var slot = number;
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture((Key)((int)Key.D0 + slot), KeyModifiers.Control),
                    Command = new ViewModels.RelayCommand(_ => _vm.NavigateToBookmark(slot))
                });
            }

            // Ctrl+Alt+X — очистить все закладки.
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.X, KeyModifiers.Control | KeyModifiers.Alt),
                Command = new ViewModels.RelayCommand(_ => _vm.ClearAllBookmarks())
            });

            // Ctrl+Alt+D1..D9 — запустить Конфигуратор по закладке.
            for (var number = 1; number <= 9; number++)
            {
                var slot = number;
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture((Key)((int)Key.D0 + slot), KeyModifiers.Control | KeyModifiers.Alt),
                    Command = new ViewModels.RelayCommand(_ => _vm.LaunchBookmark(slot, true))
                });
            }

            // Alt+E — запустить все закладки.
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.E, KeyModifiers.Alt),
                Command = new ViewModels.RelayCommand(_ => _vm.LaunchAllBookmarks())
            });

            // Ctrl+B — меню закладок.
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.B, KeyModifiers.Control),
                Command = new ViewModels.RelayCommand(_ => ShowBookmarksMenu())
            });

            // Delete в привязки не идёт: он правит текст, и в поле ввода
            // не должен удалять базу. Ему отдельный обработчик ниже.
            AddHotkey(_vm.HotkeyEnterprise, _vm.LaunchEnterpriseCommand);
            AddHotkey(_vm.HotkeyConfigurator, _vm.LaunchConfiguratorCommand);
            AddHotkey(_vm.HotkeyEdit, _vm.EditInfobaseCommand);
            AddHotkey(_vm.HotkeyAdd, _vm.AddInfobaseCommand);
            AddHotkey(_vm.HotkeyFavorite, _vm.ToggleFavoriteCommand);
            AddHotkey(_vm.HotkeyPin, _vm.TogglePinCommand);
            AddHotkey(_vm.HotkeyClearCache, _vm.ClearCacheCommand);
            // Переключение режимов списка баз: Все, Избранное, Недавние.
            AddHotkey(_vm.HotkeyShowAll, _vm.ShowAllCommand);
            AddHotkey(_vm.HotkeyShowFavorites, _vm.ShowFavoritesCommand);
            AddHotkey(_vm.HotkeyShowRecent, _vm.ShowRecentCommand);

            // Очистка строки поиска и сброс фильтра тегов — настраиваемые хоткеи (issue #160),
            // значения по умолчанию Ctrl+Shift+C / Ctrl+Shift+T задаются в настройках.
            // Добавляются ПОСЛЕ пользовательских, чтобы назначенные пользователем
            // сочетания имели приоритет.
            AddHotkey(_vm.HotkeyClearSearch, _vm.ClearSearchCommand);
            AddHotkey(_vm.HotkeyClearTags, _vm.ClearTagFiltersCommand);
            // Переключение подробностей правой панели информации — настраиваемый хоткей (issue #172);
            // значение по умолчанию Ctrl+D задаётся в настройках.
            AddHotkey(_vm.HotkeyRightPanelDetails, _vm.ToggleRightPanelDetailsCommand);
            // «Найти в списке» — переход к базе в общем списке (issue #285);
            // значение по умолчанию Ctrl+T задаётся в настройках.
            AddHotkey(_vm.HotkeyFindInList, _vm.FindInListCommand);
            // Смена пользователя — настраиваемый хоткей (issue #200).
            AddHotkey(_vm.HotkeySwitchUser, _vm.SwitchUserCommand);
            // Проверка обновлений конфигураций 1С (функции №21/№22): F9 — для выбранной
            // ИБ, ALT+F9 — окно «Актуальные релизы». Сочетания настраиваются в настройках.
            AddHotkey(_vm.HotkeyCheckUpdate, _vm.CheckUpdateCommand);
            AddHotkey(_vm.HotkeyActualReleases, _vm.ShowActualReleasesCommand);
            // Сценарии резервирования и «Список выгрузок» (функции №16/№18):
            // Ctrl+Shift+F5 — выполнить сценарий, Ctrl+Shift+F7 — список выгрузок.
            AddHotkey(_vm.HotkeyRunBackup, _vm.RunBackupScenarioCommand);
            AddHotkey(_vm.HotkeyExportsList, _vm.ShowExportsListCommand);
            // Блокировка сеансов ИБ (функция №20, Ctrl+Alt+L) и временная блокировка
            // приложения паролем (функция №19). Сочетания настраиваются в настройках.
            AddHotkey(_vm.HotkeySessionLock, _vm.ShowSessionLockCommand);
            AddHotkey(_vm.HotkeyLockApp, _vm.LockAppCommand);
            // Администрирование ИБ (Этап 6, функция №29 + консоль серверов):
            // проверка целостности файловой ИБ (chdbfl) и консоль администрирования серверов 1С.
            AddHotkey(_vm.HotkeyCheckIntegrity, _vm.CheckIntegrityCommand);
            AddHotkey(_vm.HotkeyServerConsole, _vm.OpenServerConsoleCommand);
            // Сохранение копии экрана (функция №30 StartManager): сочетание настраивается в настройках.
            AddHotkey(_vm.ScreenshotHotkey, _vm.TakeScreenshotCommand);
            // Ctrl+Shift+Plus / Ctrl+Shift+Minus — развернуть/свернуть все узлы дерева.
            // Регистрируются обе раскладки (основная клавиатура Oem* и цифровой блок Add/Subtract).
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.OemPlus, KeyModifiers.Control | KeyModifiers.Shift),
                Command = _vm.ExpandAllGroupsCommand
            });
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.Add, KeyModifiers.Control | KeyModifiers.Shift),
                Command = _vm.ExpandAllGroupsCommand
            });
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.OemMinus, KeyModifiers.Control | KeyModifiers.Shift),
                Command = _vm.CollapseAllGroupsCommand
            });
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.Subtract, KeyModifiers.Control | KeyModifiers.Shift),
                Command = _vm.CollapseAllGroupsCommand
            });
        }

        /// <summary>
        /// Удаление базы по назначенному сочетанию. В общие привязки оно
        /// не идёт, потому что по умолчанию это Delete: клавиша текстовая,
        /// и в поле ввода она должна править текст, а не удалять базу.
        /// </summary>
        /// <summary>
        /// Туннельный (Preview) обработчик ESC для закрытия открытой подсказки (issue #261).
        /// Срабатывает раньше всплывающего <see cref="OnWindowKeyDown"/>, пока на элементе
        /// ещё стоит ToolTip.GetIsOpen == true. Если подсказка закрыта — событие помечается
        /// обработанным, и окно на этом же ESC в трей не уходит (второй ESC уже сворачивает).
        /// Основной путь — общий механизм <see cref="ToolTipCloserAvalonia.CloseAll"/> (issue
        /// #270, единый реестр и трейс для всех окон); собственные
        /// <see cref="CloseOpenToolTips"/>/<see cref="CloseOpenPopups"/> оставлены резервом.
        /// </summary>
        private void OnPreviewKeyDownCloseToolTips(object? sender, KeyEventArgs e)
        {
            if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
                return;

            if (ToolTipCloserAvalonia.CloseAll() || CloseOpenToolTips() || CloseOpenPopups())
            {
                ToolTipCloserAvalonia.TraceLog("MainWindow.OnPreviewKeyDownCloseToolTips: первый ESC закрыл подсказки");
                e.Handled = true;
            }
        }

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || _vm is null)
                return;

            // Ctrl+Shift++ / Ctrl+Shift+- — «развернуть все» / «свернуть все» (issue #160).
            // Дублируем назначенные в RegisterHotkeys KeyBindings надёжным явным разбором:
            // KeyBinding/KeyGesture на части раскладок и при разном состоянии фокуса
            // срабатывают только со второго нажатия. Прямой вызов тех же команд, что и у
            // кнопок верхней панели, делает хоткей детерминированным с первого нажатия.
            // Если привязка уже обработала жест (e.Handled == true), сюда не доходим —
            // повторного срабатывания нет.
            if ((e.KeyModifiers & KeyModifiers.Control) != 0 &&
                (e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                if (e.Key is Key.OemPlus or Key.Add)
                {
                    _vm.ExpandAllGroupsCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (e.Key is Key.OemMinus or Key.Subtract)
                {
                    _vm.CollapseAllGroupsCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // Закладки: установка, навигация, очистка и запуск Конфигуратора.
            // Надёжный fallback для наборов цифр с Ctrl/Ctrl+Alt, которые могут
            // перехватываться фокусом или системой.
            var km = e.KeyModifiers;
            if ((km & KeyModifiers.Control) != 0)
            {
                var shiftKm = (km & KeyModifiers.Shift) != 0;
                var altKm = (km & KeyModifiers.Alt) != 0;

                // Ctrl+Shift+P — поставить/снять закладку выбранной базы.
                if (e.Key == Key.P && shiftKm && !altKm)
                {
                    _vm.ToggleBookmarkForCurrent();
                    e.Handled = true;
                    return;
                }

                // Ctrl+B — меню закладок.
                if (e.Key == Key.B && !shiftKm && !altKm)
                {
                    ShowBookmarksMenu();
                    e.Handled = true;
                    return;
                }

                // Ctrl+Alt+X — очистить все закладки.
                if (e.Key == Key.X && altKm && !shiftKm)
                {
                    _vm.ClearAllBookmarks();
                    e.Handled = true;
                    return;
                }

                bool isDigit = (e.Key >= Key.D1 && e.Key <= Key.D9)
                    || (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9);
                if (isDigit)
                {
                    int num = e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9
                        ? e.Key - Key.NumPad0
                        : e.Key - Key.D0;
                    if (shiftKm && !altKm)
                    {
                        _vm.AssignBookmarkSlot(_vm.SelectedInfobase, num);
                        e.Handled = true;
                        return;
                    }
                    if (altKm && !shiftKm)
                    {
                        _vm.LaunchBookmark(num, true);
                        e.Handled = true;
                        return;
                    }
                    if (!shiftKm && !altKm)
                    {
                        _vm.NavigateToBookmark(num);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Alt+E — запустить все закладки.
            if (e.Key == Key.E && km == KeyModifiers.Alt)
            {
                _vm.LaunchAllBookmarks();
                e.Handled = true;
                return;
            }

            // Esc при открытом диалоге закрывает сам диалог. Пока пользователь не
            // кликнул внутри диалога, событие приходит именно сюда: сфокусированной
            // остаётся кнопка главного окна, которой диалог и открыли, а клавиатурное
            // событие Avalonia ведёт вверх по дереву от сфокусированного элемента и
            // маршрута диалога не задевает вовсе (issue #226). После клика внутри
            // маршрут идёт через диалог, и Esc обрабатывает его собственный OnKeyDown.
            // Фокус в диалог не переносим: первым элементом обхода в безрамочном окне
            // оказывается кнопка «Свернуть» собственной полосы заголовка, и рамка
            // фокуса вставала бы на неё.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
                && TopmostModalDialog() is { } dialog)
            {
                dialog.CloseAsCancel();
                e.Handled = true;
                return;
            }

            // Сначала закрываем открытую подсказку и пользовательские Popup/оверлеи главного окна
            // (issue #261): первый ESC прячет элемент, а не уводит окно в трей. Иначе окно
            // сворачивается, а всплывающий элемент остаётся «висеть». После закрытия модального
            // диалога (ветка выше) не трогаем. Основной путь — общий механизм
            // ToolTipCloserAvalonia.CloseAll (issue #270); собственные CloseOpenToolTips/
            // CloseOpenPopups оставлены резервом для совместимости с текущим поведением.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
                && (ToolTipCloserAvalonia.CloseAll() || CloseOpenToolTips() || CloseOpenPopups()))
            {
                e.Handled = true;
                return;
            }

            // Esc уводит окно в трей, если так задано настройкой. В поле ввода
            // клавиша остаётся своей: там ей отменяют правку.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
                && _vm.EscapeToTray && _vm.ShowTrayIcon && CanRestoreHiddenWindow
                && FocusManager?.GetFocusedElement() is not TextBox
                // При открытом модальном диалоге (свойства базы, настройки) Esc
                // должен закрывать только сам диалог, а не уводить главное окно
                // в трей (issue #226).
                && !HasOpenModalDialog())
            {
                SaveWindowLayout();
                _vm.PersistSettings();
                ApplyTrayVisibility();
                Hide();
                e.Handled = true;
                return;
            }
            if (!Controls.HotkeyBox.TryParse(_vm.HotkeyDelete, out var gesture) || gesture is null)
                return;
            if (e.Key != gesture.Key || e.KeyModifiers != gesture.KeyModifiers)
                return;

            // Только для текстовых клавиш без модификаторов: назначенное F8
            // должно работать и в поле ввода, как любая другая горячая клавиша.
            var isTextEditingKey = gesture.KeyModifiers == KeyModifiers.None
                && gesture.Key is Key.Delete or Key.Back or Key.Insert;
            if (isTextEditingKey && FocusManager?.GetFocusedElement() is TextBox)
                return;

            if (_vm.DeleteInfobaseCommand.CanExecute(null))
                _vm.DeleteInfobaseCommand.Execute(null);
            e.Handled = true;
        }

        /// <summary>
        /// Показывает контекстное меню закладок (Ctrl+B). Для каждой закладки —
        /// запуск Предприятия/Конфигуратора, переход и снятие; внизу — «Очистить все».
        /// </summary>
        private void ShowBookmarksMenu()
        {
            if (_vm is not { } vm)
                return;
            var menu = new ContextMenu();
            var bookmarks = vm.GetBookmarks();

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
                        Command = new ViewModels.RelayCommand(_ => _vm.LaunchBookmark(number, false))
                    });
                    sub.Items.Add(new MenuItem
                    {
                        Header = string.Format(LocalizationManager.T("Main.BookmarksConfigurator"), number),
                        Command = new ViewModels.RelayCommand(_ => _vm.LaunchBookmark(number, true))
                    });
                    sub.Items.Add(new MenuItem
                    {
                        Header = string.Format(LocalizationManager.T("Main.BookmarksNavigate"), number),
                        Command = new ViewModels.RelayCommand(_ => _vm.NavigateToBookmark(number))
                    });
                    sub.Items.Add(new Separator());
                    sub.Items.Add(new MenuItem
                    {
                        Header = LocalizationManager.T("Main.BookmarksRemove"),
                        Command = new ViewModels.RelayCommand(_ => _vm.RemoveBookmark(ib))
                    });
                    menu.Items.Add(sub);
                }
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(new MenuItem
            {
                Header = LocalizationManager.T("Main.BookmarksClearAll"),
                Command = new ViewModels.RelayCommand(_ => _vm.ClearAllBookmarks())
            });

            menu.Open(this);
        }

        /// <summary>
        /// Верхнее по Z-порядку открытое окно, если это диалог, унаследованный от
        /// <see cref="ModalWindowBase"/>, иначе <c>null</c>. Порядок берём у платформы
        /// (<see cref="Window.SortWindowsByZOrder"/>), а не порядок открытия: у окон
        /// без отношения владения он последнему открытому не равен. Если сверху лежит
        /// окно другого рода (сообщение, ход обновления), метод возвращает <c>null</c>:
        /// закрывать вместо него диалог под ним нельзя.
        /// </summary>
        private ModalWindowBase? TopmostModalDialog()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                return null;

            var visible = new List<Window>();
            foreach (var window in desktop.Windows)
            {
                if (!ReferenceEquals(window, this) && window.IsVisible)
                    visible.Add(window);
            }

            if (visible.Count == 0)
                return null;

            var ordered = visible.ToArray();
            Window.SortWindowsByZOrder(ordered);
            return ordered[ordered.Length - 1] as ModalWindowBase;
        }

        /// <summary>
        /// Есть ли открытый модальный дочерний диалог (свойства базы, настройки и т.п.).
        /// Все дополнительные окна в приложении показываются модально (ShowDialog/
        /// ShowDialogSync). Проверяем флаг IsVisible, а не IsActive: на Linux/X11 окно
        /// после открытия не всегда сразу получает активацию (issue #226), и по одному
        /// лишь IsActive мы бы не распознали открытый диалог — тогда Esc уводил бы главное
        /// окно в трей, не закрыв диалог. Если видимо любое окно, кроме главного, Esc
        /// должен обработать сам диалог (см. ModalWindowBase.OnKeyDown), а не главное окно.
        /// Закрытые окна в списке имеют IsVisible == false и на результат не влияют.
        /// </summary>
        private bool HasOpenModalDialog()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                return false;

            foreach (var window in desktop.Windows)
            {
                if (!ReferenceEquals(window, this) && window.IsVisible)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Закрывает открытую всплывающую подсказку (ToolTip) главного окна.
        /// Используется при нажатии ESC (issue #261): первый ESC должен скрыть подсказку,
        /// а не сворачивать/закрывать окно. Возвращает true, если была закрыта хотя бы одна.
        /// </summary>
        private bool CloseOpenToolTips()
        {
            var closed = false;

            // Основной путь: закрываем ВСЕ открытые подсказки через их владельцев, записанных
            // класс-обработчиком изменения ToolTip.IsOpenProperty (см. MainWindow.Avalonia.cs).
            // Это надёжнее обхода визуального дерева, потому что открытый ToolTip рендерится
            // попапом в оверлейном слое TopLevel, который не всегда входит в GetVisualChildren()
            // окна, а ToolTip.GetIsOpen на владельце может не отражать фактически показанный попап.
            foreach (var owner in _openToolTipOwners.ToArray())
            {
                if (ToolTip.GetIsOpen(owner))
                {
                    ToolTip.SetIsOpen(owner, false);
                    closed = true;
                    // Подавляем повторное автоматическое открытие подсказки (issue #261):
                    // иначе при наведённом курсоре тултип тут же откроется снова.
                    SuppressToolTipOwner(owner);
                }
            }
            if (closed)
            {
                _openToolTipOwners.Clear();
                return true;
            }

            // Страховка по оверлейному слою TopLevel (там живут открытые попапы ToolTip) не
            // нужна: владелец любого открытого тултипа гарантированно записан в
            // _openToolTipOwners класс-обработчиком ToolTip.IsOpenProperty выше, поэтому основной
            // путь уже покрывает и попапы оверлея. Отдельный обход OverlayLayer.Children через
            // PopupHost опущен: в Avalonia 11.3 этот тип internal, а логику закрытия дублировал
            // бы без выгоды.

            // Резервный путь: старый обход визуального дерева окна и цепочки визуальных
            // родителей сфокусированного элемента (владелец мог оказаться вне оверлея).
            if (!closed)
            {
                CloseIn(this);
                if (FocusManager?.GetFocusedElement() is Visual focused)
                {
                    for (var node = focused; node is not null; node = node.GetVisualParent())
                    {
                        if (node is Control control && ToolTip.GetIsOpen(control))
                        {
                            ToolTip.SetIsOpen(control, false);
                            closed = true;
                            SuppressToolTipOwner(control);
                        }
                    }
                }
            }

            return closed;

            void CloseIn(Visual node)
            {
                if (node is Control control && ToolTip.GetIsOpen(control))
                {
                    ToolTip.SetIsOpen(control, false);
                    closed = true;
                    SuppressToolTipOwner(control);
                }

                foreach (var child in node.GetVisualChildren())
                    CloseIn(child);
            }
        }

        /// <summary>
        /// Закрывает пользовательские всплывающие элементы (Popup) главного окна, которые не
        /// являются ни стандартным <see cref="Avalonia.Controls.ToolTip"/>, ни
        /// <see cref="Avalonia.Controls.ContextMenu"/> (issue #261). На скриншотах 7OH таких
        /// элементов два; после фикса контекстных меню в 0.3.9.17 они оставались открытыми по ESC,
        /// из-за чего окно уходило в трей, а попап «висел». Обходим визуальное дерево окна и гасим
        /// открытые пользовательские Popup. ToolTip и ContextMenu рендерятся в оверлейном слое
        /// (вне визуального дерева окна), поэтому этот путь их не задевает (тултипы закрываются в
        /// <see cref="CloseOpenToolTips"/>). Возвращает true, если был закрыт хотя бы один Popup.
        /// </summary>
        private bool CloseOpenPopups()
        {
            var closed = false;
            foreach (var node in this.GetVisualDescendants())
            {
                if (node is Avalonia.Controls.Primitives.Popup { IsOpen: true } popup)
                {
                    popup.IsOpen = false;
                    closed = true;
                }
            }
            return closed;
        }

        private void AddHotkey(string? gesture, System.Windows.Input.ICommand? command)
        {
            if (command is null || !Controls.HotkeyBox.TryParse(gesture, out var parsed) || parsed is null)
                return;
            KeyBindings.Add(new KeyBinding { Gesture = parsed, Command = command });
        }
    }
}
#endif