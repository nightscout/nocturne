using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// AAPS uploads its EffectiveProfileSwitch as a <c>Note</c> treatment carrying the effective
/// <c>profileJson</c> and the <c>original*</c> fields of the switch it confirms; see
/// <see cref="EffectiveProfileSwitch"/>.
/// </summary>
public class TreatmentDecomposerEffectiveProfileSwitchTests : IDisposable
{
    private const string AapsV1 = "openaps://AndroidAPS";
    private const long SwitchMills = 1700000000000;
    private const long ConfirmedMills = SwitchMills + 90_000;

    private const string EffectiveProfileJson =
        """{"units":"mmol","dia":5,"timezone":"Australia/Brisbane","sens":[{"time":"00:00","timeAsSeconds":0,"value":2.0}],"carbratio":[{"time":"00:00","timeAsSeconds":0,"value":9.1}],"basal":[{"time":"00:00","timeAsSeconds":0,"value":0.88}],"target_low":[{"time":"00:00","timeAsSeconds":0,"value":5.5}],"target_high":[{"time":"00:00","timeAsSeconds":0,"value":5.5}]}""";

    private readonly NocturneDbContext _context;
    private readonly Mock<IStateSpanService> _stateSpanService = new();
    private readonly Mock<IProfileDecomposer> _profileDecomposer = new();
    private readonly List<StateSpan> _written = [];
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerEffectiveProfileSwitchTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var dedup = new Mock<IDeduplicationService>().Object;
        var audit = new Mock<IAuditContext>().Object;
        var factory = new TestNocturneDbContextFactory(_context);

