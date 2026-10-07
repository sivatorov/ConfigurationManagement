using System;

namespace Configuration_Management.Services;

/// <summary>
/// Единая точка маршалинга изменения UI-состояния в поток Dispatcher (issues #334/#330).
/// ViewModel окон платформы получают инжектируемый делегат <c>Action<Action>?</c>
/// (null — прямой вызов, используется юнит-тестами без UI), окна передают платформенный
/// маршаллер: WPF — <c>Dispatcher.InvokeAsync</c>, Avalonia — <c>Dispatcher.UIThread.Post</c>.
/// Паттерн совпадает с <see cref="ServerMonitorViewModel._dispatchToUi"/>: коллекции и
/// зависимые свойства, связанные с WPF CollectionView, обновляются строго в UI-потоке,
/// иначе DataGrid бросает NotSupportedException («изменение SourceCollection из потока,
/// отличного от потока Dispatcher»).
/// </summary>
public static class UiDispatch
{
    /// <summary>
    /// Выполняет действие через переданный маршаллер; если маршаллер не задан —
    /// действие вызывается напрямую (тесты, окружение без UI).
    /// </summary>
    /// <param name="dispatchToUi">Маршаллер в UI-поток или null.</param>
    /// <param name="action">Действие, изменяющее UI-состояние (синхронное, без await).</param>
    public static void Run(Action<Action>? dispatchToUi, Action action)
    {
        if (action is null)
            return;
        if (dispatchToUi is null)
        {
            action();
            return;
        }

        dispatchToUi(action);
    }
}