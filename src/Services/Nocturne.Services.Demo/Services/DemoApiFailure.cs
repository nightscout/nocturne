namespace Nocturne.Services.Demo.Services;

internal static class DemoApiFailure
{
    /// <summary>
    /// Whether <paramref name="ex"/> says an API call failed: the API refused or could not be reached, or the call
    /// timed out, which <c>HttpClient</c> reports as an <see cref="OperationCanceledException"/> that
    /// <paramref name="cancellationToken"/> did not cause.
    /// </summary>
    public static bool Is(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}
