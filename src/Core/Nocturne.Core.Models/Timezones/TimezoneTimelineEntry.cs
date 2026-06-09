namespace Nocturne.Core.Models.Timezones;

/// <summary>
/// One segment of a tenant's timezone timeline: from <see cref="EffectiveFrom"/> onward (until the
/// next entry), the person was in <see cref="Timezone"/>. Used to convert connector data that is
/// stored as local wall-clock-stamped-as-UTC ("fake UTC" — e.g. Glooko) back to true UTC, honouring
/// both daylight saving (via the zone's historical rules) and travel/relocation (via successive
/// entries).
/// </summary>
/// <remarks>
/// <see cref="EffectiveFrom"/> is a <b>local wall-clock</b> instant (<see cref="DateTimeKind.Unspecified"/>),
/// not UTC: a person thinks "I moved on the 1st" in local terms, and fake-UTC data is itself local
/// wall-clock, so matching happens in local space and avoids the chicken-and-egg of needing the zone
/// to compute the UTC needed to pick the zone.
/// </remarks>
/// <seealso cref="TimezoneTimeline"/>
public class TimezoneTimelineEntry
{
    /// <summary>Primary key (UUID v7).</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Local wall-clock instant from which this zone takes effect (<see cref="DateTimeKind.Unspecified"/>).
    /// The origin entry uses <see cref="DateTime.MinValue"/> to cover all earlier history.
    /// </summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>IANA timezone id (e.g. <c>"Australia/Sydney"</c>). Windows ids are also accepted.</summary>
    public string Timezone { get; set; } = string.Empty;

    /// <summary>When this entry was created.</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>When this entry was last updated.</summary>
    public DateTime? UpdatedAt { get; set; }
}
