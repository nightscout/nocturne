using FluentAssertions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Migration;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.SetupHub;

[Trait("Category", "Unit")]
public class AlertSetupServiceTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid CallerId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly NightscoutAlarmSettings NightscoutSettings = new(
        BgLow: 54, BgTargetBottom: 80, BgTargetTop: 200, BgHigh: 300,
        AlarmUrgentLow: true, AlarmLow: true, AlarmHigh: false, AlarmUrgentHigh: true, AlarmTimeagoWarn: true);

    private readonly SqliteTestDatabase _database;
    private readonly NocturneDbContext _db;
    private readonly TestNocturneDbContextFactory _factory;

    public AlertSetupServiceTests()
    {
        _database = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _db = _database.CreateContext();
        _factory = new TestNocturneDbContextFactory(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private AlertSetupService Service(string? ownerAppearanceUnits = null)
    {
        var tenants = new TenantRepository(_factory);
        var appearance = new Mock<ITenantAppearanceReader>();
        appearance
            .Setup(a => a.GetTenantAppearanceAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences { GlucoseUnits = ownerAppearanceUnits });
        var setup = new Mock<ITenantService>();
        setup
            .Setup(t => t.GetDefaultGlucoseUnitsAsync(TenantId, It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, CancellationToken ct) => (await tenants.GetAsync(id, ct))?.DefaultGlucoseUnits);
        var units = new TenantGlucoseUnits(MockTenantAccessor.Create(TenantId).Object, appearance.Object, setup.Object);

        return new(_factory, tenants, units, new TenantMembershipRepository(_factory), new SubjectRepository(_factory), new AlertRuleRepository(_factory), null!, null!, null!, null!, null!, TimeProvider.System);
    }

    private async Task MigrationRun(
        string state, NightscoutAlarmSettings? settings, DateTime? startedAt = null, string? rawSettings = null)
    {
        var source = new MigrationSourceEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, SourceIdentifier = $"https://ns-{Guid.NewGuid()}.example",
        };
        var started = startedAt ?? new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        _db.MigrationSources.Add(source);
        _db.MigrationRuns.Add(new MigrationRunEntity
        {
            Id = Guid.CreateVersion7(), SourceId = source.Id, TenantId = TenantId,
            CreatedAt = started, StartedAt = started, CompletedAt = started.AddMinutes(5), State = state,
            SourceAlarmSettings = rawSettings ?? settings?.ToJson(),
        });
        await _db.SaveChangesAsync();
    }

    private async Task SavedStarter(StarterAlertKind kind, decimal threshold, bool enabled)
    {
        _db.AlertRules.Add(new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Name = kind.ToString(), StarterKind = kind,
            ConditionParams = $$"""{"direction":"below","value":{{threshold}}}""", IsEnabled = enabled,
        });
        await _db.SaveChangesAsync();
    }

    private static StarterAlertRule Rule(AlertSetupStatus status, StarterAlertKind kind) =>
        status.Rules.Single(r => r.Kind == kind);

    [Fact]
    public void WithNoSettingsEveryRuleStartsAtItsDefaultAndOn()
    {
        var points = AlertSetupService.StartingPoints(null);

        points.Should().BeEquivalentTo(new Dictionary<StarterAlertKind, AlertSetupService.StartingPoint>
        {
            [StarterAlertKind.UrgentLow] = new(55, true, false),
            [StarterAlertKind.Low] = new(70, true, false),
            [StarterAlertKind.High] = new(250, true, false),
            [StarterAlertKind.NoReadings] = new(0, true, false),
        });
    }

    [Fact]
    public void EachKindTakesItsOwnThresholdAndSwitchFromTheSettings()
    {
        var points = AlertSetupService.StartingPoints(NightscoutSettings);

        points[StarterAlertKind.UrgentLow].Should().Be(new AlertSetupService.StartingPoint(54, true, true));
        points[StarterAlertKind.Low].Should().Be(new AlertSetupService.StartingPoint(80, true, true));
        points[StarterAlertKind.High].Should().Be(new AlertSetupService.StartingPoint(200, false, true));
        points[StarterAlertKind.NoReadings].Should().Be(new AlertSetupService.StartingPoint(0, true, true));
    }

    [Theory]
    [InlineData(39.9)]
    [InlineData(400.1)]
    [InlineData(0)]
    [InlineData(-70)]
    public void AThresholdOutsideWhatARuleAcceptsFallsBackToTheDefault(double mgdl)
    {
        var points = AlertSetupService.StartingPoints(
            new NightscoutAlarmSettings((decimal)mgdl, (decimal)mgdl, (decimal)mgdl, null, null, null, null, null, null));

        points[StarterAlertKind.UrgentLow].Mgdl.Should().Be(55);
        points[StarterAlertKind.Low].Mgdl.Should().Be(70);
        points[StarterAlertKind.High].Mgdl.Should().Be(250);
        points.Values.Should().OnlyContain(p => !p.FromNightscout);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(400)]
    public void AThresholdAtEitherEdgeOfTheAcceptedRangeIsKept(double mgdl)
    {
        var points = AlertSetupService.StartingPoints(
            new NightscoutAlarmSettings((decimal)mgdl, null, null, null, null, null, null, null, null));

        points[StarterAlertKind.UrgentLow].Should().Be(new AlertSetupService.StartingPoint((decimal)mgdl, true, true));
    }

    [Fact]
    public void AnAbsentThresholdKeepsTheDefaultWhileItsSwitchIsStillRead()
    {
        var points = AlertSetupService.StartingPoints(
            new NightscoutAlarmSettings(null, null, null, null, null, false, null, null, null));

        points[StarterAlertKind.Low].Should().Be(new AlertSetupService.StartingPoint(70, false, true));
        points[StarterAlertKind.UrgentLow].Should().Be(new AlertSetupService.StartingPoint(55, true, false));
    }

    [Fact]
    public void TheUrgentHighSettingsAreNotUsed()
    {
        var points = AlertSetupService.StartingPoints(
            new NightscoutAlarmSettings(null, null, null, 300, null, null, null, false, null));

        points.Values.Should().OnlyContain(p => !p.FromNightscout);
        points[StarterAlertKind.High].Mgdl.Should().Be(250);
    }

    [Fact]
    public async Task ACompletedRunsSettingsAreTheStartingPointsBeforeAnythingIsSaved()
    {
        await MigrationRun("Completed", NightscoutSettings);

        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        status.ThresholdsFrom.Should().Be(AlertThresholdSource.Nightscout);
        status.Saved.Should().BeFalse();
        status.GlucoseUnits.Should().Be("mg/dl");
        Rule(status, StarterAlertKind.UrgentLow).Should().BeEquivalentTo(new { Threshold = 54m, IsEnabled = true });
        Rule(status, StarterAlertKind.Low).Should().BeEquivalentTo(new { Threshold = 80m, IsEnabled = true });
        Rule(status, StarterAlertKind.High).Should().BeEquivalentTo(new { Threshold = 200m, IsEnabled = false });
        Rule(status, StarterAlertKind.NoReadings).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task WithNoRunTheStartingPointsAreTheDefaults()
    {
        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        status.ThresholdsFrom.Should().Be(AlertThresholdSource.Defaults);
        status.Rules.Select(r => r.Threshold).Should().Equal(55m, 70m, 250m, null);
        status.Rules.Should().OnlyContain(r => r.IsEnabled);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Interrupted")]
    [InlineData("Running")]
    public async Task ARunThatDidNotCompleteIsNotUsed(string state)
    {
        await MigrationRun(state, NightscoutSettings);

        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        status.ThresholdsFrom.Should().Be(AlertThresholdSource.Defaults);
        Rule(status, StarterAlertKind.Low).Threshold.Should().Be(70m);
    }

    [Fact]
    public async Task ACompletedRunThatReadNoSettingsLeavesTheDefaults()
    {
        await MigrationRun("Completed", null);

        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        status.ThresholdsFrom.Should().Be(AlertThresholdSource.Defaults);
        Rule(status, StarterAlertKind.High).Threshold.Should().Be(250m);
    }

    [Fact]
    public async Task ACompletedRunWhoseSettingsStateNothingUsableLeavesTheDefaults()
    {
        await MigrationRun("Completed", NightscoutAlarmSettings.Parse("""{"settings":{}}"""));

        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        status.ThresholdsFrom.Should().Be(AlertThresholdSource.Defaults);
    }

    [Fact]
    public async Task StoredSettingsThatCannotBeReadLeaveTheDefaults()
    {
        await MigrationRun("Completed", null, rawSettings: "[1]");

        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        status.ThresholdsFrom.Should().Be(AlertThresholdSource.Defaults);
    }

    [Fact]
    public async Task TheNewestCompletedRunWins()
    {
        await MigrationRun("Completed", NightscoutSettings with { BgTargetBottom = 90 }, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        await MigrationRun("Completed", NightscoutSettings with { BgTargetBottom = 75 }, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));

        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        Rule(status, StarterAlertKind.Low).Threshold.Should().Be(75m);
    }

    [Fact]
    public async Task ASavedStarterRuleWinsOverTheMigrationSettings()
    {
        await MigrationRun("Completed", NightscoutSettings);
        await SavedStarter(StarterAlertKind.Low, 65, enabled: true);

        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        status.Saved.Should().BeTrue();
        status.ThresholdsFrom.Should().Be(AlertThresholdSource.Defaults);
        Rule(status, StarterAlertKind.Low).Threshold.Should().Be(65m);
    }

    [Fact]
    public async Task OnceAnyStarterRuleIsSavedTheOthersAreNoLongerSeededFromTheMigration()
    {
        await MigrationRun("Completed", NightscoutSettings);
        await SavedStarter(StarterAlertKind.Low, 65, enabled: true);

        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        Rule(status, StarterAlertKind.High).Should().BeEquivalentTo(new { Threshold = 250m, IsEnabled = true });
    }

    [Fact]
    public async Task TheMigrationSettingsAreShownInTheCallersUnits()
    {
        await MigrationRun("Completed", NightscoutSettings);
        _db.Tenants.Single(t => t.Id == TenantId).DefaultGlucoseUnits = "mmol";
        await _db.SaveChangesAsync();

        var status = await Service().GetAsync(CallerId, CancellationToken.None);

        status.GlucoseUnits.Should().Be("mmol");
        Rule(status, StarterAlertKind.Low).Threshold.Should().Be(4.4m);
    }

    [Fact]
    public async Task AMemberWithNoChoiceReadsTheOwnersAppearanceBeforeTheSetupDefault()
    {
        await MigrationRun("Completed", NightscoutSettings);
        _db.Tenants.Single(t => t.Id == TenantId).DefaultGlucoseUnits = "mg/dl";
        await _db.SaveChangesAsync();

        var status = await Service(ownerAppearanceUnits: "mmol").GetAsync(CallerId, CancellationToken.None);

        status.GlucoseUnits.Should().Be("mmol");
        Rule(status, StarterAlertKind.Low).Threshold.Should().Be(4.4m);
    }

    [Fact]
    public async Task AMembersOwnChoiceWinsOverTheTenantsUnits()
    {
        await MigrationRun("Completed", NightscoutSettings);
        _db.Subjects.Add(new SubjectEntity
        {
            Id = CallerId, Name = "caller",
            Preferences = new UserDisplayPreferences { GlucoseUnits = "mg/dl" }.Serialize(),
        });
        await _db.SaveChangesAsync();

        var status = await Service(ownerAppearanceUnits: "mmol").GetAsync(CallerId, CancellationToken.None);

        status.GlucoseUnits.Should().Be("mg/dl");
    }
}
