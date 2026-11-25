using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using static Nocturne.Infrastructure.Data.Tests.ConnectorFoodEntryRaceData;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Dismissing and withdrawing a food entry against an accept, on real Postgres under the runtime
/// role and Row Level Security: each request runs in a scope of its own. Whichever claim on
/// Pending wins, the entry ends in one state and its <c>treatment_foods</c> row agrees with it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ConnectorFoodEntryResolutionRaceTests
{
    private const int Entries = 30;
    private const int Rounds = 3;

    private readonly RlsCompletenessFixture _fx;

    public ConnectorFoodEntryResolutionRaceTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task A_withdrawal_racing_accepts_never_leaves_a_deleted_entry_with_a_breakdown_row()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);

        for (var round = 0; round < Rounds; round++)
        {
            var tenant = await SeedTenantAsync(_fx, "withdraw-race");
            var entries = await SeedEntriesAsync(provider, tenant, Entries);
            var carbIntakes = new List<Guid>();
            foreach (var _ in entries)
            {
                carbIntakes.Add(await SeedCarbIntakeAsync(provider, tenant));
            }

            var scopes = entries.Select(_ => provider.CreateAsyncScope()).ToList();
            await using var withdrawalScope = provider.CreateAsyncScope();
            try
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var accepting = entries
                    .Select((entry, i) => Task.Run(async () =>
                    {
                        var repository = Repository(scopes[i].ServiceProvider, tenant);
                        await start.Task;
                        return await repository.AcceptMatchAsync(entry, Row(carbIntakes[i]));
                    }))
                    .ToList();
                var withdrawing = Task.Run(async () =>
                {
                    var repository = Repository(withdrawalScope.ServiceProvider, tenant);
                    await start.Task;
                    return await repository.MarkMissingAsDeletedAsync(
                        Connector,
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(1),
                        new HashSet<string>(),
                        DateTimeOffset.UtcNow);
                });
                start.SetResult();

                var outcomes = await Task.WhenAll(accepting).WaitAsync(Eventually.DefaultPatience);
                var withdrawn = await withdrawing.WaitAsync(Eventually.DefaultPatience);

                for (var i = 0; i < entries.Count; i++)
                {
                    var stored = await StoredAsync(provider, tenant, entries[i]);
                    var rows = await RowsAsync(provider, tenant, carbIntakes[i]);

                    if (outcomes[i] == AcceptMatchOutcome.Linked)
                    {
                        stored.Status.Should().Be(ConnectorFoodEntryStatus.Matched);
                        stored.MatchedCarbIntakeId.Should().Be(carbIntakes[i]);
                        rows.Should().Be(1);
                        withdrawn.Should().NotContain(entries[i]);
                    }
                    else
                    {
                        outcomes[i].Should().Be(AcceptMatchOutcome.AlreadyResolved);
                        stored.Status.Should().Be(ConnectorFoodEntryStatus.Deleted);
                        rows.Should().Be(0, "a withdrawn entry is not linked");
                        withdrawn.Should().Contain(entries[i]);
                    }
                }
            }
            finally
            {
                foreach (var scope in scopes)
                {
                    await scope.DisposeAsync();
                }
            }
        }
    }

    [Fact]
    public async Task A_dismiss_racing_an_accept_leaves_the_entry_linked_or_dismissed_and_never_both()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var tenant = await SeedTenantAsync(_fx, "dismiss-race");
        var entries = await SeedEntriesAsync(provider, tenant, Entries);

        foreach (var entry in entries)
        {
            var carbIntake = await SeedCarbIntakeAsync(provider, tenant);
            await using var acceptScope = provider.CreateAsyncScope();
            await using var dismissScope = provider.CreateAsyncScope();
            var accepts = Repository(acceptScope.ServiceProvider, tenant);
            var dismisses = Repository(dismissScope.ServiceProvider, tenant);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var accepting = Task.Run(async () =>
            {
                await start.Task;
                return await accepts.AcceptMatchAsync(entry, Row(carbIntake));
            });
            var dismissing = Task.Run(async () =>
            {
                await start.Task;
                return await dismisses.DismissAsync(entry);
            });
            start.SetResult();
            var accepted = await accepting.WaitAsync(Eventually.DefaultPatience);
            var dismissed = await dismissing.WaitAsync(Eventually.DefaultPatience);

            var stored = await StoredAsync(provider, tenant, entry);
            var rows = await RowsAsync(provider, tenant, carbIntake);
            if (accepted == AcceptMatchOutcome.Linked)
            {
                dismissed.Should().Be(DismissMatchOutcome.AlreadyResolved);
                stored.Status.Should().Be(ConnectorFoodEntryStatus.Matched);
                stored.MatchedCarbIntakeId.Should().Be(carbIntake);
                rows.Should().Be(1);
            }
            else
            {
                accepted.Should().Be(AcceptMatchOutcome.AlreadyResolved);
                dismissed.Should().Be(DismissMatchOutcome.Dismissed);
                stored.Status.Should().Be(ConnectorFoodEntryStatus.Standalone);
                stored.MatchedCarbIntakeId.Should().BeNull();
                rows.Should().Be(0);
            }
        }
    }
}
