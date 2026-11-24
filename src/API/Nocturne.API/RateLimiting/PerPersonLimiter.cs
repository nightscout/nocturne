using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;

namespace Nocturne.API.RateLimiting;

/// <summary>
/// Bounds how often one person does something: <see cref="PermitLimit"/> per <see cref="Window"/>,
/// keyed on the caller's subject, so the people behind one address do not share a bucket and a
/// session that changes address does not get a new one. A request with no subject falls back to
/// the client address (<see cref="ClientRateLimitKey"/>).
/// </summary>
/// <remarks>
/// A filter after authentication rather than a middleware policy: the rate-limiting middleware runs
/// before <c>AuthenticationMiddleware</c>, so it cannot see the subject. Held in process, as
/// <see cref="GuestActivationLimiter"/> is.
/// </remarks>
public abstract class PerPersonLimiter : IDisposable
{
    private readonly ClientRateLimitKey _address;
    private readonly PartitionedRateLimiter<string> _limiter;

    protected PerPersonLimiter(IConfiguration configuration, int permitLimit, TimeSpan window)
    {
        PermitLimit = permitLimit;
        Window = window;
        _address = new ClientRateLimitKey(configuration);
        _limiter = PartitionedRateLimiter.Create<string, string>(
            key => RateLimitPartition.GetFixedWindowLimiter(
                key,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = window, QueueLimit = 0 }));
    }

    public int PermitLimit { get; }

    public TimeSpan Window { get; }

    /// <summary>Takes one use from the caller's allowance.</summary>
    public RateLimitLease Acquire(HttpContext context) => _limiter.AttemptAcquire(
        context.GetSubjectId() is { } subjectId ? $"subject:{subjectId}" : $"address:{_address.Resolve(context)}");

    public void Dispose()
    {
        _limiter.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Applies <typeparamref name="TLimiter"/>, refusing as the rate-limiting middleware does.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LimitPerPersonAttribute<TLimiter> : Attribute, IAsyncActionFilter
    where TLimiter : PerPersonLimiter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        using var lease = context.HttpContext.RequestServices.GetRequiredService<TLimiter>().Acquire(context.HttpContext);

        if (!lease.IsAcquired)
        {
            context.Result = new RateLimitRejectionResult(
                lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null);
            return;
        }

        await next();
    }
}
