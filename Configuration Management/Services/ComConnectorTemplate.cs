using System.Text;

namespace Configuration_Management.Services;

/// <summary>
/// Разворачивает шаблон имени COM-коннектора 1С по версии платформы (issue #175).
/// Общий для обеих сборок (WPF и Avalonia): в отличие от <c>OneCComConnector</c>,
/// который входит только в Windows-сборку, этот класс используется и окном настроек
/// Linux для интерактивного предпросмотра, чтобы поведение при подключении
/// совпадало с тем, что видит пользователь.
/// </summary>
public static class ComConnectorTemplate
{
    /// <summary>
    /// Разворачивает шаблон по версии. Пустой шаблон → null. Версия нужна только шаблону
    /// с плейсхолдерами: готовое имя без них (например <c>V83.COMConnector_27</c>) работает
    /// само по себе, и требовать для него версию платформы значило бы игнорировать
    /// заданное пользователем имя у базы, где версия не указана (issue #175). Пустая
    /// версия или неразбираемая версия при шаблоне с плейсхолдерами → null.
    /// </summary>
    public static string? Expand(string? template, string? platformVersion)
    {
        if (string.IsNullOrWhiteSpace(template))
            return null;

        // Шаблон без плейсхолдеров разворачивать нечем и незачем: имя уже готово.
        // Проверка идёт раньше версии намеренно — см. замечание в сводке метода.
        if (!ContainsPlaceholder(template))
            return template;

        if (string.IsNullOrWhiteSpace(platformVersion))
            return null;

        var seg = platformVersion.Split('.');
        if (seg.Length == 0)
            return null;

        // %V12% — первые две цифры версии (для 8.3.x это «83»), %V3%/%V4% — третья/четвёртая.
        // Из каждого сегмента берутся только цифры, чтобы чужие символы из строки версии
        // не попадали в ProgID.
        var v12 = Digits(seg.Length > 1 ? seg[0] + seg[1] : seg[0]);
        var v3 = Digits(seg.Length > 2 ? seg[2] : "");
        var v4 = Digits(seg.Length > 3 ? seg[3] : "");

        // Нет первой части — расшифровать нечего.
        if (v12.Length == 0)
            return null;

        // Развернуться в пустоту шаблон тоже может: «%V4%» при версии «8.3.27» даёт пустую
        // строку. Такое «имя» в переборе бесполезно и вводило бы в заблуждение (пустой ProgID
        // первым кандидатом, он же в диагностике), поэтому считаем, что развернуть не удалось.
        var expanded = Apply(template, v12, v3, v4);
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
    }

    /// <summary>
    /// Применяет значения плейсхолдеров (issue #175):
    /// <list type="bullet">
    /// <item>пустой (отсутствующий) сегмент вне скобок: из накопленного перед ним текста
    /// удаляются только хвостовые разделители (<c>_</c>/<c>-</c>/<c>.</c>/пробел), а его
    /// содержательная часть (например <c>.COMConnector</c>) сохраняется — иначе неполная
    /// версия базы (скажем «8.3») «обрезала» бы имя до ближайшей слева, теряя суффикс;</item>
    /// <item>скобки вокруг сегмента вырезаются всегда: при наличии значения остаётся содержимое,
    /// а если внутри группы хоть один плейсхолдер пуст — удаляется вся группа целиком со скобками.</item>
    /// </list>
    /// Примеры: "V%V12%_%V3%_%V4%.ComConnector" + 8.3.27 → "V83_27.ComConnector";
    /// "V%V12%.COMConnector_%V3%_%V4%" + 8.3 → "V83.COMConnector"; + 8.3.27 → "V83.COMConnector_27";
    /// "V%V12%(вася_%V3%)(пупкин_%V4%)" + 8.3.27 → "V83вася_27".
    /// </summary>
    private static string Apply(string template, string v12, string v3, string v4)
    {
        var elements = ParseElements(template);

        // Склеиваем верхний уровень. Текст, идущий перед плейсхолдером, держим отдельно,
        // чтобы при пустом значении не потерять содержательную часть имени (issue #175).
        var sb = new StringBuilder();
        var pending = new StringBuilder();
        foreach (var el in elements)
        {
            switch (el)
            {
                case TextElement text:
                    pending.Append(text.Value);
                    break;
                case PlaceholderElement ph:
                    var value = ValueOf(ph.Name, v12, v3, v4);
                    if (value.Length > 0)
                    {
                        // Разделитель перед сегментом добавляем только вместе с ним.
                        sb.Append(pending);
                        sb.Append(value);
                        pending.Clear();
                    }
                    else
                    {
                        // Пустой сегмент: убираем лишь хвостовые разделители накопленного
                        // текста (.COMConnector_ → .COMConnector), остальное сохраняем.
                        TrimTrailingSeparators(pending);
                        sb.Append(pending);
                        pending.Clear();
                    }
                    break;
                case GroupElement group:
                    sb.Append(pending);
                    pending.Clear();
                    sb.Append(EvaluateGroup(group, v12, v3, v4));
                    break;
            }
        }

        // Хвостовой текст после последнего плейсхолдера/группы (.ComConnector и т.п.).
        sb.Append(pending);
        return sb.ToString();
    }

    /// <summary>Есть ли в шаблоне хотя бы один плейсхолдер.</summary>
    private static bool ContainsPlaceholder(string template) =>
        template.Contains("%V12%", StringComparison.Ordinal)
        || template.Contains("%V3%", StringComparison.Ordinal)
        || template.Contains("%V4%", StringComparison.Ordinal);

