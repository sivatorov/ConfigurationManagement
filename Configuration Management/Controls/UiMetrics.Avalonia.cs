#if LINUX
using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Configuration_Management.Services;

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
        public const double PaddingSection = 10;
        /// <summary>Внутренний отступ управляющих элементов (обычный режим).</summary>
        public const double PaddingControl = 8;

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

        /// <summary>
        /// Коэффициент масштабирования шрифтов при компактном режиме. Задаётся мягче,
        /// чем <see cref="Scale"/> для геометрии: отступы и иконки сжимаются сильнее,
        /// а текст остаётся читаемым (при общем Scale=0.8 мелкие надписи становились
        /// практически неразличимыми).
        /// </summary>
        public static double FontScale => Compact ? 0.9 : 1.0;

        /// <summary>Масштабирует размер шрифта на коэффициент компактного режима.</summary>
        public static double ScaledFont(double value) => value * FontScale;

        /// <summary>Вертикальный отступ верхней панели.</summary>
        /// <remarks>
        /// Значение 8 (в обычном режиме) согласовано с WPF-разметкой, где верхняя панель
        /// поиска имеет Padding="0,8" (MainWindow.xaml:265). Это даёт панели «Теги» под ней
        /// одинаковый вертикальный отступ сверху и снизу (по 8) и выравнивает группу «Теги»
        /// по высоте на обеих платформах (issue #167). Прежнее 10 оставляло сверху 10 против
        /// 8 снизу, из-за чего группа «Теги» на Linux выглядела сдвинутой вниз.
        /// </remarks>
        public static double TopBarV => Compact ? 6 : 8;
        /// <summary>Горизонтальный отступ верхней панели.</summary>
        public static double TopBarH => Compact ? 8 : 12;

        /// <summary>Внутренний отступ секций-карточек правой панели.</summary>
        public static double SectionPad => Compact ? 6 : PaddingSection;
        /// <summary>Нижний отступ между секциями-карточками.</summary>
        public static double SectionMarginBottom => Compact ? 4 : 8;

        /// <summary>Внутренний отступ управляющих элементов (кнопки, поля).</summary>
        public static double ControlPad => Compact ? 6 : PaddingControl;

        /// <summary>Стандартный вертикальный промежуток между строками внутри секции.</summary>
        public static double Gap => Compact ? 4 : 6;

        /// <summary>Вертикальный padding кнопок (primary/secondary).</summary>
        public static double ButtonPadV => Compact ? 4 : 7;
        /// <summary>Горизонтальный padding кнопок (primary/secondary).</summary>
        public static double ButtonPadH => Compact ? 6 : 10;

        /// <summary>Вертикальный padding компактных кнопок действий правой панели.</summary>
        public static double ActionButtonPadV => Compact ? 3 : 6;
        /// <summary>Горизонтальный padding компактных кнопок действий правой панели.</summary>
        public static double ActionButtonPadH => Compact ? 6 : 8;
        /// <summary>Минимальная высота кнопки действия в правой панели.</summary>
        public static double ActionButtonMinHeight => Compact ? 26 : 32;
        /// <summary>Размер иконки на кнопке действия правой панели.</summary>
        public static double ActionIconSize => Compact ? 13 : 14;
        /// <summary>Размер шрифта подписи кнопки действия правой панели.</summary>
        public static double ActionFontSize => Compact ? 11 : 12;
        /// <summary>Промежуток между ячейками сетки действий правой панели.</summary>
        public static double ActionGridGap => Compact ? 4 : 6;

        /// <summary>Размер квадратной подложки под иконку статуса базы в списке.</summary>
        public static double RowIconBox => Compact ? 28 : 38;
        /// <summary>Размер самой иконки статуса внутри подложки.</summary>
        public static double RowIcon => 14;
        /// <summary>Размер шрифта имени базы в строке списка.</summary>
        public static double RowNameFont => Compact ? 12.5 : 13;
        /// <summary>Размер шрифта вторичной информации в строке списка.</summary>
        public static double RowSecondaryFont => Compact ? 11 : 12;

        /// <summary>
        /// Размер шрифта имени группы в списке. В обычном режиме имя группы наследует
        /// применяемый к интерфейсу шрифт (без жёсткого размера), поэтому значение имеет
        /// смысл только в компактном режиме, где имя группы задаётся явно и уменьшается.
        /// </summary>
        public static double GroupNameFont => 12.5;

        /// <summary>Вертикальный внутренний отступ заголовка группы (высота оформления группы).</summary>
        public static double GroupHeaderPadV => Compact ? 1 : 3;
        /// <summary>Вертикальный внешний отступ заголовка группы (расстояние между группами).</summary>
        public static double GroupHeaderMarginV => Compact ? 0.5 : 1;

        /// <summary>
        /// Вертикальный внутренний отступ строки базы. В компактном режиме уменьшается
        /// до значения заголовка группы (<see cref="GroupHeaderPadV"/>), чтобы основная
        /// строка сжималась до высоты группы, а не оставалась прежней (issue #296).
        /// </summary>
        public static double RowPadV => Compact ? 1 : 3;
        /// <summary>Вертикальный внешний отступ строки базы (расстояние между строками).</summary>
        public static double RowMarginV => Compact ? 0.5 : 1;

        /// <summary>
        /// Вертикальный внутренний отступ шапки окна (собственный заголовок вместо
        /// системного): компактный режим уменьшает высоту заголовка окна (issue #296).
        /// </summary>
        public static double TitleBarPadV => Compact ? 3 : 6;
        /// <summary>Горизонтальный внутренний отступ шапки окна.</summary>
        public static double TitleBarPadH => Compact ? 8 : 12;

        /// <summary>Минимальная ширина правой панели сведений.</summary>
        public static double RightPanelMin => Compact ? 200 : 280;
        /// <summary>Максимальная ширина правой панели сведений.</summary>
        public static double RightPanelMax => Compact ? 255 : 340;

        // ---- Анимации ----
        /// <summary>Длительность плавного перехода цвета/прозрачности.</summary>
        public static readonly TimeSpan TransitionFast = TimeSpan.FromMilliseconds(110);

        /// <summary>
        /// Добавляет мягкую тень (BoxShadow) к элементу. Цвет тени выводится из ресурса
        /// темы «BorderColorBrush» (перекрашивается при смене схемы) — без жёстких цветов.
        /// </summary>
        public static void AddSoftShadow(Border target)
        {
            // Подписка снимается вместе с уходом элемента из дерева: тень
            // добавляется в содержимое главного окна, а оно пересобирается.
            Themes.ThemeBrushes.Observe(target, "BorderColorBrush", brush =>
            {
                if (brush is ISolidColorBrush solid)
                    ApplyShadow(target, solid.Color);
            });
        }

        /// <summary>Добавляет плавный переход цвета фона и/или границы элемента.</summary>
        public static void AddBrushTransition(Border target, bool background = true, bool border = true)
        {
            // На программном рендере/в виртуализации каждый переход держит рендер-цикл
            // занятым и перерисовывает кадры софтом; там отказываемся от плавности,
            // чтобы не жечь CPU (issue #153).
            if (LinuxRendering.DisableAnimations)
                return;
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
            // См. AddBrushTransition: на программном рендере/в виртуализации плавность
            // отключаем, чтобы не держать рендер-цикл занятым (issue #153).
            if (LinuxRendering.DisableAnimations)
                return;
            target.Transitions ??= new Transitions();
            target.Transitions.Add(new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs)
            });
        }

        /// <summary>
        /// Добавляет плавный переход масштаба к ScaleTransform (hover/press-отклик
        /// кнопок управления окном и иконок). См. AddBrushTransition: на программном
        /// рендере/в виртуализации плавность отключается (issue #153).
        /// </summary>
        public static void AddScaleTransition(ScaleTransform target, double durationMs = 100)
        {
            if (LinuxRendering.DisableAnimations)
                return;
            target.Transitions ??= new Transitions();
            target.Transitions.Add(new DoubleTransition
            {
                Property = ScaleTransform.ScaleXProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs)
            });
            target.Transitions.Add(new DoubleTransition
            {
                Property = ScaleTransform.ScaleYProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs)
            });
        }

        /// <summary>
        /// Наблюдатель, который по значению ресурса-кисти строит мягкую полупрозрачную тень
        /// и применяет её к целевому Border.
        /// </summary>
        private static void ApplyShadow(Border target, Color borderColor)
        {
            // Полупрозрачный вариант цвета границы: мягкая тень для обеих тем.
            var shadowColor = new Color((byte)(borderColor.A * 0.26), borderColor.R, borderColor.G, borderColor.B);
            target.BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetY = 3,
                Blur = 14,
                Spread = 0,
                Color = shadowColor
            });
        }
    }
}
#endif