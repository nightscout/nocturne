using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.SetupHub;

public class DeviceSetupServiceTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private const string Owner = "owner-subject";

    private readonly NocturneDbContext _db;
    private readonly DeviceSetupService _service;
    private readonly Mock<IDeviceReattributionService> _reattribution = new();
    private readonly Mock<ITrackerAlertRuleSyncService> _ruleSync = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private static readonly InsulinActionTime Profile5h = new(InsulinActionTimeSource.Profile, 5, null);

    public DeviceSetupServiceTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext($"device_setup_{Guid.NewGuid()}");
        _db.TenantId = TenantId;
        _db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "own", DisplayName = "Own" });
        _db.SaveChanges();

        var factory = new TestTenantDbContextFactory(_db);
        _service = new DeviceSetupService(
            _db,
            new PatientDeviceRepository(factory, NullLogger<PatientDeviceRepository>.Instance),
            new PatientInsulinRepository(factory, NullLogger<PatientInsulinRepository>.Instance),
            _reattribution.Object,
            new TrackerRepository(_db),
            _ruleSync.Object,
            _therapySettings.Object);
        _therapySettings.Setup(t => t.GetActionTimeAsync(It.IsAny<long>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile5h);
        _therapySettings.Setup(t => t.GetTimezoneAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync("Pacific/Auckland");
    }

    private Task<DeviceSetup> Setup() => _service.GetAsync(Owner, CancellationToken.None);

    private static DeviceSlot Slot(DeviceSetup setup, DeviceCategory category) =>
        setup.Devices.Single(s => s.Category == category);

    private void Connector(string name, string configuration = "{}") =>
        _db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.CreateVersion7(), ConnectorName = name, ConfigurationJson = configuration,
        });

    private void Reading(string device, DateTime? at = null) =>
        _db.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = at ?? DateTime.UtcNow, Mgdl = 110, Device = device,
        });

    private void PumpStatus(string manufacturer, string model) =>
        _db.PumpSnapshots.Add(new PumpSnapshotEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = DateTime.UtcNow, Manufacturer = manufacturer, Model = model,
        });

    private void LoopStatus(AidAlgorithm algorithm) =>
        _db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = DateTime.UtcNow, AidAlgorithm = algorithm.ToString(),
        });

    private TrackerDefinitionEntity ExistingTracker(TrackerCategory category, params string[] triggers)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.CreateVersion7(), UserId = Owner, Name = "Mine", Category = category,
            TriggerEventTypes = JsonSerializer.Serialize(triggers), LifespanHours = 72,
        };
        _db.TrackerDefinitions.Add(definition);
        _db.SaveChanges();
        return definition;
    }

    private Task<bool> DevicesItemWorks() =>
        SetupHubServiceTests.DevicesItemOver(_db).WorksAsync(CancellationToken.None);

    [Fact]
    public async Task NothingConnected_GuessesNothing_ButOffersTheCatalogueToPickFrom()
    {
        var setup = await Setup();

        setup.Devices.Select(s => s.Category).Should().Equal(DeviceCategory.CGM, DeviceCategory.InsulinPump);
        setup.Devices.Should().OnlyContain(s => s.Guess == null && s.Recorded == null && s.Evidence.Count == 0);
        Slot(setup, DeviceCategory.CGM).Choices.Should().Contain(e => e.Id == "dexcom-g7")
            .And.OnlyContain(e => e.Category == DeviceCategory.CGM && e.Cgm != null);
        Slot(setup, DeviceCategory.InsulinPump).Choices.Should().Contain(e => e.Id == "omnipod-dash")
            .And.OnlyContain(e => e.Pump != null);
        setup.Algorithm.Should().BeNull();
    }

    [Fact]
    public async Task AConnector_NamesTheMaker_SoTheGuessIsItsFirstModel_Unconfirmed()
    {
        Connector("Dexcom");
        await _db.SaveChangesAsync();

        var cgm = Slot(await Setup(), DeviceCategory.CGM);

        cgm.Guess!.Id.Should().Be("dexcom-g7");
        cgm.ModelKnown.Should().BeFalse();
        cgm.Evidence.Should().Equal(new DeviceEvidence(DeviceEvidenceSource.Connector, "dexcom"));
    }

    [Fact]
    public async Task ADisabledConnector_IsNotEvidence()
    {
        Connector("Dexcom", """{"enabled":false}""");
        await _db.SaveChangesAsync();

        Slot(await Setup(), DeviceCategory.CGM).Guess.Should().BeNull();
    }

    [Theory]
    [InlineData("CareLink", "medtronic-780g")]
    [InlineData("MyLife", "ypsopump")]
    public async Task APumpMakersConnector_PointsAtItsPump_AsABrandOnlyGuess(string connector, string expected)
    {
        Connector(connector);
        await _db.SaveChangesAsync();

        var pump = Slot(await Setup(), DeviceCategory.InsulinPump);

        pump.Guess!.Id.Should().Be(expected);
        pump.ModelKnown.Should().BeFalse();
    }

    [Fact]
    public async Task LoopDeviceStatus_NamesThePumpModel_AndSuggestsTheAlgorithm()
    {
        PumpStatus("Insulet", "Dash");
        LoopStatus(AidAlgorithm.Loop);
        Connector("Dexcom");
        await _db.SaveChangesAsync();

        var setup = await Setup();

        var pump = Slot(setup, DeviceCategory.InsulinPump);
        pump.Guess!.Id.Should().Be("omnipod-dash");
        pump.ModelKnown.Should().BeTrue();
        pump.Evidence.Should().Equal(new DeviceEvidence(DeviceEvidenceSource.PumpStatus, "Insulet Dash"));
        setup.Algorithm.Should().BeEquivalentTo(new AlgorithmGuess(
            AidAlgorithm.Loop, [new DeviceEvidence(DeviceEvidenceSource.AlgorithmStatus, "Loop")]));
        Slot(setup, DeviceCategory.CGM).Guess!.Id.Should().Be("dexcom-g7");
    }

    [Fact]
    public async Task AapsReadingLabel_NamesTheSensorModel_OverTheConnectorsMaker()
    {
        Connector("Dexcom");
        Reading("AndroidAPS-DexcomG6");
        LoopStatus(AidAlgorithm.AndroidAps);
        await _db.SaveChangesAsync();

        var setup = await Setup();

        var cgm = Slot(setup, DeviceCategory.CGM);
        cgm.Guess!.Id.Should().Be("dexcom-g6");
        cgm.ModelKnown.Should().BeTrue();
        cgm.Evidence.Should().Equal(
            new DeviceEvidence(DeviceEvidenceSource.Readings, "AndroidAPS-DexcomG6"),
            new DeviceEvidence(DeviceEvidenceSource.Connector, "dexcom"));
        setup.Algorithm!.Algorithm.Should().Be(AidAlgorithm.AndroidAps);
    }

    [Theory]
    [InlineData("xDrip-LibreReceiver FreeStyle Libre 2+", "libre-2-plus")]
    [InlineData("xDrip-DexcomG7", "dexcom-g7")]
    [InlineData("Trio G7CGMManager", "dexcom-g7")]
    public async Task XDripAndTrioReadingLabels_NameTheSensor(string label, string expected)
    {
        Reading(label);
        await _db.SaveChangesAsync();

        Slot(await Setup(), DeviceCategory.CGM).Guess!.Id.Should().Be(expected);
    }

    [Fact]
    public async Task DataOlderThanTheWindow_IsNotEvidence()
    {
        Reading("xDrip-DexcomG6", DateTime.UtcNow - DeviceSetupService.EvidenceWindow - TimeSpan.FromDays(1));
        await _db.SaveChangesAsync();

        Slot(await Setup(), DeviceCategory.CGM).Guess.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmingAGuess_RecordsTheCatalogueDevice_WithTheConfirmedAlgorithm()
    {
        PumpStatus("Insulet", "Dash");
        await _db.SaveChangesAsync();

        await _service.ConfirmDeviceAsync("omnipod-dash", AidAlgorithm.Loop, CancellationToken.None);

        var pump = Slot(await Setup(), DeviceCategory.InsulinPump);
        pump.Guess.Should().BeNull();
        pump.Recorded.Should().BeEquivalentTo(new
        {
            DeviceCategory = DeviceCategory.InsulinPump, Manufacturer = "Insulet", Model = "Omnipod DASH",
            CatalogId = "omnipod-dash", AidAlgorithm = (AidAlgorithm?)AidAlgorithm.Loop, IsCurrent = true,
        });
        _reattribution.Verify(r => r.ReattributeForDeviceAsync(
            It.Is<PatientDevice>(d => d.CatalogId == "omnipod-dash"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AGuessedAlgorithm_IsNeverRecorded_UnlessTheOwnerConfirmsIt()
    {
        PumpStatus("Insulet", "Dash");
        LoopStatus(AidAlgorithm.Loop);
        await _db.SaveChangesAsync();

        await _service.ConfirmDeviceAsync("omnipod-5", null, CancellationToken.None);

        Slot(await Setup(), DeviceCategory.InsulinPump).Recorded!.AidAlgorithm.Should().BeNull();
    }

    [Fact]
    public async Task SwappingAGuess_RecordsTheChosenModel_AndNeverAnAlgorithmOnACgm()
    {
        Connector("Dexcom");
        await _db.SaveChangesAsync();

        await _service.ConfirmDeviceAsync("dexcom-g6", AidAlgorithm.Loop, CancellationToken.None);

        var recorded = Slot(await Setup(), DeviceCategory.CGM).Recorded!;
        recorded.CatalogId.Should().Be("dexcom-g6");
        (recorded.Manufacturer, recorded.Model).Should().Be(("Dexcom", "G6"));
        recorded.AidAlgorithm.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmingANewDevice_EndsThePreviousCurrentOneOfItsCategory()
    {
        await _service.ConfirmDeviceAsync("dexcom-g6", null, CancellationToken.None);
        await _service.ConfirmDeviceAsync("dexcom-g7", null, CancellationToken.None);

        _db.ChangeTracker.Clear();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland")));
        var g6 = _db.PatientDevices.Single(d => d.CatalogId == "dexcom-g6");
        var g7 = _db.PatientDevices.Single(d => d.CatalogId == "dexcom-g7");
        (g6.IsCurrent, g6.StartDate, g6.EndDate).Should().Be((false, (DateOnly?)null, (DateOnly?)today),
            "the first device claims its history and ends on the patient's today");
        (g7.IsCurrent, g7.StartDate, g7.EndDate).Should().Be((true, (DateOnly?)today, (DateOnly?)null));
    }

    [Fact]
    public async Task TheActionTimeShown_IsTheOneTheResolverUses()
    {
        var external = new InsulinActionTime(InsulinActionTimeSource.ExternalProfile, 6, "Fiasp");
        _therapySettings.Setup(t => t.GetActionTimeAsync(It.IsAny<long>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(external);

        (await Setup()).ActionTime.Should().Be(external);
        (await _service.ActionTimeAsync(CancellationToken.None)).Should().Be(external);
    }

    [Fact]
    public async Task Confirm_RefusesWhatIsNotInTheCatalogue()
    {
        await _service.Invoking(s => s.ConfirmDeviceAsync("dexcom-g99", null, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task InsulinChoices_AreGroupedRapidAndLongActing()
    {
        var groups = (await Setup()).InsulinChoices;

        groups.Select(g => g.Group).Should().Equal(InsulinGroup.RapidActing, InsulinGroup.LongActing);
        groups[0].Choices.Select(c => c.Formulation).Should().Contain(f => f.Id == "novorapid")
            .And.OnlyContain(f => f.Category == InsulinCategory.RapidActing && f.Concentration >= 100)
            .And.NotContain(f => f.Id == "custom");
        groups[1].Choices.Select(c => c.Formulation.Id).Should().Contain(["lantus", "tresiba"]);
    }

    [Fact]
    public async Task PickingInsulins_RecordsCatalogueProfiles_WithPumpAwareRolesAndPrimaries()
    {
        await _service.ConfirmDeviceAsync("tandem-tslim-x2", null, CancellationToken.None);

        await _service.AddInsulinAsync("fiasp", CancellationToken.None);
        await _service.AddInsulinAsync("fiasp", CancellationToken.None);
        await _service.AddInsulinAsync("tresiba", CancellationToken.None);

        var setup = await Setup();
        setup.Insulins.Should().HaveCount(2, "picking the same insulin twice records it once");
        setup.Insulins.Single(i => i.FormulationId == "fiasp").Should().BeEquivalentTo(new
        {
            Role = InsulinRole.Both, IsPrimary = true, Dia = 3.5, Peak = 55, Curve = "ultra-rapid", IsCurrent = true,
        });
        setup.Insulins.Single(i => i.FormulationId == "tresiba").Should().BeEquivalentTo(new
        {
            Role = InsulinRole.Basal, IsPrimary = false, InsulinCategory = InsulinCategory.UltraLongActing,
        }, "the pump insulin already covers basal");
    }

    [Fact]
    public async Task WithoutAPump_ARapidInsulinIsForBolus_AndALongOneIsThePrimaryBasal()
    {
        await _service.AddInsulinAsync("novorapid", CancellationToken.None);
        await _service.AddInsulinAsync("lantus", CancellationToken.None);

        var insulins = (await Setup()).Insulins;
        insulins.Should().OnlyContain(i => i.IsPrimary);
        insulins.Single(i => i.FormulationId == "novorapid").Role.Should().Be(InsulinRole.Bolus);
    }

    [Fact]
    public async Task AnInsulinPickedHere_CanBeUnpicked()
    {
        await _service.AddInsulinAsync("novorapid", CancellationToken.None);
        var choice = (await Setup()).InsulinChoices[0].Choices.Single(c => c.Formulation.Id == "novorapid");
        choice.AddedHere.Should().BeTrue();

        await _service.RemoveInsulinAsync("novorapid", CancellationToken.None);

        (await Setup()).Insulins.Should().BeEmpty();
    }

    [Fact]
    public async Task AnInsulinOnRecordBefore_IsShownPicked_ButCannotBeUnpickedHere()
    {
        _db.PatientInsulins.Add(new PatientInsulinEntity
        {
            Id = Guid.CreateVersion7(), InsulinCategory = "RapidActing", Name = "NovoRapid",
            FormulationId = "novorapid", IsCurrent = true, Role = "Bolus",
        });
        _db.PatientInsulins.Add(new PatientInsulinEntity
        {
            Id = Guid.CreateVersion7(), InsulinCategory = "RapidActing", Name = "Humalog U10",
            FormulationId = "humalog-u10", IsCurrent = true, Role = "Bolus",
        });
        await _db.SaveChangesAsync();

        var setup = await Setup();
        var choice = setup.InsulinChoices[0].Choices.Single(c => c.Formulation.Id == "novorapid");
        choice.RecordedId.Should().NotBeNull();
        choice.AddedHere.Should().BeFalse();
        setup.OtherInsulins.Select(i => i.Name).Should().Equal("Humalog U10");

        await _service.Invoking(s => s.RemoveInsulinAsync("novorapid", CancellationToken.None))
            .Should().ThrowAsync<DeviceSetupConflictException>();
        (await Setup()).Insulins.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddInsulin_RefusesWhatIsNotOnTheList()
    {
        await _service.Invoking(s => s.AddInsulinAsync("humalog-u10", CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task None_IsAValidInsulinAnswer_UntilAnInsulinIsPicked()
    {
        await _service.SetTakesNoInsulinAsync(true, CancellationToken.None);
        (await Setup()).TakesNoInsulin.Should().BeTrue();

        await _service.AddInsulinAsync("humalog", CancellationToken.None);
        (await Setup()).TakesNoInsulin.Should().BeFalse();

        await _service.Invoking(s => s.SetTakesNoInsulinAsync(true, CancellationToken.None))
            .Should().ThrowAsync<DeviceSetupConflictException>();
    }

    [Fact]
    public async Task Trackers_AreOfferedFromTheRecordedDevices_WithCatalogueWearTimes()
    {
        (await Setup()).Trackers.Should().BeEmpty("nothing is recorded yet");

        await _service.ConfirmDeviceAsync("dexcom-g7", null, CancellationToken.None);
        await _service.ConfirmDeviceAsync("omnipod-dash", null, CancellationToken.None);

        (await Setup()).Trackers.Should().Equal(
            new TrackerOffer(TrackerOfferKind.Sensor, "Dexcom G7", 10, 0, TrackerOfferState.Off, null),
            new TrackerOffer(TrackerOfferKind.Pod, "Omnipod DASH", 3, 0, TrackerOfferState.Off, null));
    }

    [Fact]
    public async Task ATubedPump_IsOfferedAnInfusionSetAndAReservoirTracker()
    {
        await _service.ConfirmDeviceAsync("tandem-mobi", null, CancellationToken.None);

        (await Setup()).Trackers.Should().Equal(
            new TrackerOffer(TrackerOfferKind.InfusionSet, "t:slim Mobi", 3, 0, TrackerOfferState.Off, null),
            new TrackerOffer(TrackerOfferKind.Reservoir, "t:slim Mobi", 3, 0, TrackerOfferState.Off, null));
    }

    [Fact]
    public async Task TurningATrackerOn_CreatesItWithTheWearTimeAndChangeTriggers_Once_AndOffDeletesIt()
    {
        await _service.ConfirmDeviceAsync("libre-3", null, CancellationToken.None);

        await _service.AddTrackerAsync(TrackerOfferKind.Sensor, "Libre 3 sensor", Owner, CancellationToken.None);
        await _service.AddTrackerAsync(TrackerOfferKind.Sensor, "Libre 3 sensor", Owner, CancellationToken.None);

        var definition = _db.TrackerDefinitions.Should().ContainSingle().Subject;
        definition.Should().BeEquivalentTo(new
        {
            UserId = Owner, Name = "Libre 3 sensor", Category = TrackerCategory.Sensor,
            LifespanHours = (int?)336, Mode = TrackerMode.Duration,
        });
        JsonSerializer.Deserialize<string[]>(definition.TriggerEventTypes).Should().Equal("Sensor Start", "Sensor Change");
        (await Setup()).Trackers.Single().Should().BeEquivalentTo(new
        {
            State = TrackerOfferState.AddedHere, DefinitionId = (Guid?)definition.Id,
        });
        _ruleSync.Verify(r => r.SyncDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()), Times.Once);

        await _service.RemoveTrackerAsync(TrackerOfferKind.Sensor, Owner, CancellationToken.None);

        _db.TrackerDefinitions.Should().BeEmpty();
        (await Setup()).Trackers.Single().State.Should().Be(TrackerOfferState.Off);
        _ruleSync.Verify(r => r.DeleteRulesForDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ATrackerTheOwnerAlreadyHad_IsShownAsTracked_AndSurvivesTurningOff()
    {
        await _service.ConfirmDeviceAsync("dexcom-g7", null, CancellationToken.None);
        var mine = ExistingTracker(TrackerCategory.Sensor, "Sensor Start");

        (await Setup()).Trackers.Single().Should().BeEquivalentTo(new
        {
            State = TrackerOfferState.AlreadyTracked, DefinitionId = (Guid?)mine.Id,
        });

        await _service.Invoking(s => s.RemoveTrackerAsync(TrackerOfferKind.Sensor, Owner, CancellationToken.None))
            .Should().ThrowAsync<DeviceSetupConflictException>();
        await _service.AddTrackerAsync(TrackerOfferKind.Sensor, "Another", Owner, CancellationToken.None);

        _db.TrackerDefinitions.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    [Fact]
    public async Task ASiteTracker_CoversAPodOnlyWhenItFollowsPodChanges()
    {
        await _service.ConfirmDeviceAsync("omnipod-dash", null, CancellationToken.None);
        ExistingTracker(TrackerCategory.Cannula, "Site Change");

        (await Setup()).Trackers.Single().State.Should().Be(TrackerOfferState.Off,
            "an infusion-set tracker is not a pod tracker");

        ExistingTracker(TrackerCategory.Cannula, "Pod Change");
        (await Setup()).Trackers.Single().State.Should().Be(TrackerOfferState.AlreadyTracked);
    }

    [Fact]
    public async Task ATrackerForADeviceNotOnRecord_IsRefused()
    {
        await _service.Invoking(s => s.AddTrackerAsync(TrackerOfferKind.Pod, "Pod", Owner, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Done_NeedsTheCgmRecorded_AndTheInsulinQuestionAnswered()
    {
        await _service.SetTakesNoInsulinAsync(true, CancellationToken.None);
        (await DevicesItemWorks()).Should().BeFalse("no CGM is recorded");

        await _service.SetTakesNoInsulinAsync(false, CancellationToken.None);
        await _service.ConfirmDeviceAsync("dexcom-g7", null, CancellationToken.None);
        (await DevicesItemWorks()).Should().BeFalse("the insulin question is unanswered");

        await _service.SetTakesNoInsulinAsync(true, CancellationToken.None);
        (await DevicesItemWorks()).Should().BeTrue("none is an answer");

        await _service.SetTakesNoInsulinAsync(false, CancellationToken.None);
        await _service.AddInsulinAsync("lyumjev", CancellationToken.None);
        (await DevicesItemWorks()).Should().BeTrue("trackers are optional");
    }

    [Fact]
    public async Task Done_ReadsTheCategoryAsTheDeviceRecordDoes_SoASeededLowercaseCgmCounts()
    {
        _db.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = Guid.CreateVersion7(), DeviceCategory = "cgm", Manufacturer = "Dexcom", Model = "G7", IsCurrent = true,
        });
        _db.PatientInsulins.Add(new PatientInsulinEntity
        {
            Id = Guid.CreateVersion7(), InsulinCategory = "RapidActing", Name = "Humalog", IsCurrent = true,
        });
        await _db.SaveChangesAsync();

        (await DevicesItemWorks()).Should().BeTrue();
    }

    [Fact]
    public async Task Done_CountsOnlyCurrentDevicesAndInsulins()
    {
        _db.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = Guid.CreateVersion7(), DeviceCategory = "CGM", Manufacturer = "Dexcom", Model = "G6", IsCurrent = false,
        });
        _db.PatientInsulins.Add(new PatientInsulinEntity
        {
            Id = Guid.CreateVersion7(), InsulinCategory = "RapidActing", Name = "Humalog", IsCurrent = true,
        });
        await _db.SaveChangesAsync();
        (await DevicesItemWorks()).Should().BeFalse("the only CGM is no longer in use");

        await _service.ConfirmDeviceAsync("dexcom-g7", null, CancellationToken.None);
        var insulin = _db.PatientInsulins.Single();
        insulin.IsCurrent = false;
        await _db.SaveChangesAsync();
        (await DevicesItemWorks()).Should().BeFalse("the only insulin is no longer in use");
    }

    [Fact]
    public async Task APumpAlone_DoesNotMakeItDone()
    {
        await _service.ConfirmDeviceAsync("omnipod-5", null, CancellationToken.None);
        await _service.AddInsulinAsync("humalog", CancellationToken.None);

        (await DevicesItemWorks()).Should().BeFalse();
    }
}
