using System;
using System.Collections.Generic;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты общего UI-маршаллера <see cref="UiDispatch"/> (issues #334/#330): действие
/// выполняется напрямую при отсутствии маршаллера (юнит-окружение) и через переданный
/// делегат, когда он задан; исключение внутри действия пробрасывается наружу.
/// </summary>
public sealed class UiDispatchTests
{
    [Fact]
    public void Run_NullDispatcher_InvokesDirectly()
    {
        var invoked = false;

        UiDispatch.Run(null, () => invoked = true);

        Assert.True(invoked);
    }

    [Fact]
    public void Run_WithDispatcher_InvokesThroughDelegate()
    {
        var dispatched = new List<Action>();
        var invoked = false;
        Action<Action> dispatcher = action =>
        {
            dispatched.Add(action);
            action();
        };

        UiDispatch.Run(dispatcher, () => invoked = true);

        Assert.Single(dispatched);
        Assert.True(invoked);
    }

    [Fact]
    public void Run_WithDispatcher_DoesNotExecuteWhenDispatcherDrops()
    {
        // Реальный маршаллер (Dispatcher.Post) исполняет асинхронно; в тесте имитируем
        // «отложенное» исполнение, не запуская действие немедленно — маршаллер остаётся
        // единственной точкой доставки.
        var dispatched = new List<Action>();
        Action<Action> dispatcher = action => dispatched.Add(action);

        UiDispatch.Run(dispatcher, () => throw new InvalidOperationException("не должно выполниться здесь"));

        Assert.Single(dispatched);
    }

    [Fact]
    public void Run_NullAction_NoOp()
    {
        var dispatched = 0;
        Action<Action> dispatcher = _ => dispatched++;

        UiDispatch.Run(dispatcher, null!);

        Assert.Equal(0, dispatched);
    }

    [Fact]
    public void Run_ExceptionInsideAction_Propagates()
    {
        var expected = new InvalidOperationException("boom");
        Action<Action> dispatcher = action => action();

        var ex = Assert.Throws<InvalidOperationException>(() => UiDispatch.Run(dispatcher, () => throw expected));

        Assert.Same(expected, ex);
    }
}