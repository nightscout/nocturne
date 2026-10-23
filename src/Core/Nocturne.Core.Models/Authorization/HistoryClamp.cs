namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// How much recent history a history-clamped reader may read: a share without full history, or a
/// member or credential limited to 24 hours. The row level security policies clamp to it and the
/// reports that state their own window start at it.
/// </summary>
public static class HistoryClamp
{
    public const int WindowHours = 24;

    public static TimeSpan Window => TimeSpan.FromHours(WindowHours);
}
