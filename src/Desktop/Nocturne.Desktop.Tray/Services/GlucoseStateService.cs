using CommunityToolkit.Mvvm.ComponentModel;
using Nocturne.Desktop.Tray.Models;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// Observable glucose state that drives the tray icon and flyout UI.
/// Maintains the current reading and a rolling history buffer for the chart.
/// </summary>
public sealed partial class GlucoseStateService : ObservableObject, IDisposable
{
    private const int MaxHistorySize = 1000;

    [ObservableProperty]
    private GlucoseReading? _currentReading;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isStale;

    private readonly List<GlucoseReading> _history = [];
    private readonly object _lock = new();
    private Timer? _staleTimer;

    public event Action? StateChanged;

    public IReadOnlyList<GlucoseReading> History
    {
        get
        {
            lock (_lock)
            {
                return _history.ToList();
            }
        }
    }

    public GlucoseStateService()
    {
        _staleTimer = new Timer(CheckStaleness, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void ProcessReading(GlucoseReading reading)
    {
        lock (_lock)
        {
            if (_history.Any(h => h.Mills == reading.Mills))
            {
                return;
            }

            _history.Add(reading);
            _history.Sort((a, b) => a.Mills.CompareTo(b.Mills));

            while (_history.Count > MaxHistorySize)
            {
                _history.RemoveAt(0);
            }
        }

        if (CurrentReading is null || reading.Mills >= CurrentReading.Mills)
        {
            CurrentReading = reading;
            IsStale = false;
        }

        StateChanged?.Invoke();
    }

    public void SetHistory(IEnumerable<GlucoseReading> readings)
    {
        lock (_lock)
        {
            _history.Clear();
            _history.AddRange(readings);
            _history.Sort((a, b) => a.Mills.CompareTo(b.Mills));

            while (_history.Count > MaxHistorySize)
            {
                _history.RemoveAt(0);
            }
        }

        var latest = _history.LastOrDefault();
        if (latest is not null)
        {
            CurrentReading = latest;
            IsStale = Helpers.TimeAgoHelper.IsStale(latest.Timestamp);
        }

        StateChanged?.Invoke();
    }

    public void MergeReadings(IEnumerable<GlucoseReading> readings)
    {
        var added = false;
        lock (_lock)
        {
            foreach (var reading in readings)
            {
                if (_history.Any(h => h.Mills == reading.Mills))
                    continue;

                _history.Add(reading);
                added = true;
            }

            if (added)
            {
                _history.Sort((a, b) => a.Mills.CompareTo(b.Mills));

                while (_history.Count > MaxHistorySize)
                {
                    _history.RemoveAt(0);
                }
            }
        }

        if (added)
        {
            var latest = _history.LastOrDefault();
            if (latest is not null && (CurrentReading is null || latest.Mills >= CurrentReading.Mills))
            {
                CurrentReading = latest;
                IsStale = Helpers.TimeAgoHelper.IsStale(latest.Timestamp);
            }

            StateChanged?.Invoke();
        }
    }

    public IReadOnlyList<GlucoseReading> GetReadingsForChart(int hours)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeMilliseconds();
        lock (_lock)
        {
            return _history.Where(r => r.Mills >= cutoff).ToList();
        }
    }

    private void CheckStaleness(object? state)
    {
        if (CurrentReading is not null)
        {
            var wasStale = IsStale;
            IsStale = Helpers.TimeAgoHelper.IsStale(CurrentReading.Timestamp);

            if (IsStale != wasStale)
            {
                StateChanged?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _staleTimer?.Dispose();
        _staleTimer = null;
    }
}
