using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Identity;

/// <summary>How the People page shows a person: their figures now, or over a review period.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeopleLens>))]
public enum PeopleLens
{
    Now,

    Review,
}

/// <summary>A viewer's saved slice of the people they follow, which only that viewer sees.</summary>
/// <param name="Position">Its place in the viewer's order, from 0.</param>
/// <param name="ReviewDays">For the Review lens, the period it opens on: 14, 30 or 90 days; null for the Now lens.</param>
public sealed record PeopleView(
    Guid Id,
    string Name,
    int Position,
    PeopleQuery Query,
    PeopleLens Lens,
    int? ReviewDays,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>A view as it is created or changed.</summary>
public sealed record PeopleViewDraft(string Name, PeopleQuery Query, PeopleLens Lens, int? ReviewDays);

public static class PeopleViewLimits
{
    /// <summary>The most views one viewer may hold.</summary>
    public const int PerViewer = 20;

    public const int NameLength = 100;
}

/// <summary>What a write of a viewer's view found.</summary>
public enum PeopleViewChange
{
    Changed,

    /// <summary>The viewer holds no such view.</summary>
    NotFound,

    /// <summary>Another view of the viewer's has the name, ignoring case.</summary>
    NameTaken,

    /// <summary>The viewer already holds <see cref="PeopleViewLimits.PerViewer"/> views.</summary>
    LimitReached,

    /// <summary>A reorder that does not name each of the viewer's views exactly once.</summary>
    OrderMismatch,
}

/// <summary>A write of a viewer's view, and the view as written when <see cref="Change"/> is changed.</summary>
public sealed record PeopleViewWrite(PeopleViewChange Change, PeopleView? View);
