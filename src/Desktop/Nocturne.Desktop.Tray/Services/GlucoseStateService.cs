using CommunityToolkit.Mvvm.ComponentModel;
using Nocturne.Desktop.Tray.Models;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// Observable glucose state that drives the tray icon and flyout UI.
/// Maintains the current reading and a rolling history buffer for the chart.
/// </summary>
public sealed partial class GlucoseStateService : ObservableObject
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

    /// <summary>
    /// Processes a new glucose reading, updating the current value and appending to history.
    /// Deduplicates by mills timestamp.
    /// </summary>
    public void ProcessReading(GlucoseReading reading)
    {
        lock (_lock)
        {
            // Deduplicate: skip if we already have a reading with this timestamp
            if (_history.Any(h => h.Mills == reading.Mills))
            {
                return;
            }

            _history.Add(reading);

            // Maintain sorted order by timestamp
            _history.Sort((a, b) => a.Mills.CompareTo(b.Mills));

            // Trim history to max size
            while (_history.Count > MaxHistorySize)
            {
                _history.RemoveAt(0);
            }
        }

        // Update current reading if this is the most recent
        if (CurrentReading is null || reading.Mills >= CurrentReading.Mills)
        {
            CurrentReading = reading;
            IsStale = false;
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Replaces the entire history buffer (used for initial load from API).
    /// </summary>
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

        // Set current reading to the most recent
        var latest = _history.LastOrDefault();
        if (latest is not null)
        {
            CurrentReading = latest;
            IsStale = Helpers.TimeAgoHelper.IsStale(latest.Timestamp);
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Returns readings within the specified time window for chart rendering.
    /// </summary>
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
