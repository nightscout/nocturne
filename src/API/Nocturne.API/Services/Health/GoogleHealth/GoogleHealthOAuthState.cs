using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Nocturne.API.Services.Health.GoogleHealth;

/// <summary>
/// The Google Health OAuth <c>state</c>: a protected, expiring payload naming the tenant host
/// the sign-in started on, so the apex callback can be relayed back to it
/// (<see cref="Middleware.ApexCallbackRelayMiddleware"/>). The slug only routes the browser;
/// the flow is bound to its tenant by the stored flow record that
/// <see cref="GoogleHealthService.CompleteAsync"/> compares the state against.
/// </summary>
public sealed class GoogleHealthOAuthState(IDataProtectionProvider dataProtection)
{
    private readonly ITimeLimitedDataProtector protector = dataProtection
        .CreateProtector("Nocturne.GoogleHealth.OAuthState")
        .ToTimeLimitedDataProtector();

    /// <param name="tenantSlug">The tenant subdomain the browser is on, or null on the apex.</param>
    /// <param name="expires">When the state stops being readable.</param>
    public string Protect(string? tenantSlug, DateTimeOffset expires) =>
        protector.Protect(JsonSerializer.Serialize(new Payload(tenantSlug)), expires);

    /// <summary>The slug carried by an unexpired state this instance issued, else null.</summary>
    public string? TryReadTenantSlug(string? state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        try
        {
            return JsonSerializer.Deserialize<Payload>(protector.Unprotect(state))?.Slug;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    private sealed record Payload(string? Slug);
}
