namespace Nocturne.Connectors.GoogleHealth.Services;

/// <summary>
///     The keys Google Health keeps in its two state slots, and what they mean together. The resume
///     slot holds <see cref="LastSyncedTo"/> and the historical import's owed range,
///     <see cref="BackfillFloor"/> up to <see cref="BackfillCursor"/>; the vendor slot holds
///     <see cref="BackfillChunkDays"/> and <see cref="Inventory"/>.
/// </summary>
/// <remarks>
///     None of the resume keys is a declared stamp, so a cursor reset clears none of them. The range
///     is owed history. <see cref="LastSyncedTo"/> is how far the live sync reached and has no stored
///     data to fall back on: without it the live sync starts at midnight UTC, so clearing it on a
///     reset that then fails would give up what lies between. A reset that re-reads every selected
///     type moves it to its own end.
/// </remarks>
public static class GoogleHealthState
{
    /// <summary>Resume: the end of the last live sync that covered every selected type.</summary>
    public const string LastSyncedTo = "lastSyncedTo";

    /// <summary>Resume: the oldest UTC day the historical import has brought in.</summary>
    public const string BackfillCursor = "backfillCursorDate";

    /// <summary>Resume: the oldest UTC day the historical import is aiming for.</summary>
    public const string BackfillFloor = "backfillFloorDate";

    /// <summary>Vendor: the days a historical window is cut to after one failed, while that month is retried.</summary>
    public const string BackfillChunkDays = "backfillChunkDays";

    /// <summary>Vendor: the data scan of the current session (<c>GoogleHealthInventoryItem[]</c>).</summary>
    public const string Inventory = "inventory";

    /// <summary>The floor of a range stored without one, and the earliest an import may start.</summary>
    public static readonly DateTime EarliestSupportedDate = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     Whether the historical import has brought in everything it aims for: its cursor has reached
    ///     its floor. A floor moved below a finished cursor makes it unfinished again.
    /// </summary>
    public static bool BackfillComplete(DateTime? cursor, DateTime? floor) =>
        cursor is { } reached && reached <= (floor ?? EarliestSupportedDate);
}
