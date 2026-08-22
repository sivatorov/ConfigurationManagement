#if LINUX
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Configuration_Management.Controls
{
    /// <summary>
    /// Карточка строки информационной базы (Avalonia/Linux).
    /// Рисует фон и границу из ресурсов темы (без жёстких цветов) и переключает
    /// состояние «обычное / hover / выделено», отслеживая наведение указателя на
    /// карточку и признак выделения родительского контейнера <see cref="TreeViewItem"/>
    /// (<c>:selected</c>). Кисти подписываются на ресурсы темы через
    /// GetResourceObservable, поэтому карточка перекрашивается при смене схемы.
    /// </summary>
    public class InfobaseRowCard : Border
    {
        private TreeViewItem? _container;
        private IDisposable? _selectedSubscription;

        private readonly List<Func<IDisposable?>> _contentFactories = new();
        private readonly List<IDisposable> _contentSubscriptions = new();
        private bool _attached;

        // Актуальные кисти темы, обновляются при смене схемы/ресурсов.
        private IBrush _cardBrush = Brushes.Transparent;
        private IBrush _hoverBrush = Brushes.Transparent;
        private IBrush _selectedBrush = Brushes.Transparent;
        private IBrush _borderBrush = Brushes.Transparent;
        private IBrush _accentBrush = Brushes.Transparent;

        private bool _isHovered;

        public InfobaseRowCard()
        {
            // Единый радиус из общих метрик UI; плавные переходы цвета фона/границы
            // при hover/выделении (без перегрузки — короткая длительность).
            CornerRadius = new CornerRadius(UiMetrics.RadiusMd);
            Padding = new Thickness(UiMetrics.PaddingControl, 8);
            Margin = new Thickness(0, 2);
            BorderThickness = new Thickness(1);
            IsHitTestVisible = true;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            UiMetrics.AddBrushTransition(this);

            AttachedToVisualTree += OnAttachedToVisualTree;
            DetachedFromVisualTree += OnDetachedFromVisualTree;
            PointerEntered += OnPointerEntered;
            PointerExited += OnPointerExited;

            // Кисти карточки подписываются тем же способом, что и содержимое:
            // при отсоединении подписки освобождаются, при присоединении
            // создаются заново. Иначе каждая пересборка списка оставляла бы
            // по пять живых наблюдателей на выброшенную строку.
            AddSubscription(() => SubscribeBrush("CardBackgroundBrush", value => _cardBrush = value));
            AddSubscription(() => SubscribeBrush("ItemHoverBrush", value => _hoverBrush = value));
            AddSubscription(() => SubscribeBrush("ItemSelectedBrush", value => _selectedBrush = value));
            AddSubscription(() => SubscribeBrush("BorderColorBrush", value => _borderBrush = value));
            AddSubscription(() => SubscribeBrush("AccentBrush", value => _accentBrush = value));
        }

        /// <summary>
        /// Регистрирует подписку содержимого строки: кисти темы у иконок и подписей,
        /// уведомления самой базы. Подписка создаётся при присоединении карточки
        /// к дереву и освобождается при отсоединении: иначе наблюдатели держали бы
        /// уже выброшенное визуальное дерево. Хранится не сама подписка, а способ
        /// её создать, поэтому повторное присоединение восстанавливает и цвета,
        /// и слежение за моделью.
        /// </summary>
        public void AddSubscription(Func<IDisposable?> factory)
        {
            _contentFactories.Add(factory);
            if (!_attached)
                return;

            var subscription = factory();
            if (subscription is not null)
                _contentSubscriptions.Add(subscription);
        }

        /// <summary>Создаёт подписки содержимого по зарегистрированным способам.</summary>
        private void SubscribeContent()
        {
            foreach (var factory in _contentFactories)
            {
                var subscription = factory();
                if (subscription is not null)
                    _contentSubscriptions.Add(subscription);
            }
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _attached = true;
            if (_contentSubscriptions.Count == 0)
                SubscribeContent();

            _container = this.FindAncestorOfType<TreeViewItem>();
            if (_container is not null)
                _selectedSubscription = _container
                    .GetObservable(TreeViewItem.IsSelectedProperty)
                    .Subscribe(new RelayObserver(ApplyState));
            ApplyState();
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _attached = false;
            _selectedSubscription?.Dispose();
            _selectedSubscription = null;
            _container = null;

            foreach (var subscription in _contentSubscriptions)
                subscription.Dispose();
            _contentSubscriptions.Clear();
        }

        private void OnPointerEntered(object? sender, PointerEventArgs e)
        {
            _isHovered = true;
            ApplyState();
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            _isHovered = false;
            ApplyState();
        }

        /// <summary>Подписывает слот-поле на ресурс-кисть темы и вызывает перерисовку при обновлении.</summary>
        private IDisposable? SubscribeBrush(string brushKey, Action<IBrush> setter)
        {
            if (Application.Current is not { } app)
                return null;
            // После обновления кисти переприменяем состояние, чтобы фон/граница
            // корректно перекрашивались при смене схемы даже в состоянии hover/выделение.
            var slot = new BrushSlot(setter, ApplyState);
            return app.GetResourceObservable(brushKey).Subscribe(slot);
        }

        /// <summary>Применяет состояние к фону и границе в порядке приоритета: выделено > hover > обычное.</summary>
        private void ApplyState()
        {
            if (_container?.IsSelected == true)
            {
                Background = _selectedBrush;
                BorderBrush = _accentBrush;
            }
            else if (_isHovered)
            {
                Background = _hoverBrush;
                BorderBrush = _borderBrush;
            }
            else
            {
                Background = _cardBrush;
                BorderBrush = _borderBrush;
            }
        }

        /// <summary>Передаёт текущее значение ресурса-кисти в слот и инициирует перерисовку.</summary>
        private sealed class BrushSlot : IObserver<object?>
        {
            private readonly Action<IBrush> _setter;
            private readonly Action _after;

            public BrushSlot(Action<IBrush> setter, Action after)
            {
                _setter = setter;
                _after = after;
            }

            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(object? value)
            {
                if (value is IBrush brush)
                {
                    _setter(brush);
                    _after();
                }
            }
        }

        /// <summary>Простой наблюдатель, вызывающий действие при изменении значения (для IsSelected).</summary>
        private sealed class RelayObserver : IObserver<bool>
        {
            private readonly Action _onNext;

            public RelayObserver(Action onNext) => _onNext = onNext;

            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(bool value) => _onNext();
        }
    }
}
#endif