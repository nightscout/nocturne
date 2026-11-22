using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;

namespace Nocturne.API.RateLimiting;

/// <summary>
/// Bounds how many teams one person creates: <see cref="PermitLimit"/> per <see cref="Window"/>,
/// keyed on the caller's subject, so the people behind one address do not share a bucket and a
/// session that changes address does not get a new one. A request with no subject falls back to
/// the client address (<see cref="ClientRateLimitKey"/>).
/// </summary>
/// <remarks>
/// A filter after authentication rather than a middleware policy: the rate-limiting middleware runs
/// before <c>AuthenticationMiddleware</c>, so it cannot see the subject. Held in process, as
/// <see cref="GuestActivationLimiter"/> is.
/// </remarks>
public sealed class TeamCreationLimiter(IConfiguration configuration) : IDisposable
{
    public const int PermitLimit = 10;

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ClientRateLimitKey _address = new(configuration);

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(
        key => RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window, QueueLimit = 0 }));

    /// <summary>Takes one team creation from the caller's allowance.</summary>
    public RateLimitLease Acquire(HttpContext context) => _limiter.AttemptAcquire(
        context.GetSubjectId() is { } subjectId ? $"subject:{subjectId}" : $"address:{_address.Resolve(context)}");

    public void Dispose() => _limiter.Dispose();
}

/// <summary>Applies <see cref="TeamCreationLimiter"/>, refusing as the rate-limiting middleware does.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LimitTeamCreationPerPersonAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        using var lease = context.HttpContext.RequestServices.GetRequiredService<TeamCreationLimiter>()
            .Acquire(context.HttpContext);

        if (!lease.IsAcquired)
        {
            context.Result = new RateLimitRejectionResult(
                lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null);
            return;
        }

        await next();
    }
}
