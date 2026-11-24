using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     The one rule every retry handler follows: a request is sent again only when its method is
///     safe or its builder marked it repeatable. A write that was sent and answered with a failure
///     may still have landed, so a second send can store it twice.
/// </summary>
/// <remarks>
///     Mark a request only when sending it twice changes nothing: a sign-in, a token refresh, or a
///     query sent as POST. A request that creates, changes or deletes data is never marked.
/// </remarks>
public static class RepeatableRequest
{
    private static readonly HttpRequestOptionsKey<bool> RepeatableKey = new("Nocturne.Repeatable");

    /// <summary>
    ///     Marks <paramref name="request"/> as safe to send again.
    /// </summary>
    public static HttpRequestMessage MarkRepeatable(this HttpRequestMessage request)
    {
        request.Options.Set(RepeatableKey, true);
        return request;
    }

    /// <summary>
    ///     Whether a retry handler may send <paramref name="request"/> again.
    /// </summary>
    public static bool IsRepeatable(HttpRequestMessage? request) =>
        request is not null
        && (IsSafeMethod(request.Method)
            || (request.Options.TryGetValue(RepeatableKey, out var marked) && marked));

    /// <summary>
    ///     The retry strategy's <c>ShouldHandle</c>: a transient outcome of a repeatable request.
    /// </summary>
    public static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args) =>
        ValueTask.FromResult(
            IsRepeatable(args.Context.GetRequestMessage())
                && HttpClientResiliencePredicates.IsTransient(args.Outcome)
        );

    private static bool IsSafeMethod(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options;
}
