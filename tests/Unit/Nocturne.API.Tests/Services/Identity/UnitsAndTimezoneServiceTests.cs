using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Migration;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// One onboarding answer seeds the owner's display units, the tenant default a new member reads,
/// and the patient's timezone; the Nightscout pre-fill reports its profile's units as they are.
/// </summary>
[Trait("Category", "Unit")]
public class UnitsAndTimezoneServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OwnerId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid MemberId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private const string NightscoutUrl = "https://ns.example.com";

    private readonly DbContextOptions<NocturneDbContext> _options = new DbContextOptionsBuilder<NocturneDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options;

    private readonly Mock<IPatientRecordRepository> _records = new();
    private readonly Mock<IConnectorConfigurationService> _connectors = new();
    private readonly Mock<IMigrationJobService> _migrations = new(MockBehavior.Strict);
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ITenantService> _tenants = new();
    private string? _tenantUnits;
    private PatientRecord? _record;

    public UnitsAndTimezoneServiceTests()
    {
        using (var seed = Context())
        {
            seed.Subjects.Add(new SubjectEntity
            {
                Id = OwnerId,
                Name = "Owner",
                Preferences = new UserDisplayPreferences { TimeFormat = "24" }.Serialize(),
            });
            seed.Subjects.Add(new SubjectEntity { Id = MemberId, Name = "Member" });
            seed.SaveChanges();
        }

        _records.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _record);
        _records.Setup(r => r.GetOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _record ??= new PatientRecord());
        _records.Setup(r => r.UpdateAsync(It.IsAny<PatientRecord>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientRecord record, WriteOrigin _, CancellationToken _) => _record = record);
        _tenants.Setup(t => t.GetDefaultGlucoseUnitsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _tenantUnits);
        _tenants.Setup(t => t.SetDefaultGlucoseUnitsAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, string units, CancellationToken _) => _tenantUnits = units)
            .Returns(Task.CompletedTask);
        _connectors.Setup(c => c.GetConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectorConfigurationResponse?)null);
    }

    [Fact]
    public async Task SetAsync_SeedsTheOwnersUnitsTheTenantDefaultAndThePatientsTimezone()
    {
        _record = new PatientRecord { PreferredName = "Sam" };

        var result = await Service().SetAsync(TenantId, OwnerId, "mmol", "Australia/Sydney");

        result.Should().Be(new UnitsAndTimezoneDto("mmol", "Australia/Sydney"));

        await using var db = Context();
        var owner = UserDisplayPreferences.Deserialize((await db.Subjects.SingleAsync(s => s.Id == OwnerId)).Preferences);
        owner.GlucoseUnits.Should().Be("mmol");
        owner.TimeFormat.Should().Be("24");

        _tenantUnits.Should().Be("mmol");
        (await UiSettings(db).GetSettingsAsync())!.Features.Display.Units.Should().Be("mg/dl");

        _record!.Timezone.Should().Be("Australia/Sydney");
        _record.PreferredName.Should().Be("Sam");
    }

    [Fact]
    public async Task SetAsync_LeavesOtherMembersOwnPreferencesAlone()
    {
        await Service().SetAsync(TenantId, OwnerId, "mmol", "Europe/London");

        await using var db = Context();
        (await db.Subjects.SingleAsync(s => s.Id == MemberId)).Preferences.Should().BeNull();
    }

    // v1/v3 status reads the tenant default, and is cached per tenant.
    [Fact]
    public async Task SetAsync_DropsTheTenantsCachedStatus()
    {
        await Service().SetAsync(TenantId, OwnerId, "mmol", "Europe/London");

        _cache.Verify(c => c.RemoveByPatternAsync($"status:system:{TenantId}*", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("en-AU", "mmol")]
    [InlineData("en-GB", "mmol")]
    [InlineData("en-US", "mg/dl")]
    [InlineData("de-DE", "mg/dl")]
    public async Task GetAsync_DefaultsTheUnitsFromTheLocale(string locale, string expected)
    {
        var result = await Service().GetAsync(MemberId, locale, readNightscout: false);

        result.Should().Be(new UnitsAndTimezoneDto(expected, null));
    }

    [Fact]
    public async Task GetAsync_PrefersTheOwnersSavedChoiceToTheLocale()
    {
        await Service().SetAsync(TenantId, OwnerId, "mmol", "America/Chicago");

        var result = await Service().GetAsync(OwnerId, "en-US", readNightscout: false);

        result.Should().Be(new UnitsAndTimezoneDto("mmol", "America/Chicago"));
    }

    [Fact]
    public async Task GetAsync_PreFillsFromTheSavedNightscout()
    {
        await Service().SetAsync(TenantId, OwnerId, "mg/dl", "America/Chicago");
        SaveNightscout();
        var nightscout = new NightscoutDisplaySettings("mmol", "mmol", "Europe/Dublin");
        _migrations.Setup(m => m.ReadDisplaySettingsAsync(NightscoutUrl, "secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(nightscout);

        var result = await Service().GetAsync(OwnerId, "en-US", readNightscout: true);

        result.Should().Be(new UnitsAndTimezoneDto("mmol", "Europe/Dublin", nightscout));
    }

    // A profile written in other units than the ones pre-selected is reported as it stands, so the
    // step can say so; nothing reconciles the two.
    [Fact]
    public async Task GetAsync_ReportsAProfileInOtherUnitsThanTheDisplayUnits()
    {
        SaveNightscout();
        _migrations.Setup(m => m.ReadDisplaySettingsAsync(NightscoutUrl, "secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NightscoutDisplaySettings("mg/dl", "mmol", null));

        var result = await Service().GetAsync(MemberId, "en-GB", readNightscout: true);

        result.GlucoseUnits.Should().Be("mg/dl");
        result.Nightscout!.ProfileUnits.Should().Be("mmol");
    }

    [Fact]
    public async Task GetAsync_SaysSoWhenTheNightscoutCannotBeRead()
    {
        SaveNightscout();
        _migrations.Setup(m => m.ReadDisplaySettingsAsync(NightscoutUrl, "secret", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MigrationSourceException("unreachable", MigrationFailureCause.Unreachable));

        var result = await Service().GetAsync(MemberId, "en-GB", readNightscout: true);

        result.Should().Be(new UnitsAndTimezoneDto("mmol", null, null, NightscoutUnavailable: true));
    }

    [Fact]
    public async Task GetAsync_ReadsNoNightscoutWhenNoneIsSaved()
    {
        var result = await Service().GetAsync(MemberId, "en-GB", readNightscout: true);

        result.Should().Be(new UnitsAndTimezoneDto("mmol", null));
        _migrations.VerifyNoOtherCalls();
    }

    private void SaveNightscout()
    {
        _connectors.Setup(c => c.GetConfigurationAsync(UnitsAndTimezoneService.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = UnitsAndTimezoneService.NightscoutConnector,
                Configuration = JsonDocument.Parse($$"""{"url":"{{NightscoutUrl}}"}"""),
            });
        _connectors.Setup(c => c.GetSecretsAsync(UnitsAndTimezoneService.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["apiSecret"] = "secret" });
    }

    private UnitsAndTimezoneService Service()
    {
        var db = Context();
        return new UnitsAndTimezoneService(
            db, _tenants.Object, _records.Object, _connectors.Object, _migrations.Object, _cache.Object,
            NullLogger<UnitsAndTimezoneService>.Instance);
    }

    private NocturneDbContext Context() => new(_options) { TenantId = TenantId };

    private static UISettingsService UiSettings(NocturneDbContext db) =>
        new(db, NullLogger<UISettingsService>.Instance);
}
