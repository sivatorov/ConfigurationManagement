using System;
using System.Collections.Generic;
using System.Linq;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Мультивыделение баз (0.3.9.90): общая логика для обеих платформ.
/// Хранит идентификаторы баз, помеченных как «для выделенных» (Ctrl/Shift-клик
/// в дереве), синхронизирует служебный флаг <see cref="Infobase.IsBatchSelected"/>
/// (фон строки вторичной выделенности) и публикует событие
/// <see cref="BatchSelectionChanged"/> для обновления блока
/// «Для выделенных (N)…» в контекстном меню.
/// <para>
/// <see cref="SelectedInfobase"/> остаётся «якорем» диапазона: последний клик без
/// Ctrl. При Shift-клике диапазон строится от него до цели по видимому порядку
/// строк (порядок передаёт UI — только он знает свёрнутые группы и вкладки).
/// </para>
/// <para>
/// Секции дерева (issue #326): узел «Закреплённые» и обычный список не смешиваются
/// в одном наборе. Клик в другой секции очищает текущий набор и начинает новый —
/// признак секции строки под кликом передаёт UI
/// (<see cref="BatchSelectionHelper.IsPinnedSection"/>), секция текущего набора
/// хранится в <see cref="_batchSectionIsPinned"/>.
/// </para>
/// </summary>
public partial class MainViewModel
{
    private readonly HashSet<string> _batchSelectedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Секция текущего набора «для выделенных»: true — «Закреплённые»,
    /// false — обычный список, null — набор пуст (issue #326).
    /// </summary>
    private bool? _batchSectionIsPinned;

    /// <summary>
    /// Событие изменения набора «для выделенных». Поднимается после любого
    /// изменения состава; UI пересчитывает заголовок и видимость пакетного
    /// блока контекстного меню.
    /// </summary>
    public event EventHandler? BatchSelectionChanged;

    /// <summary>
    /// Секция текущего набора «для выделенных» для подсветки строк (issue #326):
    /// true — «Закреплённые», false — обычный список, null — набор пуст.
    /// Строки дерева подсвечивают пакетный фон ТОЛЬКО когда набор принадлежит
    /// их секции: закреплённая копия базы не «светит» при выделении в обычном
    /// списке и наоборот (раньше закреплённые строки не подсвечивались вовсе,
    /// и Ctrl/Shift в закреплениях подсвечивал копию базы в общем списке).
    /// Меняется вместе с составом набора; UI подписывается на PropertyChanged.
    /// </summary>
    public bool? BatchSelectionSectionIsPinned => _batchSectionIsPinned;

    /// <summary>Идентификаторы баз в мультивыделении (для пакетных операций).</summary>
    public IReadOnlyCollection<string> BatchSelectedIds => _batchSelectedIds;

    /// <summary>Число баз в мультивыделении.</summary>
    public int BatchSelectedCount => _batchSelectedIds.Count;

    /// <summary>
    /// Базы в мультивыделении в порядке списка <see cref="Infobases"/>.
    /// Служебная выборка для пакетных операций; закреплённые базы присутствуют
    /// в списке один раз.
    /// </summary>
    public IReadOnlyList<Infobase> BatchSelectedInfobases =>
        Infobases.Where(ib => ib.Id is { Length: > 0 } && _batchSelectedIds.Contains(ib.Id)).ToList();

    /// <summary>
    /// Переключает вхождение базы в мультивыделение.
    /// </summary>
    /// <param name="ib">База под курсором.</param>
    /// <param name="modifier">
    /// Модификатор клика: "Ctrl" — точечное переключение (toggle);
    /// "Shift" — диапазон от «якоря» (<see cref="SelectedInfobase"/>) до цели.
    /// Пустая строка/null — как Ctrl (точечное переключение).
    /// </param>
    /// <param name="visibleOrder">
    /// Видимый порядок строк дерева в пределах секции цели (для Shift-диапазона).
    /// Передаёт UI.
    /// </param>
    /// <param name="isPinnedSection">
    /// Секция строки под кликом: true — «Закреплённые» (issue #326). Признак
    /// берётся из данных контейнера (<see cref="BatchSelectionHelper.IsPinnedSection"/>).
    /// </param>
    /// <param name="currentRowSectionIsPinned">
    /// Секция «текущей» строки (<see cref="SelectedInfobase"/>, последняя выбранная
    /// без Ctrl): true — «Закреплённые». Признак берётся из данных контейнера
    /// текущей строки и нужен для правила issue #313 — при первом Ctrl-клике по
    /// строке, отличной от текущей, текущая добавляется в набор, но только если
    /// она лежит в той же секции, что и цель (#326).
    /// </param>
    public void ToggleBatchSelection(Infobase? ib, string? modifier,
        IReadOnlyList<Infobase>? visibleOrder = null, bool isPinnedSection = false,
        bool currentRowSectionIsPinned = false)
    {
        if (ib is null || ib.Id is not { Length: > 0 })
            return;

        if (string.Equals(modifier, "Shift", StringComparison.OrdinalIgnoreCase))
        {
            SelectRange(SelectedInfobase, ib, visibleOrder, isPinnedSection);
            return;
        }

        var next = BatchSelectionHelper.ApplyModifiedClick(
            _batchSelectedIds,
            _batchSectionIsPinned ?? false,
            ib.Id,
            isPinnedSection,
            "Ctrl",
            includeId: SelectedInfobase?.Id,
            includeSectionIsPinned: currentRowSectionIsPinned);
        ApplyBatchSet(next, isPinnedSection);
        RaiseBatchSelectionChanged();
    }

