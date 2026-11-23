using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// A tracker for a consumable, an appointment or a reminder: what restarts and ends its runs, how
/// long a run lasts, and the notification thresholds it alerts at. Trackers are the tenant's: any
/// member may read or change them, and <see cref="CreatedBy"/> grants nothing.
/// </summary>
/// <remarks>
/// A definition is made by <see cref="Create"/> and changed by <see cref="Revise"/>, which hold it
/// to the rules a tracker must keep, or read back from storage by <see cref="Restore"/>. Stored rows
/// are not checked again: some predate a rule, and a definition saved before device events were
/// enforced may still name an event that is not one.
/// </remarks>
public sealed record TrackerDefinition
{
    private TrackerDefinition() { }

    public Guid Id { get; private init; }

    /// <summary>The subject who created the definition.</summary>
    public string CreatedBy { get; private init; } = string.Empty;

    public string Name { get; private init; } = string.Empty;
    public string? Description { get; private init; }
    public TrackerCategory Category { get; private init; } = TrackerCategory.Consumable;

    /// <summary>The Lucide icon it is shown with.</summary>
    public string Icon { get; private init; } = DefaultIcon;

    /// <summary>
    /// The Nightscout event names that restart a run, as stored. A name a client should not see is
    /// one <see cref="TrackerEventTypes.CanonicalTrigger"/> does not know.
    /// </summary>
    public IReadOnlyList<string> TriggerEventTypes { get; private init; } = [];

    /// <summary>The Nightscout event names that end a run without starting another, as stored.</summary>
    public IReadOnlyList<string> EndEventTypes { get; private init; } = [];

    /// <summary>
    /// What the tracked hardware provides. Empty when not known, and then what the tracker stands
    /// for is read from its triggers and category (<see cref="TrackerCoverage.Of(TrackerDefinition)"/>).
    /// </summary>
    public IReadOnlySet<PumpCapability> Covers { get; private init; } = new HashSet<PumpCapability>();

    /// <summary>
    /// The catalog pump part the tracker follows, as the catalog id and the part's key
    /// (e.g. "omnipod-5/pod"). Null for a tracker that follows no catalog part.
    /// </summary>
    public string? DeviceComponent { get; private init; }

    /// <summary>A text a device event's notes must contain to restart or end a run.</summary>
    public string? TriggerNotesContains { get; private init; }

    public int? LifespanHours { get; private init; }

    /// <summary>
    /// For a tracker that <see cref="TrackerCoverage.HoldsInsulin(TrackerDefinition)"/>: the level in
    /// units below which its managed reservoir rule fires. Null for no level rule.
    /// </summary>
    public double? LowReservoirUnits { get; private init; }

    public NotificationUrgency LowReservoirUrgency { get; private init; } = NotificationUrgency.Warn;
    public bool IsFavorite { get; private init; }
    public DashboardVisibility DashboardVisibility { get; private init; } = DashboardVisibility.Always;

    /// <summary>The device event a manual start records.</summary>
    public string? StartEventType { get; private init; }

    /// <summary>The device event a manual completion records.</summary>
    public string? CompletionEventType { get; private init; }

    public TrackerMode Mode { get; private init; } = TrackerMode.Duration;
    public DateTime CreatedAt { get; private init; }
    public DateTime? UpdatedAt { get; private init; }

    public IReadOnlyList<TrackerNotificationThreshold> NotificationThresholds { get; private init; } = [];

    /// <summary>The icon a definition that names none is shown with.</summary>
    public const string DefaultIcon = "activity";

