using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;

namespace Nocturne.API.Middleware;

/// <summary>
/// Tells a verdict on a credential from a failure of the infrastructure that checks it.
/// </summary>
/// <remarks>
/// A database that cannot be read, an identity provider that cannot be reached, or a request
/// cancelled while either was being read says nothing about the credential. Those propagate as a
/// server error: a client that sees 401 may discard a token that is still valid. The cause is
/// looked for through inner exceptions, because a library often wraps the transport fault.
/// </remarks>
internal static class CredentialFailure
{
    /// <summary>
    /// Whether <paramref name="ex"/> is a verdict on the credential, so the request may go on as
    /// unauthenticated.
    /// </summary>
    internal static bool Is(Exception ex)
    {
        for (var cause = ex; cause is not null; cause = cause.InnerException)
        {
            if (cause is DbException
                or RetryLimitExceededException
                or OperationCanceledException
                or HttpRequestException
                or IOException)
            {
                return false;
            }
        }

        return true;
    }
}
