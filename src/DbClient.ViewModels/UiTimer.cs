namespace DbClient.ViewModels;

public sealed class UiTimer
{
    private readonly SynchronizationContext _context =
        SynchronizationContext.Current ?? throw new InvalidOperationException("UiTimer must be created on the UI thread.");
    private readonly Timer _timer;
    private bool _enabled;

    public UiTimer(TimeSpan interval)
    {
        Interval = interval;
        _timer = new Timer(_ => _context.Post(_ => { if (_enabled) Tick?.Invoke(this, EventArgs.Empty); }, null));
    }

    public event EventHandler? Tick;

    public TimeSpan Interval
    {
        get;
        set
        {
            field = value;
            if (_enabled) Start();
        }
    }

    public void Start()
    {
        _enabled = true;
        _timer.Change(Interval, Interval);
    }

    public void Stop()
    {
        _enabled = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }
}