    /// <summary>
    /// Выделяет диапазон баз от «якоря» до цели по видимому порядку строк
    /// в пределах секции цели. Если якорь не задан, не найден в порядке или
    /// набор был в другой секции — выбирается только цель.
    /// </summary>
    /// <param name="from">Якорь (последний клик без Ctrl) или null.</param>
    /// <param name="to">Цель (база под Shift-кликом).</param>
    /// <param name="visibleOrder">Видимый порядок строк дерева в пределах секции (передаёт UI).</param>
    /// <param name="isPinnedSection">Секция строки под кликом (issue #326).</param>
    public void SelectRange(Infobase? from, Infobase? to,
        IReadOnlyList<Infobase>? visibleOrder = null, bool isPinnedSection = false)
    {
        if (to is null || to.Id is not { Length: > 0 })
            return;

        var order = visibleOrder;
        if (order is null || order.Count == 0)
        {
            // Правило секций (issue #326) никогда не нарушаем: для «Закреплённых»
            // порядок строится по ДАННЫМ узла (контейнеры вне видимой области могут
            // быть не реализованы виртуализацией), а НЕ по общему списку — иначе
            // Shift-клик в закреплениях «выделял полсписка обычного». Для обычного
            // списка оставляем прежний резерв (порядок модели); пустой результат
            // закреплённой секции даёт выбор только цели (как при отсутствии якоря).
            order = isPinnedSection ? BuildPinnedSectionVisibleOrder() : Infobases.ToList();
        }

        var orderIds = order
            .Select(ib => ib.Id)
            .Where(id => id is { Length: > 0 })
            .ToList();

        var next = BatchSelectionHelper.ApplyModifiedClick(
            _batchSelectedIds,
            _batchSectionIsPinned ?? false,
            to.Id,
            isPinnedSection,
            "Shift",
            orderIds,
            from?.Id);
        ApplyBatchSet(next, isPinnedSection);
        RaiseBatchSelectionChanged();
    }

    /// <summary>
    /// Видимый порядок строк секции «Закреплённые» по данным узла дерева
    /// (issue #326). Не зависит от виртуализации: узел «Закреплённые» — плоский
    /// список обёрток <see cref="PinnedInfobaseItem"/>, его порядок однозначен.
    /// Пустой список, если узел отсутствует (например, нет закреплённых баз).
    /// </summary>
    public IReadOnlyList<Infobase> BuildPinnedSectionVisibleOrder()
    {
        var pinnedNode = GroupNodes.FirstOrDefault(n => n.Group is null
            && string.Equals(n.Marker, GroupNodeViewModel.PinnedMarker, StringComparison.Ordinal));
        return pinnedNode is null
            ? Array.Empty<Infobase>()
            : BatchSelectionHelper.BuildPinnedSectionOrder(pinnedNode.Items);
    }

    /// <summary>Снимает мультивыделение со всех баз.</summary>
    public void ClearBatchSelection()
    {
        if (_batchSelectedIds.Count == 0)
            return;
        _batchSelectedIds.Clear();
        _batchSectionIsPinned = null;
        OnPropertyChanged(nameof(BatchSelectionSectionIsPinned));
        SyncBatchFlags();
        RaiseBatchSelectionChanged();
    }

    /// <summary>
    /// Применяет вычисленный хелпером набор к хранилищу идентификаторов и флагам
    /// строк, запоминает секцию набора (issue #326). Секция устанавливается ДО
    /// синхронизации флагов строк: подсветка строки зависит и от флага, и от
    /// секции набора, поэтому к моменту уведомлений об изменении флагов секция
    /// уже должна быть актуальной (иначе строка прочитала бы секцию прошлого набора).
    /// </summary>
    private void ApplyBatchSet(IReadOnlyCollection<string> next, bool sectionIsPinned)
    {
        _batchSelectedIds.Clear();
        foreach (var id in next)
            _batchSelectedIds.Add(id);
        _batchSectionIsPinned = next.Count > 0 ? sectionIsPinned : null;
        OnPropertyChanged(nameof(BatchSelectionSectionIsPinned));
        SyncBatchFlags();
    }

    /// <summary>Есть ли хотя бы одна база в мультивыделении.</summary>
    public bool HasBatchSelection => _batchSelectedIds.Count > 0;

    /// <summary>Идентификаторы баз в мультивыделении (для предикатов навигации, issue #350).</summary>
    public IReadOnlyCollection<string> SelectedInfobaseIds => _batchSelectedIds;

    /// <summary>
    /// Приводит флаг <see cref="Infobase.IsBatchSelected"/> всех баз в соответствие
    /// с набором (используется при очистке и после внешних изменений списка).
    /// </summary>
    private void SyncBatchFlags()
    {
        foreach (var ib in Infobases)
            ib.IsBatchSelected = ib.Id is { Length: > 0 } && _batchSelectedIds.Contains(ib.Id);
    }

    private void RaiseBatchSelectionChanged()
    {
        BatchSelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}