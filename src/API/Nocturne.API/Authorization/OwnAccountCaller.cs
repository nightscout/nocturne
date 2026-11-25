using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>A person asking about their own account.</summary>
public static class OwnAccountCaller
{
    /// <summary>
    /// The caller's subject, or null for a credential that is not the account itself: a
    /// platform-access operator, an API key or the instance key.
    /// </summary>
    public static Guid? SubjectId(HttpContext httpContext) =>
        httpContext.GetAuthContext() is
        {
            SubjectId: { } subjectId,
            AuthType: not (AuthType.PlatformAccess or AuthType.ApiKey or AuthType.InstanceKey),
        }
            ? subjectId
            : null;
}