    /// <summary>
    /// Группа в скобках оценивается целиком: если внутри есть хоть один пустой плейсхолдер —
    /// возвращается пустая строка (группа удаляется со скобками); иначе возвращается
    /// содержимое без скобок.
    /// </summary>
    private static string EvaluateGroup(GroupElement group, string v12, string v3, string v4)
    {
        if (HasEmptyPlaceholder(group, v12, v3, v4))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var el in group.Children)
            sb.Append(EvaluateValue(el, v12, v3, v4));
        return sb.ToString();
    }

    private static bool HasEmptyPlaceholder(GroupElement group, string v12, string v3, string v4)
    {
        foreach (var el in group.Children)
        {
            switch (el)
            {
                case PlaceholderElement ph when ValueOf(ph.Name, v12, v3, v4).Length == 0:
                    return true;
                case GroupElement nested when HasEmptyPlaceholder(nested, v12, v3, v4):
                    return true;
            }
        }
        return false;
    }

    private static string EvaluateValue(Element el, string v12, string v3, string v4) => el switch
    {
        TextElement text => text.Value,
        PlaceholderElement ph => ValueOf(ph.Name, v12, v3, v4),
        GroupElement group => EvaluateGroup(group, v12, v3, v4),
        _ => string.Empty
    };

    private static string ValueOf(string name, string v12, string v3, string v4) => name switch
    {
        "%V12%" => v12,
        "%V3%" => v3,
        _ => v4
    };

    // ---- элементарный разбор шаблона в дерево: текст / плейсхолдер / скобочная группа ----

    private abstract class Element
    {
    }

    private sealed class TextElement : Element
    {
        public TextElement(string value) => Value = value;
        public string Value { get; }
    }

    private sealed class PlaceholderElement : Element
    {
        public PlaceholderElement(string name) => Name = name;
        public string Name { get; }
    }

    private sealed class GroupElement : Element
    {
        public GroupElement(List<Element> children) => Children = children;
        public List<Element> Children { get; }
    }

    private static List<Element> ParseElements(string template)
    {
        var elements = new List<Element>();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '(')
            {
                elements.Add(new GroupElement(ParseGroup(template, i + 1, out i)));
            }
            else if (c == '%')
            {
                var name = TryParsePlaceholder(template, i);
                if (name is not null)
                {
                    elements.Add(new PlaceholderElement(name));
                    i += name.Length;
                }
                else
                {
                    elements.Add(new TextElement("%"));
                    i++;
                }
            }
            else
            {
                var start = i;
                while (i < template.Length
                       && template[i] != '(' && template[i] != ')' && template[i] != '%')
                    i++;
                if (i > start)
                    elements.Add(new TextElement(template.Substring(start, i - start)));
                else
                    i++; // защита от зацикливания
            }
        }
        return elements;
    }

    /// <summary>
    /// Разбирает содержимое скобочной группы до ближайшей закрывающей скобки.
    /// По завершении <paramref name="end"/> указывает на позицию сразу после «)».
    /// Незакрытая скобка трактуется как открытая до конца шаблона.
    /// </summary>
    private static List<Element> ParseGroup(string template, int pos, out int end)
    {
        var elements = new List<Element>();
        var i = pos;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == ')')
            {
                end = i + 1;
                return elements;
            }
            if (c == '(')
            {
                elements.Add(new GroupElement(ParseGroup(template, i + 1, out i)));
                continue;
            }
            if (c == '%')
            {
                var name = TryParsePlaceholder(template, i);
                if (name is not null)
                {
                    elements.Add(new PlaceholderElement(name));
                    i += name.Length;
                    continue;
                }
                elements.Add(new TextElement("%"));
                i++;
                continue;
            }

            var start = i;
            while (i < template.Length
                   && template[i] != '(' && template[i] != ')' && template[i] != '%')
                i++;
            if (i > start)
                elements.Add(new TextElement(template.Substring(start, i - start)));
            else
                i++;
        }

        end = i;
        return elements;
    }

    /// <summary>
    /// Пытается прочитать плейсхолдер на позиции <paramref name="pos"/>.
    /// Возвращает имя («%V12%»/«%V3%»/«%V4%») или null, если это не плейсхолдер.
    /// </summary>
    private static string? TryParsePlaceholder(string template, int pos)
    {
        foreach (var name in new[] { "%V12%", "%V3%", "%V4%" })
        {
            if (pos + name.Length <= template.Length
                && string.CompareOrdinal(template, pos, name, 0, name.Length) == 0)
                return name;
        }
        return null;
    }

    /// <summary>
    /// Удаляет из хвоста буфера только разделители (<c>_</c>/<c>-</c>/<c>.</c>/пробел),
    /// сохраняя содержательную часть (issue #175). Пустая строка не меняется.
    /// </summary>
    private static void TrimTrailingSeparators(StringBuilder sb)
    {
        var end = sb.Length;
        while (end > 0 && IsSeparator(sb[end - 1]))
            end--;
        if (end < sb.Length)
            sb.Length = end;
    }

    private static bool IsSeparator(char c) => c switch
    {
        '_' or '-' or '.' or ' ' => true,
        _ => false
    };

    /// <summary>Оставляет в строке только десятичные цифры.</summary>
    private static string Digits(string s)
    {
        if (s.Length == 0)
            return string.Empty;

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsAsciiDigit(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }
}