        _stateSpanService
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan span, CancellationToken _) =>
            {
                _written.Add(span);
                Store(span);
                return span;
            });
        _profileDecomposer
            .Setup(d => d.DecomposeProfileSwitchAsync(It.IsAny<Profile>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V4Models.DecompositionResult());

        var deviceService = new Mock<IDeviceService>();
        deviceService
            .Setup(s => s.ResolveAsync(It.IsAny<V4Models.DeviceCategory>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        _decomposer = new TreatmentDecomposer(
            _context,
            new BolusRepository(factory, dedup, audit, NullLogger<BolusRepository>.Instance),
            new Mock<ITempBasalRepository>().Object,
            new CarbIntakeRepository(factory, dedup, audit, NullLogger<CarbIntakeRepository>.Instance),
            new BGCheckRepository(factory, dedup, audit, NullLogger<BGCheckRepository>.Instance),
            new NoteRepository(factory, dedup, audit, NullLogger<NoteRepository>.Instance),
            new DeviceEventRepository(factory, dedup, audit, NullLogger<DeviceEventRepository>.Instance),
            new BolusCalculationRepository(factory, dedup, audit, NullLogger<BolusCalculationRepository>.Instance),
            _stateSpanService.Object,
            new Mock<ITreatmentFoodService>().Object,
            deviceService.Object,
            Mock.Of<IPatientDeviceStamper>(),
            _profileDecomposer.Object,
            new Mock<IActiveProfileResolver>().Object,
            new Mock<IPatientInsulinRepository>().Object,
            Mock.Of<IAuditContext>(),
            NullLogger<TreatmentDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Treatment EffectiveSwitch(string? enteredBy = AapsV1) => new()
    {
        Id = "eps-1",
        EventType = "Note",
        Mills = ConfirmedMills,
        EnteredBy = enteredBy,
        Notes = "Rhys (110%,1h)",
        ProfileJson = EffectiveProfileJson,
        OriginalProfileName = "Rhys",
        OriginalCustomizedName = "Rhys (110%,1h)",
        OriginalTimeshift = 3_600_000,
        OriginalPercentage = 110,
        OriginalDuration = 3_600_000,
        OriginalEnd = SwitchMills + 3_600_000,
        UtcOffset = 600,
    };

    private static Treatment TemporarySwitch() => new()
    {
        Id = "ps-1",
        EventType = "Profile Switch",
        Mills = SwitchMills,
        EnteredBy = AapsV1,
        Profile = "Rhys (110%,1h)",
        OriginalProfileName = "Rhys",
        OriginalDuration = 3_600_000,
        Percentage = 110,
        Timeshift = 3_600_000,
        Duration = 60,
        ProfileJson = EffectiveProfileJson,
        UtcOffset = 600,
    };

    /// <summary>Upserts <paramref name="span"/> on its OriginalId, as the state span service does.</summary>
    private void Store(StateSpan span)
    {
        var entity = _context.StateSpans.SingleOrDefault(s => s.OriginalId == span.OriginalId);
        if (entity is null)
        {
            entity = StateSpanMapper.ToEntity(span);
            entity.Id = Guid.CreateVersion7();
            entity.TenantId = _context.TenantId;
            _context.StateSpans.Add(entity);
        }
        else
        {
            StateSpanMapper.UpdateEntity(entity, span);
        }

        _context.SaveChanges();
        _context.Entry(entity).State = EntityState.Detached;
    }

    private Task<StateSpanEntity> StoredAsync(string originalId) =>
        _context.StateSpans.AsNoTracking().SingleAsync(s => s.OriginalId == originalId);

    private static Treatment Project(StateSpanEntity entity) =>
        LegacyTreatmentTables.StateSpanTables
            .Single(t => t.RecordType == $"{nameof(StateSpan)}.{nameof(StateSpanCategory.Profile)}")
            .Project(entity, CarbFoodIndex.Empty);

    [Fact]
    public async Task AnEffectiveProfileSwitchIsAProfileSpanNotANote()
    {
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        (await _context.Notes.CountAsync()).Should().Be(0);
        var span = _written.Should().ContainSingle().Subject;
        span.Category.Should().Be(StateSpanCategory.Profile);
        span.OriginalId.Should().Be("eps-1");
        span.StartMills.Should().Be(ConfirmedMills);
        span.EndTimestamp.Should().BeNull();
        span.Source.Should().Be(AapsV1);
        span.Metadata.TryReadString("profileName").Should().Be("Rhys (110%,1h)");
        span.Metadata.TryReadString("originalProfileName").Should().Be("Rhys");
        span.Metadata.TryReadDecimal("percentage").Should().Be(110);
        span.Metadata.TryReadDecimal("timeshift").Should().Be(1, "the resolver reads hours and AAPS uploads milliseconds");
        span.Metadata.TryReadString("profileJson").Should().Be(EffectiveProfileJson);
    }

    [Fact]
    public async Task AnEffectiveProfileSwitchWritesNoProfileSnapshot()
    {
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        _profileDecomposer.Verify(
            d => d.DecomposeProfileSwitchAsync(It.IsAny<Profile>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AV3EffectiveProfileSwitchIsAttributedToTheAppThatUploadedIt()
    {
        var treatment = EffectiveSwitch(enteredBy: null);
        treatment.AdditionalProperties = new() { ["app"] = JsonSerializer.SerializeToElement("AAPS") };

        await _decomposer.DecomposeAsync(treatment, WriteOrigin.Live);

        _written.Should().ContainSingle().Which.Source.Should().Be("AAPS");
    }

    [Fact]
    public async Task ANoteWithoutAnOriginalProfileNameStaysANote()
    {
        await _decomposer.DecomposeAsync(new Treatment
        {
            Id = "note-1",
            EventType = "Note",
            Mills = SwitchMills,
            Notes = "Rhys",
            EnteredBy = AapsV1,
        }, WriteOrigin.Live);

        _written.Should().BeEmpty();
        (await _context.Notes.SingleAsync()).Text.Should().Be("Rhys");
    }

    [Fact]
    public async Task TheBatchPathWritesTheSameProfileSpan()
    {
        await _decomposer.DecomposeBatchAsync([EffectiveSwitch()], WriteOrigin.Live);

        (await _context.Notes.CountAsync()).Should().Be(0);
        _written.Should().ContainSingle().Which.Metadata.TryReadString("originalProfileName").Should().Be("Rhys");
    }

    [Fact]
    public async Task AnEffectiveProfileSwitchReadsBackAsTheNoteItWasUploadedAs()
    {
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        var projected = Project(StateSpanMapper.ToEntity(_written.Single()));

        projected.EventType.Should().Be("Note");
        projected.Mills.Should().Be(ConfirmedMills);
        projected.Notes.Should().Be("Rhys (110%,1h)");
        projected.EnteredBy.Should().Be(AapsV1);
        projected.ProfileJson.Should().Be(EffectiveProfileJson);
        projected.OriginalProfileName.Should().Be("Rhys");
        projected.OriginalCustomizedName.Should().Be("Rhys (110%,1h)");
        projected.OriginalTimeshift.Should().Be(3_600_000);
        projected.OriginalPercentage.Should().Be(110);
        projected.OriginalDuration.Should().Be(3_600_000);
        projected.OriginalEnd.Should().Be(SwitchMills + 3_600_000);
        projected.UtcOffset.Should().Be(600);
        projected.Profile.Should().BeNull();
        projected.Percentage.Should().BeNull();
        projected.Timeshift.Should().BeNull();
    }

    [Fact]
    public async Task AnAapsProfileSwitchReadsBackWithItsCustomizedNameAndOriginalFields()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);

        var span = _written.Single();
        span.Metadata.TryReadString("originalProfileName").Should().Be("Rhys");

        var projected = Project(StateSpanMapper.ToEntity(span));
        projected.EventType.Should().Be("Profile Switch");
        projected.Profile.Should().Be("Rhys (110%,1h)");
        projected.OriginalProfileName.Should().Be("Rhys");
        projected.OriginalDuration.Should().Be(3_600_000);
        projected.Duration.Should().Be(60);
    }

    [Fact]
    public async Task AnEffectiveProfileSwitchEndsTheTemporarySwitchItConfirms()
    {
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        var profileSwitch = await StoredAsync("ps-1");
        profileSwitch.EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(ConfirmedMills).UtcDateTime);
        profileSwitch.SupersededById.Should().Be((await StoredAsync("eps-1")).Id);
        Project(profileSwitch).Duration.Should().Be(60, "the switch is still served with the duration it was uploaded with");
    }

    [Fact]
    public async Task AProfileSwitchUploadedBehindItsEffectiveSwitchEndsAtIt()
    {
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(TemporarySwitch(), WriteOrigin.Live);

        var profileSwitch = await StoredAsync("ps-1");
        profileSwitch.EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(ConfirmedMills).UtcDateTime);
        profileSwitch.SupersededById.Should().Be((await StoredAsync("eps-1")).Id);
    }

    [Fact]
    public async Task AnEffectiveProfileSwitchLeavesAnotherSourcesSwitchAlone()
    {
        var other = TemporarySwitch();
        other.EnteredBy = "careportal";
        await _decomposer.DecomposeAsync(other, WriteOrigin.Live);
        await _decomposer.DecomposeAsync(EffectiveSwitch(), WriteOrigin.Live);

        var profileSwitch = await StoredAsync("ps-1");
        profileSwitch.EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(SwitchMills + 3_600_000).UtcDateTime);
        profileSwitch.SupersededById.Should().BeNull();
    }
}