    /// <summary>The definition <paramref name="draft"/> describes, created by <paramref name="createdBy"/>.</summary>
    /// <returns>The definition, or the first rule the draft breaks, checked in <see cref="TrackerDefinitionRule"/> order.</returns>
    public static TrackerDefinitionResult Create(TrackerDefinitionDraft draft, string createdBy, DateTime createdAt)
    {
        if (CheckName(draft.Name) is { } nameError)
            return nameError;
        var thresholds = draft.NotificationThresholds ?? [];
        if (CheckThresholds(thresholds, draft.LifespanHours, draft.Mode) is { } thresholdError)
            return thresholdError;
        if (CheckLifespan(draft.Mode, draft.LifespanHours) is { } lifespanError)
            return lifespanError;
        if (!TrackerCoverage.TryNormalise(draft.Covers, out var covers, out var coversError))
            return Broken(TrackerDefinitionRule.Covers, coversError!);
        var coverage = (covers ?? []).ToHashSet();
        if (CheckLowReservoir(draft.LowReservoirUnits, draft.Category, coverage) is { } reservoirError)
            return reservoirError;
        if (!TrackerEventTypes.TryNormaliseTriggers(draft.TriggerEventTypes, out var triggers, out var triggerError))
            return Broken(TrackerDefinitionRule.Triggers, triggerError!);
        if (!TrackerEventTypes.TryNormaliseEnds(draft.EndEventTypes, out var ends, out var endError))
            return Broken(TrackerDefinitionRule.Ends, endError!);
        if (CheckEventTypes(draft.StartEventType, draft.CompletionEventType, out var start, out var completion) is { } eventError)
            return eventError;

        return new TrackerDefinitionResult(new TrackerDefinition
        {
            CreatedBy = createdBy,
            Name = draft.Name,
            Description = draft.Description,
            Category = draft.Category,
            Icon = draft.Icon ?? DefaultIcon,
            TriggerEventTypes = triggers ?? [],
            EndEventTypes = ends ?? [],
            Covers = coverage,
            DeviceComponent = draft.DeviceComponent,
            TriggerNotesContains = NullIfBlank(draft.TriggerNotesContains),
            LifespanHours = draft.LifespanHours,
            LowReservoirUnits = draft.LowReservoirUnits,
            LowReservoirUrgency = draft.LowReservoirUrgency,
            IsFavorite = draft.IsFavorite,
            DashboardVisibility = draft.DashboardVisibility,
            StartEventType = start,
            CompletionEventType = completion,
            Mode = draft.Mode,
            CreatedAt = createdAt,
            NotificationThresholds = thresholds,
        });
    }

    /// <summary>
    /// This definition with <paramref name="edit"/> applied. The revised definition is held to every
    /// rule, its stored notification thresholds included when the edit keeps them, except that a
    /// start or completion event the edit re-posts unchanged is kept as stored.
    /// </summary>
    /// <returns>The revised definition, or the first rule it breaks, checked in <see cref="TrackerDefinitionRule"/> order.</returns>
    public TrackerDefinitionResult Revise(TrackerDefinitionEdit edit)
    {
        if (edit.Name is not null && CheckName(edit.Name) is { } nameError)
            return nameError;
        var mode = edit.Mode ?? Mode;
        var lifespan = mode == TrackerMode.Event ? edit.LifespanHours : edit.LifespanHours ?? LifespanHours;
        var thresholds = edit.NotificationThresholds ?? NotificationThresholds;
        if (CheckThresholds(thresholds, lifespan, mode) is { } thresholdError)
            return thresholdError;
        if (CheckLifespan(mode, lifespan) is { } lifespanError)
            return lifespanError;

        var category = edit.Category ?? Category;
        if (!TrackerCoverage.TryNormalise(edit.Covers, out var covers, out var coversError))
            return Broken(TrackerDefinitionRule.Covers, coversError!);
        var coverage = covers?.ToHashSet() ?? Covers;
        if (CheckLowReservoir(edit.LowReservoirUnits, category, coverage) is { } reservoirError)
            return reservoirError;
        if (!TrackerEventTypes.TryNormaliseTriggers(edit.TriggerEventTypes, out var triggers, out var triggerError))
            return Broken(TrackerDefinitionRule.Triggers, triggerError!);
        if (!TrackerEventTypes.TryNormaliseEnds(edit.EndEventTypes, out var ends, out var endError))
            return Broken(TrackerDefinitionRule.Ends, endError!);
        if (CheckEventTypes(
                Changed(edit.StartEventType, StartEventType),
                Changed(edit.CompletionEventType, CompletionEventType),
                out var start,
                out var completion) is { } eventError)
            return eventError;

        // The notes filter travels with the trigger list: the editor always posts the list, but a
        // cleared filter arrives as no field at all, so null-means-keep could never clear it.
        var notesFilter = triggers is not null || edit.TriggerNotesContains is not null
            ? NullIfBlank(edit.TriggerNotesContains)
            : TriggerNotesContains;

        return new TrackerDefinitionResult(this with
        {
            Name = edit.Name ?? Name,
            Description = edit.Description ?? Description,
            Category = category,
            Icon = edit.Icon ?? Icon,
            TriggerEventTypes = triggers ?? TriggerEventTypes,
            TriggerNotesContains = notesFilter,
            EndEventTypes = ends ?? EndEventTypes,
            Covers = coverage,
            LifespanHours = lifespan,
            LowReservoirUnits = TrackerCoverage.HoldsInsulin(category, coverage) ? edit.LowReservoirUnits : null,
            LowReservoirUrgency = edit.LowReservoirUrgency ?? LowReservoirUrgency,
            IsFavorite = edit.IsFavorite ?? IsFavorite,
            DashboardVisibility = edit.DashboardVisibility ?? DashboardVisibility,
            StartEventType = AsPosted(edit.StartEventType, start, StartEventType),
            CompletionEventType = AsPosted(edit.CompletionEventType, completion, CompletionEventType),
            Mode = mode,
            NotificationThresholds = thresholds,
        });
    }

