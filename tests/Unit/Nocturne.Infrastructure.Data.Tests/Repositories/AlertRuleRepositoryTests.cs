using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="AlertRuleRepository"/> over an InMemory store: what a write stores, how a channel
/// replacement carries a secret over, and that a repository over a unit stages its writes for the
/// unit's owner to save.
/// </summary>
public class AlertRuleRepositoryTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly DbContextOptions<NocturneDbContext> _options = new DbContextOptionsBuilder<NocturneDbContext>()
        .UseInMemoryDatabase($"alert_rule_repo_{Guid.NewGuid()}")
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    private readonly FakeTimeProvider _clock = new(Now);

    private NocturneDbContext Db() => new(_options) { TenantId = Tenant };

    private AlertRuleRepository Repository(NocturneDbContext seed) =>
        new(new TestNocturneDbContextFactory(seed), _clock);

    private static AlertRuleWrite Low(string body = """{"direction":"below","value":70}""") => new(
        "Low", "desc", AlertConditionType.Threshold, body, RuleScopeClass.Low, AlertRuleSeverity.Warning,
        IsEnabled: true, SortOrder: 3, AllowThroughDnd: false, AutoResolveEnabled: false,
        AutoResolveParams: null, ClientConfiguration: "{}");

    [Fact]
    public async Task CreateStoresTheRuleItsTagsAndItsChannelsInOrder()
    {
        await using var db = Db();
        var repo = Repository(db);

        var id = await repo.CreateAsync(
            new NewAlertRule(
                Low(),
                [
                    new AlertRuleChannelWrite(ChannelType.Webhook, "https://a.example", "A", "{\"k\":1}", "cipher-a"),
                    new AlertRuleChannelWrite(ChannelType.InApp, "member"),
                ],
                ManagedBy: "tracker:1",
                StarterKind: StarterAlertKind.Low),
            CancellationToken.None);

        await using var check = Db();
        var stored = await check.AlertRules.Include(r => r.Channels).SingleAsync(r => r.Id == id);
        stored.TenantId.Should().Be(Tenant);
        stored.Name.Should().Be("Low");
        stored.ScopeClass.Should().Be(RuleScopeClass.Low);
        stored.SortOrder.Should().Be(3);
        stored.ManagedBy.Should().Be("tracker:1");
        stored.StarterKind.Should().Be(StarterAlertKind.Low);
        stored.CreatedAt.Should().Be(Now.UtcDateTime);
        stored.UpdatedAt.Should().Be(Now.UtcDateTime);
        stored.Channels.OrderBy(c => c.SortOrder).Select(c => (c.ChannelType, c.Destination, c.Secret, c.TenantId, c.CreatedAt))
            .Should().Equal(
                (ChannelType.Webhook, "https://a.example", "cipher-a", Tenant, Now.UtcDateTime),
                (ChannelType.InApp, "member", null, Tenant, Now.UtcDateTime));
    }

    [Fact]
    public async Task UpdateSetsTheWriteAndStampsUpdatedLeavingTagsAndChannels()
    {
        await using var db = Db();
        var repo = Repository(db);
        var id = await repo.CreateAsync(
            new NewAlertRule(Low(), [new AlertRuleChannelWrite(ChannelType.InApp, "member")], ManagedBy: "tracker:1"),
            CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(1));

        var updated = await repo.UpdateAsync(id, Low() with { Name = "Lower", IsEnabled = false, ReviewReason = "repaired" }, CancellationToken.None);

        updated.Should().BeTrue();
        await using var check = Db();
        var stored = await check.AlertRules.Include(r => r.Channels).SingleAsync(r => r.Id == id);
        stored.Name.Should().Be("Lower");
        stored.IsEnabled.Should().BeFalse();
        stored.ReviewReason.Should().Be("repaired");
        stored.ManagedBy.Should().Be("tracker:1");
        stored.UpdatedAt.Should().Be(Now.UtcDateTime.AddHours(1));
        stored.CreatedAt.Should().Be(Now.UtcDateTime);
        stored.Channels.Should().ContainSingle();
    }

    [Fact]
    public async Task AWriteToARuleTheTenantDoesNotHaveChangesNothing()
    {
        await using var db = Db();
        var repo = Repository(db);

        (await repo.UpdateAsync(Guid.NewGuid(), Low(), CancellationToken.None)).Should().BeFalse();
        (await repo.ToggleEnabledAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
        (await repo.DeleteAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
        (await repo.ReplaceChannelsAsync(Guid.NewGuid(), [], null, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ReplacingChannelsCarriesASecretOverByTypeAndDestinationAndKeepsWhatIsAskedFor()
    {
        await using var db = Db();
        var repo = Repository(db);
        var id = await repo.CreateAsync(
            new NewAlertRule(Low(),
            [
                new AlertRuleChannelWrite(ChannelType.Webhook, "https://a.example", Secret: "cipher-a"),
                new AlertRuleChannelWrite(ChannelType.Webhook, "https://b.example", Secret: "cipher-b"),
                new AlertRuleChannelWrite(ChannelType.InApp, "other-member"),
            ]),
            CancellationToken.None);

        await repo.ReplaceChannelsAsync(
            id,
            [
                new AlertRuleChannelWrite(ChannelType.Webhook, "https://a.example", KeepsStoredSecret: true),
                new AlertRuleChannelWrite(ChannelType.Webhook, "https://b.example", Secret: null),
                new AlertRuleChannelWrite(ChannelType.Webhook, "https://c.example", KeepsStoredSecret: true),
            ],
            kept: c => c.ChannelType == ChannelType.InApp,
            CancellationToken.None);

        await using var check = Db();
        var channels = await check.AlertRuleChannels.Where(c => c.AlertRuleId == id).OrderBy(c => c.SortOrder).ToListAsync();
        channels.Select(c => (c.Destination, c.Secret, c.SortOrder)).Should().Equal(
            ("https://a.example", "cipher-a", 0),
            ("https://b.example", null, 1),
            ("https://c.example", null, 2),
            ("other-member", null, 3));
    }

    [Fact]
    public async Task ASecretHeldByTwoChannelsOfOnePairIsRetainedByNeither()
    {
        await using var db = Db();
        var repo = Repository(db);
        var id = await repo.CreateAsync(
            new NewAlertRule(Low(),
            [
                new AlertRuleChannelWrite(ChannelType.Webhook, "https://a.example", Secret: "cipher-1"),
                new AlertRuleChannelWrite(ChannelType.Webhook, "https://a.example", Secret: "cipher-2"),
            ]),
            CancellationToken.None);

        await repo.ReplaceChannelsAsync(
            id, [new AlertRuleChannelWrite(ChannelType.Webhook, "https://a.example", KeepsStoredSecret: true)], null, CancellationToken.None);

        await using var check = Db();
        (await check.AlertRuleChannels.SingleAsync(c => c.AlertRuleId == id)).Secret.Should().BeNull();
    }

    [Fact]
    public async Task ToggleFlipsEnablementAndReportsWhatItWas()
    {
        await using var db = Db();
        var repo = Repository(db);
        var id = await repo.CreateAsync(new NewAlertRule(Low(), []), CancellationToken.None);

        (await repo.ToggleEnabledAsync(id, CancellationToken.None)).Should().BeTrue();
        (await repo.ToggleEnabledAsync(id, CancellationToken.None)).Should().BeFalse();

        await using var check = Db();
        (await check.AlertRules.SingleAsync(r => r.Id == id)).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ChannelsAreAddedAfterTheStoredOnesAndRemovedByIdWithinTheRule()
    {
        await using var db = Db();
        var repo = Repository(db);
        var id = await repo.CreateAsync(
            new NewAlertRule(Low(), [new AlertRuleChannelWrite(ChannelType.InApp, "first")]), CancellationToken.None);
        var other = await repo.CreateAsync(new NewAlertRule(Low(), []), CancellationToken.None);

        await repo.AddChannelAsync(id, new AlertRuleChannelWrite(ChannelType.InApp, "second"), CancellationToken.None);

        await using (var check = Db())
        {
            var channels = await check.AlertRuleChannels.Where(c => c.AlertRuleId == id).OrderBy(c => c.SortOrder).ToListAsync();
            channels.Select(c => (c.Destination, c.SortOrder)).Should().Equal(("first", 0), ("second", 1));

            (await repo.RemoveChannelAsync(other, channels[0].Id, CancellationToken.None)).Should().BeFalse();
            (await repo.RemoveChannelAsync(id, channels[0].Id, CancellationToken.None)).Should().BeTrue();
        }

        await using var after = Db();
        (await after.AlertRuleChannels.Where(c => c.AlertRuleId == id).Select(c => c.Destination).ToListAsync())
            .Should().Equal("second");
    }

    [Fact]
    public async Task ManagedAndStarterReadsFindRulesByTheirTagsWithChannels()
    {
        await using var db = Db();
        var repo = Repository(db);
        var managed = await repo.CreateAsync(
            new NewAlertRule(Low(), [new AlertRuleChannelWrite(ChannelType.InApp, "m")], ManagedBy: "tracker:1"), CancellationToken.None);
        await repo.CreateAsync(new NewAlertRule(Low(), [], ManagedBy: "tracker:2"), CancellationToken.None);
        var starter = await repo.CreateAsync(new NewAlertRule(Low(), [], StarterKind: StarterAlertKind.High), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await repo.CreateAsync(new NewAlertRule(Low(), [], StarterKind: StarterAlertKind.High), CancellationToken.None);

        var managedRules = await repo.GetManagedRulesAsync("tracker:1", CancellationToken.None);
        var starters = await repo.GetStarterRulesAsync(CancellationToken.None);

        managedRules.Keys.Should().Equal(managed);
        managedRules[managed].ManagedBy.Should().Be("tracker:1");
        managedRules[managed].Channels.Should().ContainSingle().Which.Destination.Should().Be("m");
        starters.Keys.Should().Equal(StarterAlertKind.High);
        starters[StarterAlertKind.High].Rule.Id.Should().Be(starter, "the earliest-created rule of a kind is the kind's");
    }

    [Fact]
    public async Task ChannelAndDestinationReadsCoverTheRulesAsked()
    {
        await using var db = Db();
        var repo = Repository(db);
        var a = await repo.CreateAsync(
            new NewAlertRule(Low(), [new AlertRuleChannelWrite(ChannelType.InApp, "x"), new AlertRuleChannelWrite(ChannelType.Webhook, "https://w")]),
            CancellationToken.None);
        var b = await repo.CreateAsync(new NewAlertRule(Low(), [new AlertRuleChannelWrite(ChannelType.InApp, "x")]), CancellationToken.None);
        var none = await repo.CreateAsync(new NewAlertRule(Low(), []), CancellationToken.None);

        var channels = await repo.GetChannelsAsync([a, none], CancellationToken.None);
        var destinations = await repo.GetInAppDestinationsAsync(CancellationToken.None);

        channels.Keys.Should().Equal(a);
        channels[a].Select(c => c.Destination).Should().Equal("x", "https://w");
        destinations.Should().BeEquivalentTo(["x"]);
        _ = b;
    }

    [Fact]
    public async Task AChannelExistsOnlyForTheTenantThatOwnsIt()
    {
        await using var db = Db();
        var repo = Repository(db);
        var ruleId = await repo.CreateAsync(
            new NewAlertRule(Low(), [new AlertRuleChannelWrite(ChannelType.InApp, "member")]), CancellationToken.None);
        await using var seed = Db();
        var channelId = (await seed.AlertRuleChannels.SingleAsync(c => c.AlertRuleId == ruleId)).Id;

        await using var other = new NocturneDbContext(_options) { TenantId = OtherTenant };
        var otherRepo = Repository(other);

        (await repo.ChannelExistsAsync(channelId, CancellationToken.None)).Should().BeTrue();
        (await otherRepo.ChannelExistsAsync(channelId, CancellationToken.None)).Should().BeFalse();
        (await repo.ChannelExistsAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ChannelSecretsListsOnlyChannelsWithASecretUnderTheirRuleNameInChannelOrder()
    {
        await using var db = Db();
        var repo = Repository(db);
        var zed = await repo.CreateAsync(
            new NewAlertRule(Low() with { Name = "Zed" },
            [
                new AlertRuleChannelWrite(ChannelType.Webhook, "https://z2.example", Secret: "cipher-z2"),
                new AlertRuleChannelWrite(ChannelType.InApp, "member"),
                new AlertRuleChannelWrite(ChannelType.Webhook, "https://z1.example", "Zed hook", Secret: "cipher-z1"),
            ]),
            CancellationToken.None);
        var alpha = await repo.CreateAsync(
            new NewAlertRule(Low() with { Name = "Alpha" },
                [new AlertRuleChannelWrite(ChannelType.Webhook, "https://a.example", Secret: "cipher-a")]),
            CancellationToken.None);
        await repo.CreateAsync(
            new NewAlertRule(Low() with { Name = "Plain" }, [new AlertRuleChannelWrite(ChannelType.Webhook, "https://p.example")]),
            CancellationToken.None);
        await using var other = new NocturneDbContext(_options) { TenantId = OtherTenant };
        await Repository(other).CreateAsync(
            new NewAlertRule(Low() with { Name = "Elsewhere" },
                [new AlertRuleChannelWrite(ChannelType.Webhook, "https://e.example", Secret: "cipher-e")]),
            CancellationToken.None);

        var secrets = await repo.GetChannelSecretsAsync(CancellationToken.None);

        secrets.Should().Equal(
            new AlertChannelSecret(alpha, "Alpha", "https://a.example", null, "cipher-a"),
            new AlertChannelSecret(zed, "Zed", "https://z2.example", null, "cipher-z2"),
            new AlertChannelSecret(zed, "Zed", "https://z1.example", "Zed hook", "cipher-z1"));
    }

    [Fact]
    public async Task CreateMissingByNameMakesOnlyTheRulesTheTenantLacks()
    {
        await using var db = Db();
        var repo = Repository(db);
        var existing = await repo.CreateAsync(new NewAlertRule(Low() with { Name = "Urgent low" }, []), CancellationToken.None);

        var seeded = await repo.CreateMissingByNameAsync(
            [
                new NewAlertRule(Low() with { Name = "Urgent low" }, []),
                new NewAlertRule(Low() with { Name = "High" }, [new AlertRuleChannelWrite(ChannelType.InApp, "owner")]),
            ],
            CancellationToken.None);

        seeded.Created.Should().Be(1);
        seeded.RuleIdsByName["Urgent low"].Should().Be(existing);
        await using var check = Db();
        (await check.AlertRules.Include(r => r.Channels).SingleAsync(r => r.Id == seeded.RuleIdsByName["High"]))
            .Channels.Should().ContainSingle().Which.Destination.Should().Be("owner");
    }

    [Fact]
    public async Task OverAUnitAWriteIsStagedUntilTheOwnerSaves()
    {
        await using var seed = Db();
        var id = await Repository(seed).CreateAsync(new NewAlertRule(Low(), []), CancellationToken.None);

        await using var unit = await DbUnitOfWork.OpenAsync(new TestNocturneDbContextFactory(seed));
        var repo = new AlertRuleRepository(unit, _clock);
        var created = await repo.CreateAsync(new NewAlertRule(Low() with { Name = "Staged" }, []), CancellationToken.None);
        (await repo.UpdateAsync(id, Low() with { Name = "Renamed" }, CancellationToken.None)).Should().BeTrue();
        await repo.ReplaceChannelsAsync(created, [new AlertRuleChannelWrite(ChannelType.InApp, "on-staged")], null, CancellationToken.None);

        await using (var before = Db())
        {
            (await before.AlertRules.AnyAsync(r => r.Id == created)).Should().BeFalse();
            (await before.AlertRules.SingleAsync(r => r.Id == id)).Name.Should().Be("Low");
        }

        await unit.Context.SaveChangesAsync();

        await using var after = Db();
        (await after.AlertRules.SingleAsync(r => r.Id == id)).Name.Should().Be("Renamed");
        (await after.AlertRules.Include(r => r.Channels).SingleAsync(r => r.Id == created))
            .Channels.Should().ContainSingle().Which.Destination.Should().Be("on-staged");
    }

    [Fact]
    public async Task OverAUnitAReadTracksItsRowsSoAWriteEditsThemInPlace()
    {
        await using var seed = Db();
        var id = await Repository(seed).CreateAsync(
            new NewAlertRule(Low(), [new AlertRuleChannelWrite(ChannelType.InApp, "m")], ManagedBy: "tracker:1"), CancellationToken.None);

        await using var unit = await DbUnitOfWork.OpenAsync(new TestNocturneDbContextFactory(seed));
        var repo = new AlertRuleRepository(unit, _clock);
        var managed = await repo.GetManagedRulesAsync("tracker:1", CancellationToken.None);
        await repo.UpdateAsync(id, AlertRuleWrite.Of(managed[id]) with { Name = "Renamed" }, CancellationToken.None);

        var entry = unit.Context.ChangeTracker.Entries<AlertRuleEntity>().Should().ContainSingle().Subject;
        entry.State.Should().Be(EntityState.Modified);
        entry.Property(r => r.Name).IsModified.Should().BeTrue();
        entry.Property(r => r.Description).IsModified.Should().BeFalse("a value the write did not change is not written over a concurrent edit");
        entry.Property(r => r.ConditionParams).IsModified.Should().BeFalse();
    }

    [Fact]
    public async Task SetScopeClassNeedsAFactoryToPinTheTenant()
    {
        await using var seed = Db();
        await using var unit = await DbUnitOfWork.OpenAsync(new TestNocturneDbContextFactory(seed));

        var act = () => new AlertRuleRepository(unit).SetScopeClassAsync(Tenant, RuleScopeClass.Low, [Guid.NewGuid()], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
