using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "the rows that belong to a real operator rather than the demo".
/// </summary>
/// <remarks>
/// First-run setup and the platform-admin grant that follows it decide what to do from counts and
/// orderings over tenants and subjects. The demo contributes one of each, and neither can ever
/// become an operator's: a demo tenant has no owner to adopt it, and a demo subject is one anyone
/// can obtain a session for. Left in, the demo tenant reads as a tenant awaiting its first owner
/// and the demo subject reads as the account that owner is enrolling.
/// </remarks>
public static class DemoExclusionFilter
{
    /// <summary>Drops the demo tenant.</summary>
    public static IQueryable<TenantEntity> ExcludeDemo(this IQueryable<TenantEntity> tenants) =>
        tenants.Where(t => !t.IsDemo);

    /// <summary>Drops the demo visitor.</summary>
    public static IQueryable<SubjectEntity> ExcludeDemo(this IQueryable<SubjectEntity> subjects) =>
        subjects.Where(s => !s.IsDemoSubject);
}
