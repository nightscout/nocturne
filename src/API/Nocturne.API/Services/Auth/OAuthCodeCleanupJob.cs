using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Docs;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Removes a tenant's expired OAuth device and authorisation codes, and its OAuth clients nothing
/// uses, to prevent database bloat.
/// </summary>
/// <remarks>
/// The <see cref="RetentionBuffer"/> provides a grace window for in-flight exchanges to complete
/// before their codes are permanently deleted.
/// <para>
/// Every <c>/register</c> call mints a client, and registration is anonymous, so a client that never
/// reached a grant, an authorisation code or a device code within <see cref="UnusedClientRetention"/>
/// is deleted. A client with a grant stays even once every grant is revoked: it names the app in the
/// grant's audit trail. The tenant's Scalar docs client is never deleted.
/// </para>
/// </remarks>
public class OAuthCodeCleanupJob(ILogger<OAuthCodeCleanupJob> logger) : IScheduledJob
{
    internal const string JobName = "oauth-code-cleanup";

    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a code stays after it expires, giving clients a grace window to complete any
    /// in-flight exchange.
    /// </summary>
    internal static readonly TimeSpan RetentionBuffer = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a registered client may go without a grant or a code before it is deleted. Longer
    /// than any device code lives, so a registrant mid-flow keeps its client.
    /// </summary>
    internal static readonly TimeSpan UnusedClientRetention = TimeSpan.FromHours(24);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var tenantId = run.Tenant!.TenantId;
        var now = run.StartedAt.UtcDateTime;

        var codes = await run.Services.GetRequiredService<IOAuthTokenRepository>()
            .DeleteExpiredCodesAsync(tenantId, now - RetentionBuffer, ct);
        var clients = await run.Services.GetRequiredService<IOAuthClientRepository>()
            .DeleteUnreferencedAsync(tenantId, now - UnusedClientRetention, ScalarAuthProvider.ScalarSoftwareId, ct);

        if (codes.DeviceCodes > 0 || codes.AuthorizationCodes > 0 || clients > 0)
        {
            logger.LogInformation(
                "OAuth code cleanup for tenant {TenantId}: removed {DeviceCodes} device codes, "
                    + "{AuthCodes} authorization codes and {Clients} unused clients",
                tenantId, codes.DeviceCodes, codes.AuthorizationCodes, clients);
        }

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }
}

/// <summary>
/// Removes expired TOTP step-up tokens. <c>totp_step_up_tokens</c> is not tenant-scoped, so this is
/// one platform job rather than part of <see cref="OAuthCodeCleanupJob"/>.
/// </summary>
public class OAuthStepUpTokenCleanupJob(ILogger<OAuthStepUpTokenCleanupJob> logger) : IScheduledJob
{
    internal const string JobName = "oauth-step-up-token-cleanup";

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.Platform;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var cutoff = run.StartedAt.UtcDateTime - OAuthCodeCleanupJob.RetentionBuffer;

        int deleted;
        await using (var platform = await run.Services.GetRequiredService<INocturneDbContextFactory>().CreatePlatformAsync(ct))
        {
            deleted = await platform.TotpStepUpTokens.Where(t => t.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
        }

        if (deleted > 0)
            logger.LogInformation("Removed {StepUpTokens} expired TOTP step-up tokens", deleted);

        return JobOutcome.Resume(null, run.StartedAt + OAuthCodeCleanupJob.Interval);
    }
}
