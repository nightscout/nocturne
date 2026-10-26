using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Acts on Sign in with Apple server-to-server notifications: a user who stops using Apple with
/// this instance, deletes their Apple Account, or turns relay email off or on.
/// </summary>
/// <remarks>
/// Each notification is a JWS Apple signs with the keys it signs ID tokens with. One that fails
/// the signature, issuer or audience check is refused; one about an identity this instance does not
/// hold is accepted and ignored, since Apple sends every event for the app.
/// <list type="bullet">
///   <item><description><c>consent-revoked</c>: the kept refresh token is dropped, being dead now,
///   and the subject's sessions end. The identity stays, so signing in with Apple again reaches the
///   same account.</description></item>
///   <item><description><c>account-deleted</c>: the identity is removed, since that Apple Account
///   can never sign in again, and the subject's sessions end.</description></item>
///   <item><description><c>email-enabled</c>: the identity's email is updated.
///   <c>email-disabled</c> is only logged.</description></item>
/// </list>
/// </remarks>
public class AppleNotificationService(
    IOidcProviderService providerService,
    IOidcConfigurationSource configurations,
    INocturneDbContextFactory contexts,
    IFirstPartyTokenRepository sessions,
    IAuthAuditService auditService,
    ILogger<AppleNotificationService> logger)
{
    internal const string ConsentRevoked = "consent-revoked";
    internal const string AccountDeleted = "account-deleted";

    /// <summary>The name older Apple material gives <see cref="AccountDeleted"/>.</summary>
    internal const string AccountDelete = "account-delete";
    internal const string EmailEnabled = "email-enabled";
    internal const string EmailDisabled = "email-disabled";

    private static readonly TimeSpan ExpiryClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Verifies and acts on one notification payload.</summary>
    /// <returns>Whether the payload was a notification Apple signed for this instance's provider.</returns>
    public async Task<bool> HandleAsync(string payload, CancellationToken ct = default)
    {
        // The issuer is unique among providers, so there is at most one Apple provider.
        var provider = (await providerService.GetEnabledProvidersAsync())
            .FirstOrDefault(p => p.ProviderType == OidcProviderType.Apple);
        if (provider is null)
        {
            logger.LogWarning("Refused an Apple notification: no Apple provider is enabled");
            return false;
        }

        if (await ValidateAsync(payload, provider, ct) is not { } notification)
            return false;

        await ApplyAsync(provider, notification, ct);
        return true;
    }

    private async Task<AppleNotification?> ValidateAsync(string payload, OidcProvider provider, CancellationToken ct)
    {
        var result = await ValidateSignedAsync(payload, provider, ct);

        // Apple rotates its keys: one it signs with may be newer than the cached set. The manager
        // rate-limits refreshes, so a forged payload cannot make it fetch on every call.
        if (result.Exception is SecurityTokenInvalidSignatureException)
        {
            configurations.RequestRefresh(provider.IssuerUrl);
            result = await ValidateSignedAsync(payload, provider, ct);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
        {
            logger.LogWarning(result.Exception, "Refused an Apple notification that failed validation");
            return null;
        }

        // Apple's documentation does not promise an exp, so one is checked only when present.
        if (token.ValidTo != DateTime.MinValue && token.ValidTo.Add(ExpiryClockSkew) < DateTime.UtcNow)
        {
            logger.LogWarning("Refused an expired Apple notification");
            return null;
        }

        var notification = ReadEvents(token.EncodedPayload);
        if (notification is null)
            logger.LogWarning("Refused an Apple notification with no readable event");

        return notification;
    }

    private async Task<TokenValidationResult> ValidateSignedAsync(string payload, OidcProvider provider, CancellationToken ct)
    {
        var configuration = await configurations.GetAsync(provider.IssuerUrl, ct);
        var audiences = new[] { provider.ClientId, provider.Apple?.PrimaryAppId }
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!);

        return await new JsonWebTokenHandler().ValidateTokenAsync(payload, new TokenValidationParameters
        {
            ValidIssuer = string.IsNullOrEmpty(configuration.Issuer) ? provider.IssuerUrl : configuration.Issuer,
            ValidAudiences = audiences,
            IssuerSigningKeys = configuration.SigningKeys,
            // Checked after the signature, by ValidateAsync: with no exp, the handler reports a
            // signature failure as expiry, which would hide a rotated key.
            ValidateLifetime = false,
            RequireExpirationTime = false,
        });
    }

    /// <summary>
    /// The event in a notification's payload. Apple sends <c>events</c> as a JSON string holding
    /// the object; an object is read as well.
    /// </summary>
    internal static AppleNotification? ReadEvents(string encodedPayload)
    {
        try
        {
            using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(encodedPayload)));
            if (!payload.RootElement.TryGetProperty("events", out var events))
                return null;

            using var parsed = events.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(events.GetString()!)
                : JsonDocument.Parse(events.GetRawText());
            var root = parsed.RootElement;

            var type = ReadString(root, "type");
            var sub = ReadString(root, "sub");
            return string.IsNullOrEmpty(type) || string.IsNullOrEmpty(sub)
                ? null
                : new AppleNotification(type, sub, ReadString(root, "email"));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task ApplyAsync(OidcProvider provider, AppleNotification notification, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var identity = await db.SubjectOidcIdentities.FirstOrDefaultAsync(
            i => i.OidcSubjectId == notification.Sub && i.Issuer == provider.IssuerUrl, ct);

        if (identity is null)
        {
            logger.LogInformation("Ignored an Apple {Event} notification for an identity this instance does not hold",
                notification.Type);
            return;
        }

        switch (notification.Type)
        {
            case ConsentRevoked:
                identity.ProviderRefreshTokenEncrypted = null;
                await db.SaveChangesAsync(ct);
                await EndSessionsAsync(identity, notification.Type, AuthAuditEventType.TokenRevoked, ct);
                break;

            case AccountDeleted:
            case AccountDelete:
                db.SubjectOidcIdentities.Remove(identity);
                await db.SaveChangesAsync(ct);
                await EndSessionsAsync(identity, notification.Type, AuthAuditEventType.OidcIdentityUnlinked, ct);
                break;

            case EmailEnabled when !string.IsNullOrEmpty(notification.Email):
                identity.Email = notification.Email;
                await db.SaveChangesAsync(ct);
                break;

            default:
                logger.LogInformation("Apple {Event} notification for subject {SubjectId} needs no action",
                    notification.Type, identity.SubjectId);
                break;
        }
    }

    private async Task EndSessionsAsync(
        SubjectOidcIdentityEntity identity, string appleEvent, string auditEvent, CancellationToken ct)
    {
        var revoked = await sessions.RevokeAllForSubjectAsync(identity.SubjectId, $"Apple {appleEvent}", ct);

        await auditService.LogAsync(auditEvent, identity.SubjectId, success: true,
            detailsJson: JsonSerializer.Serialize(new { method = "apple_notification", appleEvent, identityId = identity.Id }));

        logger.LogInformation(
            "Apple {Event} notification for subject {SubjectId}: ended {Count} sessions",
            appleEvent, identity.SubjectId, revoked);
    }
}

/// <summary>One Sign in with Apple server-to-server event.</summary>
/// <param name="Type">The event type, e.g. <c>consent-revoked</c>.</param>
/// <param name="Sub">Apple's stable user identifier, the ID token's <c>sub</c>.</param>
/// <param name="Email">The relay address, on the email events.</param>
public sealed record AppleNotification(string Type, string Sub, string? Email);
