namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     The sign-in one sync run holds: the token the run's gate obtained, and the one way to replace
///     it when the source refuses it. A sync reaches no other token.
/// </summary>
public sealed class TokenSession
{
    private readonly Func<Task<string?>>? _signInAgain;
    private string? _token;

    internal TokenSession(string? token, Func<Task<string?>>? signInAgain)
    {
        _token = token;
        _signInAgain = signInAgain;
    }

    /// <summary>
    ///     The run's current token.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     <see cref="TokenConnectorService{TConfig,TTokenProvider}.NeedsSignIn"/> said the run needs none,
    ///     so none was obtained.
    /// </exception>
    public string Token => _token
        ?? throw new InvalidOperationException("No sign-in ran for this sync, so it holds no token.");

    /// <summary>
    ///     The <c>reAuthenticateOnUnauthorized</c> answer to
    ///     <see cref="BaseConnectorService{TConfig}.ExecuteWithRetryAsync{T}"/>: drops the token the
    ///     source refused and signs in again. A failed sign-in leaves <see cref="Token"/> as it was and
    ///     records its reason for <see cref="BaseConnectorService{TConfig}.AuthenticationFailedResult"/>.
    /// </summary>
    public async Task<bool> ReauthenticateAsync()
    {
        if (_signInAgain is null)
            throw new InvalidOperationException("No sign-in ran for this sync, so it cannot sign in again.");

        var token = await _signInAgain();
        if (string.IsNullOrEmpty(token)) return false;
        _token = token;
        return true;
    }
}
