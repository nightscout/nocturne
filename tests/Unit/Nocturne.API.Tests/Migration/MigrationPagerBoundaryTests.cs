using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Helpers;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// Uploaders write several treatments at one millisecond, so a migration page can end partway
/// through one. The pull has to return the rest on the next page without repeating what it
/// already returned.
/// </summary>
public class MigrationPagerBoundaryTests
{
    private const int PageSize = LegacyReadLimits.MaxMergedCount;

    private static readonly DateTime Crowded = new(2020, 3, 1, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_millisecond_more_crowded_than_the_widest_page_is_logged_and_stepped_over()
    {
        var source = new Source();
        source.Add(PageSize + 5, Crowded);
        source.Add(5, Crowded.AddMinutes(-30), step: TimeSpan.FromMinutes(-1));
        var older = source.Ids.TakeLast(5).ToList();
        var migrated = new List<string>();
        var logger = new Mock<ILogger>();

        await using var provider = MigrationJobHarness.BuildProvider(source, treatmentOutcome: page =>
        {
            migrated.AddRange(page.Select(t => t.Id!));
            return new DecompositionResult();
        });
        await MigrationJobHarness.RunAsync(provider, onCreated: null, ["treatments"], logger.Object);

        source.WidestAsked.Should().BeLessThanOrEqualTo(BackwardTimePager.MaxWidestPageSize);
        migrated.Should().Contain(older, "the pull carries on below the crowded millisecond");
        migrated.Should().OnlyHaveUniqueItems();
        logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("treatments") && state.ToString()!.Contains($"{Crowded:o}")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }

    /// <summary>
    /// The source orders created_at by the string it holds, so a treatment written at 20:00+10:00
    /// sorts above one written at 15:00Z although it happened five hours earlier. A page ending on
    /// it has to be followed from 20:00 as written, not from its 10:00Z instant.
    /// </summary>
    [Fact]
    public async Task Treatments_written_with_a_local_offset_across_a_page_boundary_are_all_migrated_once()
    {
        var source = new Source();
        source.Add(PageSize - 2, new DateTime(2020, 6, 16, 3, 0, 0, DateTimeKind.Utc), step: TimeSpan.FromSeconds(-1));
        source.Add(5, new DateTime(2020, 6, 15, 20, 0, 0, DateTimeKind.Unspecified), offset: "+10:00");
        source.Add(5, new DateTime(2020, 6, 15, 15, 0, 0, DateTimeKind.Utc), step: TimeSpan.FromMinutes(-1));
        var migrated = new List<string>();

        await using var provider = MigrationJobHarness.BuildProvider(source, treatmentOutcome: page =>
        {
            migrated.AddRange(page.Select(t => t.Id!));
            return new DecompositionResult();
        });
        var status = await MigrationJobHarness.RunAsync(provider, onCreated: null, ["treatments"]);

        status.CollectionProgress["treatments"].FailureReason.Should().BeNull();
        migrated.Should().OnlyHaveUniqueItems();
        migrated.Should().BeEquivalentTo(source.Ids);
    }

    /// <summary>
    /// Stands in for a Nightscout treatments collection, filtering and sorting on created_at as an
    /// ordinal string, newest first, ties in insertion order.
    /// </summary>
    private sealed class Source : HttpMessageHandler
    {
        private readonly List<(string Id, string CreatedAt)> _records = [];

        public List<string> Ids => _records.Select(r => r.Id).ToList();

        /// <summary>The largest count any request asked for.</summary>
        public int WidestAsked { get; private set; }

        /// <param name="offset">The offset each created_at is written with, after <paramref name="at"/>'s wall clock.</param>
        public void Add(int count, DateTime at, TimeSpan step = default, string offset = "Z")
        {
            for (var i = 0; i < count; i++)
            {
                var id = (_records.Count + 1).ToString("x24", CultureInfo.InvariantCulture);
                _records.Add((id, (at + step * i).ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + offset));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath != "/api/v1/treatments.json")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var asked = int.Parse(Regex.Match(url, @"count=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            WidestAsked = Math.Max(WidestAsked, asked);
            var lte = Regex.Match(url, @"\[\$lte\]=([^&]+)") is { Success: true } match ? match.Groups[1].Value : null;

            var page = _records
                .Where(r => lte is null || string.CompareOrdinal(r.CreatedAt, lte) <= 0)
                .OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal)
                .Take(asked)
                .Select(r => $$"""{"_id":"{{r.Id}}","eventType":"Temp Basal","created_at":"{{r.CreatedAt}}"}""");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"[{string.Join(',', page)}]", Encoding.UTF8, "application/json"),
            });
        }
    }
}
