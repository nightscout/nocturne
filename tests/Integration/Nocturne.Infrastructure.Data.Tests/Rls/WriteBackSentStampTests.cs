using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// What write-back sent is kept on the live rows of each record, also for a history-clamped member
/// whose record is dated before the 24-hour window, and the record then reads as sent upstream.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class WriteBackSentStampTests(RlsCompletenessFixture fx) : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task AClampedMembersOldRecords_AreStampedOnTheirLiveRows_AndReadAsSent()
    {
        var old = DateTime.UtcNow.AddDays(-3);
        var keyed = MongoObjectId.NewObjectId();
        var other = MongoObjectId.NewObjectId();
        var group = Guid.CreateVersion7();
        var unkeyedBolus = Guid.CreateVersion7();
        var keyedBolus = Guid.CreateVersion7();
        var keyedCarbs = Guid.CreateVersion7();
        var otherBolus = Guid.CreateVersion7();
        var unkeyedCarbs = Guid.CreateVersion7();
        var tombstone = Guid.CreateVersion7();
        await using (var owner = await OwnerAsync())
        {
            owner.Boluses.AddRange(
                new BolusEntity { Id = keyedBolus, LegacyId = keyed, Timestamp = old, Insulin = 2 },
                new BolusEntity { Id = otherBolus, LegacyId = other, Timestamp = old, Insulin = 1 },
                new BolusEntity { Id = unkeyedBolus, CorrelationId = group, Timestamp = old, Insulin = 3 });
            owner.CarbIntakes.AddRange(
                new CarbIntakeEntity { Id = keyedCarbs, LegacyId = keyed, Timestamp = old, Carbs = 30 },
                new CarbIntakeEntity { Id = unkeyedCarbs, CorrelationId = group, Timestamp = old, Carbs = 45 },
                new CarbIntakeEntity
                {
                    Id = tombstone, LegacyId = keyed, Timestamp = old, Carbs = 20, DeletedAt = old,
                    WriteBackSentFingerprint = "earlier",
                });
            await owner.SaveChangesAsync();
        }

        await using var scope = ClampedScope();
        var keys = new DecomposedTreatmentKeys(scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(), TimeProvider.System);
        (await keys.GetSentUpstreamAsync([keyed, other])).Should().BeEquivalentTo([keyed], "a deletion keeps what was sent before it");

        await keys.RecordSentAsync(
        [
            new WriteBackSentRecord(keyed, null, ["bolus-half", "carbs-half"]),
            new WriteBackSentRecord(other, null, ["other"]),
            new WriteBackSentRecord(null, unkeyedBolus, ["unkeyed"]),
        ]);

        await using var check = await OwnerAsync();
        (await check.Boluses.IgnoreQueryFilters().Where(b => b.TenantId == _tenant)
                .ToDictionaryAsync(b => b.Id, b => b.WriteBackSentFingerprint))
            .Should().BeEquivalentTo(new Dictionary<Guid, string?>
            {
                [keyedBolus] = "bolus-half carbs-half",
                [otherBolus] = "other",
                [unkeyedBolus] = "unkeyed",
            });
        (await check.CarbIntakes.IgnoreQueryFilters().Where(c => c.TenantId == _tenant)
                .ToDictionaryAsync(c => c.Id, c => c.WriteBackSentFingerprint))
            .Should().BeEquivalentTo(new Dictionary<Guid, string?>
            {
                [keyedCarbs] = "bolus-half carbs-half",
                [unkeyedCarbs] = "unkeyed",
                [tombstone] = "earlier",
            });
        (await keys.GetSentUpstreamAsync([keyed, other, "never-sent"])).Should().BeEquivalentTo([keyed, other]);
    }

    /// <summary>
    /// A send begun is added only to rows that keep an accepted one, so a record no send of which
    /// upstream accepted still reads as never sent. Once accepted, it is what every live row of its
    /// record keeps.
    /// </summary>
    [Fact]
    public async Task ASendBegun_IsAddedOnlyToRowsThatKeepAnAcceptedOne_AndAcceptedReplacesIt()
    {
        var old = DateTime.UtcNow.AddDays(-3);
        var sent = MongoObjectId.NewObjectId();
        var neverSent = MongoObjectId.NewObjectId();
        var group = Guid.CreateVersion7();
        var sentBolus = Guid.CreateVersion7();
        var sentCarbs = Guid.CreateVersion7();
        var neverSentBolus = Guid.CreateVersion7();
        var unkeyedBolus = Guid.CreateVersion7();
        var unkeyedCarbs = Guid.CreateVersion7();
        await using (var owner = await OwnerAsync())
        {
            owner.Boluses.AddRange(
                new BolusEntity { Id = sentBolus, LegacyId = sent, Timestamp = old, Insulin = 2, WriteBackSentFingerprint = "whole" },
                new BolusEntity { Id = neverSentBolus, LegacyId = neverSent, Timestamp = old, Insulin = 1 },
                new BolusEntity { Id = unkeyedBolus, CorrelationId = group, Timestamp = old, Insulin = 3, WriteBackSentFingerprint = "unkeyed" });
            owner.CarbIntakes.AddRange(
                new CarbIntakeEntity { Id = sentCarbs, LegacyId = sent, Timestamp = old, Carbs = 30 },
                new CarbIntakeEntity { Id = unkeyedCarbs, CorrelationId = group, Timestamp = old, Carbs = 45, WriteBackSentFingerprint = "unkeyed" });
            await owner.SaveChangesAsync();
        }

        await using var scope = ClampedScope();
        var keys = new DecomposedTreatmentKeys(scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(), TimeProvider.System);

        await keys.RecordSendingAsync(
        [
            new WriteBackSentRecord(sent, null, ["bolus-half", "carbs-half"]),
            new WriteBackSentRecord(neverSent, null, ["never"]),
            new WriteBackSentRecord(null, unkeyedBolus, ["unkeyed-edit"]),
        ]);

        (await StoredAsync()).Should().BeEquivalentTo(new Dictionary<Guid, string?>
        {
            [sentBolus] = "whole;bolus-half carbs-half",
            [sentCarbs] = null,
            [neverSentBolus] = null,
            [unkeyedBolus] = "unkeyed;unkeyed-edit",
            [unkeyedCarbs] = "unkeyed;unkeyed-edit",
        });
        (await keys.GetSentUpstreamAsync([sent, neverSent])).Should().BeEquivalentTo([sent]);

        await keys.RecordSentAsync([new WriteBackSentRecord(sent, null, ["bolus-half", "carbs-half"])]);

        var stored = await StoredAsync();
        WriteBackFingerprint.Sends(stored[sentBolus]!).Should().BeEquivalentTo(new[] { new[] { "bolus-half", "carbs-half" } });
        WriteBackFingerprint.MayBeSent(stored[sentBolus]!, "whole", DateTimeOffset.UtcNow).Should().BeTrue();
        stored[sentCarbs].Should().Be("bolus-half carbs-half");
    }

    private async Task<Dictionary<Guid, string?>> StoredAsync()
    {
        await using var check = await OwnerAsync();
        var rows = await check.Boluses.IgnoreQueryFilters().Where(b => b.TenantId == _tenant)
            .ToDictionaryAsync(b => b.Id, b => b.WriteBackSentFingerprint);
        foreach (var (id, stored) in await check.CarbIntakes.IgnoreQueryFilters().Where(c => c.TenantId == _tenant)
                     .ToDictionaryAsync(c => c.Id, c => c.WriteBackSentFingerprint))
            rows[id] = stored;
        return rows;
    }

    private ValueTask<NocturneDbContext> OwnerAsync() =>
        _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);

    private AsyncServiceScope ClampedScope()
    {
        var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        return scope;
    }
}
