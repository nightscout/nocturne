using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "the rows that belong to a real operator rather than the demo".
/// </summary>
/// <remarks>
/// First-run setup, the platform-admin grant that follows it, and apex tenant resolution decide
/// what to do from counts and orderings over tenants and subjects. The demo contributes its host,
/// the clinic's people and the subjects visitors are signed in as, and none of them can ever become
/// an operator's: a demo tenant has no owner to adopt it, and a demo subject is one anyone can obtain
/// a session for. Left in, a demo tenant reads as a tenant awaiting its first owner, a demo subject
/// reads as the account that owner is enrolling, and the operator's own single-tenant install stops
/// being single-tenant the moment a demo appears.
/// <para>
/// A demo-only instance therefore resolves no tenant on its apex: the demo is reached on its own
/// host, never adopted by the front door.
/// </para>
/// </remarks>
public static class DemoExclusionFilter
{
    /// <summary>Drops every demo tenant.</summary>
    public static IQueryable<TenantEntity> ExcludeDemo(this IQueryable<TenantEntity> tenants) =>
        tenants.Where(t => !t.IsDemo);

    /// <summary>Drops every demo subject.</summary>
    public static IQueryable<SubjectEntity> ExcludeDemo(this IQueryable<SubjectEntity> subjects) =>
        subjects.Where(s => !s.IsDemoSubject);
}
