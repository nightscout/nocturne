namespace Nocturne.Core.Models.Timezones;

/// <summary>A fake-UTC timestamp read as UTC, and the offset in minutes east of UTC it was read at.</summary>
public readonly record struct TimelineInstant(DateTime Utc, int OffsetMinutes);
