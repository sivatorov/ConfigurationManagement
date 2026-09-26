#if LINUX
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Configuration_Management.Localization;

namespace Configuration_Management
{
    /// <summary>
    /// Локализация главного окна (Avalonia/Linux): пересборка содержимого при смене
    /// языка и составление заголовка окна.
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Пересобирает главное окно при смене языка интерфейса, чтобы названия колонок,
        /// кнопки правой панели и подсказки (создаваемые через <c>LocalizationManager.T(...)</c>)
        /// обновились на новый язык сразу, а не после перезапуска. Компактный режим
        /// (<see cref="UiMetrics.Compact"/>) при этом сохраняется; выделение и прокрутка
        /// списка восстанавливаются после пересборки содержимого.
        /// </summary>
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                RebuildAfterLanguageChange();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(RebuildAfterLanguageChange);
        }

        private void RebuildAfterLanguageChange()
        {
            var selected = (object?)_vm?.SelectedInfobase ?? _vm?.SelectedGroupNode;
            var offset = TreeScroll?.Offset;

            Content = BuildRoot();
            Title = ComposeWindowTitle();

            // Обновляем меню и подсказку трея, чтобы подписи кнопок и ToolTip
            // тоже переключились на новый язык без перезапуска.
            if (_trayIcon is not null)
            {
                _trayIcon.Menu = BuildTrayMenu();
                _trayIcon.ToolTipText = LocalizationManager.T("App.Title");
            }

            // Выделение и прокрутка восстанавливаются после того, как новое дерево
            // построено и разложено (иначе строки ещё не существуют). Строка выбирается
            // по конкретному контейнеру, а не через SelectedItem: закреплённая база есть
            // в дереве дважды, и разрешение по данным нашло бы первую копию в узле
            // «Закреплённые» (issue #301).
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (selected is not null && _tree is not null && !ReferenceEquals(_tree.SelectedItem, selected))
                {
                    _tree.SelectionChanged -= OnTreeSelectionChanged;
                    try
                    {
                        if (FindFindInListRow(selected) is { } row)
                        {
                            _tree.SelectRow(row);
                            row.BringIntoView();
                        }
                    }
                    finally { _tree.SelectionChanged += OnTreeSelectionChanged; }
                }
                if (offset is { } off && TreeScroll is { } scroll)
                    scroll.Offset = off;
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Заголовок программы: локализованное имя и суффикс версии. Суффикс
        /// не задваивается при повторном вызове, как в версии для Windows
        /// (MainWindow.Language.cs:107-122). Пользователь видит эту строку в шапке
        /// окна, потому что системной строки заголовка на Linux у нас нет.
        /// </summary>
        private static string ComposeWindowTitle()
        {
            var baseTitle = LocalizationManager.T("App.Title");
            var version = VersionInfo.Display();
            if (string.IsNullOrWhiteSpace(version))
                return baseTitle;

            var suffix = $" v{version}";
            return baseTitle.EndsWith(suffix, StringComparison.Ordinal) ? baseTitle : baseTitle + suffix;
        }
    }
}
#endif