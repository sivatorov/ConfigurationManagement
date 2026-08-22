#if LINUX
using System;
using System.Collections.Generic;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог-конфигуратор параметров запуска платформы 1С. Состоит из поля ввода
    /// параметров и справочника ключей командной строки, из которого параметр
    /// подставляется в поле двойным кликом. Avalonia/Linux-версия WPF-окна
    /// <see cref="LaunchParametersWindow"/>.
    /// </summary>
    public class LaunchParametersWindow : ModalWindowBase
    {
        private readonly TextBox _txtCustom;

        /// <summary>
        /// Создаёт диалог конфигуратора параметров запуска.
        /// </summary>
        /// <param name="currentParameters">Текущая строка параметров для предзаполнения.</param>
        public LaunchParametersWindow(string currentParameters)
        {
            Title = LocalizationManager.T("LaunchParams.Title");
            Width = 620;
            Height = 560;
            MinWidth = 540;
            MinHeight = 480;

            _txtCustom = new TextBox
            {
                Text = currentParameters ?? string.Empty,
                Padding = new Thickness(8, 6),
                AcceptsReturn = true,
                MinHeight = 90,
                Watermark = LocalizationManager.T("LaunchParams.InputWatermark")
            };

            Content = BuildRoot();
        }

        /// <summary>Итоговая строка параметров запуска.</summary>
        public string Result { get; private set; } = string.Empty;

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var title = new TextBlock
            {
                Text = LocalizationManager.T("LaunchParams.Header"),
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var hint = new TextBlock
            {
                Text = LocalizationManager.T("LaunchParams.Hint"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 1);
            grid.Children.Add(hint);

            Grid.SetRow(_txtCustom, 2);
            grid.Children.Add(_txtCustom);

            // Справочник параметров
            var refLabel = new TextBlock { Text = LocalizationManager.T("LaunchParams.ReferenceTitle"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 6) };
            Grid.SetRow(refLabel, 3);
            grid.Children.Add(refLabel);

            var list = new ListBox();
            list.ItemsSource = BuildReferenceCatalog();
            list.ItemTemplate = new FuncDataTemplate<ParamRef>((item, _) =>
            {
                var panel = new Grid { Margin = new Thickness(2, 3) };
                panel.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(150)));
                panel.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

                var key = new TextBlock { Text = item.Key, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(key, 0);
                panel.Children.Add(key);

                var desc = new TextBlock { Text = item.Description, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(desc, 1);
                panel.Children.Add(desc);
                return panel;
            });
            list.DoubleTapped += (_, e) =>
            {
                if (list.SelectedItem is ParamRef item)
                {
                    InsertCustomText(item.Key);
                    e.Handled = true;
                }
            };

            var listBorder = new Border
            {
                Child = new ScrollViewer
                {
                    Content = list,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(8, 8)
                },
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(listBorder, 4);
            grid.Children.Add(listBorder);

            // Кнопки
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };
            var cancel = new Button { Content = LocalizationManager.T("Common.Cancel"), MinWidth = 100, IsCancel = true };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
            var ok = new Button { Content = LocalizationManager.T("Common.Ok"), MinWidth = 110, IsDefault = true };
            ok.Click += (_, _) => OnOk_Click();
            buttons.Children.Add(ok);
            Grid.SetRow(buttons, 5);
            grid.Children.Add(buttons);

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            return grid;
        }

        /// <summary>Добавляет текст в поле «Параметры», разделяя пробелом.</summary>
        private void InsertCustomText(string text)
        {
            var insert = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(insert))
                return;

            if (string.IsNullOrWhiteSpace(_txtCustom.Text))
                _txtCustom.Text = insert;
            else
                _txtCustom.Text = _txtCustom.Text.TrimEnd() + " " + insert;

            _txtCustom.CaretIndex = _txtCustom.Text.Length;
            _txtCustom.Focus();
        }

        private void OnOk_Click()
        {
            Result = (_txtCustom.Text ?? string.Empty).Trim();
            DialogResult = true;
            Close();
        }

        /// <summary>Строит каталог ключей командной строки 1С для справочника.</summary>
        private static List<ParamRef> BuildReferenceCatalog()
        {
            var list = new List<ParamRef>();
            void Add(string key, string description) => list.Add(new ParamRef(key, description));

            // Параметры-флаги.
            Add("/DisableStartupMessages", LocalizationManager.T("LaunchParams.Ref.DisableStartupMessages"));
            Add("/DisableStartupDialogs", LocalizationManager.T("LaunchParams.Ref.DisableStartupDialogs"));
            Add("/DisableSplash", LocalizationManager.T("LaunchParams.Ref.DisableSplash"));
            Add("/WA-", LocalizationManager.T("LaunchParams.Ref.WA"));
            Add("/Debug", LocalizationManager.T("LaunchParams.Ref.Debug"));
            Add("/AllowExecuteScheduledJobs", LocalizationManager.T("LaunchParams.Ref.AllowExecuteScheduledJobs"));
            Add("/RunModeManagedApplication", LocalizationManager.T("LaunchParams.Ref.RunModeManagedApplication"));
            Add("/RunModeOrdinaryApplication", LocalizationManager.T("LaunchParams.Ref.RunModeOrdinaryApplication"));
            Add("/UpdateCfg", LocalizationManager.T("LaunchParams.Ref.UpdateCfg"));
            Add("/TestServer", LocalizationManager.T("LaunchParams.Ref.TestServer"));
            Add("/RestoreIB", LocalizationManager.T("LaunchParams.Ref.RestoreIB"));
            Add("/DumpIB", LocalizationManager.T("LaunchParams.Ref.DumpIB"));
            Add("/DumpCfg", LocalizationManager.T("LaunchParams.Ref.DumpCfg"));
            Add("/LoadCfg", LocalizationManager.T("LaunchParams.Ref.LoadCfg"));
            Add("/CheckConfig", LocalizationManager.T("LaunchParams.Ref.CheckConfig"));
            Add("/UpdateConfigDumpCfg", LocalizationManager.T("LaunchParams.Ref.UpdateConfigDumpCfg"));
            Add("/CreateInfobase", LocalizationManager.T("LaunchParams.Ref.CreateInfobase"));
            Add("/Command", LocalizationManager.T("LaunchParams.Ref.Command"));
            Add("/ManagedClient", LocalizationManager.T("LaunchParams.Ref.ManagedClient"));
            Add("/ThickClient", LocalizationManager.T("LaunchParams.Ref.ThickClient"));
            Add("/UpdateConfiguration", LocalizationManager.T("LaunchParams.Ref.UpdateConfiguration"));

            // Параметры с аргументами.
            Add("/UC", LocalizationManager.T("LaunchParams.Ref.UC"));
            Add("/L", LocalizationManager.T("LaunchParams.Ref.L"));
            Add("/Out", LocalizationManager.T("LaunchParams.Ref.Out"));
            Add("/C", LocalizationManager.T("LaunchParams.Ref.C"));
            Add("/Execute", LocalizationManager.T("LaunchParams.Ref.Execute"));
            Add("/DumpResult", LocalizationManager.T("LaunchParams.Ref.DumpResult"));
            Add("/N", LocalizationManager.T("LaunchParams.Ref.N"));
            Add("/P", LocalizationManager.T("LaunchParams.Ref.P"));
            Add("/S", LocalizationManager.T("LaunchParams.Ref.S"));
            Add("/F", LocalizationManager.T("LaunchParams.Ref.F"));
            Add("/Ref", LocalizationManager.T("LaunchParams.Ref.Ref"));
            Add("/Server", LocalizationManager.T("LaunchParams.Ref.Server"));
            Add("/Srvr", LocalizationManager.T("LaunchParams.Ref.Srvr"));
            Add("/IBName", LocalizationManager.T("LaunchParams.Ref.IBName"));
            Add("/DBMS", LocalizationManager.T("LaunchParams.Ref.DBMS"));
            Add("/DBSrvr", LocalizationManager.T("LaunchParams.Ref.DBSrvr"));
            Add("/DBUID", LocalizationManager.T("LaunchParams.Ref.DBUID"));
            Add("/DBPwd", LocalizationManager.T("LaunchParams.Ref.DBPwd"));
            Add("/App", LocalizationManager.T("LaunchParams.Ref.App"));
            Add("/ConfigurationRepository", LocalizationManager.T("LaunchParams.Ref.ConfigurationRepository"));
            Add("/ConfigurationRepositoryUser", LocalizationManager.T("LaunchParams.Ref.ConfigurationRepositoryUser"));
            Add("/ConfigurationRepositoryPwd", LocalizationManager.T("LaunchParams.Ref.ConfigurationRepositoryPwd"));
            Add("/DisplayAllFunctions", LocalizationManager.T("LaunchParams.Ref.DisplayAllFunctions"));
            Add("/WSNamespace", LocalizationManager.T("LaunchParams.Ref.WSNamespace"));
            Add("/IBSecurity", LocalizationManager.T("LaunchParams.Ref.IBSecurity"));
            Add("/CPUSecurity", LocalizationManager.T("LaunchParams.Ref.CPUSecurity"));
            Add("/SaveAgent", LocalizationManager.T("LaunchParams.Ref.SaveAgent"));
            Add("/ConfigurationName", LocalizationManager.T("LaunchParams.Ref.ConfigurationName"));
            Add("/RegisterExternalDataSource", LocalizationManager.T("LaunchParams.Ref.RegisterExternalDataSource"));
            Add("/UnregisterExternalDataSource", LocalizationManager.T("LaunchParams.Ref.UnregisterExternalDataSource"));
            Add("/SqlDump", LocalizationManager.T("LaunchParams.Ref.SqlDump"));

            return list;
        }

        /// <summary>Запись справочника параметров командной строки 1С.</summary>
        private sealed class ParamRef
        {
            public ParamRef(string key, string description)
            {
                Key = key;
                Description = description;
            }

            public string Key { get; }
            public string Description { get; }
        }
    }
}
#endif