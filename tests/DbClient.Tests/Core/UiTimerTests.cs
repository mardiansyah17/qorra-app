using DbClient.ViewModels;

namespace DbClient.Tests.Core;

public sealed class UiTimerTests
{
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback, object?)> _queue = [];
        public override void Post(SendOrPostCallback d, object? state) { lock (_queue) _queue.Add((d, state)); }
        public int Count { get { lock (_queue) return _queue.Count; } }
        public void RunAll()
        {
            List<(SendOrPostCallback, object?)> items;
            lock (_queue) { items = [.. _queue]; _queue.Clear(); }
            foreach (var (d, state) in items) d(state);
        }
    }

    private static UiTimer Create(QueueContext context, TimeSpan interval)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { return new UiTimer(interval); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task Tick_RunsOnTheCreatingContext_AndStopDropsAQueuedTick()
    {
        var context = new QueueContext();
        var timer = Create(context, TimeSpan.FromMilliseconds(10));
        var ticks = 0;
        timer.Tick += (_, _) => ticks++;

        timer.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (context.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.Equal(0, ticks);

        context.RunAll();
        Assert.True(ticks > 0);

        timer.Stop();
        var before = ticks;
        context.RunAll();
        Assert.Equal(before, ticks);
    }

    [Fact]
    public void Create_WithoutUiThread_Throws()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { Assert.Throws<InvalidOperationException>(() => new UiTimer(TimeSpan.FromSeconds(1))); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
