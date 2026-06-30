using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

[Trait("Category", "Unit")]
public class TreatmentPublisherTests
{
    private readonly Mock<ITreatmentService> _mockTreatmentService;
    private readonly Mock<IBolusRepository> _mockBolusRepository;
    private readonly Mock<ICarbIntakeRepository> _mockCarbIntakeRepository;
    private readonly Mock<IBGCheckRepository> _mockBGCheckRepository;
    private readonly Mock<IBolusCalculationRepository> _mockBolusCalculationRepository;
    private readonly Mock<ITempBasalRepository> _mockTempBasalRepository;
    private readonly Mock<IBasalInjectionRepository> _mockBasalInjectionRepository;
    private readonly Mock<INoteRepository> _mockNoteRepository;
    private readonly Mock<IDeviceEventRepository> _mockDeviceEventRepository;
    private readonly Mock<IPatientInsulinRepository> _mockPatientInsulinRepository;
    private readonly Mock<IBasalRateResolver> _mockBasalRateResolver;
    private readonly Mock<ITherapySettingsResolver> _mockTherapySettingsResolver;
    private Mock<ITenantDbContextFactory> _mockContextFactory = null!;
    private readonly TreatmentPublisher _publisher;

    private static readonly Guid TestTenantId = Guid.NewGuid();

