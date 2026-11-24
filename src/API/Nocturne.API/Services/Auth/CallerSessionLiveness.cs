using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Whether the request's caller still holds the session it signed in with. A credential is minted
/// only for a caller whose session is live, so the refusals in
/// <see cref="Extensions.HttpContextExtensions"/> ask this before an action mints.
/// </summary>
/// <remarks>
/// A session access token is checked for its signature and lifetime only, so it outlives the end of
/// its session (the sessions list, sign-out, reuse detection, a subject-wide revoke) by up to its
/// lifetime. Reads go on for that time; a mint does not. Only a session credential
/// (<see cref="AuthType.SessionCookie"/>, <see cref="AuthType.LegacyJwt"/>) is judged here: every
/// other credential is proved live by its own record when it authenticates. A session token that
/// names no session (<c>sid</c>) is not live. Read on the first ask and kept, live or not, for the
/// rest of the request; nothing reads it on a request that mints nothing.
/// </remarks>
public sealed class CallerSessionLiveness
{
    private readonly IFirstPartyTokenRepository _sessions;
    private readonly TimeProvider _time;
    private (Guid SubjectId, string SessionId, bool Live)? _read;

    /// <summary>Creates the request's liveness reader.</summary>
    public CallerSessionLiveness(IFirstPartyTokenRepository sessions, TimeProvider time)
    {
        _sessions = sessions;
        _time = time;
    }

    /// <summary>Whether <paramref name="caller"/> signed in with a session, the only credential judged here.</summary>
    public static bool IsSessionCaller(AuthContext caller) =>
        caller is { AuthType: AuthType.SessionCookie or AuthType.LegacyJwt, SubjectId: not null };

    /// <summary>Whether <paramref name="caller"/> is a session caller whose session has ended.</summary>
    public async Task<bool> HasEndedAsync(AuthContext caller, CancellationToken ct)
    {
        if (!IsSessionCaller(caller) || caller.SubjectId is not { } subjectId)
            return false;

        if (string.IsNullOrEmpty(caller.SessionId))
            return true;

        if (_read is not { } read || read.SubjectId != subjectId || read.SessionId != caller.SessionId)
        {
            var live = await _sessions.HasLiveSessionAsync(
                subjectId, caller.SessionId, _time.GetUtcNow().UtcDateTime, ct);
            read = (subjectId, caller.SessionId, live);
            _read = read;
        }

        return !read.Live;
    }
}
