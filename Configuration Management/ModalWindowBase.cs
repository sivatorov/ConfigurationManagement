#if LINUX
using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Configuration_Management.Localization;

namespace Configuration_Management
{
    /// <summary>
    /// База модальных диалоговых окон (Avalonia/Linux). Предоставляет синхронный показ
    /// модального окна с эмуляцией <c>DialogResult</c>, как в <see cref="AvaloniaDialogService"/>:
    /// модальный показ крутит вложенный цикл сообщений <see cref="Dispatcher.PushFrame"/>,
    /// пока окно открыто. Это позволяет вызывать диалоги синхронно из команд ViewModel,
    /// не блокируя UI-поток.
    /// </summary>
    public abstract class ModalWindowBase : Window
    {
        /// <summary>
        /// Источник локализации для привязок XAML: <c>{Binding Loc[Key]}</c>.
        /// При смене языка открытые окна автоматически обновляют текст.
        /// </summary>
        public LocalizationSource Loc => LocalizationManager.Instance.Source;

        // Поля для живого обновления текста кнопок «Отмена»/«ОК» при смене языка.
        private TextBlock? _lastCancelText;
        private TextBlock? _lastOkText;
        private string _lastOkRaw = "";
        private bool _languageSubscribed;

        /// <summary>Результат диалога: true — подтверждён (ОК), false — отменён.</summary>
        public bool DialogResult { get; protected set; }

        /// <summary>
        /// Показывает окно модально (синхронно) относительно владельца и блокирует
        /// вызывающий поток до закрытия окна.
        /// </summary>
        /// <param name="owner">Окно-владелец (например, главное). Может быть null.</param>
        /// <returns>True, если пользователь подтвердил действие (DialogResult == true).</returns>
        public bool ShowDialogSync(Window? owner = null)
        {
            var frame = new DispatcherFrame();
            Closed += (_, _) => frame.Continue = false;

            if (owner is not null)
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                _ = ShowDialog(owner);
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Show();
            }

            Dispatcher.UIThread.PushFrame(frame);

            return DialogResult;
        }

        /// <summary>
        /// Показывает окно модально (синхронно) без владельца.
        /// </summary>
        public bool ShowDialogSync() => ShowDialogSync(null);

        /// <summary>
        /// Строит стандартный ряд кнопок «Отмена»/«ОК» с иконками и обработчиками.
        /// При нажатии «ОК» сначала выполняется <paramref name="onOk"/> (если задан),
        /// затем устанавливается <see cref="DialogResult"/> и окно закрывается.
        /// </summary>
        /// <param name="okText">Текст кнопки подтверждения. Если пуст/null — используется локализованный текст <c>Common.Ok</c>.</param>
        /// <param name="okWidth">Ширина кнопки подтверждения.</param>
        /// <param name="onOk">Необязательный обратный вызов при подтверждении (например, сохранить результат).</param>
        protected StackPanel BuildButtons(string? okText = null, double okWidth = 130, Action? onOk = null)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };

            var cancelText = new TextBlock { Text = LocalizationManager.T("Common.Cancel"), VerticalAlignment = VerticalAlignment.Center };
            var okTextBlock = new TextBlock { Text = ResolveOkText(okText), VerticalAlignment = VerticalAlignment.Center };

            var cancel = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        IconHelper.MakeIcon("IconClose", 14),
                        cancelText
                    }
                },
                MinWidth = 110,
                IsCancel = true
            };
            cancel.Click += (_, _) => Close();
            panel.Children.Add(cancel);

            var ok = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        IconHelper.MakeIcon("IconOk", 14),
                        okTextBlock
                    }
                },
                MinWidth = okWidth,
                IsDefault = true
            };
            ok.Click += (_, _) =>
            {
                onOk?.Invoke();
                DialogResult = true;
                Close();
            };
            panel.Children.Add(ok);

            // Живое обновление кнопок при смене языка.
            _lastCancelText = cancelText;
            _lastOkText = okTextBlock;
            _lastOkRaw = okText ?? "";
            EnsureLanguageSubscription();

            return panel;
        }

        /// <summary>
        /// Возвращает локализованный текст кнопки подтверждения.
        /// Пустое значение (null/пустая строка) интерпретируется как <c>Common.Ok</c>.
        /// </summary>
        private static string ResolveOkText(string? okText) =>
            string.IsNullOrEmpty(okText) ? LocalizationManager.T("Common.Ok") : okText;

        private void EnsureLanguageSubscription()
        {
            if (_languageSubscribed)
                return;
            _languageSubscribed = true;
            LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            if (_lastCancelText is not null)
                _lastCancelText.Text = LocalizationManager.T("Common.Cancel");
            if (_lastOkText is not null)
                _lastOkText.Text = ResolveOkText(_lastOkRaw);
        }
    }
}
#endif