    /// <summary>
    /// Why a run cannot start with <paramref name="scheduledAt"/>, or null when it can: an Event
    /// tracker's run is for a scheduled time, and a Duration tracker's has none.
    /// </summary>
    public string? CannotStart(DateTime? scheduledAt) => Mode switch
    {
        TrackerMode.Event when !scheduledAt.HasValue => "Event mode trackers require a ScheduledAt datetime",
        TrackerMode.Duration when scheduledAt.HasValue => "Duration mode trackers should not have a ScheduledAt datetime",
        _ => null,
    };

    /// <summary>A definition as it was stored, which is not checked again.</summary>
    /// <remarks>Only the mapper may call this, as <c>TrackerRestoreGuardTests</c> enforces.</remarks>
    public static TrackerDefinition Restore(
        Guid id,
        string name,
        TrackerCategory category = TrackerCategory.Consumable,
        TrackerMode mode = TrackerMode.Duration,
        string createdBy = "",
        string? description = null,
        string icon = DefaultIcon,
        IReadOnlyList<string>? triggerEventTypes = null,
        IReadOnlyList<string>? endEventTypes = null,
        IReadOnlySet<PumpCapability>? covers = null,
        string? deviceComponent = null,
        string? triggerNotesContains = null,
        int? lifespanHours = null,
        double? lowReservoirUnits = null,
        NotificationUrgency lowReservoirUrgency = NotificationUrgency.Warn,
        bool isFavorite = false,
        DashboardVisibility dashboardVisibility = DashboardVisibility.Always,
        string? startEventType = null,
        string? completionEventType = null,
        DateTime createdAt = default,
        DateTime? updatedAt = null,
        IReadOnlyList<TrackerNotificationThreshold>? notificationThresholds = null) => new()
    {
        Id = id,
        CreatedBy = createdBy,
        Name = name,
        Description = description,
        Category = category,
        Icon = icon,
        TriggerEventTypes = triggerEventTypes ?? [],
        EndEventTypes = endEventTypes ?? [],
        Covers = covers ?? new HashSet<PumpCapability>(),
        DeviceComponent = deviceComponent,
        TriggerNotesContains = triggerNotesContains,
        LifespanHours = lifespanHours,
        LowReservoirUnits = lowReservoirUnits,
        LowReservoirUrgency = lowReservoirUrgency,
        IsFavorite = isFavorite,
        DashboardVisibility = dashboardVisibility,
        StartEventType = startEventType,
        CompletionEventType = completionEventType,
        Mode = mode,
        CreatedAt = createdAt,
        UpdatedAt = updatedAt,
        NotificationThresholds = notificationThresholds ?? [],
    };

    private static TrackerDefinitionResult? CheckName(string name) =>
        string.IsNullOrWhiteSpace(name) ? Broken(TrackerDefinitionRule.Name, "Name is required") : null;

