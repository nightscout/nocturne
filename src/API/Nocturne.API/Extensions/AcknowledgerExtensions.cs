using System.Security.Claims;

namespace Nocturne.API.Extensions;

/// <summary>
/// Who an alert acknowledgement is recorded against.
/// </summary>
public static class AcknowledgerExtensions
{
    /// <summary>
    /// The name to record as having acknowledged an alert: the authenticated principal's name,
    /// else its subject id, else <paramref name="requested"/>.
    /// </summary>
    /// <remarks>
    /// Taken from the principal rather than the caller, so "who silenced the 3am low?" has a
    /// trustworthy answer on a tenant with more than one caregiver. The requested name is honoured
    /// only for a caller with neither claim (a machine token), where it is the only information
    /// available.
    /// </remarks>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="requested">The name the caller supplied, if any.</param>
    public static string ResolveAcknowledger(this ClaimsPrincipal user, string? requested)
    {
        var name = user.FindFirstValue(ClaimTypes.Name);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var subjectId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(subjectId))
            return subjectId;

        return string.IsNullOrWhiteSpace(requested) ? "unknown" : requested;
    }
}
