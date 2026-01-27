using Microsoft.Maui.Controls;

namespace MarketScanner.Services;

/// <summary>
/// MAUI implementation of the dispatcher service.
/// </summary>
public sealed class MauiDispatcherService : IDispatcherService
{
    public void OnUI(Action action)
    {
        if (MainThread.IsMainThread)
        {
            action();
            return;
        }

        try
        {
            MainThread.BeginInvokeOnMainThread(action);
        }
        catch (InvalidOperationException)
        {
            // Main thread not available - skip execution
            // This can happen during app shutdown or when the main thread is no longer available
        }
    }

    public async Task OnUIAsync(Func<Task> action)
    {
        if (MainThread.IsMainThread)
        {
            await action();
            return;
        }

        try
        {
            await MainThread.InvokeOnMainThreadAsync(action);
        }
        catch (InvalidOperationException)
        {
            // UI thread is gone → drop work
        }
    }

    public Task OnUIAsync(Action action)
    {
        return OnUIAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    public async Task<T> OnUIAsync<T>(Func<T> func)
    {
        if (MainThread.IsMainThread)
        {
            return func();
        }
        else
        {
            try
            {
                return await MainThread.InvokeOnMainThreadAsync(func);
            }
            catch (InvalidOperationException)
            {
                // Main thread not available - execute on current thread as fallback
                // return func() ;
                return default!;
            }
        }
    }

    public async Task InvokeAsync(Func<Task> action)
    {
        await OnUIAsync(action);
    }
}
