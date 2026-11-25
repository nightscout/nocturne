using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using static Nocturne.Infrastructure.Data.Tests.ConnectorFoodEntryRaceData;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The outcomes a race between an accept and a dismissal or a withdrawal can end in, other than an
/// accept that beats a dismissal (a unit test of the repository holds that one), reached by
/// ordering the calls, on real Postgres under the runtime role and Row Level Security. Each request
/// runs in a scope of its own. <see cref="ConnectorFoodEntryResolutionRaceTests"/> holds the
/// invariants that must hold whichever claim wins.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ConnectorFoodEntryResolutionOrderTests
{
    private readonly RlsCompletenessFixture _fx;

    public ConnectorFoodEntryResolutionOrderTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task An_accept_after_a_withdrawal_claimed_the_entry_is_refused_and_writes_no_row()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var (tenant, entry, carbIntake) = await SeedAsync(provider);

        var withdrawn = await WithdrawAsync(provider, tenant);
        var accepted = await AcceptAsync(provider, tenant, entry, carbIntake);

        withdrawn.Should().Equal(entry);
        accepted.Should().Be(AcceptMatchOutcome.AlreadyResolved);
        var stored = await StoredAsync(provider, tenant, entry);
        stored.Status.Should().Be(ConnectorFoodEntryStatus.Deleted);
        stored.MatchedCarbIntakeId.Should().BeNull();
        (await RowsAsync(provider, tenant, carbIntake)).Should().Be(0);
    }

    [Fact]
    public async Task An_accept_that_links_between_the_withdrawals_read_and_its_claim_stays_linked_and_is_not_reported()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var (tenant, entry, carbIntake) = await SeedAsync(provider);
        await using var holder = new NpgsqlConnection(_fx.AppConnectionString);
        await holder.OpenAsync();
        await using var hold = await holder.BeginTransactionAsync();
        await HoldAsync(holder, hold, tenant, entry);

        var accept = AcceptAsync(provider, tenant, entry, carbIntake);
        await PostgresLockWaits.UntilBlockedAsync(_fx.AppConnectionString, accept);
        var withdraw = WithdrawAsync(provider, tenant);
        await PostgresLockWaits.UntilWaitingAsync(_fx.AppConnectionString, 2, accept, withdraw);
        await hold.CommitAsync();

        (await accept.WaitAsync(Eventually.DefaultPatience)).Should().Be(AcceptMatchOutcome.Linked);
        (await withdraw.WaitAsync(Eventually.DefaultPatience)).Should().BeEmpty();
        var stored = await StoredAsync(provider, tenant, entry);
        stored.Status.Should().Be(ConnectorFoodEntryStatus.Matched);
        stored.MatchedCarbIntakeId.Should().Be(carbIntake);
        (await RowsAsync(provider, tenant, carbIntake)).Should().Be(1);
    }

    [Fact]
    public async Task An_accept_after_a_dismiss_claimed_the_entry_is_refused_and_writes_no_row()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var (tenant, entry, carbIntake) = await SeedAsync(provider);

        var dismissed = await DismissAsync(provider, tenant, entry);
        var accepted = await AcceptAsync(provider, tenant, entry, carbIntake);

        dismissed.Should().Be(DismissMatchOutcome.Dismissed);
        accepted.Should().Be(AcceptMatchOutcome.AlreadyResolved);
        var stored = await StoredAsync(provider, tenant, entry);
        stored.Status.Should().Be(ConnectorFoodEntryStatus.Standalone);
        stored.MatchedCarbIntakeId.Should().BeNull();
        (await RowsAsync(provider, tenant, carbIntake)).Should().Be(0);
    }

    private async Task<(Guid Tenant, Guid Entry, Guid CarbIntake)> SeedAsync(ServiceProvider provider)
    {
        var tenant = await SeedTenantAsync(_fx, "resolution-order");
        var entry = (await SeedEntriesAsync(provider, tenant, 1))[0];
        return (tenant, entry, await SeedCarbIntakeAsync(provider, tenant));
    }

    private static async Task HoldAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant, Guid entry)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT set_config('app.current_tenant_id', @tenant, true);
            SELECT id FROM connector_food_entries WHERE id = @entry FOR UPDATE;
            """;
        command.Parameters.Add(new NpgsqlParameter("tenant", tenant.ToString()));
        command.Parameters.Add(new NpgsqlParameter("entry", entry));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<AcceptMatchOutcome> AcceptAsync(ServiceProvider provider, Guid tenant, Guid entry, Guid carbIntake)
    {
        await using var scope = provider.CreateAsyncScope();
        return await Repository(scope.ServiceProvider, tenant).AcceptMatchAsync(entry, Row(carbIntake));
    }

    private static async Task<DismissMatchOutcome> DismissAsync(ServiceProvider provider, Guid tenant, Guid entry)
    {
        await using var scope = provider.CreateAsyncScope();
        return await Repository(scope.ServiceProvider, tenant).DismissAsync(entry);
    }

    private static async Task<IReadOnlyList<Guid>> WithdrawAsync(ServiceProvider provider, Guid tenant)
    {
        await using var scope = provider.CreateAsyncScope();
        return await Repository(scope.ServiceProvider, tenant).MarkMissingAsDeletedAsync(
            Connector,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(1),
            new HashSet<string>(),
            DateTimeOffset.UtcNow);
    }
}
