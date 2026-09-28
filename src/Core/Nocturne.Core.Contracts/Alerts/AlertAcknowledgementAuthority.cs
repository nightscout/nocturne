using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Who is acknowledging an excursion, as far as
/// <see cref="IAlertAcknowledgementService.AcknowledgeExcursionAsync"/> needs to decide between
/// acknowledging for everyone and muting for the caller.
/// </summary>
/// <param name="SubjectId">The member behind the credential; null only for <see cref="System"/>.</param>
/// <param name="GrantedScopes">The credential's resolved, membership-intersected scopes.</param>
public sealed record AlertAcknowledgementAuthority(Guid? SubjectId, IReadOnlySet<string> GrantedScopes)
{
    /// <summary>The alert engine itself, which acknowledges for everyone.</summary>
    public static AlertAcknowledgementAuthority System { get; } =
        new(null, new HashSet<string>(StringComparer.Ordinal) { Scope.FullAccess });
}
