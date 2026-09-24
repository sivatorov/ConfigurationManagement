#if LINUX
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Теги главного окна (Avalonia/Linux): чипы тегов в строках базы, добавление
    /// тега прямо в строке и панель фильтра по тегам.
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Теги базы под её именем: чип с крестиком на каждый тег и кнопка
        /// «+ тег». Панель перестраивается по уведомлению самой базы, поэтому
        /// после правки тегов строку пересобирать не нужно.
        /// </summary>
        private Control BuildRowTags(InfobaseRowCard card, Infobase infobase)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var chipSubscriptions = new List<IDisposable>();

            void Fill()
            {
                foreach (var subscription in chipSubscriptions)
                    subscription.Dispose();
                chipSubscriptions.Clear();
                panel.Children.Clear();

                foreach (var tag in infobase.Tags)
                    panel.Children.Add(BuildTagChip(infobase, tag, chipSubscriptions));

                panel.Children.Add(BuildAddTagButton(infobase, chipSubscriptions));
            }

            void OnInfobaseChanged(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(Infobase.Tags))
                    Fill();
            }

            card.AddSubscription(() =>
            {
                infobase.PropertyChanged += OnInfobaseChanged;
                Fill();
                return new ActionDisposable(() =>
                {
                    infobase.PropertyChanged -= OnInfobaseChanged;
                    foreach (var subscription in chipSubscriptions)
                        subscription.Dispose();
                    chipSubscriptions.Clear();
                });
            });

            return panel;
        }

        /// <summary>Чип тега: клик отбирает базы по тегу, крестик убирает тег у базы.</summary>
        private Control BuildTagChip(Infobase infobase, string tag, ICollection<IDisposable> subscriptions)
        {
            var text = new TextBlock
            {
                Text = tag,
                FontSize = UiMetrics.ScaledFont(11),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = UiMetrics.Scaled(180),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTip.SetTip(text, tag);
            ThemeBrushes.Bind(text, TextBlock.ForegroundProperty, "AccentBrush");

            var name = new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Cursor = new Cursor(StandardCursorType.Hand),
                CommandParameter = tag
            };
            ToolTip.SetTip(name, LocalizationManager.T("Main.ShowTagBases"));
            name.Bind(Button.CommandProperty, new Binding("SearchByTagCommand") { Source = _vm });

            // Крестик в разметке это знак × кеглем 11 в коробке 12 на 12
            // (MainWindow.xaml:1357), а не контур.
            var removeGlyph = new TextBlock
            {
                Text = "\u00D7",
                FontSize = UiMetrics.ScaledFont(11),
                LineHeight = UiMetrics.Scaled(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushes.Bind(removeGlyph, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var remove = new Button
            {
                Content = removeGlyph,
                Width = UiMetrics.Scaled(12),
                Height = UiMetrics.Scaled(12),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0),
                MinWidth = 0,
                MinHeight = 0,
                Cursor = new Cursor(StandardCursorType.Hand),
                // Форма параметра та же, что в WPF-версии: база и тег.
                CommandParameter = new object[] { infobase, tag }
            };
            ToolTip.SetTip(remove, LocalizationManager.T("Main.RemoveTag"));
            remove.Bind(Button.CommandProperty, new Binding("RemoveTagCommand") { Source = _vm });

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);
            row.Children.Add(remove);

            // Рамки у чипа в строке нет, подложка ItemHover и скругление 3:
            // рамка была нашей отсебятиной (MainWindow.xaml:1335-1338).
            var chip = new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0),
                Margin = new Thickness(0, 0, 3, 2),
                Height = UiMetrics.Scaled(16),
                VerticalAlignment = VerticalAlignment.Center,
                Child = row
            };
            ThemeBrushes.Bind(chip, Border.BackgroundProperty, "ItemHoverBrush");
            return chip;
        }

        /// <summary>Складывает подписку в приёмник, пропуская пустую (Application ещё не поднят).</summary>
        private static void Track(ICollection<IDisposable> sink, IDisposable? subscription)
        {
            if (subscription is not null)
                sink.Add(subscription);
        }

        /// <summary>
        /// Кнопка «+ тег» в конце списка тегов строки. По клику раскрывается поле ввода
        /// прямо в строке: Enter добавляет тег, Esc отменяет, потеря фокуса сохраняет введённое.
        /// </summary>
        private Control BuildAddTagButton(Infobase infobase, ICollection<IDisposable> subscriptions)
        {
            var text = new TextBlock
            {
                Text = LocalizationManager.T("Main.AddTagShort"),
                FontSize = UiMetrics.ScaledFont(11),
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushes.Bind(text, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            content.Children.Add(IconHelper.MakeIcon("IconTag", UiMetrics.Scaled(11), "TextSecondaryBrush"));
            content.Children.Add(text);

            var button = new Button
            {
                Content = content,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                // Отступ 2,0, высота 16 и поле 2 слева (MainWindow.xaml:1377).
                Padding = new Thickness(2, 0),
                Height = UiMetrics.Scaled(16),
                Margin = new Thickness(2, 0, 0, 0),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(button, LocalizationManager.T("Main.AddTag"));

            // Поле ввода тега показывается на месте кнопки во время редактирования.
            var input = new TextBox
            {
                // Числа и кисти из разметки (MainWindow.xaml:1390): ширина 120,
                // кегль 12, отступ 6,3, высота не меньше 24, поле 4 слева,
                // акцентная рамка толщиной 1.
                Watermark = LocalizationManager.T("Main.AddTag"),
                Width = UiMetrics.Scaled(120),
                FontSize = UiMetrics.ScaledFont(12),
                Padding = new Thickness(6, 3),
                MinHeight = UiMetrics.Scaled(24),
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                IsVisible = false
            };
            ThemeBrushes.Bind(input, TemplatedControl.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(input, TemplatedControl.ForegroundProperty, "TextPrimaryColorBrush");
            ThemeBrushes.Bind(input, TemplatedControl.BorderBrushProperty, "AccentBrush");
            ThemeBrushes.Bind(input, TextBox.CaretBrushProperty, "TextPrimaryColorBrush");
            ToolTip.SetTip(input, LocalizationManager.T("Main.EnterTagHint"));

            void ShowEditor()
            {
                button.IsVisible = false;
                input.Text = string.Empty;
                input.IsVisible = true;
                input.Focus();
                input.SelectAll();
            }

            void HideEditor()
            {
                input.IsVisible = false;
                button.IsVisible = true;
            }

            void Commit()
            {
                if (!input.IsVisible)
                    return;

                var tag = input.Text?.Trim() ?? string.Empty;
                HideEditor();
                input.Text = string.Empty;

                if (tag.Length == 0)
                    return;

                if (_vm?.AddTagInlineCommand.CanExecute(null) == true)
                    _vm.AddTagInlineCommand.Execute(new object[] { infobase, tag });
            }

            void Cancel()
            {
                if (!input.IsVisible)
                    return;

                input.Text = string.Empty;
                HideEditor();
            }

            button.Click += (_, _) => ShowEditor();

            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Cancel();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    Commit();
                    e.Handled = true;
                }
            };

            // Потеря фокуса сохраняет введённый тег, как в WPF-версии. Откладываем
            // обработку: клик вне поля сначала переводит фокус, затем фиксируем ввод.
            input.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (input.IsVisible)
                    Commit();
            });

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(button);
            row.Children.Add(input);
            return row;
        }

        /// <summary>
        /// Панель отбора по тегам: по кнопке на каждый тег и кнопка сброса.
        /// Видимость подчинена переключателю «теги» в верхней панели, а состав
        /// пересобирается при каждом изменении набора тегов.
        /// </summary>
        private Control BuildTagFilterPanel()
        {
            _tagPanelItems = new WrapPanel { Orientation = Orientation.Horizontal };

            _tagClearButton = new Button
            {
                // Короткая подпись и кегль 11, как в разметке (MainWindow.xaml:392):
                // полная строка делала кнопку заметно длиннее.
                Content = ThemedIconAndText("IconClose", LocalizationManager.T("Common.Clear"),
                    "ButtonTextBrush", UiMetrics.Scaled(12), centered: false, fontSize: UiMetrics.ScaledFont(11)),
                Padding = new Thickness(4, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            // Оформление и состояния берёт тема HeaderIconButton разметки
            // (LightTheme.xaml:586): наведение, нажатие и гашение у автора
            // заданы, а здесь кнопка была плоской без единого состояния.
            _tagClearButton.Styled(Themes.ControlThemes.HeaderIconButton);
            ToolTip.SetTip(_tagClearButton, LocalizationManager.T("Main.ClearTagFilters"));
            _tagClearButton.Bind(Button.CommandProperty, new Binding("ClearTagFiltersCommand"));

            // Подсказка остаётся на месте и когда тегов нет: панель не прячется,
            // иначе переключатель «теги» выглядел бы неработающим. Раскладка как
            // в WPF-версии: подсказка и кнопка сброса сверху, чипы тегов под ними.
            var hint = ThemedIconAndText("IconTag", LocalizationManager.T("Main.TagFilterTitle"),
                "TextSecondaryBrush", UiMetrics.ScaledFont(12), centered: false);
            hint.HorizontalAlignment = HorizontalAlignment.Left;

            // Кнопка справки рядом с заголовком панели, как в разметке WPF.
            var hintRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            hintRow.Children.Add(hint);
            hintRow.Children.Add(new Controls.HelpLink
            {
                HelpText = LocalizationManager.T("Main.TagFilterHelp"),
                VerticalAlignment = VerticalAlignment.Center
            });
            hintRow.HorizontalAlignment = HorizontalAlignment.Left;

            var header = new Grid();
            header.Children.Add(hintRow);
            header.Children.Add(_tagClearButton);

            // Выбор тега из выпадающего списка существующих: не нужно вводить
            // название вручную и можно не ошибиться в букве (issue #283).
            _tagFilterCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinHeight = UiMetrics.Scaled(32),
                FontSize = UiMetrics.ScaledFont(12),
                PlaceholderText = LocalizationManager.T("Main.TagFilterPick")
            };
            ThemeBrushes.Bind(_tagFilterCombo, TemplatedControl.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(_tagFilterCombo, TemplatedControl.ForegroundProperty, "TextPrimaryColorBrush");
            ThemeBrushes.Bind(_tagFilterCombo, TemplatedControl.BorderBrushProperty, "BorderColorBrush");
            ToolTip.SetTip(_tagFilterCombo, LocalizationManager.T("Main.TagFilterPick"));
            _tagFilterCombo.SelectionChanged += (_, _) =>
            {
                if (_tagFilterCombo?.SelectedItem is not string tag)
                    return;

                // Сбрасываем выделение до выполнения команды: иначе повторный выбор
                // того же тега не сработал бы (SelectedItem уже равен ему).
                _tagFilterCombo.SelectedItem = null;
                _vm?.SearchByTagCommand.Execute(tag);
            };

            var rows = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
            rows.Children.Add(header);
            rows.Children.Add(_tagFilterCombo);
            rows.Children.Add(_tagPanelItems);

            // Карточка с полем 4,0,4,8, отступом 8,6, рамкой и скруглением 8
            // (MainWindow.xaml:367): у нас это была полоса во всю ширину окна
            // с одной нижней линией.
            _tagPanel = new Border
            {
                Margin = new Thickness(4, 0, 4, 8),
                Padding = new Thickness(8, 6),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = rows
            };
            ThemeBrushes.Bind(_tagPanel, Border.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(_tagPanel, Border.BorderBrushProperty, "BorderColorBrush");
            return _tagPanel;
        }

        /// <summary>Пересобирает кнопки тегов и обновляет видимость панели.</summary>
        private void RefreshTagFilterPanel()
        {
            if (_vm is null || _tagPanelItems is null || _tagPanel is null || _tagClearButton is null || _tagFilterCombo is null)
                return;

            // Старые кнопки держат подписки на ресурсы темы, поэтому освобождаются
            // явно: очистка коллекции детей сама по себе их не отпускает.
            foreach (var child in _tagPanelItems.Children.OfType<IDisposable>().ToList())
                child.Dispose();
            _tagPanelItems.Children.Clear();

            foreach (var tag in _vm.TagFilterItems)
            {
                var item = tag;
                // Теги панели фильтра у автора в скруглённой рамке и мельче
                // сегментов верхней панели: свои отступы, кегль, значок и радиус
                // (MainWindow.xaml:414 и 455).
                var button = new SegmentButton("IconTag", item.Name, "ItemHoverBrush", "ItemSelectedBrush",
                    iconSize: UiMetrics.Scaled(12), cornerRadius: 8, fontSize: UiMetrics.ScaledFont(11))
                {
                    Margin = new Thickness(0, 0, 6, 4),
                    IsChecked = item.IsSelected,
                    MinHeight = 0,
                    Padding = new Thickness(7, 2)
                };
                button.SetBorderThickness(1);
                button.ShowRestingBackground("CardBackgroundBrush");
                button.ShowRestingBorder("BorderColorBrush");
                button.ShowHoverBorder("AccentBrush");
                button.Click += (_, _) => _vm.SearchByTagCommand.Execute(item.Name);
                _tagPanelItems.Children.Add(button);
            }

            // Список выбора тега всегда совпадает с чипами панели (включая новые теги).
            _tagFilterCombo.ItemsSource = _vm.TagFilterItems.Select(t => t.Name).ToList();

            _tagClearButton.IsVisible = _vm.HasActiveTagFilter;
            _tagPanel.IsVisible = _vm.ShowTagFilterPanel;
        }
    }
}
#endif