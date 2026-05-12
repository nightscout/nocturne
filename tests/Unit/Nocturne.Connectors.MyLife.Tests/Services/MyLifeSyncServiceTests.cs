using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Connectors.MyLife.Services;
using System.Runtime.CompilerServices;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Services;

public class MyLifeSyncServiceTests
{
    private const string ServiceUrl = "https://example.com";
    private const string AuthToken = "token";
    private const string PatientId = "patient-1";

    [Fact]
    public async Task FetchEventsPerMonthAsync_YieldsOneBatchPerMonth()
    {
        var batches = new List<MyLifeMonthBatch>
        {
            new("202501", [new MyLifeEvent { EventId = "a" }]),
            new("202502", [new MyLifeEvent { EventId = "b" }]),
            new("202503", [new MyLifeEvent { EventId = "c" }]),
        };
        var spy = new SpySyncService(batches);

        var results = new List<MyLifeMonthBatch>();
        await foreach (var batch in spy.FetchEventsPerMonthAsync(
            ServiceUrl, AuthToken, PatientId,
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None))
        {
            results.Add(batch);
        }

        results.Should().HaveCount(3);
        results.Select(b => b.Month).Should().Equal("202501", "202502", "202503");
    }

    [Fact]
    public async Task FetchEventsPerMonthAsync_SkipsEmptyMonths()
    {
        var batches = new List<MyLifeMonthBatch>
        {
            new("202501", [new MyLifeEvent { EventId = "a" }]),
            // 202502 is empty — not in the list
            new("202503", [new MyLifeEvent { EventId = "c" }]),
        };
        var spy = new SpySyncService(batches);

        var results = new List<MyLifeMonthBatch>();
        await foreach (var batch in spy.FetchEventsPerMonthAsync(
            ServiceUrl, AuthToken, PatientId,
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None))
        {
            results.Add(batch);
        }

        results.Should().HaveCount(2);
        results.Select(b => b.Month).Should().Equal("202501", "202503");
    }

    [Fact]
    public async Task FetchEventsAsync_DelegatesToPerMonth()
    {
        var batches = new List<MyLifeMonthBatch>
        {
            new("202501", [new MyLifeEvent { EventId = "a" }, new MyLifeEvent { EventId = "b" }]),
            new("202502", [new MyLifeEvent { EventId = "c" }]),
        };
        var spy = new SpySyncService(batches);

        var events = await spy.FetchEventsAsync(
            ServiceUrl, AuthToken, PatientId,
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        events.Should().HaveCount(3);
        events.Select(e => e.EventId).Should().Equal("a", "b", "c");
    }

    /// <summary>
    /// Spy that overrides <see cref="MyLifeSyncService.FetchEventsPerMonthAsync"/> to yield
    /// controlled batches without hitting the SOAP client.
    /// </summary>
    private sealed class SpySyncService(IReadOnlyList<MyLifeMonthBatch> batches)
        : MyLifeSyncService(null!, NullLogger<MyLifeSyncService>.Instance)
    {
        public override async IAsyncEnumerable<MyLifeMonthBatch> FetchEventsPerMonthAsync(
            string serviceUrl, string authToken, string patientId,
            DateTime since, DateTime until,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var batch in batches)
            {
                await Task.CompletedTask;
                yield return batch;
            }
        }
    }
}