    /// <summary>
    /// A Duration tracker's negative threshold counts back from the end of its lifespan, so it needs
    /// one, and must fall inside it.
    /// </summary>
    private static TrackerDefinitionResult? CheckThresholds(
        IReadOnlyList<TrackerNotificationThreshold> thresholds, int? lifespanHours, TrackerMode mode)
    {
        if (mode != TrackerMode.Duration)
            return null;

        foreach (var threshold in thresholds.Where(t => t.Hours < 0))
        {
            if (lifespanHours is not { } lifespan)
                return Broken(TrackerDefinitionRule.Thresholds, "Negative thresholds require a lifespan to be set");
            if (Math.Abs(threshold.Hours) >= lifespan)
                return Broken(
                    TrackerDefinitionRule.Thresholds,
                    $"Negative threshold {threshold.Hours} exceeds tracker lifespan of {lifespan} hours");
        }

        return null;
    }

    private static TrackerDefinitionResult? CheckLifespan(TrackerMode mode, int? lifespanHours) =>
        mode == TrackerMode.Event && lifespanHours.HasValue
            ? Broken(TrackerDefinitionRule.Lifespan, "Event mode trackers should not have a lifespan")
            : null;

    private static TrackerDefinitionResult? CheckLowReservoir(
        double? units, TrackerCategory category, IReadOnlySet<PumpCapability> covers)
    {
        if (units is not { } value)
            return null;
        if (!TrackerCoverage.HoldsInsulin(category, covers))
            return Broken(
                TrackerDefinitionRule.LowReservoir,
                "Low reservoir units apply only to trackers of a reservoir or of hardware that holds insulin");
        if (value <= 0)
            return Broken(TrackerDefinitionRule.LowReservoir, "Low reservoir units must be greater than zero");
        if (value > 1000)
            return Broken(TrackerDefinitionRule.LowReservoir, "Low reservoir units must be at most 1000");
        return null;
    }

    private static TrackerDefinitionResult? CheckEventTypes(
        string? requestedStart, string? requestedCompletion, out string? start, out string? completion)
    {
        completion = null;
        if (!TrackerEventTypes.TryNormaliseDeviceEvent(requestedStart, "StartEventType", out start, out var error)
            || !TrackerEventTypes.TryNormaliseDeviceEvent(requestedCompletion, "CompletionEventType", out completion, out error))
            return Broken(TrackerDefinitionRule.EventTypes, error!);
        return null;
    }

    /// <summary>The posted event name when it differs from the stored one, else null.</summary>
    private static string? Changed(string? requested, string? stored) =>
        string.Equals(requested?.Trim(), stored?.Trim(), StringComparison.OrdinalIgnoreCase) ? null : requested;

    /// <summary>
    /// An event name is applied as posted, since the editor posts the whole definition: blank clears
    /// it, and a name re-posted unchanged keeps its stored spelling.
    /// </summary>
    private static string? AsPosted(string? requested, string? normalised, string? stored) =>
        string.IsNullOrWhiteSpace(requested) ? null : normalised ?? stored;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TrackerDefinitionResult Broken(TrackerDefinitionRule rule, string message) =>
        new(null, new TrackerDefinitionError(rule, message));
}

/// <summary>One notification threshold of a <see cref="TrackerDefinition"/>.</summary>
/// <param name="Id">Empty for a threshold not yet stored.</param>
/// <param name="Hours">
/// Hours from a run's start, or from the scheduled time of an Event tracker. A negative value is
/// that many hours before the lifespan ends, or before the scheduled time.
/// </param>
/// <param name="AlertRuleId">The managed alert rule that delivers it; null until the rule sync has run.</param>
public sealed record TrackerNotificationThreshold(
    Guid Id,
    NotificationUrgency Urgency,
    int Hours,
    string? Description,
    int DisplayOrder,
    Guid? AlertRuleId = null);

