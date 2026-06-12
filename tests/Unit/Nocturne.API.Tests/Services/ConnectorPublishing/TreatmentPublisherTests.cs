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

        var result = await _publisher.PublishTreatmentsAsync(treatments, "test-source");

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

        var result = await _publisher.PublishTreatmentsAsync(new List<Treatment>(), "test-source");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PublishDecompositionBatchesAsync_ConcurrentCalls_AcquireIndependentContexts()
    {
        var calls = Enumerable.Range(0, 16).Select(i =>
            _publisher.PublishDecompositionBatchesAsync(
                new[]
                {
                    new DecompositionBatch
                    {
                        Id = Guid.NewGuid(),
                        Source = "test-source",
                        SourceRecordId = $"record-{i}",
                        CreatedAt = DateTime.UtcNow,
                    },
                },
                "test-source"));

        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(r => r);
        _mockContextFactory.Verify(
            f => f.CreateAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(16));
    }

    [Fact]
    public async Task GetLatestTreatmentTimestampAsync_ReturnsCreatedAt_WhenAvailable()
    {
        var createdAt = "2026-01-15T12:00:00Z";
        _mockTreatmentService
            .Setup(s => s.GetTreatmentsAsync(1, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Treatment> { new() { CreatedAt = createdAt } });

        var result = await _publisher.GetLatestTreatmentTimestampAsync("test-source");

        result.Should().Be(DateTime.Parse(createdAt));
    }

    [Fact]
    public async Task GetLatestTreatmentTimestampAsync_ReturnsTimestamp_WhenOnlyMillsSet()
    {
        // Treatment.CreatedAt auto-generates an ISO string from Mills,
        // so the CreatedAt parsing path is taken even when only Mills is set.
        var fixedTime = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var mills = fixedTime.ToUnixTimeMilliseconds();
        _mockTreatmentService
            .Setup(s => s.GetTreatmentsAsync(1, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Treatment> { new() { Mills = mills } });

        var result = await _publisher.GetLatestTreatmentTimestampAsync("test-source");

        result.Should().NotBeNull();
        result!.Value.Date.Should().Be(new DateTime(2026, 1, 15));
    }

    [Fact]
    public async Task GetLatestTreatmentTimestampAsync_ReturnsNull_WhenNoTreatments()
    {
        _mockTreatmentService
            .Setup(s => s.GetTreatmentsAsync(1, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Treatment>());

        var result = await _publisher.GetLatestTreatmentTimestampAsync("test-source");

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

        var result = await _publisher.PublishTempBasalsAsync(records, "glooko-connector");

        result.Should().BeTrue();
        records[0].Origin.Should().Be(TempBasalOrigin.Algorithm);
        records[0].ScheduledRate.Should().Be(1.0);
        records[0].Rate.Should().Be(0.4);
        _mockTempBasalRepository.Verify(
            r => r.BulkCreateAsync(records, It.IsAny<CancellationToken>()),
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

        var result = await _publisher.PublishTempBasalsAsync(records, "glooko-connector");

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

        var result = await _publisher.PublishTempBasalsAsync(records, "glooko-connector");

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

        var result = await _publisher.PublishTempBasalsAsync(records, "loop-connector");

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

        var result = await _publisher.PublishTempBasalsAsync(records, "glooko-connector");

        result.Should().BeTrue();
        records[0].Origin.Should().Be(TempBasalOrigin.Scheduled);
        records[0].ScheduledRate.Should().Be(0.4); // untouched
        _mockBasalRateResolver.Verify(
            r => r.BuildResolverAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishTempBasalsAsync_RunsSweepDeleteUnderSystemAttribution()
    {
        // The delete-then-reinsert sweep must write delete audit rows with AuthType IS NULL so the
        // dedup discriminator treats them as system-initiated and lets future resyncs through. The
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
            .Setup(r => r.DeleteBySourceAndDateRangeAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
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

        var result = await publisher.PublishTempBasalsAsync(records, "glooko-connector");

        result.Should().BeTrue();
        authTypeDuringDelete.Should().BeNull("the sweep delete must be system-attributed");
        correlationDuringDelete.Should().Be("trace-1", "trace fields must survive the scope");
        auditContext.AuthType.Should().Be("bearer", "actor fields must be restored after the scope");
        _mockTempBasalRepository.Verify(
            r => r.DeleteBySourceAndDateRangeAsync(
                "glooko-connector", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishTempBasalsAsync_NoOpResolverCall_WhenNoScheduledRecords()
    {
        var ts = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
        var records = new List<TempBasal>
        {
            new() { Id = Guid.NewGuid(), StartTimestamp = ts, Rate = 0.5, Origin = TempBasalOrigin.Algorithm },
        };

        var result = await _publisher.PublishTempBasalsAsync(records, "loop-connector");

        result.Should().BeTrue();
        _mockBasalRateResolver.Verify(
            r => r.BuildResolverAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #region PublishBasalInjectionsAsync

    [Fact]
    public async Task PublishBasalInjectionsAsync_EmptyList_ReturnsTrue()
    {
        var result = await _publisher.PublishBasalInjectionsAsync([], "glooko-connector");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task PublishBasalInjectionsAsync_WithRecords_CallsBulkCreate()
    {
        _mockPatientInsulinRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockPatientInsulinRepository
            .Setup(r => r.CreateAsync(It.IsAny<PatientInsulin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientInsulin m, CancellationToken _) => m);
        _mockBasalInjectionRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BasalInjection> records, CancellationToken _) => records);

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

        var result = await _publisher.PublishBasalInjectionsAsync(records, "glooko-connector");

        result.Should().BeTrue();
        _mockBasalInjectionRepository.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishBasalInjectionsAsync_ResolvesPatientInsulin_WhenGuidEmpty()
    {
        _mockPatientInsulinRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockPatientInsulinRepository
            .Setup(r => r.CreateAsync(It.IsAny<PatientInsulin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientInsulin m, CancellationToken _) => m);
        _mockBasalInjectionRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BasalInjection> records, CancellationToken _) => records);

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

        await _publisher.PublishBasalInjectionsAsync(records, "glooko-connector");

        // Should auto-create a PatientInsulin record
        _mockPatientInsulinRepository.Verify(
            r => r.CreateAsync(It.Is<PatientInsulin>(pi =>
                pi.Name == "Tresiba (Insulin Degludec)" &&
                pi.Role == InsulinRole.Basal &&
                pi.IsCurrent == true),
                It.IsAny<CancellationToken>()),
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
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BasalInjection> records, CancellationToken _) => records);

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

        await _publisher.PublishBasalInjectionsAsync(records, "glooko-connector");

        // Should NOT create a new PatientInsulin — reuses existing
        _mockPatientInsulinRepository.Verify(
            r => r.CreateAsync(It.IsAny<PatientInsulin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Should resolve to the existing ID
        records[0].InsulinContext.PatientInsulinId.Should().Be(existingInsulinId);
    }

    [Fact]
    public async Task PublishBasalInjectionsAsync_SkipsResolution_WhenPatientInsulinIdAlreadySet()
    {
        var existingId = Guid.NewGuid();
        _mockBasalInjectionRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BasalInjection> records, CancellationToken _) => records);

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

        await _publisher.PublishBasalInjectionsAsync(records, "glooko-connector");

        // Should not touch PatientInsulin repo at all
        _mockPatientInsulinRepository.Verify(
            r => r.GetAllAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        records[0].InsulinContext.PatientInsulinId.Should().Be(existingId);
    }

    #endregion
}
