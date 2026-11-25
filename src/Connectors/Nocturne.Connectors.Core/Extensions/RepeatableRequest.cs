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
///     query sent as POST. A request that creates, changes or deletes data is never marked. A
///     repeatable request that carries no attempt budget gets the handler's own ceiling.
/// </remarks>
public static class RepeatableRequest
{
    private static readonly HttpRequestOptionsKey<bool> RepeatableKey = new("Nocturne.Repeatable");
    private static readonly HttpRequestOptionsKey<int> AttemptBudgetKey = new("Nocturne.AttemptBudget");
    private static readonly HttpRequestOptionsKey<bool> PassedRetryHandlerKey = new("Nocturne.PassedRetryHandler");

    /// <summary>
    ///     Marks <paramref name="request"/> as safe to send again, at most <paramref name="maxAttempts"/>
    ///     sends in all. The connector retry handler's own ceiling still applies.
    /// </summary>
    public static HttpRequestMessage MarkRepeatable(this HttpRequestMessage request, int maxAttempts)
    {
        request.Options.Set(RepeatableKey, true);
        request.Options.Set(AttemptBudgetKey, Math.Max(1, maxAttempts));
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
    ///     Whether <paramref name="request"/> went through a connector retry handler, which is then
    ///     the one that sends it again.
    /// </summary>
    internal static bool PassedRetryHandler(HttpRequestMessage request) =>
        request.Options.TryGetValue(PassedRetryHandlerKey, out var passed) && passed;

    /// <summary>
    ///     The retry strategy's <c>ShouldHandle</c>: a transient outcome of a repeatable request
    ///     that has sends left in its budget.
    /// </summary>
    public static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args)
    {
        var request = args.Context.GetRequestMessage();

        return ValueTask.FromResult(
            IsRepeatable(request)
                && HasSendsLeft(request!, args.AttemptNumber)
                && HttpClientResiliencePredicates.IsTransient(args.Outcome)
        );
    }

    /// <summary>
    ///     Records on each request that it went through the retry handler. Sits just ahead of the
    ///     handler, so the record is made whether or not the handler goes on to send it again.
    /// </summary>
    internal sealed class PassageRecorder : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Options.Set(PassedRetryHandlerKey, true);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static bool HasSendsLeft(HttpRequestMessage request, int attemptNumber) =>
        !request.Options.TryGetValue(AttemptBudgetKey, out var budget) || attemptNumber + 1 < budget;

    private static bool IsSafeMethod(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options;
}
