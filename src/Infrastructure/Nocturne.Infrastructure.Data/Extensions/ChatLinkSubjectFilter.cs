using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Narrows chat identity links to those whose subject is active. A link is the key a bot or an alert
/// goes through, so one held by a deactivated subject neither resolves, acknowledges nor receives, and
/// resumes on reactivation because the row stays.
/// </summary>
public static class ChatLinkSubjectFilter
{
    /// <summary>The links in <paramref name="links"/> whose subject is active, read on <paramref name="db"/>.</summary>
    public static IQueryable<ChatIdentityDirectoryEntry> WithActiveSubject(
        this IQueryable<ChatIdentityDirectoryEntry> links, NocturneDbContext db) =>
        links.Where(d => db.Subjects.Any(s => s.Id == d.NocturneUserId && s.IsActive));
}
