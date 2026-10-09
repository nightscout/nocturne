namespace Nocturne.Connectors.GoogleHealth.Services;

internal static class GoogleHealthHttpError
{
    public static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta ??
        response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow;

    public static string? SafeProviderReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) &&
        reason.Length <= 100 &&
        reason.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or >= 'a' and <= 'z')
            ? reason
            : null;
}
