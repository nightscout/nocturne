namespace Nocturne.Core.Models.Demo;

/// <summary>
/// The demo clinic: a team that follows the demo host and one synthetic person for each archetype, and the staff
/// persona a visitor is signed in as to see them.
/// </summary>
public static class DemoClinic
{
    public const string TeamName = "Demotown Diabetes Clinic";

    /// <summary>The value of the demo session's <c>as</c> that signs a visitor in as <see cref="PersonaName"/>.</summary>
    public const string SessionPersona = "clinic";

    public const string PersonaName = "Demotown Staff";

    public const string Paediatrics = "Paediatrics";

    public const string MyList = "My list";

    public static readonly IReadOnlyList<string> Groups = [Paediatrics, MyList];

    /// <summary>The days of history each synthetic person is seeded with.</summary>
    public const int PersonBackfillDays = 30;

    /// <summary>The groups the demo host's own person is in.</summary>
    public static readonly IReadOnlyList<string> HostGroups = [MyList];

    /// <summary>
    /// The synthetic people, one per archetype. Each person's tenant slug is the host's slug, a hyphen and
    /// <see cref="DemoClinicPerson.Key"/>.
    /// </summary>
    public static readonly IReadOnlyList<DemoClinicPerson> People =
    [
        new("john", "John Limitedaccess", LimitTo24Hours: true, [MyList]),
        new("katie", "Katie Staledata", LimitTo24Hours: false, [MyList]),
        new("lena", "Lena Lowlastnight", LimitTo24Hours: false, [Paediatrics, MyList]),
        new("tom", "Tom Belowtarget", LimitTo24Hours: false, [MyList]),
        new("rory", "Rory Risingfast", LimitTo24Hours: false, [Paediatrics]),
        new("paula", "Paula Pump", LimitTo24Hours: false, []),
        new("liam", "Liam Loop", LimitTo24Hours: false, [Paediatrics]),
        new("maddie", "Maddie Injections", LimitTo24Hours: false, [Paediatrics]),
        new("sam", "Sam Steady", LimitTo24Hours: false, [Paediatrics]),
        new("gus", "Gus Grownup", LimitTo24Hours: false, []),
    ];

    public static string SlugOf(string hostSlug, DemoClinicPerson person) => $"{hostSlug}-{person.Key}";
}

/// <summary>One of the demo clinic's synthetic people.</summary>
/// <param name="LimitTo24Hours">Whether the clinic's access to the person's site reads only the last 24 hours.</param>
/// <param name="Groups">The clinic's groups the person is in, of <see cref="DemoClinic.Groups"/>.</param>
public sealed record DemoClinicPerson(string Key, string DisplayName, bool LimitTo24Hours, IReadOnlyList<string> Groups);