/// <summary>
/// What a new definition is made from (<see cref="TrackerDefinition.Create"/>). Lists and event names
/// are as requested; the definition holds them canonicalised.
/// </summary>
public sealed record TrackerDefinitionDraft
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public TrackerCategory Category { get; init; } = TrackerCategory.Consumable;

    /// <summary>Null for <see cref="TrackerDefinition.DefaultIcon"/>.</summary>
    public string? Icon { get; init; }

    public IReadOnlyList<string>? TriggerEventTypes { get; init; }
    public IReadOnlyList<string>? EndEventTypes { get; init; }

    /// <summary><see cref="PumpCapability"/> names.</summary>
    public IReadOnlyList<string>? Covers { get; init; }

    public string? DeviceComponent { get; init; }
    public string? TriggerNotesContains { get; init; }
    public int? LifespanHours { get; init; }
    public double? LowReservoirUnits { get; init; }
    public NotificationUrgency LowReservoirUrgency { get; init; } = NotificationUrgency.Warn;
    public IReadOnlyList<TrackerNotificationThreshold>? NotificationThresholds { get; init; }
    public bool IsFavorite { get; init; }
    public DashboardVisibility DashboardVisibility { get; init; } = DashboardVisibility.Always;
    public string? StartEventType { get; init; }
    public string? CompletionEventType { get; init; }
    public TrackerMode Mode { get; init; } = TrackerMode.Duration;
}

/// <summary>
/// A change to a definition (<see cref="TrackerDefinition.Revise"/>). Null keeps the stored value,
/// except where a member says otherwise.
/// </summary>
public sealed record TrackerDefinitionEdit
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public TrackerCategory? Category { get; init; }
    public string? Icon { get; init; }
    public IReadOnlyList<string>? TriggerEventTypes { get; init; }

    /// <summary>An empty list clears it.</summary>
    public IReadOnlyList<string>? EndEventTypes { get; init; }

    /// <summary><see cref="PumpCapability"/> names; an empty list clears it.</summary>
    public IReadOnlyList<string>? Covers { get; init; }

    /// <summary>Applied with <see cref="TriggerEventTypes"/>, so a posted trigger list with no filter clears it.</summary>
    public string? TriggerNotesContains { get; init; }

    /// <summary>Null keeps the stored lifespan, except that a revision to Event mode drops it.</summary>
    public int? LifespanHours { get; init; }

    /// <summary>Applied as posted, since the editor posts the whole definition: null clears the level rule.</summary>
    public double? LowReservoirUnits { get; init; }

    public NotificationUrgency? LowReservoirUrgency { get; init; }

    /// <summary>Replaces every stored threshold when posted.</summary>
    public IReadOnlyList<TrackerNotificationThreshold>? NotificationThresholds { get; init; }

    public bool? IsFavorite { get; init; }
    public DashboardVisibility? DashboardVisibility { get; init; }

    /// <summary>Applied as posted: null or blank clears it.</summary>
    public string? StartEventType { get; init; }

    /// <summary>Applied as posted: null or blank clears it.</summary>
    public string? CompletionEventType { get; init; }

    public TrackerMode? Mode { get; init; }
}

/// <summary>The rules a <see cref="TrackerDefinition"/> keeps, in the order they are checked.</summary>
public enum TrackerDefinitionRule
{
    /// <summary>A definition has a name that is not blank.</summary>
    Name,

    /// <summary>A Duration tracker's negative threshold needs a lifespan and falls inside it.</summary>
    Thresholds,

    /// <summary>An Event tracker has no lifespan.</summary>
    Lifespan,

    /// <summary>Every covered name is a <see cref="PumpCapability"/>.</summary>
    Covers,

    /// <summary>A low reservoir level is for a tracker that holds insulin, and is more than 0 and at most 1000 units.</summary>
    LowReservoir,

    /// <summary>Every restart event is one of <see cref="TrackerEventTypes.TriggerableEventTypes"/>.</summary>
    Triggers,

    /// <summary>Every end event is one of <see cref="TrackerEventTypes.EndingEventTypes"/>.</summary>
    Ends,

    /// <summary>The start and completion events are device events.</summary>
    EventTypes,
}

/// <summary>A rule a definition breaks, and what to tell the person who asked for it.</summary>
public sealed record TrackerDefinitionError(TrackerDefinitionRule Rule, string Message);

/// <summary>A definition, or the rule that kept it from being made.</summary>
public sealed record TrackerDefinitionResult(TrackerDefinition? Definition, TrackerDefinitionError? Error = null);
