#if LINUX
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Services;
using Configuration_Management.Themes;

namespace Configuration_Management;

/// <summary>
/// Мини-диалог выбора файла релиза (issue #352.1): «Дистрибутив обновления» /
/// «Полный дистрибутив» со страницы файлов версии. Avalonia/Linux-версия окна
/// <see cref="UpdateFileChoiceWindow"/> (строится кодом, без AXAML).
/// </summary>
public sealed class UpdateFileChoiceWindow : ModalWindowBase
{
    private sealed class ChoiceItem
    {
        public string Caption { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public UpdateFileChoice Choice { get; init; } = new(string.Empty, string.Empty, string.Empty);
        public override string ToString() => Caption;
    }

    private readonly ListBox _list = new();
    private Button _okButton = new();
    private UpdateFileChoice? _selected;

    public UpdateFileChoiceWindow(IReadOnlyList<UpdateFileChoice> choices, string heading)
    {
        Title = LocalizationManager.T("Updates.FileChoice.Title");
        Width = 520;
        FontSize = 13;
        CanResize = false;

        var root = new StackPanel { Margin = new Thickness(16) };

        var headingText = new TextBlock
        {
            Text = heading,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        Themes.ThemeBrushes.Bind(headingText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        root.Children.Add(headingText);

        _list.Margin = new Thickness(0, 12, 0, 0);
        _list.MaxHeight = 260;
        _list.ItemsSource = choices.Select(c => new ChoiceItem
        {
            Caption = LocalizationManager.T(c.CaptionKey),
            FileName = c.FileName,
            Choice = c,
        }).ToList();
        _list.SelectionChanged += (_, _) => _okButton.IsEnabled = _list.SelectedItem is ChoiceItem;
        _list.DoubleTapped += (_, _) => Confirm();
        root.Children.Add(_list);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 16, 0, 0),
        };
        _okButton = new Button
        {
            Content = LocalizationManager.T("Updates.FileChoice.Download"),
            MinWidth = 130,
            Height = 34,
            IsEnabled = false,
        };
        _okButton.Styled(ControlThemes.DialogConfirmButton);
        _okButton.Click += (_, _) => Confirm();

        var cancel = BuildCancelActionButton(110, 34);
        cancel.Click += (_, _) => Close();

        buttons.Children.Add(_okButton);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Opened += (_, _) =>
        {
            if (_list.ItemCount == 1)
                _list.SelectedIndex = 0;
            _list.Focus();
        };
    }

    /// <summary>Показывает диалог модально и возвращает выбранный вариант (null при отмене).</summary>
    public static UpdateFileChoice? Pick(IReadOnlyList<UpdateFileChoice> choices, string heading, Window? owner)
    {
        var window = new UpdateFileChoiceWindow(choices, heading);
        window.ShowDialogSync(owner);
        return window._selected;
    }

    private void Confirm()
    {
        if (_list.SelectedItem is ChoiceItem item)
        {
            _selected = item.Choice;
            Close();
        }
    }
}
#endif
