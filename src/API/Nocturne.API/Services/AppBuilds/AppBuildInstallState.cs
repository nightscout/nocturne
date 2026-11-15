using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace Nocturne.API.Services.AppBuilds;

/// <summary>
/// The <c>state</c> GitHub carries from the App installation page back to the callback. It is a
/// protected, expiring, single-use payload. It binds the flow to the tenant, subject and build target
/// that started it. The <c>installation_id</c> beside it in the callback is not trusted on its own; see
/// <see cref="AppBuildService.CompleteLinkAsync"/>.
/// </summary>
/// <remarks>
/// Single use is enforced per instance, in memory. A replay on another instance still needs a fresh
/// OAuth code and the same signed-in subject on the same tenant, so it gains nothing.
/// </remarks>
public sealed class AppBuildInstallState(IDataProtectionProvider dataProtection, IMemoryCache consumed, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ITimeLimitedDataProtector protector = dataProtection
        .CreateProtector("Nocturne.AppBuilds.GitHubInstallState")
        .ToTimeLimitedDataProtector();

    /// <summary>The slug is the tenant subdomain, so an apex callback is relayed to it; null on a tenantless host.</summary>
    public string Protect(Guid tenantId, string? tenantSlug, Guid subjectId, string targetId)
    {
        var expires = time.GetUtcNow() + Lifetime;
        var payload = new Payload(tenantId, tenantSlug, subjectId, targetId,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), expires);
        return protector.Protect(JsonSerializer.Serialize(payload), expires);
    }

    /// <summary>The payload of an unexpired state this instance issued, else null. Does not consume it.</summary>
    public Payload? TryRead(string? state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(state));
            return payload is not null && payload.Expires > time.GetUtcNow() ? payload : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    public string? TryReadTenantSlug(string? state) => TryRead(state)?.Slug;

    /// <returns>False when the state was already used.</returns>
    public bool TryConsume(Payload payload)
    {
        var key = $"app-builds:state:{payload.Nonce}";
        lock (consumed)
        {
            if (consumed.TryGetValue(key, out _)) return false;
            consumed.Set(key, true, payload.Expires);
            return true;
        }
    }

    public sealed record Payload(Guid Tenant, string? Slug, Guid Subject, string Target, string Nonce, DateTimeOffset Expires);
}
