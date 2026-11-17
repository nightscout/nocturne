namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     A <typeparamref name="TTokenProvider"/> over a named client of its own, for credential
///     verification. The sync client's circuit breaker is shared by every tenant, so a member
///     testing credentials must never count towards opening it; this client has no resilience
///     handler, as verification makes one attempt and is rate limited.
/// </summary>
public sealed class CredentialVerificationTokenProvider<TTokenProvider>(TTokenProvider provider)
    where TTokenProvider : class
{
    public static readonly string ClientName = $"{typeof(TTokenProvider).Name}.CredentialVerification";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public TTokenProvider Provider { get; } = provider;
}
