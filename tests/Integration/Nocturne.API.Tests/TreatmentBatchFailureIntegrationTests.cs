using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Castle.DynamicProxy;
using Nocturne.API.Helpers;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A treatment batch against real PostgreSQL: a treatment the store refuses for its data is
/// answered and skipped, one that fails for the store's state stops the batch with what came
/// before it committed.
/// </summary>
[Collection(ApiIntegrationShards.One)]
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
        echoed[1].TryGetProperty(TreatmentEcho.RefusalMember, out _).Should().BeTrue();
        echoed[0].TryGetProperty(TreatmentEcho.RefusalMember, out _).Should().BeFalse();
        echoed[2].TryGetProperty(TreatmentEcho.RefusalMember, out _).Should().BeFalse();

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var stored = await db.Set<NoteEntity>().Select(n => n.Text).ToListAsync();
        stored.Should().BeEquivalentTo(["first", longNote]);

        var rows = await RejectedUploadsAsync();
        rows.Should().ContainSingle();
        rows[0].GetProperty("recordType").GetString().Should().Be("treatment");
        rows[0].GetProperty("occurrences").GetInt32().Should().Be(1);
        rows[0].GetProperty("reason").GetString().Should().StartWith("A text value is longer than Nocturne can store");
        JsonDocument.Parse(rows[0].GetProperty("payload").GetString()!).RootElement
            .GetProperty("notes").GetString().Should().Be("second");

        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", batch)).StatusCode.Should().Be(HttpStatusCode.OK);
        var resent = await RejectedUploadsAsync();
        resent.Should().ContainSingle().Which.GetProperty("occurrences").GetInt32().Should().Be(2);

        var clear = await AuthenticatedClient.PostAsJsonAsync(
            "/api/v4/rejected-uploads/clear", new { ids = new[] { resent[0].GetProperty("id").GetGuid() } });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RejectedUploadsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_treatment_with_text_jsonb_cannot_hold_is_still_kept()
    {
        const string body = """
            [{"_id":"a-very-long-id-that-cannot-be-stored-a-very-long-id-that-cannot-be-stored-a-very-long-id-that-cannot-be-stored-a-very-long-id-that-cannot-be-stored-a-very-long-id-that-cannot-be-stored-a-very-long-id-that-cannot-be-stored-a-very-long-id-that-cannot-be-stored-xx","eventType":"Note","notes":"lone \ud800 and nul \u0000","created_at":"2026-09-30T01:00:00.000Z"}]
            """;

        var response = await AuthenticatedClient.PostAsync(
            "/api/v1/treatments", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var kept = (await RejectedUploadsAsync()).Should().ContainSingle().Subject;
        JsonDocument.Parse(kept.GetProperty("payload").GetString()!).RootElement
            .GetProperty("notes").GetString().Should().Be("lone \uFFFD and nul \uFFFD");
    }

    [Fact]
    public async Task A_span_the_store_refuses_rejects_only_its_treatment_and_every_resend_is_answered()
    {
        var batch = new object[]
        {
            new
            {
                eventType = "Temporary Target", enteredBy = new string('e', 60), duration = 60,
                targetTop = 140, targetBottom = 120, units = "mg/dl", created_at = "2026-09-30T02:00:00.000Z",
            },
            new { eventType = "Note", notes = "after the target", created_at = "2026-09-30T02:05:00.000Z" },
        };

        for (var send = 0; send < 2; send++)
        {
            var response = await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", batch);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var echoed = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
            echoed.Should().HaveCount(2);
            echoed[0].TryGetProperty(TreatmentEcho.RefusalMember, out _).Should().BeTrue();
            echoed[1].TryGetProperty(TreatmentEcho.RefusalMember, out _).Should().BeFalse();
        }

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        (await db.Set<NoteEntity>().Select(n => n.Text).ToListAsync()).Should().Equal("after the target");
        (await db.StateSpans.CountAsync()).Should().Be(0);
        (await RejectedUploadsAsync()).Should().ContainSingle()
            .Which.GetProperty("reason").GetString().Should().StartWith("A text value is longer than Nocturne can store");
    }

    [Fact]
    public async Task A_food_line_the_store_refuses_rejects_only_its_treatment()
    {
        var batch = new object[]
        {
            new { eventType = "Meal Bolus", carbs = 30, foodType = new string('f', 1200), created_at = "2026-09-30T03:00:00.000Z" },
            new { eventType = "Meal Bolus", carbs = 20, foodType = "toast", created_at = "2026-09-30T03:05:00.000Z" },
        };

        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", batch);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var echoed = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        echoed.Should().HaveCount(2);
        echoed[0].TryGetProperty(TreatmentEcho.RefusalMember, out _).Should().BeTrue();
        echoed[1].TryGetProperty(TreatmentEcho.RefusalMember, out _).Should().BeFalse();

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        (await db.Set<TreatmentFoodEntity>().Select(l => l.Note).ToListAsync()).Should().Equal("toast");
        (await RejectedUploadsAsync()).Should().ContainSingle()
            .Which.GetProperty("reason").GetString().Should().StartWith("A text value is longer than Nocturne can store");
    }

    private async Task<List<JsonElement>> RejectedUploadsAsync() =>
        (await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v4/rejected-uploads"))
            .GetProperty("data").EnumerateArray().ToList();

    /// <summary>
    /// A batch is written table by table, so a store failure part way keeps the tables before it: the
    /// bolus is stored, the notes are not, and the first treatment with nothing stored names the stop.
    /// </summary>
    [Fact]
    public async Task A_batch_stopped_by_a_store_failure_keeps_what_it_stored_before_it()
    {
        await using var scope = TenantJobScope.Open(
            Fixture.Services, new TenantContext(Fixture.TenantId, "", "", true, false), "test:treatment-batch");
        var notes = new ProxyGenerator().CreateInterfaceProxyWithTarget(
            scope.ServiceProvider.GetRequiredService<INoteRepository>(), new TimesOutWritingNotes());
        var decomposer = ActivatorUtilities.CreateInstance<TreatmentDecomposer>(scope.ServiceProvider, notes);
        var store = ActivatorUtilities.CreateInstance<TreatmentReadService>(scope.ServiceProvider, decomposer);

        var act = () => store.CreateAsync(
        [
            new Treatment { Id = "batch-stop-bolus", EventType = "Correction Bolus", Insulin = 1.5, Mills = 1_790_000_000_000 },
            new Treatment { Id = "batch-stop-second", EventType = "Note", Notes = "second", Mills = 1_790_000_300_000 },
            new Treatment { Id = "batch-stop-third", EventType = "Note", Notes = "third", Mills = 1_790_000_600_000 },
        ], WriteOrigin.Live);

        var failed = (await act.Should().ThrowAsync<TreatmentBatchFailedException>()).Which;
        failed.Written.Select(t => t.Id).Should().Equal("batch-stop-bolus");
        failed.FailedIndex.Should().Be(1);

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        (await db.Set<BolusEntity>().Select(b => b.LegacyId).ToListAsync()).Should().Equal("batch-stop-bolus");
        (await db.Set<NoteEntity>().CountAsync()).Should().Be(0);
    }

    /// <summary>Times out every note write.</summary>
    private sealed class TimesOutWritingNotes : IInterceptor
    {
        public void Intercept(IInvocation invocation)
        {
            if (invocation.Method.Name == nameof(INoteRepository.BulkUpsertAsync))
                throw new TimeoutException();

            invocation.Proceed();
        }
    }
}
