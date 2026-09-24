#if LINUX
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог настроек приложения (Avalonia/Linux). Восемь вкладок: «Настройки»,
    /// «Платформы», «Отображение», «Цветовое оформление», «Базы», «Резервное
    /// копирование», «Клавиши», «О программе». Блок ibases.v8i вложен во вкладку
    /// «Базы», тогда как в версии для Windows это отдельная вкладка.
    /// </summary>
    public partial class SettingsWindow : ModalWindowBase
    {
        private readonly MainViewModel _viewModel;

        /// <summary>Пара «подпись режима функциональности — канонический код» для списка выбора (issue #263).</summary>
        private sealed class FunctionalModeOption
        {
            public FunctionalModeOption(string label, string code)
            {
                Label = label;
                Code = code;
            }

            public string Label { get; }
            public string Code { get; }
        }

        /// <summary>Главный контрол вкладок окна (полоса слева).</summary>
        private TabControl? _settingsTabs;

        /// <summary>Вкладка «Отображение» внутри главного контрола вкладок.</summary>
        private TabItem? _displayTab;

        /// <summary>Контрол вложенных вкладок раздела «Отображение» (Значки/Колонки/…).</summary>
        private TabControl? _displaySubTabs;

        /// <summary>
        /// Переключает окно настроек сразу на подвкладку «Колонки» (issue #173).
        /// Используется из контекстного меню заголовка колонки списка баз.
        /// </summary>
        public void SelectColumnsTab()
        {
            if (_settingsTabs is not null && _displayTab is not null)
                _settingsTabs.SelectedItem = _displayTab;
            if (_displaySubTabs is not null)
                _displaySubTabs.SelectedIndex = 1;
        }

        /// <summary>
        /// Создаёт диалог настроек приложения.
        /// </summary>
        /// <param name="viewModel">Главная модель представления приложения.</param>
        public SettingsWindow(MainViewModel viewModel)
        {
            Title = LocalizationManager.T("Settings.Title");
            // Семь вкладок с длинными подписями в одну строку не помещаются
            // ни в какую разумную ширину, поэтому полоса вкладок слева.
            // Размеры и кегль окна из разметки (SettingsWindow.xaml:14-21).
            Width = 880;
            Height = 680;
            MinWidth = 760;
            MinHeight = 560;
            FontSize = 13;

            _viewModel = viewModel;
            // Без контекста привязки переключателей клиента и разрядности
            // не находили свойств и всегда стояли пустыми.
            DataContext = viewModel;
            Content = BuildRoot();
            // Кнопки окно строит само, поэтому подписку на смену языка
            // базовый класс не включает: включаем явно.
            EnsureLanguageSubscription();
        }

        /// <summary>Наблюдатель за значением свойства контрола.</summary>
        private sealed class SettingsObserver<T> : IObserver<T>
        {
            private readonly Action<T> _apply;
            public SettingsObserver(Action<T> apply) => _apply = apply;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(T value) => _apply(value);
        }

        /// <summary>Форматы метки времени в имени файла выгрузки, как в версии для Windows.</summary>
        private static readonly string[] TimestampFormats =
        {
            "yyyyMMdd_HHmmss",
            "yyyy-MM-dd_HH-mm-ss",
            "yyyy-MM-dd_HHmmss",
            "dd.MM.yyyy HH-mm-ss",
            "yyyyMMdd",
            "HHmmss"
        };

        /// <summary>
        /// Пересобирает окно при смене языка: подписи вкладок, флажков и кнопок
        /// создаются в коде через LocalizationManager.T, поэтому сами по себе
        /// они не переключаются. Базовая реализация обновляет только ОК и
        /// Отмену, а язык здесь применяется сразу при выборе в списке, и без
        /// пересборки окно оставалось наполовину на прежнем языке.
        /// Введённые, но не сохранённые значения при этом читаются заново
        /// из настроек: сменить язык посреди правки означает начать её заново.
        /// </summary>
        protected override void OnLanguageChanged(object? sender, EventArgs e)
        {
            base.OnLanguageChanged(sender, e);

            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                Content = BuildRoot();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Content = BuildRoot());
        }

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Вид колонки вкладок задаёт тема из словаря; TabStripPlacement её
            // шаблон не читает, но свойство ставится, чтобы состояние контрола
            // отвечало фактическому расположению полосы, как в разметке.
            var tabs = new TabControl { TabStripPlacement = Dock.Left };
            tabs.Styled(ControlThemes.SettingsTabControl);
            _settingsTabs = tabs;

            // ===== Настройки =====
            // Общего зазора у панели нет: в Avalonia он складывается с полями
            // соседей, а поля взяты из разметки поштучно (SettingsWindow.xaml:1106
            // и далее), поэтому иначе каждый промежуток вырос бы на 6.
            var settings = new StackPanel();

            // Тема оформления. Редактируемая схема и колбэк обновления редактора объявляются
            // здесь, чтобы радиокнопки «Светлая/Тёмная» переключали базовую тему именно той
            // схемы, которую пользователь редактирует (и которая сохраняется по «Применить»).
            var editedScheme = _viewModel.ActiveColorScheme.Clone();
            System.Action? refreshEditedScheme = null;
            var themeLabel = new TextBlock { Text = LocalizationManager.T("Settings.ThemeLabel"), FontWeight = FontWeight.SemiBold };
            settings.Children.Add(themeLabel);
            var themePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            var lightTheme = new RadioButton { Content = LocalizationManager.T("Main.LightTheme"), GroupName = "Theme", IsChecked = ThemeManager.CurrentTheme != ThemeManager.DarkThemeName };
            var darkTheme = new RadioButton { Content = LocalizationManager.T("Main.DarkTheme"), GroupName = "Theme", IsChecked = ThemeManager.CurrentTheme == ThemeManager.DarkThemeName };
            ThemeChanged(lightTheme, darkTheme, _viewModel, theme =>
            {
                editedScheme = _viewModel.GetSchemeForTheme(theme);
                refreshEditedScheme?.Invoke();
            });
            themePanel.Children.Add(lightTheme);
            themePanel.Children.Add(darkTheme);
            settings.Children.Add(themePanel);

            // Язык интерфейса лежит в рамке с заголовком, отступом 8 и полем
            // снизу 12 (SettingsWindow.xaml:1088). Список 280 на 34, подпись
            // с полем 10 до него, пояснение с верхним отступом 8.
            var langRow = new StackPanel { Orientation = Orientation.Horizontal };
            langRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.LanguageLabel"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var langBox = new ComboBox
            {
                Width = 280,
                Height = 34,
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            langBox.ItemsSource = LocalizationManager.Instance.AvailableLanguages;
            langBox.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
            langBox.SelectedItem = LocalizationManager.Instance.AvailableLanguages
                .FirstOrDefault(l => l.Code == LocalizationManager.Instance.CurrentLanguage);
            // Язык применяется сразу при выборе (и сохраняется в настройках), чтобы
            // интерфейс перестраивался на новый язык без нажатия «OK».
            langBox.SelectionChanged += (_, _) =>
            {
                if (langBox.SelectedItem is LanguageInfo li &&
                    !string.Equals(li.Code, LocalizationManager.Instance.CurrentLanguage, StringComparison.Ordinal))
                {
                    _viewModel.ApplyLanguage(li.Code);
                }
            };
            langRow.Children.Add(langBox);

            var langContent = new StackPanel();
            langContent.Children.Add(langRow);
            var langHint = new TextBlock
            {
                Text = LocalizationManager.T("Settings.Language.AppliedHint"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            ThemeBrushes.Bind(langHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            langContent.Children.Add(langHint);

            settings.Children.Add(Controls.GroupBoxPanel.Build(
                "Settings.Language", langContent,
                margin: new Thickness(0, 0, 0, 12),
                padding: new Thickness(8)));

            // Режим функциональности (Этап 10 StartManager): «Пользователь»/«Специалист»/«Разработчик».
            // В режиме «Пользователь» ограничивается доступ к системному меню. Блок повторяет
            // разметку WPF (SettingsWindow.xaml:1450) и показывает пояснение по каждому режиму
            // (issue #263): разницу «Специалист»/«Разработчик» пользователь видит сразу при выборе.
            var functionalModeOptions = new List<FunctionalModeOption>
            {
                new FunctionalModeOption(LocalizationManager.T("FunctionalMode.User"), Models.FunctionalModes.User),
                new FunctionalModeOption(LocalizationManager.T("FunctionalMode.Specialist"), Models.FunctionalModes.Specialist),
                new FunctionalModeOption(LocalizationManager.T("FunctionalMode.Developer"), Models.FunctionalModes.Developer)
            };

            var functionalModeContent = new StackPanel();
            var functionalModeIntro = new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.FunctionalModeHint"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            ThemeBrushes.Bind(functionalModeIntro, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            functionalModeContent.Children.Add(functionalModeIntro);

            var functionalModeBox = new ComboBox
            {
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8)
            };
            functionalModeBox.ItemsSource = functionalModeOptions;
            functionalModeBox.DisplayMemberBinding = new Avalonia.Data.Binding("Label");
            functionalModeBox.SelectedItem = functionalModeOptions.FirstOrDefault(o =>
                string.Equals(o.Code, _viewModel.FunctionalMode, StringComparison.OrdinalIgnoreCase));

            var functionalModeHint = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            ThemeBrushes.Bind(functionalModeHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var launchConfigHint = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            ThemeBrushes.Bind(launchConfigHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            // Подсказка по выбранному режиму и по 1CLaunch.cfg/портативному режиму.
            void UpdateFunctionalModeHints()
            {
                functionalModeHint.Text = (functionalModeBox.SelectedItem as FunctionalModeOption)?.Code switch
                {
                    Models.FunctionalModes.User => LocalizationManager.T("FunctionalMode.UserHint"),
                    Models.FunctionalModes.Developer => LocalizationManager.T("FunctionalMode.DeveloperHint"),
                    _ => LocalizationManager.T("FunctionalMode.SpecialistHint")
                };
                var hasConfig = OneCLaunchConfigReader.FindConfigFile() != null;
                launchConfigHint.Text = hasConfig
                    ? LocalizationManager.T("FunctionalMode.LaunchConfigPresent")
                    : LocalizationManager.T("FunctionalMode.LaunchConfigAbsent");
                if (PortablePaths.IsPortable)
                    launchConfigHint.Text += " " + LocalizationManager.T("FunctionalMode.Portable");
            }

            functionalModeBox.SelectionChanged += (_, _) =>
            {
                if (functionalModeBox.SelectedItem is FunctionalModeOption opt)
                    _viewModel.FunctionalMode = opt.Code;
                UpdateFunctionalModeHints();
            };

            functionalModeContent.Children.Add(functionalModeBox);
            functionalModeContent.Children.Add(functionalModeHint);
            functionalModeContent.Children.Add(launchConfigHint);

            UpdateFunctionalModeHints();

            settings.Children.Add(Controls.GroupBoxPanel.Build(
                "FunctionalMode.Label", functionalModeContent,
                margin: new Thickness(0, 14, 0, 0),
                padding: new Thickness(8)));

            // Раздел поведения приложения: в разметке WPF (SettingsWindow.xaml:1104)
            // он начинается своим заголовком, а первым в нём идёт разрешение
            // нескольких экземпляров.
            settings.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.Behavior"),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // Несколько экземпляров: настройка лежит в общем с версией для
            // Windows файле и уже учитывается при запуске (App.axaml.cs),
            // но в Linux-сборке её нечем было изменить.
            var multipleInstancesCheck = SettingsSwitch("Settings.General.AllowMultipleInstances", _viewModel.AllowMultipleInstances, "IconApplicationOutline", "#3B82F6");
            multipleInstancesCheck.Margin = new Thickness(0, 0, 0, 6);
            settings.Children.Add(multipleInstancesCheck);

            // Обновление приложения: обе настройки учитываются при запуске
            // (App.axaml.cs), но в Linux-сборке их нечем было изменить. Цвет значка
            // взят из разметки Windows (SettingsWindow.xaml:1269, 1275); значка
            // Update в наборе Icons.axaml нет, поэтому стоит ближайший IconRefresh.
            var checkUpdatesCheck = SettingsSwitch("Settings.General.CheckForUpdatesOnStartup", _viewModel.CheckForUpdatesOnStartup, "IconRefresh", "#22C55E", "Settings.General.CheckForUpdatesOnStartupTooltip");
            var autoUpdateCheck = SettingsSwitch("Settings.General.AutoUpdate", _viewModel.AutoUpdateEnabled, "IconRefresh", "#22C55E", "Settings.General.AutoUpdateTooltip");
            checkUpdatesCheck.Margin = new Thickness(0, 0, 0, 6);
            autoUpdateCheck.Margin = new Thickness(0, 0, 0, 6);
            settings.Children.Add(checkUpdatesCheck);
            settings.Children.Add(autoUpdateCheck);

            // Авторизация на сайте 1С при проверке обновлений конфигураций (HTTP Basic Auth).
            var updatesAuthContent = new StackPanel();
            var updatesAuthHint = new TextBlock
            {
                Text = LocalizationManager.T("Updates.AuthHint"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            ThemeBrushes.Bind(updatesAuthHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            updatesAuthContent.Children.Add(updatesAuthHint);

            var loginRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            loginRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Updates.Login"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 80
            });
            var updatesLoginBox = new TextBox
            {
                Text = _viewModel.UpdatesLogin,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch
            }.Styled(ControlThemes.ModernTextBox);
            ToolTip.SetTip(updatesLoginBox, new TextBlock
            {
                Text = LocalizationManager.T("Updates.AuthHint"),
                MaxWidth = 320,
                TextWrapping = TextWrapping.Wrap
            });
            loginRow.Children.Add(updatesLoginBox);

            var passwordRow = new StackPanel { Orientation = Orientation.Horizontal };
            passwordRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Updates.Password"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 80
            });
            var updatesPasswordBox = new PasswordBox
            {
                Password = _viewModel.UpdatesPassword,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ToolTip.SetTip(updatesPasswordBox, new TextBlock
            {
                Text = LocalizationManager.T("Updates.AuthHint"),
                MaxWidth = 320,
                TextWrapping = TextWrapping.Wrap
            });
            passwordRow.Children.Add(updatesPasswordBox);

            updatesAuthContent.Children.Add(loginRow);
            updatesAuthContent.Children.Add(passwordRow);

            settings.Children.Add(Controls.GroupBoxPanel.Build(
                "Updates.AuthGroupTitle", updatesAuthContent,
                margin: new Thickness(0, 14, 0, 0),
                padding: new Thickness(8)));

            // Поведение значка в области уведомлений. До этого три настройки
            // жили только в файле и в версии для Windows: в Linux-сборке ни
            // флажков, ни учёта не было.
            var trayIconCheck = SettingsSwitch("Settings.General.ShowTrayIcon", _viewModel.ShowTrayIcon, "IconTrayFull", "#14B8A6");
            var closeToTrayCheck = SettingsSwitch("Settings.General.CloseToTray", _viewModel.CloseToTray, "IconWindowMinimize", "#F59E0B");
            var escapeToTrayCheck = SettingsSwitch("Settings.General.EscapeToTray", _viewModel.EscapeToTray, "IconKeyboard", "#8B5CF6");
            trayIconCheck.Margin = new Thickness(0, 0, 0, 6);
            closeToTrayCheck.Margin = new Thickness(0, 0, 0, 6);
            escapeToTrayCheck.Margin = new Thickness(0, 0, 0, 6);
            settings.Children.Add(trayIconCheck);
            settings.Children.Add(closeToTrayCheck);
            settings.Children.Add(escapeToTrayCheck);


            // Параметры текущей сессии
            settings.Children.Add(new TextBlock { Text = LocalizationManager.T("Settings.DefaultClientLabel"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            // Пять вариантов клиента в строку не помещаются в окно, поэтому переносятся.
            var clientPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            clientPanel.Children.Add(Radio("SessionClient", "IsSessionClientAuto", LocalizationManager.T("Main.SessionClientAuto")));
            clientPanel.Children.Add(Radio("SessionClient", "IsSessionClientThin", LocalizationManager.T("Main.SessionClientThin")));
            clientPanel.Children.Add(Radio("SessionClient", "IsSessionClientThick", LocalizationManager.T("Main.SessionClientThickManaged")));
            clientPanel.Children.Add(Radio("SessionClient", "IsSessionClientOrdinary", LocalizationManager.T("Main.SessionClientOrdinary")));
            settings.Children.Add(clientPanel);

            settings.Children.Add(new TextBlock { Text = LocalizationManager.T("Settings.DefaultArch"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            var archPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            archPanel.Children.Add(Radio("SessionArch", "IsSessionArchAuto", LocalizationManager.T("Main.SessionClientAuto")));
            archPanel.Children.Add(Radio("SessionArch", "IsSessionArch32", "32"));
            archPanel.Children.Add(Radio("SessionArch", "IsSessionArch64", "64"));
            settings.Children.Add(archPanel);

            // Действие после запуска идёт одной строкой: значок, подпись
            // с полем 10 и список 230 на 30 (SettingsWindow.xaml:1131-1136).
            var afterLaunchRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 6)
            };
            var afterLaunchIcon = IconHelper.MakeIcon("IconRocketLaunch", 16, new SolidColorBrush(Color.Parse("#22C55E")));
            afterLaunchIcon.Margin = new Thickness(0, 0, 8, 0);
            afterLaunchIcon.VerticalAlignment = VerticalAlignment.Center;
            afterLaunchRow.Children.Add(afterLaunchIcon);
            afterLaunchRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.AfterLaunchAction"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            var afterLaunchBox = new ComboBox { Width = 230, Height = 30 };
            afterLaunchBox.ItemsSource = new[]
            {
                LocalizationManager.T("Settings.General.AfterLaunchAction.None"),
                LocalizationManager.T("Settings.General.AfterLaunchAction.Minimize"),
                LocalizationManager.T("Settings.General.AfterLaunchAction.MinimizeToTray"),
                LocalizationManager.T("Settings.General.AfterLaunchAction.Close")
            };
            afterLaunchBox.SelectedIndex = (int)Models.AfterLaunchActionHelper.Parse(_viewModel.AfterLaunchAction);
            afterLaunchRow.Children.Add(afterLaunchBox);
            settings.Children.Add(afterLaunchRow);

            // Запоминание геометрии окна. Значения лежали в общем файле настроек,
            // но Linux-сборка их не читала и не писала вовсе.
            var rememberLayoutCheck = SettingsSwitch("Settings.General.RememberWindowLayout", _viewModel.RememberWindowLayout, "IconMonitor", "#EC4899");
            // У автора у этой строки своего отступа нет (SettingsWindow.xaml:1139).
            rememberLayoutCheck.Margin = new Thickness(0);
            settings.Children.Add(rememberLayoutCheck);

            // Компактный режим интерфейса.
            var compactToggle = SettingsSwitch("Settings.CompactMode", _viewModel.CompactMode, "IconCompress", "#22C55E");
            // Отступ сверху из разметки (SettingsWindow.xaml:1144).
            compactToggle.Margin = new Thickness(0, 12, 0, 0);
            compactToggle.IsCheckedChanged += (_, _) =>
            {
                var value = compactToggle.IsChecked == true;
                _viewModel.CompactMode = value;
                _viewModel.ApplyCompactMode(value);
            };
            settings.Children.Add(compactToggle);

            // Имя COM-коннектора 1С по шаблону версии платформы (issue #175).
            var comTemplateHint = new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.ComConnectorTemplateHint"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 6)
            };
            ThemeBrushes.Bind(comTemplateHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            settings.Children.Add(comTemplateHint);

            var comTemplateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            comTemplateRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.ComConnectorTemplate"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            // Пояснение пункта вынесено под знак «?» рядом с заголовком (как в WPF).
            comTemplateRow.Children.Add(new HelpLink
            {
                HelpText = LocalizationManager.T("Settings.General.ComConnectorTemplateTooltip"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            var comTemplateBox = new TextBox
            {
                Text = _viewModel.ComConnectorNameTemplate,
                Width = 280,
                Height = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // Placeholder с примером шаблона по умолчанию (issue #175): при пустом поле
                // видно, какая строка берётся, если настройка не задана. Пустое значение
                // означает стандартные ProgID (V85/V83/V82/V81.COMConnector), а развёрнутый
                // по версии платформы «V%V12%.ComConnector» даёт V83.COMConnector для 8.3.
                Watermark = "V%V12%.ComConnector"
            }.Styled(ControlThemes.ModernTextBox);
            comTemplateRow.Children.Add(comTemplateBox);
            settings.Children.Add(comTemplateRow);

            // Интерактивный предпросмотр имени COM-коннектора (issue #175):
            // редактируемая версия + результат разворота шаблона. Поле версии
            // исключительно для предпросмотра, в настройки не сохраняется.
            var comPreviewRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            comPreviewRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.ComConnectorPreviewVersionLabel"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            var previewVersionBox = new TextBox
            {
                Text = "8.3.45.6789",
                Width = 120,
                Height = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            }.Styled(ControlThemes.ModernTextBox);
            comPreviewRow.Children.Add(previewVersionBox);
            comPreviewRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.ComConnectorPreviewResultLabel"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 10, 0)
            });
            var previewResultBox = new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.ComConnectorPreviewEmpty"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            comPreviewRow.Children.Add(previewResultBox);
            settings.Children.Add(comPreviewRow);

            void UpdateComConnectorPreview()
            {
                var result = ComConnectorTemplate.Expand(comTemplateBox.Text, previewVersionBox.Text);
                previewResultBox.Text = result ?? LocalizationManager.T("Settings.General.ComConnectorPreviewEmpty");
            }

            comTemplateBox.TextChanged += (_, _) => UpdateComConnectorPreview();
            previewVersionBox.TextChanged += (_, _) => UpdateComConnectorPreview();
            UpdateComConnectorPreview();

            // Таймаут определения свойств конфигурации через COM (issue #174).
            var detectTimeoutHint = new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.ComDetectTimeoutHint"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 6)
            };
            ThemeBrushes.Bind(detectTimeoutHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            settings.Children.Add(detectTimeoutHint);

            var detectTimeoutRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            detectTimeoutRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.ComDetectTimeout"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            // Пояснение пункта вынесено под знак «?» рядом с заголовком (как в WPF).
            detectTimeoutRow.Children.Add(new HelpLink
            {
                HelpText = LocalizationManager.T("Settings.General.ComDetectTimeoutTooltip"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            var detectTimeoutBox = new TextBox
            {
                Text = _viewModel.ComDetectTimeoutMs.ToString(),
                Width = 120,
                Height = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            }.Styled(ControlThemes.ModernTextBox);
            detectTimeoutRow.Children.Add(detectTimeoutBox);
            detectTimeoutRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.General.ComDetectTimeoutUnit"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            settings.Children.Add(detectTimeoutRow);

            // Управление учётными записями (профилями).
            // Кнопка учётных записей: значок и тема из разметки
            // (SettingsWindow.xaml:1151-1157).
            var profilesIcon = IconHelper.MakeIcon("IconAccountMultiple", 16, new SolidColorBrush(Color.Parse("#3B82F6")));
            profilesIcon.Margin = new Thickness(0, 0, 8, 0);
            profilesIcon.VerticalAlignment = VerticalAlignment.Center;
            var manageProfilesButton = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        profilesIcon,
                        new TextBlock
                        {
                            Text = LocalizationManager.T("Settings.General.ManageProfiles"),
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                },
                HorizontalAlignment = HorizontalAlignment.Left,
                // Числа из разметки (SettingsWindow.xaml:1151).
                Width = 250,
                Height = 36,
                Margin = new Thickness(0, 18, 0, 0)
            };
            manageProfilesButton.Styled(ControlThemes.ModernButton);
            manageProfilesButton.Click += (_, _) =>
            {
                var profiles = AppServices.GetRequiredService<IProfileService>();
                new ProfilesWindow(profiles).ShowDialogSync(this);
            };
            settings.Children.Add(manageProfilesButton);

            var tabGeneral = MainTab("IconApplicationCog", "Settings.TabGeneral",
                new ScrollViewer
                {
                    Content = settings,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    // Отступы прокрутки из разметки (SettingsWindow.xaml:1085).
                    Margin = new Thickness(4, 12, 4, 0),
                    Padding = new Thickness(0, 0, 4, 0)
                });

            // ===== Платформы =====
            // Раздел собран гридом из пяти строк, как в разметке
            // (SettingsWindow.xaml:302-310), а не панелью с общим зазором:
            // строка дерева тянется, остальные идут по содержимому, поэтому
            // дерево прокручивается внутри себя, а карточки ниже остаются
            // на экране при любом числе найденных версий.
            var platforms = new Grid { Margin = new Thickness(4, 12, 4, 0) };
            platforms.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            platforms.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            platforms.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            platforms.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            platforms.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Кегль вводной строки в разметке не задан, то есть берётся оконный
            // 13 (SettingsWindow.xaml:21), а не 12 общего пояснения.
            var platformsIntro = new TextBlock
            {
                Text = LocalizationManager.T("Settings.Platforms.Intro"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            ThemeBrushes.Bind(platformsIntro, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            platforms.Children.Add(platformsIntro);

            // Дерево вместо плоского списка, как в разметке (SettingsWindow.xaml:322):
            // линия 8.3, группа сборок 8.3.27, сама сборка с путём под именем.
            var versionsTree = new TreeView
            {
                MinHeight = 180,
                SelectionMode = SelectionMode.Single,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4)
            };
            ThemeBrushes.Bind(versionsTree, TemplatedControl.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(versionsTree, TemplatedControl.BorderBrushProperty, "BorderColorBrush");
            versionsTree.ItemTemplate = new FuncTreeDataTemplate(
                typeof(object),
                (item, _) => BuildPlatformRow(item),
                item => item is PlatformVersionGroup group && group.Children.Count > 0 ? group.Children : Array.Empty<PlatformVersionGroup>());
            // Дерево раскрыто целиком, как задаёт ItemContainerStyle разметки
            // (SettingsWindow.xaml:386): группировка видна сразу, а свернуть узел
            // вручную по-прежнему можно.
            versionsTree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
            {
                Setters = { new Setter(TreeViewItem.IsExpandedProperty, true) }
            });
            if (Application.Current?.TryFindResource(ControlThemes.ModernTreeItem, out var platformItemTheme) == true
                && platformItemTheme is ControlTheme platformTreeItemTheme)
            {
                versionsTree.ItemContainerTheme = platformTreeItemTheme;
            }
            ToolTip.SetTip(versionsTree, LocalizationManager.T("Settings.Platforms.TreeTooltip"));

            // Строка состояния под деревом, как в разметке (SettingsWindow.xaml:400):
            // число найденных версий, а при пустом дереве пояснение вместо него.
            var versionsStatus = Hint(string.Empty, bottom: 12);

            var pathsList = new ListBox
            {
                MinHeight = 80,
                MaxHeight = 140,
                BorderThickness = new Thickness(1)
            };
            // Фон и рамка списка из стиля ListBox в темах автора
            // (DarkTheme.xaml:853): штатный фон Avalonia заметно темнее карточки.
            ThemeBrushes.Bind(pathsList, TemplatedControl.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(pathsList, TemplatedControl.BorderBrushProperty, "BorderColorBrush");
            // Обе полосы прокрутки Auto, как в стиле ListBox тем автора
            // (DarkTheme.xaml:860): без горизонтальной длинный путь обрезается
            // по правому краю и хвост не прочитать.
            ScrollViewer.SetHorizontalScrollBarVisibility(pathsList, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(pathsList, ScrollBarVisibility.Auto);
            ToolTip.SetTip(pathsList, LocalizationManager.T("Settings.AdditionalPaths.ListTooltip"));
            // Наблюдаемый список: список сам обновляется и не теряет выделение
            // с прокруткой, как было бы при подмене ItemsSource.
            var paths = new ObservableCollection<string>(_viewModel.AdditionalPlatformSearchPaths);
            pathsList.ItemsSource = paths;

            void RefreshVersions()
            {
                var infos = PlatformVersionService.FindInstalledVersionInfos(paths);
                versionsTree.ItemsSource = PlatformVersionService.BuildGroupedTree(infos);
                // Пустое дерево без пояснения выглядит как поломка, поэтому
                // показываем ту же подсказку, что и WPF-версия.
                versionsStatus.Text = infos.Count == 0
                    ? LocalizationManager.T("Settings.PlatformsNotFound")
                    : string.Format(LocalizationManager.T("Settings.PlatformsFound"), infos.Count);
            }

            RefreshVersions();

            // Кнопка обновления стоит справа от дерева и прижата к его верху
            // (SettingsWindow.xaml:389), а не под ним.
            var refreshButton = new Button
            {
                Content = IconTextContent("IconRefresh", "#3B82F6", "Settings.Platforms.Refresh"),
                Padding = new Thickness(10, 6),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            refreshButton.Styled(ControlThemes.SecondaryButton);
            ToolTip.SetTip(refreshButton, LocalizationManager.T("Settings.Platforms.RefreshTooltip"));
            refreshButton.Click += (_, _) => RefreshVersions();

            var versionsRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            versionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            versionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(refreshButton, 1);
            versionsRow.Children.Add(versionsTree);
            versionsRow.Children.Add(refreshButton);
            Grid.SetRow(versionsRow, 1);
            platforms.Children.Add(versionsRow);
            Grid.SetRow(versionsStatus, 2);
            platforms.Children.Add(versionsStatus);

            var pathButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var addPath = new Button
            {
                Content = IconTextContent("IconFolderPlus", "#22C55E", "Settings.AdditionalPaths.Add"),
                Padding = new Thickness(10, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            addPath.Styled(ControlThemes.SecondaryButton);
            ToolTip.SetTip(addPath, LocalizationManager.T("Settings.AdditionalPaths.AddTooltip"));
            addPath.Click += (_, _) =>
            {
                var folder = _viewModel.PickFolder(LocalizationManager.T("Settings.AdditionalPaths.Add"))?.Trim();
                if (string.IsNullOrWhiteSpace(folder))
                    return;
                // На дубле WPF показывает предупреждение (SettingsWindow.Platforms.cs:56),
                // иначе кнопка выглядит нерабочей.
                if (paths.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    _viewModel.ShowInfo(LocalizationManager.T("Settings.PathAlreadyAdded"),
                        LocalizationManager.T("Settings.AdditionalPathsTitle"));
                    return;
                }
                paths.Add(folder);
                // Дерево версий пересчитывается сразу, как в WPF-версии.
                RefreshVersions();
            };
            // Кнопка «Изменить» из разметки WPF (SettingsWindow.xaml:434). Поведение
            // повторяет OnEditPlatformPath_Click целиком, включая оба сообщения:
            // без них при пустом выделении кнопка выглядит нерабочей, а на дубле
            // строка молча исчезала бы вместо предупреждения.
            var editPath = new Button
            {
                Content = IconTextContent("IconFolderEdit", "#F59E0B", "Common.Edit"),
                Padding = new Thickness(10, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            editPath.Styled(ControlThemes.SecondaryButton);
            ToolTip.SetTip(editPath, LocalizationManager.T("Settings.AdditionalPaths.EditTooltip"));
            editPath.Click += (_, _) =>
            {
                if (pathsList.SelectedItem is not string selected || string.IsNullOrEmpty(selected))
                {
                    _viewModel.ShowInfo(LocalizationManager.T("Settings.SelectPathToEdit"),
                        LocalizationManager.T("Settings.AdditionalPathsTitle"));
                    return;
                }

                // Диалог открывается на редактируемом каталоге, как в версии
                // для Windows: иначе искать приходится с начала.
                var folder = _viewModel.PickFolder(
                    LocalizationManager.T("Settings.ChooseNewPlatformFolder"), selected)?.Trim();
                if (string.IsNullOrWhiteSpace(folder)
                    || string.Equals(folder, selected, StringComparison.OrdinalIgnoreCase))
                    return;

                if (paths.Any(existing => !string.Equals(existing, selected, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(existing, folder, StringComparison.OrdinalIgnoreCase)))
                {
                    _viewModel.ShowInfo(LocalizationManager.T("Settings.PathAlreadyAdded"),
                        LocalizationManager.T("Settings.AdditionalPathsTitle"));
                    return;
                }

                var index = paths.IndexOf(selected);
                if (index < 0)
                    return;
                paths[index] = folder;
                pathsList.SelectedItem = folder;
                RefreshVersions();
            };

            var removePath = new Button
            {
                Content = IconTextContent("IconFolderRemove", "#EF4444", "Common.Delete"),
                Padding = new Thickness(10, 6)
            };
            removePath.Styled(ControlThemes.SecondaryButton);
            ToolTip.SetTip(removePath, LocalizationManager.T("Settings.AdditionalPaths.RemoveTooltip"));
            removePath.Click += (_, _) =>
            {
                if (pathsList.SelectedItem is not string selected)
                {
                    _viewModel.ShowInfo(LocalizationManager.T("Settings.SelectPathToRemove"),
                        LocalizationManager.T("Settings.AdditionalPathsTitle"));
                    return;
                }
                paths.Remove(selected);
                RefreshVersions();
            };
            pathButtons.Children.Add(addPath);
            pathButtons.Children.Add(editPath);
            pathButtons.Children.Add(removePath);

            var pathsBody = new StackPanel();
            pathsBody.Children.Add(Hint(LocalizationManager.T("Settings.AdditionalPaths.HintLinux"), bottom: 8));
            pathsBody.Children.Add(pathsList);
            pathsBody.Children.Add(pathButtons);
            var pathsGroup = SettingsGroup(LocalizationManager.T("Settings.AdditionalPaths"),
                pathsBody, new Thickness(10, 8), bottom: 8);
            Grid.SetRow(pathsGroup, 3);
            platforms.Children.Add(pathsGroup);

            // Разрядность: подпись и список в одну строку, как в разметке
            // (SettingsWindow.xaml:458), а не подпись над списком.
            var archRow = new Grid();
            archRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            archRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var archLabel = new TextBlock
            {
                Text = LocalizationManager.T("Settings.DefaultArch.Hint"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var archBox = new ComboBox
            {
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            // Подписи локализованные, как в WPF (SettingsWindow.Display.cs:35),
            // а наружу по-прежнему уходит режим по номеру строки: X64 / X86 / Priority.
            archBox.ItemsSource = new[]
            {
                LocalizationManager.T("Settings.Arch64Recommended"),
                LocalizationManager.T("Settings.Arch32"),
                LocalizationManager.T("Settings.ArchBasePriority")
            };
            archBox.SelectedIndex =
                string.Equals(_viewModel.DefaultArchitecture, "X86", StringComparison.OrdinalIgnoreCase) ? 1
                : string.Equals(_viewModel.DefaultArchitecture, "Priority", StringComparison.OrdinalIgnoreCase) ? 2
                : 0;
            Grid.SetColumn(archBox, 1);
            archRow.Children.Add(archLabel);
            archRow.Children.Add(archBox);
            var archGroup = SettingsGroup(LocalizationManager.T("Settings.DefaultArch"),
                archRow, new Thickness(10, 8), bottom: 8);
            Grid.SetRow(archGroup, 4);
            platforms.Children.Add(archGroup);

            var tabPlatforms = MainTab("IconServer", "Settings.TabPlatforms", platforms);

            // ===== Отображение =====
            // Общего зазора у панелей нет: он складывается с полями детей,
            // а поля взяты из разметки поштучно (SettingsWindow.xaml:495 и далее).
            var displayIcons = new StackPanel();
            var displayColumns = new StackPanel();
            var displayPanels = new StackPanel();
            var displayStatus = new StackPanel();
            var displayFont = new StackPanel();

            displayIcons.Children.Add(Hint(LocalizationManager.T("Settings.Icons.Description"), bottom: 10));
            // Значки и их цвета из разметки (SettingsWindow.xaml:498-517).
            var favoritesCheck = DisplayCheck("Settings.Icons.FavoritesButton", _viewModel.ShowFavoritesButton, "IconStar", "#FBBF24");
            var pinnedCheck = DisplayCheck("Settings.Icons.PinButton", _viewModel.ShowPinnedButton, "IconPin", "#F59E0B");
            var tagsCheck = DisplayCheck("Settings.Icons.Tags", _viewModel.ShowTags, "IconTag", "#EC4899");
            var tagPanelCheck = DisplayCheck("Settings.Icons.TagFilterPanel", _viewModel.ShowTagFilterPanel, "IconFilter", "#EC4899");
            foreach (var check in new[] { favoritesCheck, pinnedCheck, tagsCheck, tagPanelCheck })
                displayIcons.Children.Add(check);

            // Видимость и порядок колонок редактируются в одном списке: у каждой
            // строки есть флажок видимости, а порядок задаётся кнопками «Вверх»/«Вниз»
            // по выбранной строке. Так не нужно держать две раздельные группы настроек.
            displayColumns.Children.Add(Hint(LocalizationManager.T("Settings.Columns.Description"), bottom: 10));
            // Заголовок порядка колонок и его пояснение по числам разметки
            // (SettingsWindow.xaml:544-548).
            var orderTitle = GroupTitle(LocalizationManager.T("Settings.Columns.OrderTitle"));
            orderTitle.FontSize = 14;
            orderTitle.Margin = new Thickness(0, 12, 0, 4);
            displayColumns.Children.Add(orderTitle);
            displayColumns.Children.Add(Hint(LocalizationManager.T("Settings.Columns.OrderHint"), bottom: 10));

            static string ColumnOrderLabel(string key) => LocalizationManager.T(key switch
            {
                "Version" => "Column.Version",
                "Configuration" => "Column.Configuration",
                "ConfigurationVersion" => "Column.ConfigurationVersion",
                "LaunchMode" => "Column.LaunchMode",
                "ServerBase" => "Column.ServerBase",
                "LastLaunch" => "Column.LastLaunch",
                "Size" => "Column.Size",
                "Modified" => "Column.Modified",
                "Actions" => "Column.Actions",
                _ => "Column.Name"
            });

            bool ColumnVisible(string key) => key switch
            {
                "Version" => _viewModel.ShowVersionColumn,
                "Configuration" => _viewModel.ShowConfigurationColumn,
                "ConfigurationVersion" => _viewModel.ShowConfigurationVersionColumn,
                "LaunchMode" => _viewModel.ShowLaunchModeColumn,
                "ServerBase" => _viewModel.ShowServerColumn,
                "LastLaunch" => _viewModel.ShowLastLaunchColumn,
                "Size" => _viewModel.ShowSizeColumn,
                "Modified" => _viewModel.ShowModifiedColumn,
                "Actions" => _viewModel.ShowActionsColumn,
                _ => true
            };

            var orderItems = new ObservableCollection<ColumnOrderItem>(
                _viewModel.ColumnOrderKeys.Select(k => new ColumnOrderItem(k, ColumnOrderLabel(k), ColumnVisible(k), IconHelper.ColumnIconKey(k))));
            var orderList = new ListBox
            {
                ItemsSource = orderItems,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                MinHeight = UiMetrics.Scaled(180),
                MaxHeight = UiMetrics.Scaled(240),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // Каждая строка — флажок видимости с именем колонки; переключение
                // правит только видимость, порядок меняется кнопками ниже. Иконка
                // колонки совпадает с иконкой заголовка списка баз.
                ItemTemplate = new FuncDataTemplate<ColumnOrderItem>((item, _) =>
                {
                    // Переработка контейнеров виртуализацией строит шаблон с null:
                    // ClearContainerForItemOverride сбрасывает Content, и ContentPresenter
                    // зовёт шаблон ещё раз уже без данных.
                    if (item is null)
                        return new Control();

                    var content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    // Размер значка из разметки (SettingsWindow.xaml:577).
                    content.Children.Add(IconHelper.MakeIcon(item.IconKey, 16, "TextSecondaryBrush"));
                    var label = new TextBlock
                    {
                        Text = item.Display,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    Themes.ThemeBrushes.Bind(label, TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    content.Children.Add(label);
                    ToolTip.SetTip(content, LocalizationManager.T("Settings.Columns.RowSelectHint"));

                    // Пилюля-переключатель стиля ColumnVisibilitySwitch разметки
                    // (SettingsWindow.xaml:31): дорожка без подписи.
                    var check = new ToggleButton();
                    check.Styled(ControlThemes.ColumnVisibilitySwitch);
                    check.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,
                        new Avalonia.Data.Binding(nameof(ColumnOrderItem.Visible))
                        { Mode = Avalonia.Data.BindingMode.TwoWay });

                    var row = new Grid { Margin = new Thickness(4, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                    row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                    Grid.SetColumn(content, 0);
                    Grid.SetColumn(check, 1);
                    row.Children.Add(content);
                    row.Children.Add(check);
                    return row;
                })
            };
            // Колонка «Название» закреплена и всегда первая, её тумблер неактивен.
            // В разметке WPF она стоит отдельной строкой над списком, за ней
            // разделитель, и всё вместе лежит в карточке.
            var nameRowContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            // Значок закреплённой строки «Название» акцентно-синий, как в разметке
            // (SettingsWindow.xaml:556), и того же размера 16.
            nameRowContent.Children.Add(IconHelper.MakeIcon(IconHelper.ColumnIconKey("Name"), 16,
                new SolidColorBrush(Color.Parse("#3B82F6"))));
            var nameRowLabel = new TextBlock
            {
                Text = LocalizationManager.T("Column.Name"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Themes.ThemeBrushes.Bind(nameRowLabel, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            nameRowContent.Children.Add(nameRowLabel);

            var nameRow = new Grid { Margin = new Thickness(4, 3) };
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            // Строка «Название» закреплена: переключатель показан отмеченным
            // и недоступен, стиль тот же (SettingsWindow.xaml:554).
            var nameRowSwitch = new ToggleButton
            {
                IsChecked = true,
                IsEnabled = false
            };
            nameRowSwitch.Styled(ControlThemes.ColumnVisibilitySwitch);
            Grid.SetColumn(nameRowContent, 0);
            Grid.SetColumn(nameRowSwitch, 1);
            nameRow.Children.Add(nameRowContent);
            nameRow.Children.Add(nameRowSwitch);

            var orderCard = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6)
            };
            Themes.ThemeBrushes.Bind(orderCard, Border.BackgroundProperty, "CardBackgroundBrush");
            Themes.ThemeBrushes.Bind(orderCard, Border.BorderBrushProperty, "BorderColorBrush");
            var orderCardBody = new StackPanel();
            orderCardBody.Children.Add(nameRow);
            orderCardBody.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 4) });
            orderCardBody.Children.Add(orderList);
            orderCard.Child = orderCardBody;

            // Список колонок и кнопки перестановки справа — та же сетка, что
            // во вкладке «Клавиши» для избранного: список по ширине, справа
            // узкая колонка с вертикально расположенными кнопками «Вверх»/«Вниз».
            var orderGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            orderGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            orderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(orderCard, 0);
            orderGrid.Children.Add(orderCard);

            var orderButtons = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 6,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            var moveUp = new Button { Content = "\u2191", IsEnabled = false };
            ToolTip.SetTip(moveUp, LocalizationManager.T("Settings.Columns.MoveUpTooltip"));
            var moveDown = new Button { Content = "\u2193", IsEnabled = false };
            ToolTip.SetTip(moveDown, LocalizationManager.T("Settings.Columns.MoveDownTooltip"));
            void UpdateOrderButtons()
            {
                var idx = orderList.SelectedIndex;
                moveUp.IsEnabled = idx > 0;
                moveDown.IsEnabled = idx >= 0 && idx < orderItems.Count - 1;
            }
            // В разметке у этого списка свой ItemContainerStyle без BasedOn
            // (SettingsWindow.xaml:565-570), то есть тема строки там отключена и
            // работает штатный контейнер WPF. Дословно повторить это нельзя:
            // штатный контейнер Avalonia красит выбранную строку акцентом,
            // и замер даёт оранжевый 210,151,12 против синего 32,69,97 на снимке
            // Windows, тогда как ModernListBoxItem даёт 30,58,95. Механизм
            // расходится, вид совпадает, поэтому тема оставлена.
            // Отступы контейнера строки из разметки (SettingsWindow.xaml:566-570):
            // у штатной темы Avalonia они заметно больше, и карточка растёт.
            orderList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(ListBoxItem.PaddingProperty, new Thickness(4, 2)),
                    new Setter(ListBoxItem.MinHeightProperty, 0d),
                    new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
                }
            });
            orderList.SelectionChanged += (_, _) => UpdateOrderButtons();

            moveUp.Click += (_, _) =>
            {
                var idx = orderList.SelectedIndex;
                if (idx <= 0) return;
                orderItems.Move(idx, idx - 1);
                orderList.SelectedIndex = idx - 1;
                UpdateOrderButtons();
            };
            moveDown.Click += (_, _) =>
            {
                var idx = orderList.SelectedIndex;
                if (idx < 0 || idx >= orderItems.Count - 1) return;
                orderItems.Move(idx, idx + 1);
                orderList.SelectedIndex = idx + 1;
                UpdateOrderButtons();
            };
            orderButtons.Children.Add(moveUp);
            orderButtons.Children.Add(moveDown);
            Grid.SetColumn(orderButtons, 1);
            orderGrid.Children.Add(orderButtons);
            displayColumns.Children.Add(orderGrid);

            displayPanels.Children.Add(Hint(LocalizationManager.T("Settings.Panels.Description"), bottom: 10));
            var rightPanelCheck = DisplayCheck("Settings.Panels.RightPanelDetails", _viewModel.ShowRightPanelDetails, "IconPageLayoutSidebarRight", "#14B8A6");
            var sessionPanelCheck = DisplayCheck("Settings.Panels.SessionLaunchPanel", _viewModel.ShowSessionLaunchPanel, "IconMonitor", "#8B5CF6");
            var groupByGroupCheck = DisplayCheck("Settings.Panels.GroupByGroups", _viewModel.GroupByGroup, "IconFolderMultiple", "#3B82F6");
            // Системный заголовок окна вместо собственного безрамкового (issue #152).
            var systemTitleBarCheck = DisplayCheck("Settings.SystemTitleBar", _viewModel.UseSystemTitleBar, "IconMonitoring", "#6366F1");
            // Режим списка «только избранные» тот же, что переключается кнопкой
            // в главном окне: флажок и кнопка меняют одно значение.
            var favoritesOnlyCheck = DisplayCheck("Settings.Panels.ShowFavoritesOnly", _viewModel.IsListModeFavorites, "IconStarCircle", "#FBBF24");
            var emptyGroupsCheck = DisplayCheck("Settings.Panels.ShowEmptyGroups", _viewModel.ShowEmptyGroups, "IconFolderOutline", "#0EA5E9");

            // Пояснения под переключателями стоят там же, где в разметке WPF
            // (SettingsWindow.xaml:628): у правой панели, у блока сессии
            // и у пустых групп. Ключи для них были в локализации, но не
            // использовались нигде.
            displayPanels.Children.Add(rightPanelCheck);
            var hintRightPanelDetailsHint = Hint(LocalizationManager.T("Settings.Panels.RightPanelDetailsHint"), bottom: 12);
            // Пояснение под переключателем сдвинуто на ширину переключателя
            // (SettingsWindow.xaml:636).
            hintRightPanelDetailsHint.Margin = new Thickness(24, 0, 0, 12);
            displayPanels.Children.Add(hintRightPanelDetailsHint);
            displayPanels.Children.Add(sessionPanelCheck);
            var hintSessionLaunchPanelHint = Hint(LocalizationManager.T("Settings.Panels.SessionLaunchPanelHint"), bottom: 12);
            // Пояснение под переключателем сдвинуто на ширину переключателя
            // (SettingsWindow.xaml:636).
            hintSessionLaunchPanelHint.Margin = new Thickness(24, 0, 0, 12);
            displayPanels.Children.Add(hintSessionLaunchPanelHint);
            displayPanels.Children.Add(groupByGroupCheck);
            // Пояснение к переключателю системного заголовка (issue #152).
            displayPanels.Children.Add(systemTitleBarCheck);
            displayPanels.Children.Add(favoritesOnlyCheck);
            displayPanels.Children.Add(emptyGroupsCheck);
            var hintShowEmptyGroupsHint = Hint(LocalizationManager.T("Settings.Panels.ShowEmptyGroupsHint"), bottom: 12);
            // Пояснение под переключателем сдвинуто на ширину переключателя
            // (SettingsWindow.xaml:636).
            hintShowEmptyGroupsHint.Margin = new Thickness(24, 0, 0, 12);
            displayPanels.Children.Add(hintShowEmptyGroupsHint);

            displayStatus.Children.Add(Hint(LocalizationManager.T("Settings.Status.Description"), bottom: 10));
            var statusPathCheck = DisplayCheck("Settings.Status.ConnectionPath", _viewModel.StatusShowConnectionPath, "IconFolderOutline", "#3B82F6");
            var statusPortCheck = DisplayCheck("Settings.Status.Port", _viewModel.StatusShowPort, "IconLan", "#6366F1");
            var statusArchCheck = DisplayCheck("Settings.Status.Architecture", _viewModel.StatusShowArchitecture, "IconChip", "#8B5CF6");
            var statusVersionCheck = DisplayCheck("Column.Version", _viewModel.StatusShowPlatformVersion, "IconCubeOutline", "#A855F7");
            var statusLaunchModeCheck = DisplayCheck("Column.LaunchMode", _viewModel.StatusShowLaunchMode, "IconPlayCircleOutline", "#22C55E");
            var statusClientTypeCheck = DisplayCheck("Settings.Status.ClientType", _viewModel.StatusShowClientType, "IconMonitor", "#EC4899");
            var statusConnectionTypeCheck = DisplayCheck("Settings.Status.ConnectionType", _viewModel.StatusShowConnectionType, "IconDatabase", "#6366F1");
            var statusUserCheck = DisplayCheck("Settings.Status.User", _viewModel.StatusShowUser, "IconAccount", "#94A3B8");
            var statusIdCheck = DisplayCheck("Settings.Status.Id", _viewModel.StatusShowId, "IconIdentifier", "#0EA5E9");
            foreach (var check in new[]
            {
                statusPathCheck, statusPortCheck, statusArchCheck, statusVersionCheck, statusLaunchModeCheck,
                statusClientTypeCheck, statusConnectionTypeCheck, statusUserCheck, statusIdCheck
            })
                displayStatus.Children.Add(check);

            // Подвкладка «Шрифт»: область интерфейса, семейство, размер, начертание,
            // образец и кнопка предпросмотра. Состав и порядок из SettingsWindow.xaml:752.
            var editedFonts = new Dictionary<string, ElementFontSettings>();
            foreach (var kv in _viewModel.ElementFonts)
                editedFonts[kv.Key] = kv.Value?.Clone() ?? new ElementFontSettings();
            if (!editedFonts.ContainsKey(ThemeManager.FontDefault))
                editedFonts[ThemeManager.FontDefault] = new ElementFontSettings
                {
                    FontFamily = _viewModel.FontFamily,
                    FontSize = _viewModel.FontSize,
                    FontWeight = _viewModel.FontWeight,
                    FontStyle = _viewModel.FontStyle
                };

            displayFont.Children.Add(Hint(LocalizationManager.T("Settings.Font.Description"), bottom: 12));
            var fontElementLabel = new TextBlock
            {
                Text = LocalizationManager.T("Settings.Font.Element"),
                Margin = new Thickness(0, 0, 0, 6)
            };
            Themes.ThemeBrushes.Bind(fontElementLabel, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            displayFont.Children.Add(fontElementLabel);

            // Числа из разметки (SettingsWindow.xaml:717): список области шрифта
            // высотой 34 с нижним отступом 12.
            var fontScopeBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 34,
                Margin = new Thickness(0, 0, 0, 12)
            };
            foreach (var key in ThemeManager.AllFontScopes)
                fontScopeBox.Items.Add(new FontScopeItem(key));
            fontScopeBox.SelectedIndex = 0;
            displayFont.Children.Add(fontScopeBox);

            var fontGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            fontGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160)));
            fontGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            for (var i = 0; i < 3; i++)
                fontGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var fontFamilyBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 34, Margin = new Thickness(0, 0, 0, 8) };
            // Первыми идут те же десять имён, что и у автора
            // (Views/SettingsWindow.Fonts.cs:53-60): настройка, сделанная
            // на Windows, должна открываться на Linux своим же значением.
            var authorFamilies = new[]
            {
                "Segoe UI", "Arial", "Calibri", "Tahoma", "Verdana",
                "Trebuchet MS", "Georgia", "Times New Roman", "Courier New", "Consolas"
            };
            foreach (var family in authorFamilies)
                fontFamilyBox.Items.Add(family);
            // Дальше установленные в системе. Без них список на Linux наполовину
            // мёртвый: четырёх имён автора здесь нет, а Skia подставляет вместо
            // них Noto Sans молча, без исключения и без записи в журнал, поэтому
            // выбор такого имени внешне не менял ничего.
            foreach (var family in InstalledFontFamilies(authorFamilies))
                fontFamilyBox.Items.Add(family);

            // Размер можно и выбрать из списка, и набрать руками: в разметке WPF
            // у этого списка стоит IsEditable, и в Avalonia он тоже есть.
            // Текст редактируемого поля центрируем по вертикали: без этого при
            // фиксированной высоте число прижимается к верху/низу (issue #216).
            var fontSizeBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 34,
                Margin = new Thickness(0, 0, 0, 8),
                IsEditable = true,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            // VerticalContentAlignment у ComboBox выравнивает только содержимое
            // в режиме «выбранное значение» (SelectionBoxItem). В редактируемом
            // режиме число рисует вложенный TextBox — часть шаблона
            // PART_EditableText, и у него собственное вертикальное выравнивание,
            // которое по умолчанию прижимает текст к краю (issue #216).
            // Стили-селекторы здесь не помогают: шаблонная часть не является
            // логическим потомком ComboBox, поэтому ни OfType/Descendant,
            // ни прежний селектор по WPF-имени PART_EditableTextBox не сработали
            // (фиксы 0.3.7.5 и 0.3.7.11). Надёжный способ — дождаться материализации
            // шаблона (TemplateApplied) и выставить выравнивание прямо на найденной
            // части как локальное значение: приоритет локального значения выше,
            // чем у темы и стилей, поэтому тема его не перекроет.
            fontSizeBox.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find("PART_EditableText") is TextBox edit)
                {
                    edit.VerticalContentAlignment = VerticalAlignment.Center;
                    // Вертикальный отступ Fluent-шаблона уводит число вниз при
                    // фиксированной высоте поля: обнуляем его, сохраняя боковые.
                    edit.Padding = new Thickness(edit.Padding.Left, 0, edit.Padding.Right, 0);
                }
            };
            foreach (var size in new double[]
            {
                8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24,
                26, 28, 32, 36, 40, 48, 56, 64, 72
            })
                fontSizeBox.Items.Add(size);

            var fontFaceBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 34 };
            foreach (var face in FontFaces)
                fontFaceBox.Items.Add(face);

            void AddFontRow(int row, string labelKey, Control editor)
            {
                var label = new TextBlock
                {
                    Text = LocalizationManager.T(labelKey),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, row < 2 ? 8 : 0)
                };
                Grid.SetRow(label, row);
                Grid.SetColumn(label, 0);
                Grid.SetRow(editor, row);
                Grid.SetColumn(editor, 1);
                fontGrid.Children.Add(label);
                fontGrid.Children.Add(editor);
            }

            AddFontRow(0, "Settings.Font.Family", fontFamilyBox);
            AddFontRow(1, "Settings.Font.Size", fontSizeBox);
            AddFontRow(2, "Settings.Font.Style", fontFaceBox);
            displayFont.Children.Add(fontGrid);

            var fontPreview = new TextBlock
            {
                Text = LocalizationManager.T("Settings.Font.Preview"),
                TextWrapping = TextWrapping.Wrap
            };
            Themes.ThemeBrushes.Bind(fontPreview, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var fontPreviewCard = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = fontPreview
            };
            Themes.ThemeBrushes.Bind(fontPreviewCard, Border.BackgroundProperty, "CardBackgroundBrush");
            Themes.ThemeBrushes.Bind(fontPreviewCard, Border.BorderBrushProperty, "BorderColorBrush");
            displayFont.Children.Add(fontPreviewCard);

            var fontApplyContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            fontApplyContent.Children.Add(IconHelper.MakeIcon("IconFormatFont", UiMetrics.Scaled(16), "ButtonTextBrush"));
            var fontApplyLabel = new TextBlock
            {
                Text = LocalizationManager.T("Common.Apply"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Themes.ThemeBrushes.Bind(fontApplyLabel, TextBlock.ForegroundProperty, "ButtonTextBrush");
            fontApplyContent.Children.Add(fontApplyLabel);
            var fontApply = new Button
            {
                Content = fontApplyContent,
                Padding = new Thickness(12, 6),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            // Тема кнопки целиком, а не только цвета: у автора здесь ModernButton
            // с его минимальной высотой и скруглением (SettingsWindow.xaml:800).
            fontApply.Styled(ControlThemes.ModernButton);
            ToolTip.SetTip(fontApply, LocalizationManager.T("Settings.Font.ApplyTooltip"));
            displayFont.Children.Add(fontApply);

            // Правки живут в editedFonts: переключение области их не теряет,
            // а «Сохранить» пишет весь набор разом.
            var suppressFontLoad = false;

            void StoreFontScope()
            {
                if (suppressFontLoad || fontScopeBox.SelectedItem is not FontScopeItem scope)
                    return;
                // Пока поля не заполнены загрузкой области, писать нечего:
                // иначе в набор уйдут значения по умолчанию вместо сохранённых.
                if (fontFamilyBox.SelectedItem is null && fontFaceBox.SelectedItem is null)
                    return;
                var face = fontFaceBox.SelectedItem as FontFaceItem ?? FontFaces[0];
                editedFonts[scope.Key] = new ElementFontSettings
                {
                    FontFamily = fontFamilyBox.SelectedItem as string ?? ThemeManager.DefaultFontFamily,
                    FontSize = SelectedFontSize(),
                    FontWeight = face.Weight,
                    FontStyle = face.Style
                };
                UpdateFontPreview();
            }

            void UpdateFontPreview()
            {
                var face = fontFaceBox.SelectedItem as FontFaceItem ?? FontFaces[0];
                fontPreview.FontFamily = new FontFamily(fontFamilyBox.SelectedItem as string ?? ThemeManager.DefaultFontFamily);
                fontPreview.FontSize = SelectedFontSize();
                fontPreview.FontWeight = string.Equals(face.Weight, "Bold", StringComparison.OrdinalIgnoreCase)
                    ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;
                fontPreview.FontStyle = string.Equals(face.Style, "Italic", StringComparison.OrdinalIgnoreCase)
                    ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal;
            }

            void LoadFontScope()
            {
                if (fontScopeBox.SelectedItem is not FontScopeItem scope)
                    return;
                if (!editedFonts.TryGetValue(scope.Key, out var fs) || fs is null)
                    fs = editedFonts.TryGetValue(ThemeManager.FontDefault, out var def) && def is not null
                        ? def.Clone()
                        : new ElementFontSettings();

                suppressFontLoad = true;
                var known = fontFamilyBox.Items.OfType<string>()
                    .FirstOrDefault(f => string.Equals(f, fs.FontFamily, StringComparison.OrdinalIgnoreCase));
                if (known is null && !string.IsNullOrWhiteSpace(fs.FontFamily))
                {
                    // Сохранённое семейство дописывается в список, а не подменяется
                    // умолчанием: перечислены десять шрифтов Windows, и любой
                    // системный шрифт Linux иначе потерялся бы при первом сохранении.
                    fontFamilyBox.Items.Insert(0, fs.FontFamily);
                    known = fs.FontFamily;
                }
                fontFamilyBox.SelectedItem = known ?? ThemeManager.DefaultFontFamily;
                var listed = fontSizeBox.Items.OfType<double>()
                    .FirstOrDefault(v => Math.Abs(v - fs.FontSize) < 0.01);
                if (listed > 0)
                    fontSizeBox.SelectedItem = listed;
                else
                {
                    // Размер задан вручную и в списке его нет: показываем текстом.
                    fontSizeBox.SelectedItem = null;
                    fontSizeBox.Text = fs.FontSize > 0
                        ? fs.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : ThemeManager.DefaultFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                fontFaceBox.SelectedItem = FontFaces.FirstOrDefault(x =>
                    string.Equals(x.Weight, fs.FontWeight, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Style, fs.FontStyle, StringComparison.OrdinalIgnoreCase)) ?? FontFaces[0];
                suppressFontLoad = false;

                UpdateFontPreview();
            }

            // Набранный руками размер приходит в Text, а не в SelectedItem.
            double SelectedFontSize()
            {
                if (fontSizeBox.SelectedItem is double picked && picked > 0)
                    return picked;
                if (double.TryParse((fontSizeBox.Text ?? string.Empty).Trim().Replace(',', '.'),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var typed)
                    && typed >= 6 && typed <= 96)
                    return typed;
                return ThemeManager.DefaultFontSize;
            }

            fontScopeBox.SelectionChanged += (_, _) => LoadFontScope();
            fontFamilyBox.SelectionChanged += (_, _) => StoreFontScope();
            fontSizeBox.SelectionChanged += (_, _) => StoreFontScope();
            fontFaceBox.SelectionChanged += (_, _) => StoreFontScope();
            fontApply.Click += (_, _) =>
            {
                StoreFontScope();
                _viewModel.PreviewElementFonts(editedFonts);
            };
            // Подписка оформляется до загрузки области, и это важно:
            // GetObservable отдаёт текущее значение прямо при подписке. Пока поля
            // пусты, StoreFontScope выходит сразу, а установки внутри самой
            // загрузки закрыты флагом suppressFontLoad. Если подписаться после
            // загрузки, немедленный вызов запишет в набор показанное значение
            // поверх сохранённого, и «Сохранить» унесёт его в настройки.
            fontSizeBox.GetObservable(ComboBox.TextProperty)
                .Subscribe(new SettingsObserver<string?>(_ => StoreFontScope()));

            LoadFontScope();

            // Раздел «Отображение» разложен по вложенным вкладкам, как в разметке WPF:
            // подвкладки «Значки», «Колонки», «Панели», «Статус» и «Шрифт».
            var displayTabs = new TabControl { Margin = new Thickness(0, 4, 0, 0) };
            displayTabs.Styled(ControlThemes.SettingsSubTabControl);
            _displaySubTabs = displayTabs;
            displayTabs.Items.Add(SubTab("Settings.Subtab.Icons", "Settings.Subtab.IconsTooltip", "IconStarOutline", displayIcons));
            displayTabs.Items.Add(SubTab("Settings.Subtab.Columns", "Settings.Subtab.ColumnsTooltip", "IconViewColumn", displayColumns));
            displayTabs.Items.Add(SubTab("Settings.Subtab.Panels", "Settings.Subtab.PanelsTooltip", "IconPageLayoutSidebarRight", displayPanels));
            displayTabs.Items.Add(SubTab("Settings.Subtab.Status", "Settings.Subtab.StatusTooltip", "IconDockBottom", displayStatus));
            displayTabs.Items.Add(SubTab("Settings.Subtab.Font", "Settings.Subtab.FontTooltip", "IconFormatFont", displayFont));

            var tabDisplay = MainTab("IconEye", "Settings.TabDisplay", displayTabs);
            _displayTab = tabDisplay;

            // ===== Оформление =====
            // Контейнер вкладки — Grid, заполняющий всю доступную высоту, чтобы правая
            // колонка со списком цветов могла прокручиваться внутри оставшейся высоты.
            var appearance = new Grid();
            // Две колонки: слева управление схемой и превью, справа список цветов
            // (превью — часть левой колонки под блоком схемы, по варианту 2 из #155).
            var schemeColumn = new StackPanel();
            var colorsColumn = new StackPanel();
            // Заголовок группы из разметки WPF (SettingsWindow.xaml:824).
            schemeColumn.Children.Add(GroupTitle(LocalizationManager.T("Settings.Theme")));

            // Правки идут по копии сохранённой схемы, а не применённой предпросмотром:
            // закрытие окна крестиком не должно оставлять редактор на непринятых цветах.
            editedScheme = _viewModel.ActiveColorScheme.Clone();
            // Какая палитра сейчас редактируется/показывается (светлая или тёмная).
            var previewDark = ThemeManager.CurrentTheme == ThemeManager.DarkThemeName;

            // Живой предпросмотр темы — одно миниатюрное окно, перекрашивается текущей
            // редактируемой палитрой при каждом изменении цветов через RepaintThemePreview.
            _preview = BuildThemePreview();

            // Список схем 280 на 34 с левым полем 10 (SettingsWindow.xaml:829).
            var schemeBox = new ComboBox
            {
                Width = 240,
                Height = 34,
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var colorsPanel = new StackPanel { Spacing = 2 };
            var schemeNames = new List<string>();
            var suppressSchemeEvent = false;
            Button? renameButton = null;
            Button? deleteButton = null;

            static bool IsBuiltInScheme(string name)
                => string.Equals(name, ColorScheme.CreateLight().Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ColorScheme.CreateDark().Name, StringComparison.OrdinalIgnoreCase);

            static string SchemeDisplayName(string name)
            {
                if (string.Equals(name, ColorScheme.CreateLight().Name, StringComparison.OrdinalIgnoreCase))
                    return LocalizationManager.T("Theme.Light");
                if (string.Equals(name, ColorScheme.CreateDark().Name, StringComparison.OrdinalIgnoreCase))
                    return LocalizationManager.T("Theme.Dark");
                return name;
            }

            void UpdateSchemeButtons()
            {
                var index = schemeBox.SelectedIndex;
                var builtIn = index >= 0 && index < schemeNames.Count && IsBuiltInScheme(schemeNames[index]);
                if (renameButton is not null)
                    renameButton.IsEnabled = !builtIn;
                if (deleteButton is not null)
                    deleteButton.IsEnabled = !builtIn;
            }

            void ReloadSchemes(string? select = null)
            {
                var target = select ?? editedScheme.Name;
                schemeNames.Clear();
                schemeNames.AddRange(ThemeManager.EnumerateAllSchemes().Select(x => x.Name));
                if (!schemeNames.Any(n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase)))
                    schemeNames.Add(target);

                suppressSchemeEvent = true;
                schemeBox.ItemsSource = schemeNames.Select(SchemeDisplayName).ToList();
                var index = schemeNames.FindIndex(n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase));
                schemeBox.SelectedIndex = index >= 0 ? index : 0;
                suppressSchemeEvent = false;
                UpdateSchemeButtons();
            }

            void RefreshColors()
            {
                colorsPanel.Children.Clear();
                foreach (var (key, label) in Models.ColorScheme.Definitions)
                {
                    var current = editedScheme.PaletteValue(previewDark, key);
                    colorsPanel.Children.Add(ColorRow(editedScheme, previewDark, key, label, current));
                }
                RepaintThemePreview(editedScheme, previewDark);
            }

            bool NameTaken(string name)
                => IsBuiltInScheme(name) || ThemeManager.FindCustomScheme(name) is not null;

            void ReportSchemeFailure(Exception ex)
                => _viewModel.ShowError(string.Format(LocalizationManager.T("Settings.SchemeFailedLinux"), ex.Message));

            string? SelectedSchemeName()
            {
                var index = schemeBox.SelectedIndex;
                return index >= 0 && index < schemeNames.Count ? schemeNames[index] : null;
            }

            schemeBox.SelectionChanged += (_, _) =>
            {
                if (suppressSchemeEvent)
                    return;
                var name = SelectedSchemeName();
                if (name is null)
                    return;
                var scheme = ThemeManager.EnumerateAllSchemes()
                    .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (scheme is null)
                    return;
                editedScheme = scheme.Clone();
                RefreshColors();
                UpdateSchemeButtons();
            };

            ReloadSchemes();
            RefreshColors();
            refreshEditedScheme = () => { ReloadSchemes(editedScheme.Name); RefreshColors(); };
            schemeColumn.Children.Add(schemeBox);
            schemeColumn.Children.Add(Hint(LocalizationManager.T("Settings.Theme.Description"), bottom: 10));

            var schemeButtons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

            Button SchemeButton(string textKey, string tooltipKey, Action action)
            {
                // Числа из разметки (SettingsWindow.xaml:843): отступ 10 на 6,
                // поля справа 8 и снизу 4, вторичная тема.
                var button = new Button
                {
                    Content = LocalizationManager.T(textKey),
                    Padding = new Thickness(10, 6),
                    Margin = new Thickness(0, 0, 8, 4)
                };
                button.Styled(ControlThemes.SecondaryButton);
                ToolTip.SetTip(button, LocalizationManager.T(tooltipKey));
                button.Click += (_, _) => action();
                schemeButtons.Children.Add(button);
                return button;
            }

            SchemeButton("Common.Apply", "Settings.Theme.ApplyTooltip", () =>
            {
                ThemeManager.ApplyScheme(editedScheme);
                // Общий цвет папок применяется при построении дерева — пересобираем его.
                _viewModel.RebuildTree();
            });

            SchemeButton("Settings.CreateTheme", "Settings.CreateThemeTooltip", () =>
            {
                var name = AskName(LocalizationManager.T("Settings.CreateTheme"),
                    string.Format(LocalizationManager.T("Settings.CopyOf"), editedScheme.Name));
                if (string.IsNullOrWhiteSpace(name))
                    return;
                name = name.Trim();
                if (NameTaken(name))
                {
                    _viewModel.ShowWarning(LocalizationManager.T("Settings.ReservedName"));
                    return;
                }

                var copy = editedScheme.Clone();
                copy.Name = name;
                try
                {
                    ThemeManager.SaveCustomScheme(copy);
                }
                catch (Exception ex)
                {
                    ReportSchemeFailure(ex);
                    return;
                }

                editedScheme = copy;
                ReloadSchemes(name);
                RefreshColors();
            });

            renameButton = SchemeButton("Settings.Rename", "Settings.RenameTooltip", () =>
            {
                var current = SelectedSchemeName();
                if (current is null)
                    return;
                if (IsBuiltInScheme(current))
                {
                    _viewModel.ShowInfo(LocalizationManager.T("Settings.CannotRenameBuiltIn"));
                    return;
                }

                var name = AskName(LocalizationManager.T("Settings.Rename"), current);
                if (string.IsNullOrWhiteSpace(name)
                    || string.Equals(name.Trim(), current, StringComparison.OrdinalIgnoreCase))
                    return;
                name = name.Trim();
                if (NameTaken(name))
                {
                    _viewModel.ShowWarning(LocalizationManager.T("Settings.ReservedName"));
                    return;
                }

                var scheme = ThemeManager.FindCustomScheme(current);
                if (scheme is not null)
                {
                    scheme.Name = name;
                    try
                    {
                        ThemeManager.RenameCustomScheme(scheme, current);
                    }
                    catch (Exception ex)
                    {
                        ReportSchemeFailure(ex);
                        return;
                    }
                }

                if (string.Equals(editedScheme.Name, current, StringComparison.OrdinalIgnoreCase))
                    editedScheme.Name = name;
                ReloadSchemes(name);
                RefreshColors();
            });

            deleteButton = SchemeButton("Common.Delete", "Settings.DeleteThemeTooltip", () =>
            {
                var current = SelectedSchemeName();
                if (current is null)
                    return;
                if (IsBuiltInScheme(current))
                {
                    _viewModel.ShowInfo(LocalizationManager.T("Settings.CannotDeleteBuiltIn"));
                    return;
                }
                if (!_viewModel.Confirm(string.Format(LocalizationManager.T("Settings.DeleteThemeConfirm"), current)))
                    return;

                try
                {
                    ThemeManager.DeleteCustomScheme(current);
                }
                catch (Exception ex)
                {
                    ReportSchemeFailure(ex);
                    return;
                }

                if (string.Equals(editedScheme.Name, current, StringComparison.OrdinalIgnoreCase))
                    editedScheme = ColorScheme.CreateLight();
                ReloadSchemes(editedScheme.Name);
                RefreshColors();
            });

            SchemeButton("Settings.ResetColors", "Settings.ResetColorsTooltip", () =>
            {
                // Сброс обеих палитр на значения по умолчанию.
                editedScheme = ColorScheme.Create(editedScheme.Name, false);
                RefreshColors();
            });

            SchemeButton("Settings.ExportTheme", "Settings.ExportThemeTooltip", () =>
            {
                var path = _viewModel.PickSaveFile(LocalizationManager.T("Settings.ExportSchemeTitle"),
                    editedScheme.Name + ".json");
                if (string.IsNullOrWhiteSpace(path))
                    return;
                try
                {
                    ThemeManager.ExportScheme(editedScheme, path);
                    _viewModel.ShowInfo(string.Format(LocalizationManager.T("Settings.ExportedOk"), path));
                }
                catch (Exception ex)
                {
                    _viewModel.ShowError(string.Format(LocalizationManager.T("Settings.ExportFailed"), ex.Message));
                }
            });

            SchemeButton("Settings.ImportTheme", "Settings.ImportThemeTooltip", () =>
            {
                var path = _viewModel.PickFile(LocalizationManager.T("Settings.ImportSchemeTitle"), string.Empty);
                if (string.IsNullOrWhiteSpace(path))
                    return;
                ColorScheme? imported;
                try
                {
                    imported = ThemeManager.ImportScheme(path);
                }
                catch
                {
                    imported = null;
                }

                if (imported is null
                    || (imported.LightColors.Count == 0 && imported.DarkColors.Count == 0)
                    || string.IsNullOrWhiteSpace(imported.Name))
                {
                    _viewModel.ShowError(LocalizationManager.T("Settings.ImportFailed"));
                    return;
                }

                if (IsBuiltInScheme(imported.Name))
                {
                    _viewModel.ShowWarning(LocalizationManager.T("Settings.ReservedName"));
                    return;
                }

                if (ThemeManager.FindCustomScheme(imported.Name) is not null
                    && !_viewModel.Confirm(string.Format(
                        LocalizationManager.T("Settings.ImportReplaceLinux"), imported.Name)))
                    return;

                try
                {
                    ThemeManager.SaveCustomScheme(imported);
                }
                catch (Exception ex)
                {
                    ReportSchemeFailure(ex);
                    return;
                }

                editedScheme = imported;
                ReloadSchemes(imported.Name);
                RefreshColors();
                _viewModel.ShowInfo(string.Format(LocalizationManager.T("Settings.ImportedOk"), imported.Name));
            });

            // Автогенерация схемы из одного базового цвета (issue #271): пользователь выбирает
            // цвет (по умолчанию — текущий акцент), обе палитры строятся автоматически.
            SchemeButton("Settings.GenerateScheme", "Settings.GenerateSchemeTooltip", () =>
            {
                var baseColor = editedScheme.PaletteValue(previewDark, "AccentColor");
                var picker = new ColorPickerWindow(baseColor);
                if (!picker.ShowDialogSync(this))
                    return;

                var generated = ColorScheme.GenerateFromColor(picker.Result);
                editedScheme.LightColors.Clear();
                foreach (var (key, value) in generated.LightColors)
                    editedScheme.LightColors[key] = value;
                editedScheme.DarkColors.Clear();
                foreach (var (key, value) in generated.DarkColors)
                    editedScheme.DarkColors[key] = value;
                RefreshColors();
            });

            // Кнопки создаются после первого ReloadSchemes, поэтому доступность
            // для встроенной темы выставляется здесь, а не только по смене выбора.
            UpdateSchemeButtons();

            schemeColumn.Children.Add(schemeButtons);

            // Редактор цветов выбранной палитры — в левой колонке, под блоком схемы.
            schemeColumn.Children.Add(GroupTitle(LocalizationManager.T("Settings.Colors")));
            schemeColumn.Children.Add(Hint(LocalizationManager.T("Settings.Colors.Description"), bottom: 8));

            // Переключатель палитры: какая сейчас редактируется и показывается (светлая/тёмная).
            var lightPalette = new RadioButton { Content = LocalizationManager.T("Theme.Light"), GroupName = "Palette", IsChecked = !previewDark };
            var darkPalette = new RadioButton { Content = LocalizationManager.T("Theme.Dark"), GroupName = "Palette", IsChecked = previewDark };
            void SelectPalette(bool dark)
            {
                if (previewDark == dark)
                    return;
                previewDark = dark;
                RefreshColors();
            }
            // IsCheckedChanged (вместо устаревшего ToggleButton.Checked) срабатывает и при
            // снятии отметки; повторная отрисовка гасится внутренней проверкой в SelectPalette.
            lightPalette.IsCheckedChanged += (_, _) => SelectPalette(false);
            darkPalette.IsCheckedChanged += (_, _) => SelectPalette(true);
            var paletteSwitch = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 4, 0, 0) };
            paletteSwitch.Children.Add(lightPalette);
            paletteSwitch.Children.Add(darkPalette);
            schemeColumn.Children.Add(paletteSwitch);

            schemeColumn.Children.Add(colorsPanel);

            // Единый живой предпросмотр текущей редактируемой палитры — в правой колонке.
            RepaintThemePreview(editedScheme, previewDark);
            var previewGroup = SettingsGroup(
                LocalizationManager.T("Settings.Preview"),
                new ScrollViewer
                {
                    Content = _preview.Shell,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = 520
                },
                new Thickness(8), bottom: 0);
            previewGroup.VerticalAlignment = VerticalAlignment.Top;

            // Корневой Grid из двух колонок: слева прокручиваемые настройки (тема +
            // редактор цветов), справа закреплённый живой предпросмотр, который НЕ
            // скроллится вместе со списком цветов (Auto — по содержимому).
            var appearanceGrid = new Grid();
            appearanceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            appearanceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            // Левая колонка — в собственном вертикальном ScrollViewer, чтобы при нехватке
            // высоты окна контент прокручивался и панели управления оставались доступны.
            var schemeScroll = new ScrollViewer
            {
                Content = schemeColumn,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 12, 0)
            };
            schemeColumn.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(schemeScroll, 0);
            Grid.SetColumn(previewGroup, 1);
            appearanceGrid.Children.Add(schemeScroll);
            appearanceGrid.Children.Add(previewGroup);
            appearance.Children.Add(appearanceGrid);

            var tabAppearance = MainTab("IconPalette", "Settings.TabAppearance", appearance);

            // ===== Базы =====
            var bases = new StackPanel { Spacing = 6 };

            // Подвкладки раздела «Базы»: список баз, каталоги шаблонов и обслуживание.
            // Каждая секция собирается в свой StackPanel, затем кладётся во вложенный
            // TabControl с горизонтальной полосой подвкладок (как в разметке WPF).
            var basesListPanel = new StackPanel { Spacing = 6 };
            var basesTemplatesPanel = new StackPanel { Spacing = 6 };
            var basesMaintenancePanel = new StackPanel { Spacing = 6 };

            // Вводное описание вкладки, как в разметке WPF (SettingsWindow.xaml:1326).
            bases.Children.Add(Hint(LocalizationManager.T("Settings.Bases.Description")));

            // Каталоги шаблонов конфигураций: список путей и правка вручную,
            // как на этой же вкладке в версии для Windows.
            var templatePaths = new ObservableCollection<string>(_viewModel.TemplateCatalogPaths);
            var templateList = new ListBox
            {
                ItemsSource = templatePaths,
                Height = 110,
                SelectionMode = SelectionMode.Single
            };

            basesTemplatesPanel.Children.Add(GroupTitle(LocalizationManager.T("Settings.TemplateDirs")));
            basesTemplatesPanel.Children.Add(Hint(LocalizationManager.T("Settings.Bases.TemplateDirsHintLinux")));
            basesTemplatesPanel.Children.Add(templateList);

            // Перенос на следующую строку, как WrapPanel в разметке WPF: горизонтальной
            // прокрутки у подвкладки нет, и на увеличенном шрифте кнопок строка из
            // четырёх кнопок уходила за край окна без возможности их нажать.
            var templateButtons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };

            var addTemplate = new Button { Content = LocalizationManager.T("Settings.Bases.AddTemplate") };
            ToolTip.SetTip(addTemplate, LocalizationManager.T("Settings.Bases.AddTemplateTooltip"));
            addTemplate.Click += (_, _) =>
            {
                var folder = _viewModel.PickFolder(LocalizationManager.T("Settings.Bases.AddTemplateFolderDesc"));
                if (string.IsNullOrWhiteSpace(folder) || templatePaths.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    return;
                templatePaths.Add(folder);
            };

            var editTemplate = new Button { Content = LocalizationManager.T("Settings.Bases.EditTemplate") };
            ToolTip.SetTip(editTemplate, LocalizationManager.T("Settings.Bases.EditTemplateTooltip"));
            editTemplate.Click += (_, _) =>
            {
                if (templateList.SelectedItem is not string current)
                    return;
                var folder = _viewModel.PickFolder(LocalizationManager.T("Settings.Bases.EditTemplateFolderDesc"));
                if (string.IsNullOrWhiteSpace(folder) || string.Equals(folder, current, StringComparison.OrdinalIgnoreCase))
                    return;
                if (templatePaths.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    return;
                templatePaths[templatePaths.IndexOf(current)] = folder;
                templateList.SelectedItem = folder;
            };

            var removeTemplate = new Button { Content = LocalizationManager.T("Common.Delete") };
            ToolTip.SetTip(removeTemplate, LocalizationManager.T("Settings.Bases.RemoveTemplateTooltip"));
            removeTemplate.Click += (_, _) =>
            {
                if (templateList.SelectedItem is string selected)
                    templatePaths.Remove(selected);
            };

            var loadTemplates = new Button { Content = LocalizationManager.T("Settings.Bases.LoadDefault") };
            ToolTip.SetTip(loadTemplates, LocalizationManager.T("Settings.Bases.LoadDefaultTooltip"));
            loadTemplates.Click += (_, _) =>
            {
                templatePaths.Clear();
                foreach (var path in _viewModel.DiscoverTemplateCatalogPaths())
                    templatePaths.Add(path);
            };

            templateButtons.Children.Add(addTemplate);
            templateButtons.Children.Add(editTemplate);
            templateButtons.Children.Add(removeTemplate);
            templateButtons.Children.Add(loadTemplates);
            foreach (var templateButton in templateButtons.Children.OfType<Button>())
                templateButton.Margin = new Thickness(0, 0, 8, 4);
            // У последней кнопки правого отступа нет, как в разметке WPF: в WrapPanel он
            // входит в измеряемую ширину и сдвигал бы перенос на восемь точек раньше.
            loadTemplates.Margin = new Thickness(0, 0, 0, 4);
            basesTemplatesPanel.Children.Add(templateButtons);

            // Операции со списком баз целиком: выгрузка и загрузка JSON,
            // разовый импорт из ibases.v8i.
            basesListPanel.Children.Add(GroupTitle(LocalizationManager.T("Settings.IbaseList")));

            var timestampCheck = new CheckBox
            {
                Content = LocalizationManager.T("Settings.AddTimestamp"),
                IsChecked = _viewModel.AddTimestampToExportFileName,
                Margin = new Thickness(0, 4, 0, 0)
            };
            // Пояснение пункта вынесено под знак «?» рядом с флажком (как в WPF).
            var timestampCheckRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            timestampCheckRow.Children.Add(timestampCheck);
            timestampCheckRow.Children.Add(new HelpLink
            {
                HelpText = LocalizationManager.T("Settings.AddTimestampTooltip"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var timestampBox = new AutoCompleteBox
            {
                MinWidth = 280,
                MinHeight = 38,
                ItemsSource = TimestampFormats,
                FilterMode = AutoCompleteFilterMode.Contains,
                Text = string.IsNullOrWhiteSpace(_viewModel.ExportTimestampFormat)
                    ? TimestampFormats[0]
                    : _viewModel.ExportTimestampFormat
            };
            // Вертикальная обрезка текста шаблона: у AutoCompleteBox нет своего
            // VerticalContentAlignment (в отличие от TextBox), поэтому центрируем
            // и чуть «дышим» внутреннему редактируемому TextBox, а высоту поля
            // (MinHeight=38) согласуем с Windows-версией (SettingsWindow.xaml).
            // Стиль добавляется локально, в Styles самого поля, чтобы не задеть
            // другие поля ввода окна.
            timestampBox.Styles.Add(new Style(x => x.OfType<AutoCompleteBox>().Descendant().OfType<TextBox>())
            {
                Setters =
                {
                    new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBox.PaddingProperty, new Thickness(6, 4))
                }
            });

            var timestampPreview = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

            string TimestampFormat() =>
                string.IsNullOrWhiteSpace(timestampBox.Text) ? TimestampFormats[0] : timestampBox.Text.Trim();

            void ApplyExportFileNameSettings() =>
                _viewModel.ApplyExportFileNameSettings(timestampCheck.IsChecked == true, TimestampFormat());

            // Предпросмотр собирается теми же двумя шаблонами, что и в версии
            // для Windows: один подставляет метку в имя файла, второй обрамляет
            // это словом «Пример».
            void UpdateTimestampPreview()
            {
                var format = timestampBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(format))
                {
                    timestampPreview.Text = LocalizationManager.T("Settings.TimestampSpecifyHint");
                    return;
                }

                try
                {
                    var name = string.Format(LocalizationManager.T("Settings.TimestampBasePrefix"),
                        DateTime.Now.ToString(format));
                    timestampPreview.Text = string.Format(LocalizationManager.T("Settings.TimestampExample"), name);
                }
                catch (FormatException)
                {
                    timestampPreview.Text = LocalizationManager.T("Settings.TimestampInvalid");
                }
            }

            // Кнопки идут столбцом во всю ширину, как в разметке WPF: семь кнопок
            // в строку не помещаются даже в широком окне, и последние две
            // (поиск потерянных баз, очистка истории) уходили за край окна.
            var listButtons = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8 };

            var exportList = new Button { Content = LocalizationManager.T("Settings.Bases.ExportList") };
            ToolTip.SetTip(exportList, LocalizationManager.T("Settings.Bases.ExportListTooltip"));
            // Значения передаются выгрузке прямо из полей, а на диск попадают
            // только по ОК: иначе отмена выгрузки или закрытие окна крестиком
            // всё равно меняли бы настройку.
            exportList.Click += (_, _) =>
                _viewModel.ExportInfobases(timestampCheck.IsChecked == true, TimestampFormat());

            var importList = new Button { Content = LocalizationManager.T("Settings.Bases.ImportList") };
            ToolTip.SetTip(importList, LocalizationManager.T("Settings.Bases.ImportListTooltip"));
            importList.Click += (_, _) => _viewModel.ImportInfobases();

            var importV8i = new Button { Content = LocalizationManager.T("Settings.Bases.ImportV8i") };
            ToolTip.SetTip(importV8i, LocalizationManager.T("Settings.Bases.ImportV8iTooltip"));
            importV8i.Click += (_, _) => _viewModel.ImportFromIbasesV8i();

            // Импорт баз и настроек платформы из программы StartManager (issue #163).
            var importStartManager = new Button { Content = LocalizationManager.T("Settings.Bases.ImportStartManager") };
            ToolTip.SetTip(importStartManager, LocalizationManager.T("Settings.Bases.ImportStartManagerTooltip"));
            importStartManager.Click += (_, _) => _viewModel.ImportFromStartManager();

            // Определение/обновление конфигураций всех баз (issue #236). Правки вносятся
            // прямо в объекты Infobase, поэтому после закрытия сохраняем список.
            var detectAll = new Button { Content = LocalizationManager.T("Settings.Bases.DetectAllConfigs") };
            ToolTip.SetTip(detectAll, LocalizationManager.T("Settings.Bases.DetectAllConfigsTooltip"));
            detectAll.Click += (_, _) =>
            {
                var dialog = new DetectConfigurationsWindow(
                    _viewModel.Infobases.ToList(),
                    editBase: ib => _viewModel.EditInfobaseCommand.Execute(ib));
                dialog.ShowDialogSync(this);
                if (dialog.DataChanged)
                    _viewModel.PersistInfobasesAfterInlineEdit();
            };

            // Поиск потерянных и забытых баз 1С на дисках (issue #247). Новые базы
            // добавляются в коллекцию приложения, поэтому после закрытия сохраняем список.
            var findLostBases = new Button { Content = LocalizationManager.T("Settings.Bases.FindLostBases") };
            ToolTip.SetTip(findLostBases, LocalizationManager.T("Settings.Bases.FindLostBasesTooltip"));
            findLostBases.Click += (_, _) =>
            {
                var dialog = new FindLostBasesWindow(
                    _viewModel.Infobases,
                    ib => _viewModel.AddFoundInfobase(ib));
                dialog.ShowDialogSync(this);
                if (dialog.DataChanged)
                    _viewModel.PersistInfobasesAfterInlineEdit();
            };

            // Очистка истории запусков выбранных баз (issue #246). Правки вносятся прямо
            // в объекты Infobase (очищается LaunchHistory), поэтому после закрытия сохраняем.
            var clearHistory = new Button { Content = LocalizationManager.T("Settings.Bases.ClearHistory") };
            ToolTip.SetTip(clearHistory, LocalizationManager.T("Settings.Bases.ClearHistoryTooltip"));
            clearHistory.Click += (_, _) =>
            {
                var dialog = new ClearHistoryWindow(_viewModel.Infobases.ToList());
                dialog.ShowDialogSync(this);
                if (dialog.DataChanged)
                    _viewModel.PersistInfobasesAfterInlineEdit();
            };

            listButtons.Children.Add(exportList);
            listButtons.Children.Add(importList);
            listButtons.Children.Add(importV8i);
            listButtons.Children.Add(importStartManager);
            listButtons.Children.Add(detectAll);
            listButtons.Children.Add(findLostBases);
            listButtons.Children.Add(clearHistory);
            foreach (var listButton in listButtons.Children.OfType<Button>())
                listButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            basesListPanel.Children.Add(listButtons);

            // Глубина истории запусков одной базы (issue #246).
            var historyDepthRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0)
            };
            historyDepthRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.Bases.HistoryDepth"),
                VerticalAlignment = VerticalAlignment.Center
            });
            // Пояснение пункта вынесено под знак «?» рядом с заголовком (как в WPF).
            historyDepthRow.Children.Add(new HelpLink
            {
                HelpText = LocalizationManager.T("Settings.Bases.HistoryDepthTooltip"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var historyDepthBox = new TextBox
            {
                Text = _viewModel.MaxLaunchHistoryPerBase.ToString(),
                Width = 120,
                VerticalContentAlignment = VerticalAlignment.Center
            }.Styled(ControlThemes.ModernTextBox);
            historyDepthRow.Children.Add(historyDepthBox);
            basesListPanel.Children.Add(historyDepthRow);

            // Глобальное действие по двойному щелчку на базе (функция №28 StartManager).
            var dblClickRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0)
            };
            dblClickRow.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.DblClickActionLabel"),
                VerticalAlignment = VerticalAlignment.Center
            });
            // Пояснение пункта вынесено под знак «?» рядом с заголовком (как в WPF).
            dblClickRow.Children.Add(new HelpLink
            {
                HelpText = LocalizationManager.T("Settings.DblClickActionTooltip"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var dblClickBox = new ComboBox
            {
                Width = 200,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            dblClickBox.ItemsSource = new[]
            {
                LocalizationManager.T("Connection.DefaultLaunchEnterprise"),
                LocalizationManager.T("Connection.DefaultLaunchConfigurator"),
                LocalizationManager.T("Connection.DblClickNone")
            };
            dblClickBox.SelectedIndex = _viewModel.DefaultDoubleClickAction switch
            {
                Configuration_Management.Models.DoubleClickAction.Configurator => 1,
                Configuration_Management.Models.DoubleClickAction.None => 2,
                _ => 0
            };
            dblClickBox.SelectionChanged += (_, _) =>
            {
                _viewModel.SetDefaultDoubleClickAction(dblClickBox.SelectedIndex switch
                {
                    1 => Configuration_Management.Models.DoubleClickAction.Configurator,
                    2 => Configuration_Management.Models.DoubleClickAction.None,
                    _ => Configuration_Management.Models.DoubleClickAction.Enterprise
                });
            };
            dblClickRow.Children.Add(dblClickBox);
            basesListPanel.Children.Add(dblClickRow);

            basesListPanel.Children.Add(timestampCheckRow);

            // Как в Windows-разметке (SettingsWindow.xaml:1419): подпись сверху, поле —
            // на всю ширину, предпросмотр снизу. В горизонтальной панели рядом с подписью
            // и предпросмотром AutoCompleteBox не получал всю ширину, и строка формата
            // (yyyyMMdd_HHmmss) обрезалась.
            var timestampRow = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            // Заголовок и знак «?» с пояснением формата — в одну строку (как в WPF).
            var timestampFormatHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 0, 0, 4)
            };
            timestampFormatHeader.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.Bases.TimestampFormat")
            });
            timestampFormatHeader.Children.Add(new HelpLink
            {
                HelpText = LocalizationManager.T("Settings.Bases.TimestampFormatTooltip"),
                VerticalAlignment = VerticalAlignment.Center
            });
            timestampRow.Children.Add(timestampFormatHeader);
            timestampRow.Children.Add(timestampBox);
            timestampRow.Children.Add(timestampPreview);
            basesListPanel.Children.Add(timestampRow);

            timestampCheck.IsCheckedChanged += (_, _) => UpdateTimestampPreview();
            timestampBox.GetObservable(AutoCompleteBox.TextProperty)
                .Subscribe(new SettingsObserver<string?>(_ => UpdateTimestampPreview()));
            UpdateTimestampPreview();

            basesMaintenancePanel.Children.Add(GroupTitle(LocalizationManager.T("Settings.Maintenance")));
            basesMaintenancePanel.Children.Add(Hint(LocalizationManager.T("Settings.Bases.MaintenanceHint")));

            var maintenanceButtons = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };

            var removeMissing = new Button { Content = LocalizationManager.T("Settings.Bases.RemoveMissing") };
            ToolTip.SetTip(removeMissing, LocalizationManager.T("Settings.Bases.RemoveMissingTooltip"));
            removeMissing.Click += (_, _) => _viewModel.RemoveMissingFileBases();

            var killProcesses = new Button { Content = LocalizationManager.T("Settings.Bases.KillProcesses") };
            ToolTip.SetTip(killProcesses, LocalizationManager.T("Settings.Bases.KillProcessesTooltip"));
            killProcesses.Click += (_, _) => _viewModel.KillOneCProcesses();

            maintenanceButtons.Children.Add(removeMissing);
            maintenanceButtons.Children.Add(killProcesses);
            foreach (var maintenanceButton in maintenanceButtons.Children.OfType<Button>())
                maintenanceButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            basesMaintenancePanel.Children.Add(maintenanceButtons);

            // Интеграция с проводником Windows (функция №12): на Linux недоступна,
            // поэтому показываем заблокированный пункт с пояснением.
            var explorerIntegrationHint = new TextBlock
            {
                Text = LocalizationManager.T("Settings.ExplorerIntegrationHint"),
                Margin = new Thickness(0, 8, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };
            Themes.ThemeBrushes.Bind(explorerIntegrationHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            basesMaintenancePanel.Children.Add(explorerIntegrationHint);

            var explorerIntegrationCheck = new CheckBox
            {
                Content = LocalizationManager.T("Settings.ExplorerIntegrationEnable"),
                IsEnabled = false,
                IsChecked = false
            };
            // Пояснение пункта вынесено под знак «?» рядом с флажком (как в WPF).
            var explorerIntegrationRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 0, 0, 8)
            };
            explorerIntegrationRow.Children.Add(explorerIntegrationCheck);
            explorerIntegrationRow.Children.Add(new HelpLink
            {
                HelpText = LocalizationManager.T("Settings.ExplorerIntegrationEnableTooltip"),
                VerticalAlignment = VerticalAlignment.Center
            });
            basesMaintenancePanel.Children.Add(explorerIntegrationRow);

            // Автозапуск при старте ОС (функция №31, Этап 8) и каталог копий экрана (функция №30).
            basesMaintenancePanel.Children.Add(GroupTitle(LocalizationManager.T("Settings.AutoStart")));
            basesMaintenancePanel.Children.Add(Hint(LocalizationManager.T("Settings.AutoStartHint")));
            var autoStartCheck = new CheckBox
            {
                Content = LocalizationManager.T("Settings.AutoStartEnable"),
                IsChecked = _viewModel.AutoStartEnabled,
                Margin = new Thickness(0, 0, 0, 8)
            };
            basesMaintenancePanel.Children.Add(autoStartCheck);

            var screenshotDirLabel = new TextBlock
            {
                Text = LocalizationManager.T("Settings.Screenshot.DirectoryLabel"),
                Margin = new Thickness(0, 4, 0, 4)
            };
            basesMaintenancePanel.Children.Add(screenshotDirLabel);

            var screenshotDirRow = new Grid();
            screenshotDirRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            screenshotDirRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var screenshotDirBox = new TextBox
            {
                Text = _viewModel.ScreenshotSaveDirectory,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 34
            };
            screenshotDirBox.Styled(ControlThemes.ModernTextBox);
            screenshotDirRow.Children.Add(screenshotDirBox);
            var screenshotBrowse = new Button
            {
                Content = LocalizationManager.T("Common.Browse"),
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(12, 6)
            };
            screenshotBrowse.Click += (_, _) =>
            {
                var picked = _viewModel.PickFolder(
                    LocalizationManager.T("Settings.Screenshot.ChooseDir"), screenshotDirBox.Text);
                if (!string.IsNullOrWhiteSpace(picked))
                    screenshotDirBox.Text = picked;
            };
            Grid.SetColumn(screenshotBrowse, 1);
            screenshotDirRow.Children.Add(screenshotBrowse);
            basesMaintenancePanel.Children.Add(screenshotDirRow);

            basesMaintenancePanel.Children.Add(GroupTitle(LocalizationManager.T("Settings.DangerousOps")));
            basesMaintenancePanel.Children.Add(Hint(LocalizationManager.T("Settings.Bases.DangerousHint")));

            var clearAll = new Button
            {
                Content = LocalizationManager.T("Settings.Bases.ClearAll"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8)
            };
            ToolTip.SetTip(clearAll, LocalizationManager.T("Settings.Bases.ClearAllTooltip"));
            clearAll.Click += (_, _) => _viewModel.ClearAllInfobases();
            basesMaintenancePanel.Children.Add(clearAll);

            // Вложенные горизонтальные подвкладки раздела «Базы», как в разметке
            // WPF (SettingsWindow.xaml): «Список баз», «Каталоги шаблонов» и
            // «Обслуживание». Секция ibases.v8i остаётся блоком ниже вкладок.
            var basesTabs = new TabControl { Margin = new Thickness(0, 4, 0, 0) };
            basesTabs.Styled(ControlThemes.SettingsSubTabControl);
            basesTabs.Items.Add(SubTab("Settings.Bases.Subtab.List", "Settings.Bases.Subtab.ListTooltip", "IconDatabase", basesListPanel));
            basesTabs.Items.Add(SubTab("Settings.Bases.Subtab.Templates", "Settings.Bases.Subtab.TemplatesTooltip", "IconFolder", basesTemplatesPanel));
            basesTabs.Items.Add(SubTab("Settings.Bases.Subtab.Maintenance", "Settings.Bases.Subtab.MaintenanceTooltip", "IconWrench", basesMaintenancePanel));
            bases.Children.Add(basesTabs);

            // Справка ставится к заголовку блока, а не отдельной строкой:
            // в разметке WPF «ibases.v8i» это имя вкладки, а «Настройки
            // синхронизации» заголовок группы внутри неё, и рядом они
            // не стоят никогда. Здесь блок вложен во вкладку «Базы», поэтому
            // два заголовка подряд означали бы одно и то же.
            var ibasesHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 2)
            };
            ibasesHeader.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.TabIbases"),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            ibasesHeader.Children.Add(new Controls.HelpLink
            {
                HelpText = LocalizationManager.T("Settings.Ibases.HelpTextLinux"),
                Margin = new Thickness(6, 0, 0, 0)
            });
            bases.Children.Add(ibasesHeader);
            bases.Children.Add(Hint(LocalizationManager.T("Settings.Ibases.Description")));

            bases.Children.Add(GroupTitle(LocalizationManager.T("Settings.Ibases.SyncMode")));
            var syncModes = new[]
            {
                (Mode: IbasesSyncMode.None, Text: LocalizationManager.T("Settings.Ibases.SyncModeDisabled")),
                (Mode: IbasesSyncMode.Import, Text: LocalizationManager.T("Settings.Ibases.SyncModeImport")),
                (Mode: IbasesSyncMode.Export, Text: LocalizationManager.T("Settings.Ibases.SyncModeExport")),
                (Mode: IbasesSyncMode.Both, Text: LocalizationManager.T("Settings.Ibases.SyncModeBoth"))
            };
            var syncModeBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            syncModeBox.ItemsSource = syncModes.Select(m => m.Text).ToList();
            syncModeBox.SelectedIndex = Array.FindIndex(syncModes, m => m.Mode == _viewModel.IbasesSyncMode);
            bases.Children.Add(syncModeBox);

            bases.Children.Add(GroupTitle(LocalizationManager.T("Settings.Ibases.File")));
            // Строка пути на Grid: поле растягивается на доступную ширину, а кнопка
            // обзора закреплена справа — в отличие от горизонтального StackPanel
            // с фиксированной MinWidth это не вызывает обрезания по горизонтали.
            var fileGrid = new Grid();
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var fileBox = new TextBox { Text = _viewModel.IbasesSyncFilePath, HorizontalAlignment = HorizontalAlignment.Stretch }.Styled(ControlThemes.ModernTextBox);
            var browse = new Button { Content = "...", Margin = new Thickness(8, 0, 0, 0) };
            ToolTip.SetTip(browse, LocalizationManager.T("Settings.Ibases.BrowseTooltip"));
            browse.Click += (_, _) =>
            {
                var picked = _viewModel.PickFile(
                    LocalizationManager.T("Sync.ChooseIbasesFile"),
                    LocalizationManager.T("Sync.IbasesFilter"));
                if (!string.IsNullOrWhiteSpace(picked))
                    fileBox.Text = picked;
            };
            Grid.SetColumn(fileBox, 0);
            Grid.SetColumn(browse, 1);
            fileGrid.Children.Add(fileBox);
            fileGrid.Children.Add(browse);
            bases.Children.Add(fileGrid);

            bases.Children.Add(GroupTitle(LocalizationManager.T("Settings.Ibases.SyncTrigger")));
            var triggers = new[]
            {
                (Trigger: IbasesSyncTrigger.OnStartup, Text: LocalizationManager.T("Settings.Ibases.TriggerStartup")),
                (Trigger: IbasesSyncTrigger.Interval, Text: LocalizationManager.T("Settings.Ibases.TriggerInterval")),
                (Trigger: IbasesSyncTrigger.Schedule, Text: LocalizationManager.T("Settings.Ibases.TriggerSchedule"))
            };
            var triggerBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            triggerBox.ItemsSource = triggers.Select(t => t.Text).ToList();
            triggerBox.SelectedIndex = Array.FindIndex(triggers, t => t.Trigger == _viewModel.IbasesSyncTrigger);
            bases.Children.Add(triggerBox);

            var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            // Подписи держатся в переменных: версия для Windows гасит их вместе
            // с полями (SettingsWindow.Sync.cs, SyncIntervalLabel и SyncScheduleLabel),
            // иначе рядом с погашенным полем стоит подпись в полную яркость.
            var intervalLabel = new TextBlock { Text = LocalizationManager.T("Settings.Ibases.Interval"), VerticalAlignment = VerticalAlignment.Center };
            intervalRow.Children.Add(intervalLabel);
            var intervalBox = new TextBox { Text = _viewModel.IbasesSyncIntervalMinutes.ToString(), Width = 80 }.Styled(ControlThemes.ModernTextBox);
            intervalRow.Children.Add(intervalBox);
            var scheduleLabel = new TextBlock { Text = LocalizationManager.T("Settings.Ibases.ScheduleTime"), VerticalAlignment = VerticalAlignment.Center };
            intervalRow.Children.Add(scheduleLabel);
            var scheduleBox = new TextBox { Text = _viewModel.IbasesSyncScheduleTime, Width = 80 }.Styled(ControlThemes.ModernTextBox);
            intervalRow.Children.Add(scheduleBox);
            bases.Children.Add(intervalRow);

            // Строка состояния и ручные операции, как в разметке WPF
            // (SettingsWindow.xaml:1258-1281): сначала статус, затем загрузка
            // и выгрузка. Обработчики WPF под #if WINDOWS, но сами действия
            // в Linux-сборке есть, к ним и подключено.
            var syncStatus = Hint(string.Empty);
            syncStatus.Margin = new Thickness(0, 4, 0, 8);
            bases.Children.Add(syncStatus);

            var syncButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var importButton = new Button { Content = LocalizationManager.T("Settings.Ibases.Import") };
            ToolTip.SetTip(importButton, LocalizationManager.T("Settings.Ibases.ImportTooltip"));
            importButton.Click += (_, _) => _viewModel.ImportFromIbasesFile(fileBox.Text);
            var exportButton = new Button { Content = LocalizationManager.T("Settings.Ibases.Export") };
            ToolTip.SetTip(exportButton, LocalizationManager.T("Settings.Ibases.ExportTooltip"));
            exportButton.Click += (_, _) => _viewModel.ExportToIbasesFile(fileBox.Text);
            syncButtons.Children.Add(importButton);
            syncButtons.Children.Add(exportButton);
            bases.Children.Add(syncButtons);

            // «Сохранять после правки» (issue #269). Объявляем здесь, чтобы локальная функция
            // UpdateSyncControls могла управлять его доступностью (замыкание), а сам элемент
            // добавляем в bases ниже, рядом с блоком резервных копий.
            CheckBox? saveAfterEditCheck = null;

            // Доступность и статус пересчитываются на каждое изменение блока,
            // как это делает UpdateSyncControls в версии для Windows: при
            // отключённой синхронизации поля и кнопки гаснут, а не молча
            // ничего не делают.
            void UpdateSyncControls()
            {
                var mode = syncModeBox.SelectedIndex >= 0
                    ? syncModes[syncModeBox.SelectedIndex].Mode
                    : IbasesSyncMode.None;
                var enabled = mode != IbasesSyncMode.None;
                var trigger = triggerBox.SelectedIndex >= 0
                    ? triggers[triggerBox.SelectedIndex].Trigger
                    : IbasesSyncTrigger.OnStartup;

                fileBox.IsEnabled = enabled;
                browse.IsEnabled = enabled;
                triggerBox.IsEnabled = enabled;
                intervalBox.IsEnabled = enabled && trigger == IbasesSyncTrigger.Interval;
                intervalLabel.IsEnabled = intervalBox.IsEnabled;
                scheduleBox.IsEnabled = enabled && trigger == IbasesSyncTrigger.Schedule;
                scheduleLabel.IsEnabled = scheduleBox.IsEnabled;
                importButton.IsEnabled = enabled && mode is IbasesSyncMode.Import or IbasesSyncMode.Both;
                exportButton.IsEnabled = enabled && mode is IbasesSyncMode.Export or IbasesSyncMode.Both;
                // «Сохранять после правки» (issue #269): доступно только в режимах с сохранением.
                if (saveAfterEditCheck is not null)
                    saveAfterEditCheck.IsEnabled = enabled && mode is IbasesSyncMode.Export or IbasesSyncMode.Both;
                syncStatus.Text = BuildSyncStatus(mode, fileBox.Text, trigger, intervalBox.Text, scheduleBox.Text);
            }

            syncModeBox.SelectionChanged += (_, _) => UpdateSyncControls();
            triggerBox.SelectionChanged += (_, _) => UpdateSyncControls();
            fileBox.TextChanged += (_, _) => UpdateSyncControls();
            intervalBox.TextChanged += (_, _) => UpdateSyncControls();
            scheduleBox.TextChanged += (_, _) => UpdateSyncControls();
            UpdateSyncControls();

            // Подписи как в оригинале: флажок называет само действие, а строка
            // про имена копий идёт пояснением под числом хранимых копий.
            var backupCheck = new CheckBox
            {
                Content = LocalizationManager.T("Settings.BackupBeforeSync"),
                IsChecked = _viewModel.IbasesBackupEnabled
            };
            bases.Children.Add(backupCheck);

            // «Сохранять после правки» (issue #269): записывать изменения в ibases.v8i сразу.
            saveAfterEditCheck = new CheckBox
            {
                Content = LocalizationManager.T("Settings.Ibases.SaveAfterEdit"),
                IsChecked = _viewModel.IbasesSaveAfterEdit,
                Margin = new Thickness(0, 6, 0, 0)
            };
            ToolTip.SetTip(saveAfterEditCheck, LocalizationManager.T("Settings.Ibases.SaveAfterEditTooltip"));
            bases.Children.Add(saveAfterEditCheck);

            var keepRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            keepRow.Children.Add(new TextBlock { Text = LocalizationManager.T("Settings.Ibases.BackupKeepCount"), VerticalAlignment = VerticalAlignment.Center });
            var keepBox = new TextBox { Text = _viewModel.IbasesBackupKeepCount.ToString(), Width = 80 }.Styled(ControlThemes.ModernTextBox);
            keepRow.Children.Add(keepBox);
            bases.Children.Add(keepRow);
            bases.Children.Add(Hint(LocalizationManager.T("Settings.Ibases.BackupNote")));

            var restoreButton = new Button
            {
                Content = LocalizationManager.T("Settings.Ibases.Restore"),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ToolTip.SetTip(restoreButton, LocalizationManager.T("Settings.Ibases.RestoreTooltip"));
            restoreButton.Click += (_, _) => _viewModel.RestoreIbasesBackup(fileBox.Text);
            bases.Children.Add(restoreButton);

            var tabBases = MainTab("IconDatabase", "Settings.TabBases",
                new ScrollViewer
                {
                    Content = bases,
                    // Горизонтальная прокрутка отключена, чтобы элементы растягивались
                    // по ширине окна (Stretch). При изменении размера окна реквизиты
                    // сжимаются вслед за ним; строки уже адаптивны (Grid со Star-колонкой).
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                });

            // ===== Резервное копирование профиля =====
            // Отступы поштучно, как в коде за разметкой (SettingsWindow.Profile.cs:61-130),
            // а не общим зазором панели.
            var profile = new StackPanel { Margin = new Thickness(4, 12, 4, 0) };

            var profileDescription = new TextBlock
            {
                Text = LocalizationManager.T("Settings.Profile.Description"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            ThemeBrushes.Bind(profileDescription, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            profile.Children.Add(profileDescription);
            var profileIncludes = new TextBlock
            {
                Text = LocalizationManager.T("Settings.Profile.Includes"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            ThemeBrushes.Bind(profileIncludes, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            profile.Children.Add(profileIncludes);

            var profileDirGrid = new Grid();
            profileDirGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            profileDirGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var profileDirBox = new TextBox
            {
                Text = _viewModel.ProfileBackupDirectory,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            profileDirBox.Styled(ControlThemes.ModernTextBox);
            var profileBrowse = new Button
            {
                Content = LocalizationManager.T("Settings.Profile.Browse"),
                Padding = new Thickness(10, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            ToolTip.SetTip(profileBrowse, LocalizationManager.T("Settings.Profile.BrowseTooltip"));
            profileBrowse.Click += (_, _) =>
            {
                var picked = _viewModel.PickFolder(LocalizationManager.T("Settings.Profile.Directory"));
                if (!string.IsNullOrWhiteSpace(picked))
                    profileDirBox.Text = picked;
            };
            Grid.SetColumn(profileDirBox, 0);
            Grid.SetColumn(profileBrowse, 1);
            profileDirGrid.Children.Add(profileDirBox);
            profileDirGrid.Children.Add(profileBrowse);

            // Каталог лежит в рамке с заголовком, отступом 8 и полем снизу 10.
            profile.Children.Add(Controls.GroupBoxPanel.Build(
                "Settings.Profile.Directory", profileDirGrid,
                margin: new Thickness(0, 0, 0, 10),
                padding: new Thickness(8)));

            var profileRestoreCheck = new CheckBox
            {
                Content = LocalizationManager.T("Settings.Profile.RestoreOnStartup"),
                IsChecked = _viewModel.ProfileRestoreOnStartup,
                Margin = new Thickness(0, 0, 0, 4)
            };
            profile.Children.Add(profileRestoreCheck);
            var profileRestoreHint = new TextBlock
            {
                Text = LocalizationManager.T("Settings.Profile.RestoreOnStartupHint"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24, 0, 0, 12)
            };
            ThemeBrushes.Bind(profileRestoreHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            profile.Children.Add(profileRestoreHint);

            var profileButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            var backupNow = new Button
            {
                Content = LocalizationManager.T("Settings.Profile.BackupNow"),
                Padding = new Thickness(12, 6)
            };
            ToolTip.SetTip(backupNow, LocalizationManager.T("Settings.Profile.BackupNowTooltip"));
            backupNow.Click += (_, _) =>
            {
                // Применяем выбранный каталог перед сохранением, чтобы профиль ушёл туда.
                _viewModel.ApplyProfileBackupSettings(profileDirBox.Text, profileRestoreCheck.IsChecked == true);
                _viewModel.BackupProfile();
            };
            var restoreNow = new Button
            {
                Content = LocalizationManager.T("Settings.Profile.RestoreNow"),
                Padding = new Thickness(12, 6)
            };
            ToolTip.SetTip(restoreNow, LocalizationManager.T("Settings.Profile.RestoreNowTooltip"));
            restoreNow.Click += (_, _) =>
            {
                // Применяем выбранный каталог перед восстановлением.
                _viewModel.ApplyProfileBackupSettings(profileDirBox.Text, profileRestoreCheck.IsChecked == true);
                // При успехе данные уже перезагружены; окно закрываем, чтобы не затереть
                // восстановленные настройки старыми значениями из полей формы.
                if (_viewModel.RestoreProfile())
                {
                    DialogResult = true;
                    Close();
                }
            };
            profileButtons.Children.Add(backupNow);
            profileButtons.Children.Add(restoreNow);
            profile.Children.Add(profileButtons);

            // Значок вкладки из словаря автора, как в коде Windows-версии
            // (SettingsWindow.Profile.cs:37 берёт BackupRestore); прежде здесь
            // был самодельный контур.
            var tabProfile = MainTab("IconBackupRestore", "Settings.TabProfile",
                new ScrollViewer { Content = profile, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            // ===== Клавиши =====
            // Spacing не задаётся: в Avalonia он складывается с полями соседей,
            // а поля строк взяты из разметки WPF и уже держат нужный шаг.
            var hotkeys = new StackPanel();
            var hotkeysTitle = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // Нижний отступ строки заголовка из разметки
                // (SettingsWindow.xaml:945).
                Margin = new Thickness(0, 0, 0, 4)
            };
            hotkeysTitle.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.Hotkeys.Title"),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            hotkeysTitle.Children.Add(new Controls.HelpLink
            {
                HelpText = LocalizationManager.T("Settings.Hotkeys.HelpText"),
                Margin = new Thickness(6, 0, 0, 0)
            });
            hotkeys.Children.Add(hotkeysTitle);
            var hotkeysHint = Hint(LocalizationManager.T("Settings.Hotkeys.Description"));
            hotkeysHint.Margin = new Thickness(0, 0, 0, 8);
            hotkeys.Children.Add(hotkeysHint);

            // Поля назначения: HotkeyBox ловит сочетание с клавиатуры, Delete
            // снимает назначение, Escape отменяет ввод. Подписи и порядок строк
            // взяты из разметки WPF (SettingsWindow.xaml, вкладка «Клавиши»).
            var hotkeyEnterprise = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.Enterprise"), _viewModel.HotkeyEnterprise);
            var hotkeyConfigurator = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.Configurator"), _viewModel.HotkeyConfigurator);
            var hotkeyFavorite = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.Favorites"), _viewModel.HotkeyFavorite);
            var hotkeyEdit = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.Edit"), _viewModel.HotkeyEdit);
            var hotkeyDelete = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.Delete"), _viewModel.HotkeyDelete);
            var hotkeyClearCache = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.ClearCache"), _viewModel.HotkeyClearCache);
            var hotkeyAdd = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.AddBase"), _viewModel.HotkeyAdd);
            var hotkeyPin = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.Pin"), _viewModel.HotkeyPin);
            var hotkeyShowAll = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.ShowAll"), _viewModel.HotkeyShowAll);
            var hotkeyShowFavorites = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.ShowFavorites"), _viewModel.HotkeyShowFavorites);
            var hotkeyShowRecent = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.ShowRecent"), _viewModel.HotkeyShowRecent);
            var hotkeyClearSearch = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.ClearSearch"), _viewModel.HotkeyClearSearch);
            var hotkeyClearTags = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.ClearTags"), _viewModel.HotkeyClearTags);
            var hotkeyRightPanelDetails = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.RightPanelDetails"), _viewModel.HotkeyRightPanelDetails);
            var hotkeyFindInList = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.FindInList"), _viewModel.HotkeyFindInList);
            var hotkeySwitchUser = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.SwitchUser"), _viewModel.HotkeySwitchUser);
            // Блокировка сеансов ИБ (функция №20, Ctrl+Alt+L) и временная блокировка приложения (функция №19).
            var hotkeySessionLock = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.SessionLock"), _viewModel.HotkeySessionLock);
            var hotkeyLockApp = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.LockApp"), _viewModel.HotkeyLockApp);
            // Администрирование ИБ (Этап 6, функция №29 + консоль серверов).
            var hotkeyCheckIntegrity = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.CheckIntegrity"), _viewModel.HotkeyCheckIntegrity);
            var hotkeyServerConsole = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.ServerConsole"), _viewModel.HotkeyServerConsole);
            // Сохранение копии экрана (функция №30 StartManager).
            var hotkeyScreenshot = HotkeyRow(hotkeys, LocalizationManager.T("Settings.Hotkeys.Screenshot"), _viewModel.ScreenshotHotkey);
            // У автора последняя строка идёт без нижнего поля, а весь блок строк
            // несёт низ 12 (SettingsWindow.xaml:957 и 1039). У нас строки лежат
            // в общей панели, поэтому поле снимается у последней и добирается
            // отступом следующего заголовка.
            if (hotkeys.Children.Count > 0 && hotkeys.Children[^1] is Control lastHotkeyRow)
                lastHotkeyRow.Margin = new Thickness(0, 0, 0, 12);

            // Порядок слотов Alt+1…Alt+9, как в разметке WPF (SettingsWindow.xaml:1030):
            // заголовок, пояснение, список слотов и кнопки перестановки справа.
            hotkeys.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.Hotkeys.FavoritesOrder"),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 4, 0, 6)
            });
            var favoritesHint = Hint(LocalizationManager.T("Settings.Hotkeys.FavoritesOrderHint"));
            favoritesHint.Margin = new Thickness(0, 0, 0, 8);
            favoritesHint.Opacity = 1;
            ThemeBrushes.Bind(favoritesHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            hotkeys.Children.Add(favoritesHint);

            var favoriteSlots = new ObservableCollection<FavoriteSlotItem>(
                _viewModel.FavoriteHotkeyIds
                    .Select(key => new FavoriteSlotItem(key, _viewModel.FindByFavoriteKey(key)?.Name ?? key))
                    .ToList());
            void RenumberSlots()
            {
                for (var i = 0; i < favoriteSlots.Count; i++)
                    favoriteSlots[i].Number = i + 1;
            }
            RenumberSlots();

            var favoritesGrid = new Grid { MinHeight = 140, Margin = new Thickness(0, 0, 0, 12) };
            favoritesGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            favoritesGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var favoritesList = new ListBox
            {
                ItemsSource = favoriteSlots,
                SelectionMode = SelectionMode.Single,
                MinHeight = 140,
                MaxHeight = 220,
                BorderThickness = new Thickness(1)
            };
            // Фон и рамка карточки, как в разметке (SettingsWindow.xaml:1052).
            ThemeBrushes.Bind(favoritesList, ListBox.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(favoritesList, ListBox.BorderBrushProperty, "BorderColorBrush");
            favoritesList.ItemTemplate = new FuncDataTemplate<FavoriteSlotItem>((item, _) =>
            {
                // Номер слота стоит в карточке цветом избранного, со скруглением 4
                // и отступом 6 на 2, а до имени 10 (SettingsWindow.xaml:1050).
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2) };
                var badgeText = new TextBlock
                {
                    FontWeight = FontWeight.Bold,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    // Цвет из разметки: на жёлтой плашке текст всегда тёмный,
                    // иначе в тёмной теме он сливается (SettingsWindow.xaml:1061).
                    Foreground = new SolidColorBrush(Color.Parse("#1C1917"))
                };
                badgeText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(FavoriteSlotItem.Caption)));
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = badgeText
                };
                ThemeBrushes.Bind(badge, Border.BackgroundProperty, "FavoriteBrush");
                row.Children.Add(badge);
                var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                name.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(FavoriteSlotItem.Name)));
                row.Children.Add(name);
                return row;
            }, supportsRecycling: true);
            favoritesGrid.Children.Add(favoritesList);

            var favoriteButtons = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 6,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            var slotUp = new Button
            {
                Content = IconHelper.MakeIcon("IconArrowUp", 18),
                Padding = new Thickness(10, 6)
            };
            slotUp.Styled(ControlThemes.SecondaryButton);
            ToolTip.SetTip(slotUp, LocalizationManager.T("Settings.Hotkeys.MoveUpTooltip"));
            slotUp.Click += (_, _) =>
            {
                var idx = favoritesList.SelectedIndex;
                if (idx <= 0)
                    return;
                favoriteSlots.Move(idx, idx - 1);
                RenumberSlots();
                favoritesList.SelectedIndex = idx - 1;
            };
            var slotDown = new Button
            {
                Content = IconHelper.MakeIcon("IconArrowDown", 18),
                Padding = new Thickness(10, 6)
            };
            slotDown.Styled(ControlThemes.SecondaryButton);
            ToolTip.SetTip(slotDown, LocalizationManager.T("Settings.Hotkeys.MoveDownTooltip"));
            slotDown.Click += (_, _) =>
            {
                var idx = favoritesList.SelectedIndex;
                if (idx < 0 || idx >= favoriteSlots.Count - 1)
                    return;
                favoriteSlots.Move(idx, idx + 1);
                RenumberSlots();
                favoritesList.SelectedIndex = idx + 1;
            };
            favoriteButtons.Children.Add(slotUp);
            favoriteButtons.Children.Add(slotDown);
            Grid.SetColumn(favoriteButtons, 1);
            favoritesGrid.Children.Add(favoriteButtons);
            hotkeys.Children.Add(favoritesGrid);

            var tabHotkeys = MainTab("IconKeyboardOutline", "Settings.TabHotkeys",
                new ScrollViewer
                {
                    Content = hotkeys,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    // Отступы прокрутки из разметки (SettingsWindow.xaml:943).
                    Margin = new Thickness(4, 12, 4, 0),
                    Padding = new Thickness(0, 0, 4, 0)
                });

            // ===== О программе =====
            var about = BuildAboutTab();
            var tabAbout = MainTab("IconInformationOutline", "Settings.TabAbout",
                new ScrollViewer
                {
                    Content = about,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    // Отступ прокрутки из разметки (SettingsWindow.xaml:1486).
                    Margin = new Thickness(4, 12, 4, 0)
                });

            // Порядок вкладок по разметке (SettingsWindow.xaml:293 и далее):
            // платформы, отображение, оформление, клавиши, настройки, базы,
            // резервное копирование профиля, о программе. Девятой вкладки
            // «ibases.v8i» (xaml:1164) здесь нет: её содержимое лежит разделом
            // внутри «Баз», это расхождение старше правки и не закрыто.
            tabs.Items.Add(tabPlatforms);
            tabs.Items.Add(tabDisplay);
            tabs.Items.Add(tabAppearance);
            tabs.Items.Add(tabHotkeys);
            tabs.Items.Add(tabGeneral);
            tabs.Items.Add(tabBases);
            tabs.Items.Add(tabProfile);
            tabs.Items.Add(tabAbout);

            Grid.SetRow(tabs, 0);
            grid.Children.Add(tabs);

            // Подвал: верхний отступ 12 и зазор между кнопками 10
            // (SettingsWindow.xaml:1526-1527).
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
                Spacing = 10
            };
            // Подвал как в разметке WPF: зелёная «Сохранить» со значком дискеты
            // и красная контурная «Отмена» со значком крестика.
            // Цвета взяты из разметки WPF числами: зелёный фон сохранения и белый
            // текст на нём заданы там напрямую, ключей темы под них нет.
            var saveBrush = new SolidColorBrush(Color.Parse("#16A34A"));
            var onSaveBrush = Brushes.White;
            var okContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            okContent.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = UiMetrics.Scaled(16),
                Height = UiMetrics.Scaled(16),
                Data = IconHelper.Geometry("IconSave"),
                Stretch = Stretch.Uniform,
                Fill = onSaveBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            okContent.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Common.Save"),
                FontWeight = FontWeight.SemiBold,
                Foreground = onSaveBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            var ok = new Button
            {
                Content = okContent,
                // Числа из разметки (SettingsWindow.xaml:1527): у автора ширина
                // и высота жёсткие, а не минимум по содержимому.
                Width = 140,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                BorderThickness = new Thickness(0),
                IsDefault = true
            };
            PaintButtonStates(ok, saveBrush,
                new SolidColorBrush(Color.Parse("#15803D")),
                new SolidColorBrush(Color.Parse("#166534")));
            ok.Click += (_, _) =>
            {
                // Проверка дублей идёт первой: иначе при конфликте окно
                // остаётся открытым, а часть настроек уже на диске.
                // Имена действий в сообщении о дубле берутся не из подписей вкладки:
                // там они с двоеточием на конце и в предложение не встают. Набор
                // ключей тот же, что в массиве assigned проверки WPF
                // (Views/SettingsWindow.xaml.cs): номер строки там сдвинется
                // при первом же обновлении от автора, имя переменной нет.
                var assignments = new (string Action, Controls.HotkeyBox Box)[]
                {
                    (LocalizationManager.T("Main.Enterprise"), hotkeyEnterprise),
                    (LocalizationManager.T("Main.SectionConfigurator"), hotkeyConfigurator),
                    (LocalizationManager.T("Main.Favorites"), hotkeyFavorite),
                    (LocalizationManager.T("Main.EditShort"), hotkeyEdit),
                    (LocalizationManager.T("Common.Delete"), hotkeyDelete),
                    (LocalizationManager.T("Main.ClearCache"), hotkeyClearCache),
                    (LocalizationManager.T("Main.AddBase"), hotkeyAdd),
                    (LocalizationManager.T("Main.Pin"), hotkeyPin),
                    (LocalizationManager.T("Main.AllBasesTooltip"), hotkeyShowAll),
                    (LocalizationManager.T("Main.FavoritesTooltip"), hotkeyShowFavorites),
                    (LocalizationManager.T("Main.RecentTooltip"), hotkeyShowRecent),
                    (LocalizationManager.T("Main.ClearSearch"), hotkeyClearSearch),
                    (LocalizationManager.T("Main.ClearTags"), hotkeyClearTags),
                    // Панель информации (Ctrl+D, issue #172) участвует в проверке
                    // дублей, как и в Windows-версии (SettingsWindow.xaml.cs).
                    (LocalizationManager.T("Main.CollapseRightPanel"), hotkeyRightPanelDetails),
                    (LocalizationManager.T("SessionLock.Title"), hotkeySessionLock),
                    (LocalizationManager.T("AppLock.LockTitle"), hotkeyLockApp),
                    (LocalizationManager.T("Admin.CheckIntegrityTitle"), hotkeyCheckIntegrity),
                    (LocalizationManager.T("Admin.ServerConsoleTitle"), hotkeyServerConsole),
                    (LocalizationManager.T("Settings.Screenshot.Title"), hotkeyScreenshot)
                };

                if (!ValidateHotkeys(assignments))
                    return;

                if (langBox.SelectedItem is LanguageInfo li &&
                    !string.Equals(li.Code, LocalizationManager.Instance.CurrentLanguage, StringComparison.Ordinal))
                {
                    _viewModel.ApplyLanguage(li.Code);
                }

                // Схема запоминается активной, иначе правка цветов держалась бы
                // только до перезапуска.
                _viewModel.ApplyColorScheme(editedScheme);

                _viewModel.ApplyPlatformSettings(paths, archBox.SelectedIndex switch
                {
                    1 => "X86",
                    2 => "Priority",
                    _ => "X64"
                });
                _viewModel.ApplyBehaviorSettings(
                    multipleInstancesCheck.IsChecked == true,
                    rememberLayoutCheck.IsChecked == true,
                    checkUpdatesCheck.IsChecked == true,
                    autoUpdateCheck.IsChecked == true);
                _viewModel.ApplyTraySettings(
                    trayIconCheck.IsChecked == true,
                    closeToTrayCheck.IsChecked == true,
                    escapeToTrayCheck.IsChecked == true);
                _viewModel.ApplyTemplateCatalogPaths(templatePaths);
                ApplyExportFileNameSettings();

                _viewModel.AfterLaunchAction = afterLaunchBox.SelectedIndex switch
                {
                    0 => Models.AfterLaunchAction.None.ToSettingString(),
                    1 => Models.AfterLaunchAction.Minimize.ToSettingString(),
                    2 => Models.AfterLaunchAction.MinimizeToTray.ToSettingString(),
                    3 => Models.AfterLaunchAction.Close.ToSettingString(),
                    // Ничего не выбрано: значение остаётся прежним, как в WPF-версии.
                    _ => _viewModel.AfterLaunchAction
                };

                // Имя COM-коннектора 1С по шаблону версии платформы (issue #175).
                _viewModel.ComConnectorNameTemplate = comTemplateBox.Text?.Trim() ?? "";
                // Таймаут определения свойств конфигурации через COM (issue #174).
                if (int.TryParse(detectTimeoutBox.Text, out var detectTimeout))
                    _viewModel.ComDetectTimeoutMs = detectTimeout;

                // Авторизация на сайте 1С при проверке обновлений конфигураций (HTTP Basic Auth).
                _viewModel.UpdatesLogin = updatesLoginBox.Text?.Trim() ?? "";
                _viewModel.UpdatesPassword = updatesPasswordBox.Password ?? "";

                // Глубина истории запусков одной базы (issue #246).
                if (int.TryParse(historyDepthBox.Text, out var historyDepth))
                    _viewModel.MaxLaunchHistoryPerBase = historyDepth;

                _viewModel.ApplyIbasesSyncSettings(
                    syncModeBox.SelectedIndex >= 0 ? syncModes[syncModeBox.SelectedIndex].Mode : IbasesSyncMode.None,
                    fileBox.Text?.Trim() ?? string.Empty,
                    triggerBox.SelectedIndex >= 0 ? triggers[triggerBox.SelectedIndex].Trigger : IbasesSyncTrigger.OnStartup,
                    int.TryParse(intervalBox.Text, out var interval) && interval > 0 ? interval : 30,
                    scheduleBox.Text?.Trim() ?? string.Empty,
                    backupCheck.IsChecked == true,
                    int.TryParse(keepBox.Text, out var keep) && keep > 0 ? keep : 5,
                    saveAfterEditCheck?.IsChecked == true);

                _viewModel.ApplyProfileBackupSettings(profileDirBox.Text, profileRestoreCheck.IsChecked == true);

                // Список слотов в окне это снимок, снятый при его построении.
                // Пока окно живёт, состав избранного могли изменить импорт,
                // автосинхронизация или само главное окно, если окно настроек
                // открыто немодально из трея. Поэтому снимок не пишется целиком,
                // а накладывается на текущее состояние: сохраняется заданный
                // пользователем порядок тех слотов, что ещё есть, а появившиеся
                // за это время дописываются в конец и не теряются.
                var currentSlots = _viewModel.FavoriteHotkeyIds;
                var orderedSlots = favoriteSlots
                    .Select(slot => slot.Key)
                    .Where(currentSlots.Contains)
                    .Concat(currentSlots.Where(key => favoriteSlots.All(slot => slot.Key != key)))
                    .ToList();
                _viewModel.SetFavoriteHotkeyOrder(orderedSlots);

                _viewModel.ApplyHotkeys(
                    hotkeyEnterprise.Value, hotkeyConfigurator.Value, hotkeyEdit.Value, hotkeyAdd.Value,
                    hotkeyFavorite.Value, hotkeyPin.Value, hotkeyDelete.Value, hotkeyClearCache.Value,
                    hotkeyShowAll.Value, hotkeyShowFavorites.Value, hotkeyShowRecent.Value,
                    hotkeyClearSearch.Value, hotkeyClearTags.Value, hotkeyRightPanelDetails.Value,
                    hotkeySwitchUser.Value, hotkeyFindInList.Value, hotkeySessionLock.Value, hotkeyLockApp.Value,
                    hotkeyCheckIntegrity.Value, hotkeyServerConsole.Value);

                // Копия экрана (функция №30) и автозапуск при старте ОС (функция №31, Этап 8).
                _viewModel.ScreenshotHotkey = hotkeyScreenshot.Value ?? "";
                _viewModel.ScreenshotSaveDirectory = screenshotDirBox.Text?.Trim() ?? "";
                _viewModel.ApplyAutoStart(autoStartCheck.IsChecked == true);

                // Настройки отображения применяются и сохраняются одним вызовом.
                // Видимость колонок читается из тех же элементов списка, где
                // задаётся и порядок: флажок каждой строки и есть её видимость.
                bool VisibleOf(string key) => orderItems.FirstOrDefault(o => o.Key == key)?.Visible ?? true;

                _viewModel.ApplyDisplaySettings(
                    favoritesCheck.IsChecked == true,
                    pinnedCheck.IsChecked == true,
                    tagsCheck.IsChecked == true,
                    tagPanelCheck.IsChecked == true,
                    VisibleOf("Version"),
                    VisibleOf("Configuration"),
                    VisibleOf("ConfigurationVersion"),
                    VisibleOf("LaunchMode"),
                    VisibleOf("ServerBase"),
                    VisibleOf("LastLaunch"),
                    VisibleOf("Size"),
                    VisibleOf("Modified"),
                    // Видимость колонки «Действия» (issue #158): раньше она не
                    // передавалась и колонку нельзя было ни скрыть, ни показать.
                    VisibleOf("Actions"),
                    rightPanelCheck.IsChecked == true,
                    sessionPanelCheck.IsChecked == true,
                    groupByGroupCheck.IsChecked == true,
                    emptyGroupsCheck.IsChecked == true,
                    orderItems.Select(o => o.Key).ToList());

                // «Только избранные» это режим списка, а не отдельный фильтр:
                // снятие флажка возвращает список к показу всех баз, но режим
                // «недавние» не трогает, иначе флажок отменял бы чужой выбор.
                if (favoritesOnlyCheck.IsChecked == true)
                    _viewModel.IsListModeFavorites = true;
                else if (_viewModel.IsListModeFavorites)
                    _viewModel.IsListModeAll = true;

                // Системный заголовок окна (issue #159): настройка сохраняется и применяется
                // к главному окну сразу, без перезапуска; кэш модальных окон сбрасывается,
                // чтобы и новые диалоги взяли свежее значение.
                _viewModel.UseSystemTitleBar = systemTitleBarCheck.IsChecked == true;
                _viewModel.ApplySystemTitleBar(systemTitleBarCheck.IsChecked == true);

                _viewModel.ApplyStatusBarSettings(
                    statusPathCheck.IsChecked == true,
                    statusArchCheck.IsChecked == true,
                    statusLaunchModeCheck.IsChecked == true,
                    statusPortCheck.IsChecked == true,
                    statusVersionCheck.IsChecked == true,
                    statusClientTypeCheck.IsChecked == true,
                    statusConnectionTypeCheck.IsChecked == true,
                    statusUserCheck.IsChecked == true,
                    statusIdCheck.IsChecked == true);

                _viewModel.SaveElementFonts(editedFonts);

                DialogResult = true;
                Close();
            };
            // Красный в этом проекте задан числом, а не ключом темы: в разметке WPF
            // отмена нарисована цветом #EF4444 напрямую, кисти под него нет ни там,
            // ни здесь.
            var dangerBrush = new SolidColorBrush(Color.Parse("#EF4444"));
            var cancelContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            cancelContent.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = UiMetrics.Scaled(16),
                Height = UiMetrics.Scaled(16),
                Data = IconHelper.Geometry("IconClose"),
                Stretch = Stretch.Uniform,
                Fill = dangerBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            cancelContent.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Common.Cancel"),
                FontWeight = FontWeight.SemiBold,
                Foreground = dangerBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            var cancel = new Button
            {
                Content = cancelContent,
                Width = 140,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                BorderThickness = new Thickness(1.5),
                IsCancel = true
            };
            // У автора у отмены только наведение, нажатого состояния нет
            // (SettingsWindow.xaml:1560), поэтому цвет нажатия равен наведению.
            PaintButtonStates(cancel, Brushes.Transparent,
                new SolidColorBrush(Color.Parse("#FEF2F2")),
                new SolidColorBrush(Color.Parse("#FEF2F2")));
            cancel.BorderBrush = dangerBrush;
            // Отмена закрывает окно так же, как крестик: DialogResult остаётся
            // ложным, и вызывающая сторона ничего не применяет.
            cancel.Click += (_, _) => Close();

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);

            return grid;
        }

        private static IBrush ParseBrush(string value)
        {
            try { return new SolidColorBrush(Color.Parse(value)); }
            catch (Exception) { return Brushes.Transparent; }
        }



        private static Border NewNavItem(TextBlock text) => new()
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 4),
            Margin = new Thickness(0, 0, 0, 4),
            Child = text
        };



        /// <summary>Чёрный или белый — цвет с максимальным контрастом к заданному.</summary>
        private static Color ContrastColor(Color c)
        {
            var lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            return lum > 0.5 ? Colors.Black : Colors.White;
        }

        private static void PaintSolid(Border b, string hex) => b.Background = ParseBrush(hex);

        private static void PaintBorder(Border b, string bg, string bd)
        {
            b.Background = ParseBrush(bg);
            b.BorderBrush = ParseBrush(bd);
        }

        private static void PaintText(TextBlock t, string hex) => t.Foreground = ParseBrush(hex);

        private static void PaintTextBrush(TextBlock t, IBrush brush) => t.Foreground = brush;

        private static void PaintTextBox(TextBox t, string bg, string bd, string fg)
        {
            t.Background = ParseBrush(bg);
            t.BorderBrush = ParseBrush(bd);
            t.Foreground = ParseBrush(fg);
        }

        /// <summary>Запрашивает имя схемы отдельным окном ввода.</summary>
        private string? AskName(string title, string initial)
        {
            var dialog = new NameInputWindow(title, LocalizationManager.T("NameInput.Prompt"),
                LocalizationManager.T("Common.Ok"), initial);
            if (!dialog.ShowDialogSync(this))
                return null;

            var name = dialog.Result?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        /// <summary>
        /// Красит кнопку с учётом наведения и нажатия. Своим свойством Background
        /// этого не добиться: тема Fluent задаёт состояния вложенным стилем
        /// на части шаблона и перекрывает значение кнопки.
        /// </summary>
        private static void PaintButtonStates(Button button, IBrush normal, object hover, object pressed)
        {
            button.Background = normal;
            foreach (var (state, brush) in new[] { (":pointerover", hover), (":pressed", pressed) })
            {
                var style = new Style(x => x.OfType<Button>().Class(state)
                    .Template().OfType<ContentPresenter>());
                style.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, brush));
                button.Styles.Add(style);
            }
        }


        /// <summary>
        /// Вложенная вкладка раздела настроек: значок и подпись в заголовке,
        /// содержимое в своей прокрутке. Повторяет заголовки подвкладок WPF.
        /// </summary>
        private static TabItem SubTab(string titleKey, string tooltipKey, string iconKey, Control content)
        {
            // Кегль и насыщенность подписи задаёт тема SettingsSubTabItem,
            // как стиль разметки (SettingsWindow.xaml:227).
            var tab = new TabItem
            {
                Content = new ScrollViewer
                {
                    Content = content,
                    Padding = new Thickness(8, 4, 4, 4),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            };
            tab.Styled(ControlThemes.SettingsSubTabItem);
            // Кегль подписи масштабируется компактным режимом: с полным кеглем
            // пять вкладок не влезают в ряд, а перенос строки у UniformGrid
            // невозможен. Местное значение старше темы, поэтому ставится здесь.
            tab.FontSize = UiMetrics.ScaledFont(12);

            var icon = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(14), out var path);
            path.Bind(Avalonia.Controls.Shapes.Shape.FillProperty,
                new Avalonia.Data.Binding(nameof(TabItem.Foreground)) { Source = tab });

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    icon,
                    new TextBlock
                    {
                        Text = LocalizationManager.T(titleKey),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
            ToolTip.SetTip(header, LocalizationManager.T(tooltipKey));
            tab.Header = header;
            return tab;
        }

        /// <summary>
        /// Карточка группы настроек по шаблону GroupBox из тем автора
        /// (DarkTheme.xaml:821): шапка с заголовком и тело под ней, общая рамка
        /// и скругление 6.
        /// </summary>
        private static Control SettingsGroup(string header, Control content, Thickness padding, double bottom)
        {
            var title = new TextBlock { Text = header, FontWeight = FontWeight.SemiBold };
            ThemeBrushes.Bind(title, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var headerBorder = new Border
            {
                Child = title,
                Padding = new Thickness(8, 4),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            ThemeBrushes.Bind(headerBorder, Border.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(headerBorder, Border.BorderBrushProperty, "BorderColorBrush");

            var bodyBorder = new Border
            {
                Child = content,
                Padding = padding,
                BorderThickness = new Thickness(1, 0, 1, 1),
                CornerRadius = new CornerRadius(0, 0, 6, 6)
            };
            ThemeBrushes.Bind(bodyBorder, Border.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(bodyBorder, Border.BorderBrushProperty, "BorderColorBrush");
            // Цвет текста содержимого задаёт сама карточка, как TextElement.Foreground
            // в шаблоне (DarkTheme.xaml:843): иначе подписи внутри достаются
            // от штатной темы и не следуют за цветовой схемой.
            ThemeBrushes.Bind(bodyBorder, Avalonia.Controls.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");

            var grid = new Grid { Margin = new Thickness(0, 0, 0, bottom) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            Grid.SetRow(bodyBorder, 1);
            grid.Children.Add(headerBorder);
            grid.Children.Add(bodyBorder);
            return grid;
        }

        /// <summary>
        /// Содержимое кнопки из разметки: цветной значок 16 и подпись рядом,
        /// зазор 6 (SettingsWindow.xaml:394 и далее).
        /// </summary>
        private static Control IconTextContent(string iconKey, string iconColor, string textKey)
        {
            var icon = IconHelper.MakeIcon(iconKey, 16, new SolidColorBrush(Color.Parse(iconColor)));
            icon.Margin = new Thickness(0, 0, 6, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    icon,
                    new TextBlock
                    {
                        Text = LocalizationManager.T(textKey),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
        }


        /// <summary>Заголовок группы настроек на вкладке.</summary>
        private static TextBlock GroupTitle(string text) => new()
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 12, 0, 2)
        };


        /// <summary>
        /// Подпись и ссылка под ней. Ссылка открывается системным обработчиком:
        /// в версии для Windows это делает OnAboutLink_Click, которого
        /// в Linux-сборке нет.
        /// </summary>
        private StackPanel LinkBlock(string caption, string url)
        {
            var block = new StackPanel();
            var captionBlock = new TextBlock
            {
                Text = caption,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            ThemeBrushes.Bind(captionBlock, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            block.Children.Add(captionBlock);

            var link = new TextBlock
            {
                Text = url,
                TextDecorations = TextDecorations.Underline,
                Cursor = new Cursor(StandardCursorType.Hand),
                TextWrapping = TextWrapping.Wrap,
                // По умолчанию TextBlock растягивается на всю ширину строки,
                // и тогда Bounds шире нарисованного текста. Проверка попадания
                // ниже считает по Bounds, поэтому ширина прижимается к тексту.
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ThemeBrushes.Bind(link, TextBlock.ForegroundProperty, "AccentBrush");
            link.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left)
                    return;
                // Отпускание вне текста щелчком не считается: Avalonia
                // захватывает указатель при нажатии, и без этой проверки
                // ссылка срабатывала после перетаскивания далеко в сторону.
                // В версии для Windows этого нет: там MouseLeftButtonUp
                // без захвата, и отпускание вне элемента до ссылки не доходит.
                var point = e.GetPosition(link);
                if (point.X < 0 || point.Y < 0
                    || point.X > link.Bounds.Width || point.Y > link.Bounds.Height)
                    return;
                if (!Services.OneCLauncher.OpenUrl(url))
                    ShowAboutMessage(LocalizationManager.T("Settings.About.LinkOpenFailed"));
            };
            block.Children.Add(link);
            return block;
        }

        /// <summary>
        /// Пояснение под заголовком: кегль 12 и вторичный цвет темы, как
        /// в разметке (SettingsWindow.xaml:495 и далее). Нижний отступ там
        /// разный по местам, поэтому задаётся вызывающим кодом.
        /// </summary>
        private static TextBlock Hint(string text, double bottom = 4)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, bottom)
            };
            Themes.ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return block;
        }

        /// <summary>
        /// Вкладка окна: значок 18 и подпись, содержимое как есть.
        /// Значок красится подписью, как в разметке (SettingsWindow.xaml:296).
        /// </summary>
        private static TabItem MainTab(string iconKey, string titleKey, Control content)
        {
            var tab = new TabItem { Content = content };
            tab.Styled(ControlThemes.SettingsTabItem);
            // Ширина, кегль и значок вкладки уменьшаются в компактном режиме.
            tab.Width = UiMetrics.Scaled(235);
            tab.FontSize = UiMetrics.ScaledFont(13);

            var icon = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(18), out var path);
            path.Bind(Avalonia.Controls.Shapes.Shape.FillProperty,
                new Avalonia.Data.Binding(nameof(TabItem.Foreground)) { Source = tab });
            icon.Margin = new Thickness(0, 0, 8, 0);

            tab.Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    icon,
                    new TextBlock { Text = LocalizationManager.T(titleKey), VerticalAlignment = VerticalAlignment.Center }
                }
            };
            return tab;
        }

        /// <summary>
        /// Переключатель настройки: подпись слева, дорожка справа. В разметке
        /// это ToggleButton со стилем SettingsToggle, а не флажок.
        /// </summary>
        private static ToggleButton SettingsSwitch(string textKey, bool value,
            string? iconKey = null, string? iconColor = null, string? helpTextKey = null)
        {
            var caption = new TextBlock
            {
                Text = LocalizationManager.T(textKey),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            // Значок слева от подписи и его цвет заданы в разметке числом
            // у каждого переключателя (SettingsWindow.xaml:500 и далее).
            Control content;
            if (iconKey is null && helpTextKey is null)
            {
                content = caption;
            }
            else
            {
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { caption }
                };
                if (iconKey is not null)
                {
                    var icon = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(16),
                        new SolidColorBrush(Color.Parse(iconColor ?? "#94A3B8")));
                    icon.Margin = new Thickness(0, 0, 8, 0);
                    panel.Children.Insert(0, icon);
                }
                // Пояснение пункта выносим под знак «?» рядом с подписью (как в WPF).
                if (helpTextKey is not null)
                {
                    panel.Children.Add(new HelpLink
                    {
                        HelpText = LocalizationManager.T(helpTextKey),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0)
                    });
                }
                content = panel;
            }

            var toggle = new ToggleButton
            {
                Content = content,
                IsChecked = value
            };
            toggle.Styled(ControlThemes.SettingsToggle);
            return toggle;
        }

        private static ToggleButton DisplayCheck(string textKey, bool value,
            string? iconKey = null, string? iconColor = null)
        {
            var toggle = SettingsSwitch(textKey, value, iconKey, iconColor);
            // Нижнее поле у всех переключателей раздела одинаковое
            // (SettingsWindow.xaml:498 и далее).
            toggle.Margin = new Thickness(0, 0, 0, 6);
            return toggle;
        }

        private static void ThemeChanged(
            RadioButton light, RadioButton dark, MainViewModel viewModel,
            Action<string> onTheme)
        {
            light.IsCheckedChanged += (_, _) =>
            {
                if (light.IsChecked != true)
                    return;
                onTheme(ThemeManager.LightThemeName);
            };
            dark.IsCheckedChanged += (_, _) =>
            {
                if (dark.IsChecked != true)
                    return;
                onTheme(ThemeManager.DarkThemeName);
            };
        }

        /// <summary>Радиокнопка с TwoWay-привязкой к свойству ViewModel (режим сессии).</summary>
        private RadioButton Radio(string groupName, string path, string content)
        {
            var r = new RadioButton { Content = content, GroupName = groupName, Margin = new Thickness(0, 0, 12, 0) };
            r.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Avalonia.Data.Binding(path) { Mode = Avalonia.Data.BindingMode.TwoWay });
            return r;
        }


        private Control BuildAboutTab()
        {
            // Отступы между элементами заданы поштучно, как в разметке
            // (SettingsWindow.xaml:1487-1519), а не общим зазором панели.
            var panel = new StackPanel();

            // Номер версии без суффикса «+<sha>» из InformationalVersion.
            var infoVersion = VersionInfo.Display();
            // Название берётся из ключа локализации, как в разметке
            // (SettingsWindow.xaml:1489): из атрибута сборки оно не переводится
            // и при английском языке осталось бы русским.
            var title = LocalizationManager.T("App.Title");

            // Название и справка по приложению в одной строке, как в разметке WPF
            // (SettingsWindow.xaml:1488-1494).
            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushes.Bind(titleBlock, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            titleRow.Children.Add(titleBlock);
            titleRow.Children.Add(new Controls.HelpLink
            {
                // Свой текст без строки про версию: в общем ключе она вписана
                // строкой и отстала (0.3.5.1 против 0.3.5.10), а живая версия
                // печатается здесь же строкой ниже. Windows-сторона это
                // расхождение подтвердила у себя.
                HelpText = LocalizationManager.T("Settings.About.HelpTextLinux"),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(titleRow);

            // Версия и кнопка ручной проверки обновлений в одной строке, как в
            // разметке WPF (SettingsWindow.xaml:1725-1738). Раньше кнопки здесь
            // не было (issue #228) — проверка была доступна только при запуске.
            var versionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };
            versionRow.Children.Add(new TextBlock
            {
                Text = string.Format(LocalizationManager.T("Settings.About.Version"), infoVersion),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });
            var checkCaption = new TextBlock
            {
                Text = LocalizationManager.T("Settings.About.CheckForUpdates"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            var checkIcon = IconHelper.MakeIcon("IconRefresh", 14,
                new SolidColorBrush(Color.Parse("#3B82F6")));
            checkIcon.VerticalAlignment = VerticalAlignment.Center;
            checkIcon.Margin = new Thickness(0, 0, 5, 0);
            var checkButton = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { checkIcon, checkCaption }
                },
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            checkButton.Styled(ControlThemes.SecondaryButton);
            checkButton.Click += async (_, _) =>
            {
                try
                {
                    var updateService = AppServices.GetRequiredService<UpdateService>();
                    await updateService.CheckForUpdatesManualAsync();
                }
                catch
                {
                    // Внутренние ошибки уже показаны в UpdateService; здесь только страхуемся.
                }
            };
            versionRow.Children.Add(checkButton);
            panel.Children.Add(versionRow);

            var authorBlock = new TextBlock
            {
                Text = LocalizationManager.T("Settings.About.Author"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 16)
            };
            ThemeBrushes.Bind(authorBlock, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(authorBlock);

            // Подписи и ссылки на публикацию и репозиторий, как в разметке WPF
            // (SettingsWindow.xaml:1497-1510). В версии для Windows их открывает
            // обработчик под #if WINDOWS, здесь используется системный xdg-open.
            var infostart = LinkBlock(LocalizationManager.T("Settings.About.Infostart"),
                "https://infostart.ru/1c/tools/2764888/");
            infostart.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(infostart);
            panel.Children.Add(LinkBlock(LocalizationManager.T("Settings.About.GitHub"),
                "https://github.com/sivatorov/ConfigurationManagement"));

            // Спонсорская картинка во вкладке «О программе», встроенная в ресурсы
            // сборки (см. donat.png в csproj), как в разметке WPF. По клику
            // открывается в полном размере.
            if (TryLoadDonatImage() is { } donat)
            {
                var donatImage = new Image
                {
                    Source = donat,
                    MaxWidth = 240,
                    MaxHeight = 320,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 0),
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                donatImage.Tapped += async (_, _) => await ShowDonatImageFullAsync(this);
                panel.Children.Add(donatImage);
            }

            panel.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.About.AvaloniaText"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Margin = new Thickness(0, 16, 0, 0)
            });

            panel.Children.Add(new TextBlock
            {
                // Разрядность печатается как x64 или x86: булево значение
                // выводилось словом True и не переводилось. Каталог данных
                // берётся у той же службы, что и остальное приложение, иначе
                // на нестандартном XDG_CONFIG_HOME он расходится с настоящим.
                Text = string.Format(LocalizationManager.T("Settings.About.RuntimeInfo"),
                           Environment.OSVersion,
                           Environment.Is64BitOperatingSystem ? "x64" : "x86") + "\n" +
                       string.Format(LocalizationManager.T("Settings.About.DataDir"),
                           Services.PlatformPaths.AppDataDirectory),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 8, 0, 0)
            });

            // Кнопка копирования по разметке (SettingsWindow.xaml:1511-1518):
            // вторичная кнопка, отступ сверху 24, значок копирования 16 синим
            // с зазором 6 до подписи.
            var copyCaption = new TextBlock
            {
                Text = LocalizationManager.T("Settings.About.CopyTechInfo"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var copyIcon = IconHelper.MakeIcon("IconCopy", 16, new SolidColorBrush(Color.Parse("#3B82F6")));
            copyIcon.VerticalAlignment = VerticalAlignment.Center;
            copyIcon.Margin = new Thickness(0, 0, 6, 0);
            var copyButton = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { copyIcon, copyCaption }
                },
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 24, 0, 0),
                Padding = new Thickness(14, 8)
            };
            copyButton.Styled(ControlThemes.SecondaryButton);
            copyButton.Click += async (_, _) =>
            {
                try
                {
                    var text = TechnicalInfoService.Collect();
                    if (this.Clipboard is { } cb)
                        await cb.SetTextAsync(text);
                    ShowAboutMessage(LocalizationManager.T("Settings.About.TechInfoCopied"));
                }
                catch (Exception ex)
                {
                    // У автора это окно ошибки с текстом исключения
                    // (SettingsWindow.Platforms.cs:361), а не сообщение.
                    ShowAboutError(LocalizationManager.T("Settings.About.TechInfoCopyFailed")
                        + "\n" + ex.Message);
                }
            };
            panel.Children.Add(copyButton);

            return panel;
        }

        /// <summary>Окно ошибки вкладки «О программе» с текстом исключения.</summary>
        private void ShowAboutError(string message)
        {
            var win = new MaterialMessageWindowAvalonia(message, LocalizationManager.T("Common.Error"), MaterialMessageKind.Error)
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            _ = win.ShowDialog(this);
        }

        /// <summary>
        /// Загружает спонсорскую картинку «О программе» (donat.png) из встроенных
        /// ресурсов сборки, либо null, если ресурс отсутствует или не декодируется.
        /// </summary>
        private static Bitmap? TryLoadDonatImage()
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("donat.png");
                if (stream is not null)
                    return new Bitmap(stream);
            }
            catch
            {
                // Ресурс отсутствует или не декодируется — изображение просто не показывается.
            }
            return null;
        }

        /// <summary>
        /// Открывает спонсорскую картинку «О программе» (donat.png) в полном размере
        /// в отдельном окне с прокруткой, если картинка больше окна.
        /// </summary>
        private async Task ShowDonatImageFullAsync(Window owner)
        {
            if (TryLoadDonatImage() is not { } bmp) return;

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Image { Source = bmp, Stretch = Stretch.None }
            };

            var win = new Window
            {
                Title = "donat.png",
                Background = Brushes.Black,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = scroll,
                // Окно ограничено ~1000 px; при большем размере картинки появляется прокрутка.
                Width = Math.Min(bmp.PixelSize.Width, 1000),
                Height = Math.Min(bmp.PixelSize.Height, 1000)
            };

            await win.ShowDialog(owner);
        }

        /// <summary>
        /// Показывает информационное окно поверх текущего окна настроек.
        /// </summary>
        private void ShowAboutMessage(string message)
        {
            var win = new MaterialMessageWindowAvalonia(message, LocalizationManager.T("Common.Information"), MaterialMessageKind.Info)
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            _ = win.ShowDialog(this);
        }


    }
}
#endif