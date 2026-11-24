using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>How an attempt to send an account link ended.</summary>
public enum AccountLinkSend
{
    /// <summary>The link was handed to the delivery route.</summary>
    Sent,

    /// <summary>No link can be minted and sent (<see cref="IAccountLinkRoute.IsConfigured"/>).</summary>
    NotConfigured,

    /// <summary>The account holds no address.</summary>
    NoAddress,

    /// <summary>The account's address is already confirmed.</summary>
    AlreadyConfirmed,

    /// <summary>The account was sent a link too recently (<see cref="ILoginCodeService.IssueEmailedAsync"/>).</summary>
    Throttled,

    /// <summary>The delivery route refused the link or could not be reached.</summary>
    DeliveryFailed,
}

/// <summary>
/// Mints the links Nocturne emails to an account's address, each a code bound to that address
/// (<see cref="LoginCodeService.EmailPurposes"/>) on the site's own host, and hands them to
/// <see cref="IAccountLinkDelivery"/>. Runs on a scope whose tenant is the site the link is for.
/// </summary>
public sealed class AccountLinkService(
    INocturneDbContextFactory contexts,
    ILoginCodeService loginCodes,
    ISubjectRepository subjects,
    IAccountLinkDelivery delivery,
    IOptions<BaseDomainOptions> baseDomain,
    TimeProvider time)
{
    /// <summary>The web page that redeems a recovery link.</summary>
    public const string RecoveryPath = "/auth/recovery";

    /// <summary>The web page that redeems a confirmation link.</summary>
    public const string ConfirmationPath = "/auth/confirm-email";

    /// <summary>
    /// Sends a recovery link to each active member of <paramref name="tenant"/> whose confirmed
    /// address is <paramref name="email"/>, one delivery per account. An unconfirmed address, an
    /// unknown one and a throttled account get nothing; the requester is never told which.
    /// </summary>
    public async Task SendRecoveryLinksAsync(
        TenantContext tenant, string email, AuthAuditActor actor, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        if (!delivery.IsConfigured)
            return;

        foreach (var member in await subjects.FindMembersByConfirmedEmailAsync(tenant.TenantId, email, time.GetUtcNow().UtcDateTime, ct))
            await SendAsync(tenant, member, member.Email!, LoginCodePurpose.EmailRecovery, actor, ipAddress, userAgent, ct);
    }

    /// <summary>
    /// Sends a confirmation link for the address <paramref name="subjectId"/> holds now, bound to
    /// that address, so it confirms nothing once the account moves to another.
    /// </summary>
    public async Task<AccountLinkSend> SendEmailConfirmationAsync(
        TenantContext tenant, Guid subjectId, AuthAuditActor actor, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        if (!delivery.IsConfigured)
            return AccountLinkSend.NotConfigured;

        var subject = await subjects.GetAsync(subjectId, ct);
        if (subject?.Email is not { } email || string.IsNullOrWhiteSpace(email))
            return AccountLinkSend.NoAddress;
        if (subject.EmailConfirmedAt is not null)
            return AccountLinkSend.AlreadyConfirmed;

        return await SendAsync(tenant, subject, email, LoginCodePurpose.EmailConfirm, actor, ipAddress, userAgent, ct);
    }

    private async Task<AccountLinkSend> SendAsync(
        TenantContext tenant,
        SubjectRecord subject,
        string email,
        LoginCodePurpose purpose,
        AuthAuditActor actor,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        // Reached only past IAccountLinkRoute.IsConfigured, which requires the base domain.
        var origin = baseDomain.Value.TenantOrigin(tenant.Slug)!;

        await using var db = await contexts.CreateAsync(ct);
        if (await loginCodes.IssueEmailedAsync(db, subject.Id, purpose, email, ipAddress, userAgent, actor, ct)
            is not { } issued)
            return AccountLinkSend.Throttled;

        var (kind, path) = purpose == LoginCodePurpose.EmailRecovery
            ? (AccountLinkKind.Recovery, RecoveryPath)
            : (AccountLinkKind.EmailConfirmation, ConfirmationPath);

        var delivered = await delivery.DeliverAsync(
            new AccountLinkMessage(
                kind,
                email,
                $"{origin}{path}?code={Uri.EscapeDataString(issued.Code)}",
                issued.ExpiresAt,
                tenant.Slug,
                tenant.DisplayName,
                subject.Name),
            ct);
        return delivered ? AccountLinkSend.Sent : AccountLinkSend.DeliveryFailed;
    }
}

/// <summary>
/// Runs a recovery-link request after its response is sent. Whether an address names an account
/// must not show in the answer, and the work only some addresses cause (the code, the delivery)
/// would show in its timing, so the request path does none of it.
/// </summary>
public class RecoveryLinkRequests(IServiceProvider services, ILogger<RecoveryLinkRequests> logger)
{
    private const string AuditEndpoint = "service:recovery-link";

    /// <summary>
    /// Starts <see cref="AccountLinkService.SendRecoveryLinksAsync"/> for <paramref name="tenant"/>
    /// and returns without waiting for it. The returned task completes when the work does and never
    /// faults; only a test awaits it.
    /// </summary>
    public virtual Task RequestAsync(
        TenantContext tenant, string email, AuthAuditActor actor, string? ipAddress, string? userAgent) =>
        Task.Run(async () =>
        {
            try
            {
                // Outlives the request, so it reads nothing of the request's scope.
                await using var scope = TenantJobScope.Open(services, tenant, AuditEndpoint);
                await scope.ServiceProvider.GetRequiredService<AccountLinkService>()
                    .SendRecoveryLinksAsync(tenant, email, actor, ipAddress, userAgent, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A recovery-link request on tenant {TenantId} failed", tenant.TenantId);
            }
        });
}

/// <summary>The <c>code</c> extension of the problems the account-link endpoints answer.</summary>
public static class AccountLinkProblemCodes
{
    /// <summary>No delivery route is configured, so the flow does not exist.</summary>
    public const string Unavailable = "email_links_unavailable";

    /// <summary>The account holds no address.</summary>
    public const string NoAddress = "email_missing";

    /// <summary>The account's address is already confirmed.</summary>
    public const string AlreadyConfirmed = "email_already_confirmed";

    /// <summary>A link was sent to the account too recently.</summary>
    public const string Throttled = "email_link_throttled";

    /// <summary>The delivery route refused the link or could not be reached.</summary>
    public const string DeliveryFailed = "email_delivery_failed";

    /// <summary>The link is unknown, spent, expired, for another site, or no longer applies.</summary>
    public const string InvalidLink = "email_link_invalid";
}
