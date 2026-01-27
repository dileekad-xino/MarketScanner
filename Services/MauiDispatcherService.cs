using Microsoft.Maui.Controls;

namespace MarketScanner.Services;

/// <summary>
/// MAUI implementation of the dispatcher service.
/// </summary>
public sealed class MauiDispatcherService : IDispatcherService
{
    private static bool IsUiAvailable =>
        Application.Current?.Dispatcher is not null;

    public void OnUI(Action action)
    {
        if (!IsUiAvailable)
            return;

        var dispatcher = Application.Current!.Dispatcher;

        if (dispatcher.IsDispatchRequired)
        {
            dispatcher.Dispatch(() =>
            {
                try { action(); } catch { }
            });
        }
        else
        {
            try { action(); } catch { }
        }
    }

    public Task OnUIAsync(Action action)
        => OnUIAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });

    public async Task OnUIAsync(Func<Task> action)
    {
        if (!IsUiAvailable)
            return;

        var dispatcher = Application.Current!.Dispatcher;

        if (!dispatcher.IsDispatchRequired)
        {
            try { await action(); } catch { }
            return;
        }

        var tcs = new TaskCompletionSource();

        dispatcher.Dispatch(async () =>
        {
            try
            {
                await action();
                tcs.TrySetResult();
            }
            catch
            {
                tcs.TrySetResult();
            }
        });

        await tcs.Task;
    }

    public async Task<T> OnUIAsync<T>(Func<T> func)
    {
        if (!IsUiAvailable)
            return default!;

        var dispatcher = Application.Current!.Dispatcher;

        if (!dispatcher.IsDispatchRequired)
        {
            try { return func(); } catch { return default!; }
        }

        var tcs = new TaskCompletionSource<T>();

        dispatcher.Dispatch(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch
            {
                tcs.TrySetResult(default!);
            }
        });

        return await tcs.Task;
    }

    public Task InvokeAsync(Func<Task> action) => OnUIAsync(action);
}

