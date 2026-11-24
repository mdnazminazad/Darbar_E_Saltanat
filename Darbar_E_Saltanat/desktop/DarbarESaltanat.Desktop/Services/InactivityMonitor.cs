using System.Windows.Input;
using System.Windows.Threading;
using DarbarESaltanat.Application.Auth;

namespace DarbarESaltanat.Desktop.Services;

/// <summary>Locks the terminal after N minutes without mouse/keyboard/touch input (setting: auto_lock_minutes).</summary>
public sealed class InactivityMonitor
{
    private readonly ICurrentSession _session;
    private readonly DispatcherTimer _timer;
    private DateTime _lastInput = DateTime.UtcNow;
    private bool _hooked;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    public InactivityMonitor(ICurrentSession session)
    {
        _session = session;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) =>
        {
            if (_session.Current is not null && !_session.IsLocked && DateTime.UtcNow - _lastInput >= Timeout) _session.Lock();
        };
    }

    public void Start(TimeSpan timeout)
    {
        Timeout = timeout;
        _lastInput = DateTime.UtcNow;
        if (!_hooked) { InputManager.Current.PreProcessInput += (_, e) => { if (e.StagingItem.Input is KeyboardEventArgs or MouseButtonEventArgs or TouchEventArgs or MouseWheelEventArgs) _lastInput = DateTime.UtcNow; }; _hooked = true; }
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
