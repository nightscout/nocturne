using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.SetupHub;
using Nocturne.API.Services.SetupHub.Items;
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
            _ruleSync.Object);
    }

    private Task<DeviceSetup> Setup() => _service.GetAsync(Owner, CancellationToken.None);

    private static DeviceSlot Slot(DeviceSetup setup, DeviceCategory category) =>
        setup.Devices.Single(s => s.Category == category);

    private void Connector(string name) =>
        _db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity { Id = Guid.CreateVersion7(), ConnectorName = name });

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

    private Task<bool> DevicesItemWorks() => new DevicesItem(_db).WorksAsync(CancellationToken.None);

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
    public async Task ACareLinkConnector_PointsAtAMedtronicSensorAndPump()
    {
        Connector("CareLink");
        await _db.SaveChangesAsync();

        var setup = await Setup();

        Slot(setup, DeviceCategory.CGM).Guess!.Manufacturer.Should().Be("Medtronic");
        Slot(setup, DeviceCategory.InsulinPump).Guess!.Id.Should().Be("medtronic-780g");
    }

    [Fact]
    public async Task LoopDeviceStatus_NamesThePumpModel_AndTheAlgorithm()
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
    public async Task ConfirmingAGuess_RecordsTheCatalogueDevice_AndStopsGuessingForIt()
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
        groups[0].Formulations.Should().Contain(f => f.Id == "novorapid")
            .And.OnlyContain(f => f.Category == InsulinCategory.RapidActing && f.Concentration >= 100)
            .And.NotContain(f => f.Id == "custom");
        groups[1].Formulations.Select(f => f.Id).Should().Contain(["lantus", "tresiba"]);
    }

    [Fact]
    public async Task PickingInsulins_RecordsCatalogueProfiles_WithPumpAwareRolesAndPrimaries()
    {
        await _service.ConfirmDeviceAsync("tandem-tslim-x2", null, CancellationToken.None);

        await _service.AddInsulinAsync("fiasp", CancellationToken.None);
        await _service.AddInsulinAsync("fiasp", CancellationToken.None);
        await _service.AddInsulinAsync("tresiba", CancellationToken.None);

        var insulins = (await Setup()).Insulins;
        insulins.Should().HaveCount(2, "picking the same insulin twice records it once");
        insulins.Single(i => i.FormulationId == "fiasp").Should().BeEquivalentTo(new
        {
            Role = InsulinRole.Both, IsPrimary = true, Dia = 3.5, Peak = 55, Curve = "ultra-rapid", IsCurrent = true,
        });
        insulins.Single(i => i.FormulationId == "tresiba").Should().BeEquivalentTo(new
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
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Trackers_AreOfferedFromTheRecordedDevices_WithCatalogueWearTimes()
    {
        (await Setup()).Trackers.Should().BeEmpty("nothing is recorded yet");

        await _service.ConfirmDeviceAsync("dexcom-g7", null, CancellationToken.None);
        await _service.ConfirmDeviceAsync("omnipod-dash", null, CancellationToken.None);

        (await Setup()).Trackers.Should().Equal(
            new TrackerOffer(TrackerOfferKind.Sensor, "Dexcom G7", 240, null),
            new TrackerOffer(TrackerOfferKind.Pod, "Omnipod DASH", 72, null));
    }

    [Fact]
    public async Task ATubedPump_IsOfferedAnInfusionSetAndAReservoirTracker()
    {
        await _service.ConfirmDeviceAsync("tandem-mobi", null, CancellationToken.None);

        (await Setup()).Trackers.Should().Equal(
            new TrackerOffer(TrackerOfferKind.InfusionSet, "t:slim Mobi", 72, null),
            new TrackerOffer(TrackerOfferKind.Reservoir, "t:slim Mobi", 72, null));
    }

    [Fact]
    public async Task TurningATrackerOn_CreatesItWithTheWearTimeAndChangeTriggers_Once()
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
        (await Setup()).Trackers.Single().DefinitionId.Should().Be(definition.Id);
        _ruleSync.Verify(r => r.SyncDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task APumpAlone_DoesNotMakeItDone()
    {
        await _service.ConfirmDeviceAsync("omnipod-5", null, CancellationToken.None);
        await _service.AddInsulinAsync("humalog", CancellationToken.None);

        (await DevicesItemWorks()).Should().BeFalse();
    }
}
