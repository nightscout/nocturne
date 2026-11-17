using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// The span read a treatment create keys served-shaped ids by, against PostgreSQL: it reads fifty ids
/// per statement in each span table, however many ids the batch carries.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class StateSpanTreatmentIdReadsTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    [Fact]
    public async Task A_create_reads_the_span_tables_fifty_served_ids_per_statement()
    {
        var few = await SpanReadsCreatingAsync(10, offset: 0);
        var many = await SpanReadsCreatingAsync(110, offset: 1_000);

        many.Should().Be(few + 4,
            "110 ids naming no record are three statements of fifty in each of the two span tables, where 10 are one in each");
    }

    /// <returns>The SELECTs on <c>state_spans</c> a create of <paramref name="count"/> notes made.</returns>
    private async Task<int> SpanReadsCreatingAsync(int count, int offset)
    {
        var at = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var treatments = Enumerable.Range(offset, count).Select(i => new Treatment
        {
            Id = $"6d{i:x10}" + "7000" + "8" + "0000000",
            EventType = "Note",
            Notes = $"note {i}",
            Mills = new DateTimeOffset(at.AddMinutes(i)).ToUnixTimeMilliseconds(),
        }).ToList();

        var statements = new ConcurrentQueue<string>();
        using var root = new System.Diagnostics.Activity("span-id-reads").Start();
        var trace = root.TraceId;
        using (var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.TraceId == trace && (a.GetTagItem("db.query.text") ?? a.GetTagItem("db.statement")) is string text)
                    statements.Enqueue(text);
            },
        })
        {
            ActivitySource.AddActivityListener(listener);
            await WithTenantScopeAsync(services =>
                services.GetRequiredService<ITreatmentService>().CreateTreatmentsAsync(treatments));
        }

        return statements.Count(s => s.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && s.Contains("state_spans", StringComparison.Ordinal));
    }
}
