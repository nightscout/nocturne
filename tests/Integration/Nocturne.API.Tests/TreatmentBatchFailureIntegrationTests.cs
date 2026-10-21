using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A treatment batch against real PostgreSQL: a treatment the store refuses for its data is
/// answered and skipped, one that fails for the store's state stops the batch with what came
/// before it committed.
/// </summary>
[Trait("Category", "Integration")]
public class TreatmentBatchFailureIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    [Fact]
    public async Task A_treatment_the_store_refuses_is_echoed_with_an_error_and_the_rest_are_stored()
    {
        var longNote = new string('n', 5000);
        var batch = new object[]
        {
            new { eventType = "Note", notes = "first", created_at = "2026-09-30T01:00:00.000Z" },
            new { _id = new string('x', 300), eventType = "Note", notes = "second", created_at = "2026-09-30T01:05:00.000Z" },
            new { eventType = "Note", notes = longNote, created_at = "2026-09-30T01:10:00.000Z" },
        };

        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", batch);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var echoed = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        echoed.Should().HaveCount(3);
        echoed[1].GetProperty("notes").GetString().Should().Be("second");
        echoed[1].TryGetProperty("error", out _).Should().BeTrue();
        echoed[0].TryGetProperty("error", out _).Should().BeFalse();
        echoed[2].TryGetProperty("error", out _).Should().BeFalse();

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var stored = await db.Set<NoteEntity>().Select(n => n.Text).ToListAsync();
        stored.Should().BeEquivalentTo(["first", longNote]);
    }

    [Fact]
    public async Task A_batch_stopped_by_a_store_failure_keeps_what_it_stored_before_it()
    {
        await using var scope = TenantJobScope.Open(
            Fixture.Services, new TenantContext(Fixture.TenantId, "", "", true, false), "test:treatment-batch");
        var real = scope.ServiceProvider.GetRequiredService<ITreatmentDecomposer>();
        var failingSecond = new Mock<ITreatmentDecomposer>();
        failingSecond
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Returns((Treatment t, WriteOrigin origin, CancellationToken ct) =>
                t.Notes == "second" ? throw new TimeoutException() : real.DecomposeAsync(t, origin, ct));
        var store = ActivatorUtilities.CreateInstance<TreatmentReadService>(scope.ServiceProvider, failingSecond.Object);

        var act = () => store.CreateAsync(
        [
            new Treatment { EventType = "Note", Notes = "first", Mills = 1_790_000_000_000 },
            new Treatment { EventType = "Note", Notes = "second", Mills = 1_790_000_300_000 },
            new Treatment { EventType = "Note", Notes = "third", Mills = 1_790_000_600_000 },
        ]);

        var failed = (await act.Should().ThrowAsync<TreatmentBatchFailedException>()).Which;
        failed.Written.Select(t => t.Notes).Should().Equal("first");

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        (await db.Set<NoteEntity>().Select(n => n.Text).ToListAsync()).Should().Equal("first");
    }
}
