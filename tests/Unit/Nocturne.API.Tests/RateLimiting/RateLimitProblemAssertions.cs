using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Nocturne.API.Tests.RateLimiting;

internal static class RateLimitProblemAssertions
{
    /// <summary>Asserts the response is the problem document every limiter refusal answers with.</summary>
    public static async Task ShouldBeTheRateLimitProblemAsync(this HttpResponse response)
    {
        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        AssertProblem(response.StatusCode, response.ContentType, json);
    }

    /// <inheritdoc cref="ShouldBeTheRateLimitProblemAsync(HttpResponse)"/>
    public static async Task ShouldBeTheRateLimitProblemAsync(this HttpResponseMessage response) =>
        AssertProblem(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            await response.Content.ReadAsStringAsync());

    private static void AssertProblem(int status, string? contentType, string json)
    {
        status.Should().Be(StatusCodes.Status429TooManyRequests);
        contentType.Should().StartWith("application/problem+json");

        using var body = JsonDocument.Parse(json);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status429TooManyRequests);
        root.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("code").GetString().Should().Be("rate_limit_exceeded");
        root.GetProperty("error").GetString().Should().Be("rate_limit_exceeded");
        root.GetProperty("error_description").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
