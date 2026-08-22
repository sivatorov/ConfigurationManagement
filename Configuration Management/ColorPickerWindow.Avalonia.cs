#if LINUX
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Themes;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог выбора произвольного цвета с RGB-слайдерами и HEX-полем.
    /// Avalonia/Linux-версия WPF-окна <see cref="ColorPickerWindow"/>.
    /// </summary>
    public class ColorPickerWindow : ModalWindowBase
    {
        private static readonly string[] PaletteColors =
        {
            "#EF4444", "#F97316", "#F59E0B", "#EAB308", "#84CC16", "#22C55E", "#10B981", "#14B8A6",
            "#06B6D4", "#0EA5E9", "#3B82F6", "#2D6CDF", "#6366F1", "#8B5CF6", "#A855F7", "#D946EF"
        };

        private bool _isUpdating;

        private readonly Slider _redSlider = new() { Minimum = 0, Maximum = 255 };
        private readonly Slider _greenSlider = new() { Minimum = 0, Maximum = 255 };
        private readonly Slider _blueSlider = new() { Minimum = 0, Maximum = 255 };

        private readonly TextBlock _redValue = new() { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _greenValue = new() { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _blueValue = new() { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

        private readonly TextBox _hexBox;
        private readonly Border _colorPreview;

        /// <summary>
        /// Создаёт диалог выбора цвета.
        /// </summary>
        /// <param name="initialColor">Начальный цвет в формате #RRGGBB.</param>
        public ColorPickerWindow(string? initialColor = null)
        {
            Title = LocalizationManager.T("ColorPicker.Title");
            Width = 380;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            SystemDecorations = SystemDecorations.Full;

            _colorPreview = new Border
            {
                Height = 56,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 12),
                BorderThickness = new Thickness(1)
            };

            _hexBox = new TextBox { Width = 110, Padding = new Thickness(4, 3) };
            _hexBox.TextChanged += OnHex_TextChanged;

            _redSlider.ValueChanged += OnRgb_ValueChanged;
            _greenSlider.ValueChanged += OnRgb_ValueChanged;
            _blueSlider.ValueChanged += OnRgb_ValueChanged;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            // Предпросмотр
            Grid.SetColumnSpan(_colorPreview, 2);
            root.Children.Add(_colorPreview);

            // Палитра
            var paletteLabel = new TextBlock
            {
                Text = LocalizationManager.T("ColorPicker.Palette"),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(paletteLabel, 1);
            Grid.SetColumnSpan(paletteLabel, 2);
            root.Children.Add(paletteLabel);

            var palette = new UniformGrid { Rows = 2, Columns = 8, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var hex in PaletteColors)
            {
                var button = new Button
                {
                    Width = 30,
                    Height = 30,
                    Margin = new Thickness(2),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(ParseColor(hex))
                };
                button.Click += (_, _) => SetColor(ParseColor(hex));
                palette.Children.Add(button);
            }
            Grid.SetRow(palette, 2);
            Grid.SetColumnSpan(palette, 2);
            root.Children.Add(palette);

            // HEX
            var hexLabel = new TextBlock { Text = LocalizationManager.T("ColorPicker.HexLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            Grid.SetRow(hexLabel, 3);
            root.Children.Add(hexLabel);

            _hexBox.Margin = new Thickness(8, 4, 0, 4);
            Grid.SetRow(_hexBox, 3);
            Grid.SetColumn(_hexBox, 1);
            root.Children.Add(_hexBox);

            // RGB слайдеры
            var red = BuildRgbRow(4, LocalizationManager.T("ColorPicker.ChannelRed"), "#EF4444", _redSlider, _redValue);
            Grid.SetColumnSpan(red, 2);
            root.Children.Add(red);

            var green = BuildRgbRow(5, LocalizationManager.T("ColorPicker.ChannelGreen"), "#10B981", _greenSlider, _greenValue);
            Grid.SetColumnSpan(green, 2);
            root.Children.Add(green);

            var blue = BuildRgbRow(6, LocalizationManager.T("ColorPicker.ChannelBlue"), "#2D6CDF", _blueSlider, _blueValue);
            Grid.SetColumnSpan(blue, 2);
            root.Children.Add(blue);

            // Кнопки
            var buttons = BuildButtons(LocalizationManager.T("Common.Ok"), 90, OnOk_Click);
            Grid.SetRow(buttons, 7);
            Grid.SetColumnSpan(buttons, 2);
            root.Children.Add(buttons);

            Content = root;

            SetColor(ParseColor(initialColor));
        }

        /// <summary>
        /// Возвращает выбранный цвет в формате #RRGGBB.
        /// </summary>
        public string Result { get; private set; } = "#2D6CDF";

        private static Grid BuildRgbRow(int row, string label, string accent, Slider slider, TextBlock value)
        {
            var grid = new Grid { Margin = new Thickness(0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(20)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(40)));

            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(ParseColor(accent)),
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(labelBlock);

            Grid.SetColumn(slider, 1);
            slider.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(slider);

            Grid.SetColumn(value, 2);
            // Значения RGB — вторичный текст из темы.
            ThemeBrushes.Bind(value, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            grid.Children.Add(value);

            Grid.SetRow(grid, row);
            return grid;
        }

        private void SetColor(Color color)
        {
            _isUpdating = true;
            try
            {
                _redSlider.Value = color.R;
                _greenSlider.Value = color.G;
                _blueSlider.Value = color.B;
                _redValue.Text = color.R.ToString();
                _greenValue.Text = color.G.ToString();
                _blueValue.Text = color.B.ToString();
                _hexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                _colorPreview.Background = new SolidColorBrush(color);
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void OnRgb_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdating)
                return;

            var color = Color.FromRgb(
                (byte)_redSlider.Value,
                (byte)_greenSlider.Value,
                (byte)_blueSlider.Value);

            _isUpdating = true;
            try
            {
                _redValue.Text = color.R.ToString();
                _greenValue.Text = color.G.ToString();
                _blueValue.Text = color.B.ToString();
                _hexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                _colorPreview.Background = new SolidColorBrush(color);
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void OnHex_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_isUpdating)
                return;

            var text = _hexBox.Text?.Trim() ?? string.Empty;
            if (text.Length != 7 || !text.StartsWith("#"))
                return;

            try
            {
                SetColor(Color.Parse(text));
            }
            catch
            {
                // Игнорируем некорректный ввод HEX.
            }
        }

        private void OnOk_Click()
        {
            Result = _hexBox.Text?.Trim() ?? "#2D6CDF";
        }

        private static Color ParseColor(string? hex)
        {
            try
            {
                return Color.Parse(string.IsNullOrWhiteSpace(hex) ? "#2D6CDF" : hex);
            }
            catch
            {
                return Color.Parse("#2D6CDF");
            }
        }
    }
}
#endif