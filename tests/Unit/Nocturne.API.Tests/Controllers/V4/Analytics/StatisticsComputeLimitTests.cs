using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

/// <summary>
/// Guards the bounds on the statistics actions that compute over a caller-supplied body.
/// </summary>
/// <remarks>
/// These actions are gated on <c>reports.read</c>, which every public share link holds, so an
/// anonymous viewer can reach them; nothing they compute is stored, so what a caller spends is
/// the API's CPU and the bytes it reads. The sweep pins the rule rather than today's action list,
/// so a new compute POST that ships without the policy fails here.
/// </remarks>
public class StatisticsComputeLimitTests
{
    [Fact]
    public void EveryStatisticsComputePost_CarriesTheComputeRateLimitPolicy()
    {
        var actions = ComputePosts();

        // A sweep that discovers nothing would pass while guarding nothing.
        actions.Should().HaveCountGreaterThan(10,
            "the scan should discover the statistics actions that take a body");

        var uncovered = actions
            .Where(a => a.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName
                        != ServiceRegistrationExtensions.StatisticsComputeRateLimitPolicy)
            .Select(a => a.Name)
            .ToList();

        uncovered.Should().BeEmpty(
            "an action computing over a caller-supplied body must be rate limited, or a share link "
            + "holder can spend the API's CPU without bound. Uncovered: " + string.Join(", ", uncovered));
    }

    [Fact]
    public void TheComputePartitionKey_IsTheSameForEitherCasingOfAHost()
    {
        const string host = "AbC123.share.example.com";

        var upper = ServiceRegistrationExtensions.StatisticsComputePartitionKey(
            ContextFor(host));
        var lower = ServiceRegistrationExtensions.StatisticsComputePartitionKey(
            ContextFor(host.ToLowerInvariant()));

        upper.Should().Be(lower,
            "a host is case-insensitive, so a caller writing its own Host header would otherwise "
            + "get a fresh window per casing of the same tenant");
    }

    private static HttpContext ContextFor(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        return context;
    }

    /// <summary>
    /// The declared body limit has to cover the largest range a report can ask for: the web client
    /// pages every reading in the range into one <c>site-change-impact</c> post, so a year of
    /// 5-minute readings from one CGM arrives in a single body.
    /// </summary>
    [Fact]
    public void TheDeclaredBodyLimit_CoversAYearOfReadings()
    {
        const int readingsPerYear = 365 * 24 * 12;

        ComputePosts()
            .Count(a => a.GetCustomAttribute<RequestSizeLimitAttribute>() is not null)
            .Should().BeGreaterThan(0, "the collection-taking posts declare a body ceiling");

        (readingsPerYear * (long)SerializedReadingBytes()).Should()
            .BeLessThan(StatisticsController.ComputeBodyLimitBytes,
            "a year-long report legitimately posts every reading in the range, so a lower bound "
            + "would fail the report rather than an abusive caller");
    }

    /// <summary>
    /// One reading as the client posts it back: every field populated, MVC's serializer settings,
    /// and the null fields still written out.
    /// </summary>
    private static int SerializedReadingBytes()
    {
        var reading = new SensorGlucose
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            UtcOffset = 600,
            Device = "Dexcom G7 (Nocturne Connect)",
            App = "nocturne-connect",
            DataSource = "dexcom-share",
            CorrelationId = Guid.NewGuid(),
            PatientDeviceId = Guid.NewGuid(),
            LegacyId = "6650f3b1a2c4d5e6f7089abc",
            SyncIdentifier = "dexcom:6650f3b1a2c4d5e6f7089abc",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Mgdl = 123.45678,
            Direction = GlucoseDirection.Flat,
            TrendRate = 0.123456,
            Noise = 1,
            Filtered = 123456.789,
            Unfiltered = 123456.789,
            Delta = -1.2345,
            SmoothedMgdl = 123.456789,
            UnsmoothedMgdl = 123.456789,
        };

        return JsonSerializer
            .SerializeToUtf8Bytes(reading, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Length;
    }

    private static List<MethodInfo> ComputePosts() =>
        typeof(StatisticsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpPostAttribute>().Any())
            .ToList();
}
