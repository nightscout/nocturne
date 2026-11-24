using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Identity;

/// <summary>How a response names the people it mentions.</summary>
internal static class PeopleNames
{
    /// <summary>
    /// The name of each of <paramref name="subjectIds"/>: a member by their label on
    /// <paramref name="tenantId"/>, and one who has since been removed by their own name. A subject
    /// that is neither is left out.
    /// </summary>
    public static async Task<Dictionary<Guid, string?>> OfAsync(
        ITenantMembershipRepository members,
        ISubjectRepository subjects,
        Guid tenantId,
        IReadOnlyCollection<Guid> subjectIds,
        CancellationToken ct)
    {
        var names = new Dictionary<Guid, string?>();
        if (subjectIds.Count == 0)
            return names;

        foreach (var person in await members.ListPeopleAsync(tenantId, ct))
            names.TryAdd(person.SubjectId, person.Label ?? person.SubjectName);
        var removed = subjectIds.Where(id => !names.ContainsKey(id)).ToList();
        if (removed.Count > 0)
        {
            foreach (var subject in await subjects.GetManyAsync(removed, ct))
                names.TryAdd(subject.Id, subject.Name);
        }

        return names;
    }
}
