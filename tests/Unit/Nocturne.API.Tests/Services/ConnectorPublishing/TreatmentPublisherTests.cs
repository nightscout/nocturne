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
    private readonly Mock<IBasalRateResolver> _mockBasalRateResolver;
    private readonly Mock<ITherapySettingsResolver> _mockTherapySettingsResolver;
    private readonly TreatmentPublisher _publisher;

    public TreatmentPublisherTests()
    {
        _mockTreatmentService = new Mock<ITreatmentService>();
        _mockBolusRepository = new Mock<IBolusRepository>();
        _mockCarbIntakeRepository = new Mock<ICarbIntakeRepository>();
        _mockBGCheckRepository = new Mock<IBGCheckRepository>();
        _mockBolusCalculationRepository = new Mock<IBolusCalculationRepository>();
        _mockTempBasalRepository = new Mock<ITempBasalRepository>();
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
        var dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new NocturneDbContext(dbOptions);

        return new TreatmentPublisher(
            dbContext,
            _mockTreatmentService.Object,
            _mockBolusRepository.Object,
            _mockCarbIntakeRepository.Object,
            _mockBGCheckRepository.Object,
            _mockBolusCalculationRepository.Object,
            _mockTempBasalRepository.Object,
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
}
