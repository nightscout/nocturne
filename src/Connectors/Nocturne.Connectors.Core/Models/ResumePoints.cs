namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     Where each family of one sync run resumes, as the base resolved it for the run
///     (<see cref="SyncRequest.Resume"/>). A sync reads its resume points here and nowhere else.
/// </summary>
/// <remarks>
///     The base reads every point the connector declares for the run once, before the sync signs in
///     or publishes anything, and the values hold for the whole run. A point read when first asked
///     for could follow the run's own publishes: the glucose point counts meter glucose, so a run
///     that published a meter reading before asking would move its own glucose start past readings
///     it never fetched. A read that failed is held, and asking for that point rethrows it.
/// </remarks>
public sealed class ResumePoints
{
    private readonly IReadOnlyDictionary<ResumeKey, Task<DateTime?>> _points;

    internal ResumePoints(bool scheduled)
        : this(scheduled, new Dictionary<ResumeKey, Task<DateTime?>>(), hasRunStart: false, runStart: null)
    {
    }

    private ResumePoints(
        bool scheduled,
        IReadOnlyDictionary<ResumeKey, Task<DateTime?>> points,
        bool hasRunStart,
        DateTime? runStart)
    {
        Scheduled = scheduled;
        _points = points;
        HasRunStart = hasRunStart;
        RunStart = runStart;
    }

    /// <summary>The points of a request the base has not run, which hold nothing to read.</summary>
    internal static ResumePoints Unresolved => new(scheduled: false);

    /// <summary>
    ///     Whether no caller bounded the run: the scheduled sync, whose families each start at their
    ///     own point.
    /// </summary>
    public bool Scheduled { get; }

    /// <summary>Whether a window policy set one start for every family of the run.</summary>
    internal bool HasRunStart { get; }

    /// <summary>The start a window policy set; meaningful only when <see cref="HasRunStart"/> is set.</summary>
    internal DateTime? RunStart { get; }

    /// <summary>
    ///     The family's resume point: its newest stored record minus the catch-up overlap. With
    ///     nothing stored, glucose, treatments and state spans give the connector's
    ///     <see cref="Services.BaseConnectorService{TConfig}.FirstSyncStart"/>, and device status and
    ///     activity give its <see cref="Services.BaseConnectorService{TConfig}.HistoryStart"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connector did not declare the family for this run.</exception>
    /// <remarks>Rethrows the failure of a read that failed.</remarks>
    public DateTime? Point(ResumeFamily family) => Point((ResumeKey)family);

    /// <summary>
    ///     One treatment type's resume point, as <see cref="Point(ResumeFamily)"/> gives the
    ///     treatment family's, over the newest stored record of that type alone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connector did not declare the type for this run.</exception>
    public DateTime? Point(SyncDataType treatmentType) => Point(ResumeKey.Of(treatmentType));

    /// <summary>
    ///     These points, with every family of the run starting at <paramref name="start"/>, for a
    ///     connector whose window policy reads all its families in one window.
    /// </summary>
    public ResumePoints StartingAt(DateTime? start) => new(Scheduled, _points, hasRunStart: true, start);

    /// <summary>Whether the base read <paramref name="family"/>'s point for the run.</summary>
    internal bool Holds(ResumeFamily family) => _points.ContainsKey(family);

    internal ResumePoints Resolved(IReadOnlyDictionary<ResumeKey, Task<DateTime?>> points) =>
        new(Scheduled, points, HasRunStart, RunStart);

    private DateTime? Point(ResumeKey key) =>
        _points.TryGetValue(key, out var point)
            ? point.GetAwaiter().GetResult()
            : throw new InvalidOperationException(
                $"The {key} resume point was not resolved for this run: the connector does not declare it in ResumesFrom.");
}
