using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using static Nocturne.Infrastructure.Data.Tests.ConnectorFoodEntryRaceData;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <see cref="ConnectorFoodEntryRepository.AcceptMatchAsync"/> against real Postgres under the runtime
/// role and Row Level Security: each accept runs in a scope of its own, as two requests would.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ConnectorFoodEntryAcceptMatchRaceTests
{
    private const int Entries = 12;

    private readonly RlsCompletenessFixture _fx;

    public ConnectorFoodEntryAcceptMatchRaceTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task Two_accepts_racing_for_one_entry_and_one_carb_intake_write_one_row()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var tenant = await SeedTenantAsync(_fx, "accept-match-race");
        var carbIntake = await SeedCarbIntakeAsync(provider, tenant);
        var entries = await SeedEntriesAsync(provider, tenant, Entries);

        foreach (var entry in entries)
        {
            var outcomes = await RaceAsync(provider, tenant, entry, carbIntake, carbIntake);

            outcomes.Should().BeEquivalentTo([AcceptMatchOutcome.Linked, AcceptMatchOutcome.AlreadyLinked]);
        }

        (await RowsAsync(provider, tenant, carbIntake)).Should().Be(Entries, "one row per entry, not one per accept");
    }

    [Fact]
    public async Task Two_accepts_racing_for_one_entry_and_two_carb_intakes_link_it_once_and_refuse_the_other()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var tenant = await SeedTenantAsync(_fx, "accept-match-race");
        var first = await SeedCarbIntakeAsync(provider, tenant);
        var second = await SeedCarbIntakeAsync(provider, tenant);
        var entries = await SeedEntriesAsync(provider, tenant, Entries);

        foreach (var entry in entries)
        {
            var outcomes = await RaceAsync(provider, tenant, entry, first, second);

            outcomes.Should().BeEquivalentTo([AcceptMatchOutcome.Linked, AcceptMatchOutcome.AlreadyResolved]);
        }

        (await RowsAsync(provider, tenant, first) + await RowsAsync(provider, tenant, second))
            .Should().Be(Entries, "each entry is on exactly one carb intake");
    }

    private static async Task<AcceptMatchOutcome[]> RaceAsync(
        ServiceProvider provider, Guid tenant, Guid entry, Guid carbIntakeA, Guid carbIntakeB)
    {
        await using var scopeA = provider.CreateAsyncScope();
        await using var scopeB = provider.CreateAsyncScope();
        var a = Repository(scopeA.ServiceProvider, tenant);
        var b = Repository(scopeB.ServiceProvider, tenant);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<AcceptMatchOutcome> Accept(ConnectorFoodEntryRepository repository, Guid carbIntake)
        {
            await start.Task;
            return await repository.AcceptMatchAsync(entry, Row(carbIntake));
        }

        var racing = new[] { Task.Run(() => Accept(a, carbIntakeA)), Task.Run(() => Accept(b, carbIntakeB)) };
        start.SetResult();
        return await Task.WhenAll(racing).WaitAsync(Eventually.DefaultPatience);
    }
}
