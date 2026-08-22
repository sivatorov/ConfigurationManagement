#if LINUX
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог настроек приложения (Avalonia/Linux). Портированы ключевые вкладки:
    /// «Настройки», «Клавиши», «О программе». Полноценные вкладки «Отображение»,
    /// «Платформы», «ibases.v8i», «Базы» и редактор цветовых схем требуют
    /// публичного API сохранения настроек в Avalonia-версии <see cref="MainViewModel"/>
    /// (отложено — см. комментарии и итоговый отчёт).
    /// </summary>
    public class SettingsWindow : ModalWindowBase
    {
        private readonly MainViewModel _viewModel;

        /// <summary>
        /// Создаёт диалог настроек приложения.
        /// </summary>
        /// <param name="viewModel">Главная модель представления приложения.</param>
        public SettingsWindow(MainViewModel viewModel)
        {
            Title = LocalizationManager.T("Settings.Title");
            Width = 720;
            Height = 580;
            MinWidth = 640;
            MinHeight = 520;

            _viewModel = viewModel;
            Content = BuildRoot();
        }

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var tabs = new TabControl();

            // ===== Настройки =====
            var settings = new StackPanel { Spacing = 14 };

            // Тема оформления
            var themeLabel = new TextBlock { Text = LocalizationManager.T("Settings.ThemeLabel"), FontWeight = FontWeight.SemiBold };
            settings.Children.Add(themeLabel);
            var themePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            var lightTheme = new RadioButton { Content = LocalizationManager.T("Main.LightTheme"), GroupName = "Theme", IsChecked = !ThemeManager.CurrentScheme.IsDark };
            var darkTheme = new RadioButton { Content = LocalizationManager.T("Main.DarkTheme"), GroupName = "Theme", IsChecked = ThemeManager.CurrentScheme.IsDark };
            ThemeChanged(lightTheme, darkTheme);
            themePanel.Children.Add(lightTheme);
            themePanel.Children.Add(darkTheme);
            settings.Children.Add(themePanel);

            // Язык интерфейса
            settings.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.Language") + ":",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 8, 0, 4)
            });
            var langBox = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
            langBox.ItemsSource = LocalizationManager.Instance.AvailableLanguages;
            langBox.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
            langBox.SelectedItem = LocalizationManager.Instance.AvailableLanguages
                .FirstOrDefault(l => l.Code == LocalizationManager.Instance.CurrentLanguage);
            settings.Children.Add(langBox);
            settings.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.LanguageHint"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });

            // Компактный режим интерфейса.
            var compactToggle = new CheckBox
            {
                Content = LocalizationManager.T("Settings.CompactMode"),
                IsChecked = _viewModel.CompactMode,
                Margin = new Thickness(0, 8, 0, 4)
            };
            compactToggle.IsCheckedChanged += (_, _) =>
            {
                var value = compactToggle.IsChecked == true;
                _viewModel.CompactMode = value;
                _viewModel.ApplyCompactMode(value);
            };
            settings.Children.Add(compactToggle);

            // Параметры текущей сессии
            settings.Children.Add(new TextBlock { Text = LocalizationManager.T("Settings.DefaultClientLabel"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            var clientPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            clientPanel.Children.Add(Radio("SessionClient", "IsSessionClientAuto", LocalizationManager.T("Main.SessionClientAuto")));
            clientPanel.Children.Add(Radio("SessionClient", "IsSessionClientThin", LocalizationManager.T("Main.SessionClientThin")));
            clientPanel.Children.Add(Radio("SessionClient", "IsSessionClientThick", LocalizationManager.T("Main.SessionClientThickManaged")));
            clientPanel.Children.Add(Radio("SessionClient", "IsSessionClientThickOrdinary", LocalizationManager.T("Main.SessionClientThickOrdinary")));
            clientPanel.Children.Add(Radio("SessionClient", "IsSessionClientOrdinary", LocalizationManager.T("Main.SessionClientOrdinary")));
            settings.Children.Add(clientPanel);

            settings.Children.Add(new TextBlock { Text = LocalizationManager.T("Settings.DefaultArch"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            var archPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            archPanel.Children.Add(Radio("SessionArch", "IsSessionArchAuto", LocalizationManager.T("Main.SessionClientAuto")));
            archPanel.Children.Add(Radio("SessionArch", "IsSessionArch32", "32"));
            archPanel.Children.Add(Radio("SessionArch", "IsSessionArch64", "64"));
            settings.Children.Add(archPanel);

            settings.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.AvaloniaPendingTabs"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });

            tabs.Items.Add(new TabItem { Header = LocalizationManager.T("Settings.TabGeneral"), Content = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            // ===== Клавиши =====
            var hotkeys = new StackPanel { Spacing = 10 };
            hotkeys.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.Hotkeys.Title"),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var rows = new (string Action, string Key)[]
            {
                (LocalizationManager.T("Main.LaunchEnterprise"), _viewModel.HotkeyEnterprise),
                (LocalizationManager.T("Main.SectionConfigurator"), _viewModel.HotkeyConfigurator),
                (LocalizationManager.T("Main.EditSettings"), _viewModel.HotkeyEdit),
                (LocalizationManager.T("Main.AddBaseOrGroup"), _viewModel.HotkeyAdd),
                (LocalizationManager.T("Main.Favorites"), _viewModel.HotkeyFavorite),
                (LocalizationManager.T("Main.Pin"), _viewModel.HotkeyPin),
                (LocalizationManager.T("Common.Delete"), _viewModel.HotkeyDelete),
                (LocalizationManager.T("Main.ClearCache"), _viewModel.HotkeyClearCache)
            };
            foreach (var (action, key) in rows)
                hotkeys.Children.Add(BuildHotkeyRow(action, key));

            hotkeys.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.AvaloniaHotkeysPending"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });

            tabs.Items.Add(new TabItem { Header = LocalizationManager.T("Settings.TabHotkeys"), Content = new ScrollViewer { Content = hotkeys, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            // ===== О программе =====
            var about = BuildAboutTab();
            tabs.Items.Add(new TabItem { Header = LocalizationManager.T("Settings.TabAbout"), Content = new ScrollViewer { Content = about, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            Grid.SetRow(tabs, 0);
            grid.Children.Add(tabs);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };
            var ok = new Button { Content = LocalizationManager.T("Common.Ok"), MinWidth = 110, IsDefault = true };
            ok.Click += (_, _) =>
            {
                if (langBox.SelectedItem is LanguageInfo li &&
                    !string.Equals(li.Code, LocalizationManager.Instance.CurrentLanguage, StringComparison.Ordinal))
                {
                    _viewModel.ApplyLanguage(li.Code);
                }
                DialogResult = true;
                Close();
            };
            buttons.Children.Add(ok);
            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);

            return grid;
        }

        private static void ThemeChanged(RadioButton light, RadioButton dark)
        {
            light.IsCheckedChanged += (_, _) =>
            {
                if (light.IsChecked == true)
                    ThemeManager.ApplyTheme(ThemeManager.LightThemeName);
            };
            dark.IsCheckedChanged += (_, _) =>
            {
                if (dark.IsChecked == true)
                    ThemeManager.ApplyTheme(ThemeManager.DarkThemeName);
            };
        }

        /// <summary>Радиокнопка с TwoWay-привязкой к свойству ViewModel (режим сессии).</summary>
        private RadioButton Radio(string groupName, string path, string content)
        {
            var r = new RadioButton { Content = content, GroupName = groupName };
            r.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Avalonia.Data.Binding(path) { Mode = Avalonia.Data.BindingMode.TwoWay });
            return r;
        }

        private static Grid BuildHotkeyRow(string action, string key)
        {
            var grid = new Grid { Margin = new Thickness(0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(140)));

            var actionBlock = new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(actionBlock, 0);
            grid.Children.Add(actionBlock);

            var keyBorder = new Border
            {
                Child = new TextBlock { Text = key, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Padding = new Thickness(10, 4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(keyBorder, 1);
            grid.Children.Add(keyBorder);
            return grid;
        }

        private Control BuildAboutTab()
        {
            var panel = new StackPanel { Spacing = 12 };

            var asm = Assembly.GetExecutingAssembly();
            var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                              ?? asm.GetName().Version?.ToString() ?? "";
            var title = asm.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? LocalizationManager.T("App.Title");

            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeight.Bold
            });

            panel.Children.Add(new TextBlock
            {
                Text = string.Format(LocalizationManager.T("Settings.About.Version"), infoVersion),
                FontSize = 14
            });

            panel.Children.Add(new TextBlock
            {
                Text = LocalizationManager.T("Settings.About.AvaloniaText"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            });

            panel.Children.Add(new TextBlock
            {
                Text = string.Format(LocalizationManager.T("Settings.About.RuntimeInfo"), Environment.OSVersion, Environment.Is64BitOperatingSystem) + "\n" +
                       string.Format(LocalizationManager.T("Settings.About.DataDir"), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7
            });

            return panel;
        }
    }
}
#endif