    private static NocturneDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options)
        { TenantId = TestTenantId };

    public TreatmentPublisherTests()
    {
        _mockTreatmentService = new Mock<ITreatmentService>();
        _mockBolusRepository = new Mock<IBolusRepository>();
        _mockCarbIntakeRepository = new Mock<ICarbIntakeRepository>();
        _mockBGCheckRepository = new Mock<IBGCheckRepository>();
        _mockBolusCalculationRepository = new Mock<IBolusCalculationRepository>();
        _mockTempBasalRepository = new Mock<ITempBasalRepository>();
        _mockBasalInjectionRepository = new Mock<IBasalInjectionRepository>();
        _mockNoteRepository = new Mock<INoteRepository>();
        _mockDeviceEventRepository = new Mock<IDeviceEventRepository>();
        _mockPatientInsulinRepository = new Mock<IPatientInsulinRepository>();
        _mockBasalRateResolver = new Mock<IBasalRateResolver>();
        _mockTherapySettingsResolver = new Mock<ITherapySettingsResolver>();

        // Default: resolver returns a constant 1.0 U/hr. Individual tests override as needed.
        _mockBasalRateResolver
            .Setup(r => r.BuildResolverAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Func<long, double>)(_ => 1.0));
        _mockTherapySettingsResolver
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _publisher = CreatePublisher(Mock.Of<IAuditContext>());
    }

    private TreatmentPublisher CreatePublisher(IAuditContext auditContext)
    {
        _mockContextFactory = new Mock<ITenantDbContextFactory>();
        _mockContextFactory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new ValueTask<NocturneDbContext>(NewDbContext()));

        return new TreatmentPublisher(
            _mockContextFactory.Object,
            _mockTreatmentService.Object,
            _mockBolusRepository.Object,
            _mockCarbIntakeRepository.Object,
            _mockBGCheckRepository.Object,
            _mockBolusCalculationRepository.Object,
            _mockTempBasalRepository.Object,
            _mockBasalInjectionRepository.Object,
            _mockNoteRepository.Object,
            _mockDeviceEventRepository.Object,
            _mockPatientInsulinRepository.Object,
            _mockBasalRateResolver.Object,
            _mockTherapySettingsResolver.Object,
            auditContext,
            NullLogger<TreatmentPublisher>.Instance
        );
    }

    [Fact]
    public async Task PublishTreatmentsAsync_DelegatesToTreatmentService()
    {
        var treatments = new List<Treatment> { new() { Id = "1" } };
        _mockTreatmentService
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);

        var result = await _publisher.PublishTreatmentsAsync(treatments, "test-source", WriteOrigin.Live);

        result.Should().BeTrue();
        _mockTreatmentService.Verify(
            s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishTreatmentsAsync_ReturnsFalse_OnException()
    {
        _mockTreatmentService
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var result = await _publisher.PublishTreatmentsAsync(new List<Treatment>(), "test-source", WriteOrigin.Live);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetLatestTreatmentTimestampAsync_ReturnsMaxAcrossAllTreatmentTypes()
    {
        var older = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var newest = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        _mockBolusRepository
            .Setup(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(older);
        _mockCarbIntakeRepository
            .Setup(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(newest);
        // The remaining treatment repos return null (no data of that type) by default.

        var result = await _publisher.GetLatestTreatmentTimestampAsync("connector-a");

        result.Should().Be(newest);
    }

    [Fact]
    public async Task GetLatestTreatmentTimestampAsync_IsSourceScoped_AcrossEveryTreatmentType()
    {
        // Regression: the resume watermark must be scoped to the requesting source. A tenant-global
        // latest (the previous behaviour) mis-classifies a newly enabled connector's first sync as
        // incremental and silently skips its backfill. Every decomposed treatment type must be
        // queried for THIS source, and the legacy global treatment-store query must no longer run.
        await _publisher.GetLatestTreatmentTimestampAsync("connector-a");

        _mockBolusRepository.Verify(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()), Times.Once);
        _mockCarbIntakeRepository.Verify(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()), Times.Once);
        _mockBGCheckRepository.Verify(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()), Times.Once);
        _mockBolusCalculationRepository.Verify(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()), Times.Once);
        _mockTempBasalRepository.Verify(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()), Times.Once);
        _mockBasalInjectionRepository.Verify(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()), Times.Once);
        _mockNoteRepository.Verify(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()), Times.Once);
        _mockDeviceEventRepository.Verify(r => r.GetLatestTimestampAsync("connector-a", It.IsAny<CancellationToken>()), Times.Once);
        _mockTreatmentService.Verify(
            s => s.GetTreatmentsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetLatestTreatmentTimestampAsync_ReturnsNull_WhenNoTreatmentsForSource()
    {
        // No stored treatment of any type for this source — the connector should treat it as a
        // first sync (backfill), not resume from a (nonexistent) watermark.
        var result = await _publisher.GetLatestTreatmentTimestampAsync("connector-a");

        result.Should().BeNull();
    }

    [Fact]
    public async Task PublishTempBasalsAsync_ReclassifiesScheduledToAlgorithm_WhenRateDiffersFromProgrammed()
    {
        // Programmed schedule is a steady 1.0 U/hr, but the pump delivered 0.4 (low-temp).
        // A connector that flattens algorithmic adjustments emits these as Scheduled.
        _mockBasalRateResolver
            .Setup(r => r.BuildResolverAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Func<long, double>)(_ => 1.0));

        var startTs = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
        var records = new List<TempBasal>
        {
            new()
            {
                Id = Guid.NewGuid(),
                StartTimestamp = startTs,
                EndTimestamp = startTs.AddMinutes(5),
                Rate = 0.4,
                ScheduledRate = 0.4, // connector copied Rate into ScheduledRate
                Origin = TempBasalOrigin.Scheduled,
                DataSource = "glooko-connector",
            }
        };

        var result = await _publisher.PublishTempBasalsAsync(records, "glooko-connector", WriteOrigin.Live);

        result.Should().BeTrue();
        records[0].Origin.Should().Be(TempBasalOrigin.Algorithm);
        records[0].ScheduledRate.Should().Be(1.0);
        records[0].Rate.Should().Be(0.4);
        _mockTempBasalRepository.Verify(
            r => r.BulkCreateAsync(records, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishTempBasalsAsync_KeepsScheduledOrigin_WhenRateMatchesProgrammed()
    {
        _mockBasalRateResolver
            .Setup(r => r.BuildResolverAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Func<long, double>)(_ => 1.0));

        var records = new List<TempBasal>
        {
            new()
            {
                Id = Guid.NewGuid(),
                StartTimestamp = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
                Rate = 1.0,
                ScheduledRate = 1.0,
                Origin = TempBasalOrigin.Scheduled,
                DataSource = "glooko-connector",
            }
        };

        var result = await _publisher.PublishTempBasalsAsync(records, "glooko-connector", WriteOrigin.Live);

        result.Should().BeTrue();
        records[0].Origin.Should().Be(TempBasalOrigin.Scheduled);
        records[0].ScheduledRate.Should().Be(1.0);
    }

    [Fact]
    public async Task PublishTempBasalsAsync_DoesNotReclassify_WithinFloatingPointTolerance()
    {
        // 0.025 U/hr is the typical pump rate increment. Below that should not trigger reclassification.
        _mockBasalRateResolver
            .Setup(r => r.BuildResolverAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Func<long, double>)(_ => 1.0));

        var records = new List<TempBasal>
        {
            new()
            {
                Id = Guid.NewGuid(),
                StartTimestamp = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
                Rate = 1.0 + 1e-6, // pure floating-point noise
                ScheduledRate = 1.0,
                Origin = TempBasalOrigin.Scheduled,
                DataSource = "glooko-connector",
            }
        };

        var result = await _publisher.PublishTempBasalsAsync(records, "glooko-connector", WriteOrigin.Live);

        result.Should().BeTrue();
        records[0].Origin.Should().Be(TempBasalOrigin.Scheduled);
    }

    [Fact]
    public async Task PublishTempBasalsAsync_DoesNotTouchAlreadyAlgorithmOrManualOrigins()
    {
        _mockBasalRateResolver
            .Setup(r => r.BuildResolverAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Func<long, double>)(_ => 1.0));

        var ts = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
        var records = new List<TempBasal>
        {
            new() { Id = Guid.NewGuid(), StartTimestamp = ts, Rate = 0.5, ScheduledRate = 1.0, Origin = TempBasalOrigin.Algorithm },
            new() { Id = Guid.NewGuid(), StartTimestamp = ts.AddMinutes(5), Rate = 0.5, ScheduledRate = 1.0, Origin = TempBasalOrigin.Manual },
            new() { Id = Guid.NewGuid(), StartTimestamp = ts.AddMinutes(10), Rate = 0, ScheduledRate = null, Origin = TempBasalOrigin.Suspended },
        };

        var result = await _publisher.PublishTempBasalsAsync(records, "loop-connector", WriteOrigin.Live);

        result.Should().BeTrue();
        records[0].Origin.Should().Be(TempBasalOrigin.Algorithm);
        records[0].ScheduledRate.Should().Be(1.0); // untouched
        records[1].Origin.Should().Be(TempBasalOrigin.Manual);
        records[1].ScheduledRate.Should().Be(1.0); // untouched
        records[2].Origin.Should().Be(TempBasalOrigin.Suspended);
        records[2].ScheduledRate.Should().BeNull(); // untouched
    }

    [Fact]
    public async Task PublishTempBasalsAsync_SkipsReclassification_WhenNoTherapyData()
    {
        // First-sync scenario: no basal_schedules on file yet, so we can't determine what was
        // programmed. Leave records as-is rather than mass-reclassifying against the fallback default.
        _mockTherapySettingsResolver
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var records = new List<TempBasal>
        {
            new()
            {
                Id = Guid.NewGuid(),
                StartTimestamp = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
                Rate = 0.4,
                ScheduledRate = 0.4,
                Origin = TempBasalOrigin.Scheduled,
            }
        };

        var result = await _publisher.PublishTempBasalsAsync(records, "glooko-connector", WriteOrigin.Live);

        result.Should().BeTrue();
        records[0].Origin.Should().Be(TempBasalOrigin.Scheduled);
        records[0].ScheduledRate.Should().Be(0.4); // untouched
        _mockBasalRateResolver.Verify(
            r => r.BuildResolverAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishTempBasalsAsync_RunsReconcileDeleteUnderSystemAttribution()
    {
        // The reconcile delete must write delete audit rows with AuthType IS NULL so the dedup
        // discriminator treats them as system-initiated and lets future resyncs through. The
        // delete — not just the insert — is what the discriminator reads, so it has to run inside
        // the SystemAuditScope; trace fields must survive so the rows stay tied to the request.
        var auditContext = new AuditContext
        {
            AuthType = "bearer",
            SubjectId = Guid.NewGuid(),
            CorrelationId = "trace-1",
            Endpoint = "POST /sync",
        };
        var publisher = CreatePublisher(auditContext);

        string? authTypeDuringDelete = "unset";
        string? correlationDuringDelete = null;
        _mockTempBasalRepository
            .Setup(r => r.SoftDeleteAbsentBySourceAndDateRangeAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                authTypeDuringDelete = auditContext.AuthType;
                correlationDuringDelete = auditContext.CorrelationId;
            })
            .ReturnsAsync(0);

        var records = new List<TempBasal>
        {
            new()
            {
                Id = Guid.NewGuid(),
                StartTimestamp = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
                Rate = 0.5,
                Origin = TempBasalOrigin.Algorithm,
                DataSource = "glooko-connector",
            }
        };

        var result = await publisher.PublishTempBasalsAsync(records, "glooko-connector", WriteOrigin.Live);

        result.Should().BeTrue();
        authTypeDuringDelete.Should().BeNull("the reconcile delete must be system-attributed");
        correlationDuringDelete.Should().Be("trace-1", "trace fields must survive the scope");
        auditContext.AuthType.Should().Be("bearer", "actor fields must be restored after the scope");
        _mockTempBasalRepository.Verify(
            r => r.SoftDeleteAbsentBySourceAndDateRangeAsync(
                "glooko-connector", It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishTempBasalsAsync_ReconcilesByIncomingLegacyIds_SoAResyncDoesNotChurn()
    {
        // The reconcile must pass the batch's legacy ids as the keep-set so still-reported rows are
        // left active (and thus skipped by BulkCreateAsync's legacy-id dedup) instead of being
        // deleted and re-created. This is what stops the per-cycle tombstone accumulation.
        IReadOnlySet<string>? keepLegacyIds = null;
        _mockTempBasalRepository
            .Setup(r => r.SoftDeleteAbsentBySourceAndDateRangeAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, DateTime _, DateTime _, IReadOnlySet<string> keep, CancellationToken _) =>
                keepLegacyIds = keep)
            .ReturnsAsync(0);

        var ts = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
        var records = new List<TempBasal>
        {
            new() { Id = Guid.NewGuid(), LegacyId = "glooko_tempbasal_1", StartTimestamp = ts, Rate = 0.5, Origin = TempBasalOrigin.Algorithm, DataSource = "glooko-connector" },
            new() { Id = Guid.NewGuid(), LegacyId = "glooko_tempbasal_2", StartTimestamp = ts.AddMinutes(5), Rate = 0.6, Origin = TempBasalOrigin.Algorithm, DataSource = "glooko-connector" },
            new() { Id = Guid.NewGuid(), LegacyId = null, StartTimestamp = ts.AddMinutes(10), Rate = 0.7, Origin = TempBasalOrigin.Algorithm, DataSource = "glooko-connector" },
        };

        var result = await _publisher.PublishTempBasalsAsync(records, "glooko-connector", WriteOrigin.Live);

        result.Should().BeTrue();
        keepLegacyIds.Should().BeEquivalentTo(new[] { "glooko_tempbasal_1", "glooko_tempbasal_2" },
            "only non-null incoming legacy ids form the keep-set");
        _mockTempBasalRepository.Verify(
            r => r.BulkCreateAsync(records, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishTempBasalsAsync_NoOpResolverCall_WhenNoScheduledRecords()
    {
        var ts = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
        var records = new List<TempBasal>
        {
            new() { Id = Guid.NewGuid(), StartTimestamp = ts, Rate = 0.5, Origin = TempBasalOrigin.Algorithm },
        };

        var result = await _publisher.PublishTempBasalsAsync(records, "loop-connector", WriteOrigin.Live);

        result.Should().BeTrue();
        _mockBasalRateResolver.Verify(
            r => r.BuildResolverAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #region PublishBasalInjectionsAsync

    [Fact]
    public async Task PublishBasalInjectionsAsync_EmptyList_ReturnsTrue()
    {
        var result = await _publisher.PublishBasalInjectionsAsync([], "glooko-connector", WriteOrigin.Live);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task PublishBasalInjectionsAsync_WithRecords_CallsBulkCreate()
    {
        _mockPatientInsulinRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockPatientInsulinRepository
            .Setup(r => r.CreateAsync(It.IsAny<PatientInsulin>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientInsulin m, WriteOrigin _, CancellationToken _) => m);
        _mockBasalInjectionRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BasalInjection> records, WriteOrigin _, CancellationToken _) => records);

        var records = new List<BasalInjection>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                Units = 22,
                DataSource = "glooko-connector",
                InsulinContext = new TreatmentInsulinContext
                {
                    PatientInsulinId = Guid.Empty,
                    InsulinName = "Tresiba (Insulin Degludec)",
                    Dia = 42.0,
                    Peak = 660,
                    Curve = "bilinear",
                    Concentration = 100,
                },
            }
        };

        var result = await _publisher.PublishBasalInjectionsAsync(records, "glooko-connector", WriteOrigin.Live);

        result.Should().BeTrue();
        _mockBasalInjectionRepository.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishBasalInjectionsAsync_ResolvesPatientInsulin_WhenGuidEmpty()
    {
        _mockPatientInsulinRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockPatientInsulinRepository
            .Setup(r => r.CreateAsync(It.IsAny<PatientInsulin>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientInsulin m, WriteOrigin _, CancellationToken _) => m);
        _mockBasalInjectionRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BasalInjection> records, WriteOrigin _, CancellationToken _) => records);

        var records = new List<BasalInjection>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                Units = 22,
                DataSource = "glooko-connector",
                InsulinContext = new TreatmentInsulinContext
                {
                    PatientInsulinId = Guid.Empty,
                    InsulinName = "Tresiba (Insulin Degludec)",
                    Dia = 42.0,
                    Peak = 660,
                    Curve = "bilinear",
                    Concentration = 100,
                },
            }
        };

        await _publisher.PublishBasalInjectionsAsync(records, "glooko-connector", WriteOrigin.Live);

        // Should auto-create a PatientInsulin record
        _mockPatientInsulinRepository.Verify(
            r => r.CreateAsync(It.Is<PatientInsulin>(pi =>
                pi.Name == "Tresiba (Insulin Degludec)" &&
                pi.Role == InsulinRole.Basal &&
                pi.IsCurrent == true),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // The record should now have a real PatientInsulinId (not Guid.Empty)
        records[0].InsulinContext.PatientInsulinId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task PublishBasalInjectionsAsync_ReusesExistingPatientInsulin()
    {
        var existingInsulinId = Guid.NewGuid();
        _mockPatientInsulinRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PatientInsulin
                {
                    Id = existingInsulinId,
                    Name = "Tresiba (Insulin Degludec)",
                    Role = InsulinRole.Basal,
                    IsCurrent = true,
                    Dia = 42.0,
                    Peak = 660,
                    Curve = "bilinear",
                    Concentration = 100,
                }
            ]);
        _mockBasalInjectionRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BasalInjection> records, WriteOrigin _, CancellationToken _) => records);

        var records = new List<BasalInjection>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                Units = 22,
                DataSource = "glooko-connector",
                InsulinContext = new TreatmentInsulinContext
                {
                    PatientInsulinId = Guid.Empty,
                    InsulinName = "Tresiba (Insulin Degludec)",
                    Dia = 42.0,
                    Peak = 660,
                    Curve = "bilinear",
                    Concentration = 100,
                },
            }
        };

        await _publisher.PublishBasalInjectionsAsync(records, "glooko-connector", WriteOrigin.Live);

        // Should NOT create a new PatientInsulin — reuses existing
        _mockPatientInsulinRepository.Verify(
            r => r.CreateAsync(It.IsAny<PatientInsulin>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Should resolve to the existing ID
        records[0].InsulinContext.PatientInsulinId.Should().Be(existingInsulinId);
    }

    [Fact]
    public async Task PublishBasalInjectionsAsync_SkipsResolution_WhenPatientInsulinIdAlreadySet()
    {
        var existingId = Guid.NewGuid();
        _mockBasalInjectionRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BasalInjection> records, WriteOrigin _, CancellationToken _) => records);

        var records = new List<BasalInjection>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                Units = 22,
                DataSource = "glooko-connector",
                InsulinContext = new TreatmentInsulinContext
                {
                    PatientInsulinId = existingId,
                    InsulinName = "Tresiba (Insulin Degludec)",
                    Dia = 42.0,
                    Peak = 660,
                    Curve = "bilinear",
                    Concentration = 100,
                },
            }
        };

        await _publisher.PublishBasalInjectionsAsync(records, "glooko-connector", WriteOrigin.Live);

        // Should not touch PatientInsulin repo at all
        _mockPatientInsulinRepository.Verify(
            r => r.GetAllAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        records[0].InsulinContext.PatientInsulinId.Should().Be(existingId);
    }

    #endregion
}
