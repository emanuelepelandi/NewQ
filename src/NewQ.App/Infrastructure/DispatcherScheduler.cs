using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using NewQ.Core.Engine;

namespace NewQ.App.Infrastructure;

/// <summary>
/// Runs engine work on the UI thread. Timers use a thread-pool timer (≈1 ms with timeBeginPeriod)
/// and are dispatched at Send priority so they are not delayed by rendering.
/// </summary>
public sealed class DispatcherScheduler : IScheduler
{
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public DispatcherScheduler(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public TimeSpan Now => _clock.Elapsed;

    public void Post(Action action) => _dispatcher.BeginInvoke(DispatcherPriority.Send, action);

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var entry = new ScheduledEntry();
        entry.Timer = new Timer(_ =>
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                if (entry.Cancelled) return;
                entry.Dispose();
                action();
            });
        }, null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        return entry;
    }

    private sealed class ScheduledEntry : IDisposable
    {
        public Timer? Timer;
        public volatile bool Cancelled;

        public void Dispose()
        {
            Cancelled = true;
            Timer?.Dispose();
        }
    }
}
