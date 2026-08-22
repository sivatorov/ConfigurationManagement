#if LINUX
using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Configuration_Management.Controls
{
    /// <summary>
    /// Единые метрики UI и вспомогательные методы полировки (Avalonia/Linux).
    /// Элементы, собираемые в коде, используют эти константы, чтобы отступы и скругления
    /// выглядели цельно, а тени и плавные переходы брались из ресурсов темы (без жёстких цветов).
    /// Всё ограничено #if LINUX и не влияет на Windows (WPF) сборку.
    /// </summary>
    public static class UiMetrics
    {
        // ---- Скругления ----
        /// <summary>Крупные карточки-секции (правый экран, empty-state).</summary>
        public const double RadiusXl = 12;
        /// <summary>Управляющие элементы (кнопки, поле поиска, сегмент-контейнер).</summary>
        public const double RadiusLg = 10;
        /// <summary>Карточки строк и иконки-«аватары».</summary>
        public const double RadiusMd = 8;
        /// <summary>Мелкие элементы (заголовки групп, сегменты).</summary>
        public const double RadiusSm = 6;

        // ---- Отступы ----
        /// <summary>Внутренний отступ секций-карточек правой панели (обычный режим).</summary>
        public const double PaddingSection = 14;
        /// <summary>Внутренний отступ управляющих элементов (обычный режим).</summary>
        public const double PaddingControl = 10;

        // ---- Компактный режим ----
        private static bool _compact;
        /// <summary>
        /// Компактный режим интерфейса: уменьшает размеры иконок, кнопок, шрифтов,
        /// отступов и расстояний между элементами. Устанавливается из настроек при
        /// запуске и из окна настроек; применяется через пересчёт UI.
        /// </summary>
        public static bool Compact
        {
            get => _compact;
            set { if (_compact != value) { _compact = value; CompactChanged?.Invoke(); } }
        }

        /// <summary>Событие изменения компактного режима (для перестроения UI главного окна).</summary>
        public static event Action? CompactChanged;

        /// <summary>Коэффициент масштабирования отступов/размеров при компактном режиме.</summary>
        public static double Scale => Compact ? 0.8 : 1.0;

        /// <summary>Масштабирует значение на коэффициент компактного режима.</summary>
        public static double Scaled(double value) => value * Scale;

        /// <summary>Вертикальный отступ верхней панели.</summary>
        public static double TopBarV => Compact ? 6 : 10;
        /// <summary>Горизонтальный отступ верхней панели.</summary>
        public static double TopBarH => Compact ? 8 : 12;

        /// <summary>Внутренний отступ секций-карточек правой панели.</summary>
        public static double SectionPad => Compact ? 8 : PaddingSection;
        /// <summary>Нижний отступ между секциями-карточками.</summary>
        public static double SectionMarginBottom => Compact ? 6 : 12;

        /// <summary>Внутренний отступ управляющих элементов (кнопки, поля).</summary>
        public static double ControlPad => Compact ? 7 : PaddingControl;

        /// <summary>Стандартный вертикальный промежуток между строками внутри секции.</summary>
        public static double Gap => Compact ? 5 : 8;

        /// <summary>Вертикальный padding кнопок (primary/secondary).</summary>
        public static double ButtonPadV => Compact ? 5 : 9;
        /// <summary>Горизонтальный padding кнопок (primary/secondary).</summary>
        public static double ButtonPadH => Compact ? 8 : 12;

        /// <summary>Размер квадратной подложки под иконку статуса базы в списке.</summary>
        public static double RowIconBox => Compact ? 28 : 38;
        /// <summary>Размер самой иконки статуса внутри подложки.</summary>
        public static double RowIcon => Compact ? 14 : 20;
        /// <summary>Размер шрифта имени базы в строке списка.</summary>
        public static double RowNameFont => Compact ? 13 : 14;
        /// <summary>Размер шрифта вторичной информации в строке списка.</summary>
        public static double RowSecondaryFont => Compact ? 10 : 11;

        /// <summary>Минимальная ширина правой панели сведений.</summary>
        public static double RightPanelMin => Compact ? 230 : 300;
        /// <summary>Максимальная ширина правой панели сведений.</summary>
        public static double RightPanelMax => Compact ? 290 : 380;

        // ---- Анимации ----
        /// <summary>Длительность плавного перехода цвета/прозрачности.</summary>
        public static readonly TimeSpan TransitionFast = TimeSpan.FromMilliseconds(110);

        /// <summary>
        /// Добавляет мягкую тень (BoxShadow) к элементу. Цвет тени выводится из ресурса
        /// темы «BorderColorBrush» (перекрашивается при смене схемы) — без жёстких цветов.
        /// </summary>
        public static void AddSoftShadow(Border target)
        {
            if (Application.Current is not { } app)
                return;
            app.GetResourceObservable("BorderColorBrush").Subscribe(new ShadowObserver(target));
        }

        /// <summary>Добавляет плавный переход цвета фона и/или границы элемента.</summary>
        public static void AddBrushTransition(Border target, bool background = true, bool border = true)
        {
            target.Transitions ??= new Transitions();
            if (background)
                target.Transitions.Add(new BrushTransition
                {
                    Property = Border.BackgroundProperty,
                    Duration = TransitionFast
                });
            if (border)
                target.Transitions.Add(new BrushTransition
                {
                    Property = Border.BorderBrushProperty,
                    Duration = TransitionFast
                });
        }

        /// <summary>Добавляет плавное появление/исчезание по прозрачности.</summary>
        public static void AddOpacityTransition(Visual target, double durationMs = 180)
        {
            target.Transitions ??= new Transitions();
            target.Transitions.Add(new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs)
            });
        }

        /// <summary>
        /// Наблюдатель, который по значению ресурса-кисти строит мягкую полупрозрачную тень
        /// и применяет её к целевому Border.
        /// </summary>
        private sealed class ShadowObserver : IObserver<object?>
        {
            private readonly Border _target;

            public ShadowObserver(Border target) => _target = target;

            public void OnCompleted() { }
            public void OnError(Exception error) { }

            public void OnNext(object? value)
            {
                if (value is not ISolidColorBrush solid)
                    return;
                var c = solid.Color;
                // Полупрозрачный вариант цвета границы — мягкая тень для обеих тем.
                var shadowColor = new Color((byte)(c.A * 0.26), c.R, c.G, c.B);
                _target.BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetY = 3,
                    Blur = 14,
                    Spread = 0,
                    Color = shadowColor
                });
            }
        }
    }
}
#endif