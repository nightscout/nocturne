using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class ConnectorRecordIdentityTests
{
    private const string Source = "test-connector";
    private static readonly DateTimeOffset Instant = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);

    private sealed class TestConfig : BaseConnectorConfiguration;

    private sealed class TestConnectorService(IConnectorPublisher publisher, TimeProvider clock)
        : BaseConnectorService<TestConfig>(
            new HttpClient(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            NullLogger<TestConnectorService>.Instance,
            new RecordingRetryDelayStrategy(),
            publisher,
            clock)
    {
        protected override string ConnectorSource => Source;
        public override string ServiceName => "Test";

        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> Publish<T>(List<T> records) where T : class
            => PublishByRouteAsync(records, new TestConfig(), CancellationToken.None);
    }

    private readonly FakeTimeProvider _clock = new(Instant);
    private readonly List<Bolus> _publishedBoluses = [];
    private readonly TestConnectorService _service;

    public ConnectorRecordIdentityTests()
    {
        var treatments = new Mock<ITreatmentPublisher>();
        treatments.SetReturnsDefault(Task.FromResult<DateTime?>(null));
        treatments
            .Setup(t => t.PublishBolusesAsync(
                It.IsAny<IEnumerable<Bolus>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Bolus>, string, WriteOrigin, CancellationToken>(
                (records, _, _, _) => _publishedBoluses.AddRange(records))
            .ReturnsAsync(true);
        var device = new Mock<IDevicePublisher>();
        device.SetReturnsDefault(Task.FromResult(true));
        device.SetReturnsDefault(Task.FromResult<DateTime?>(null));
        var metadata = new Mock<IMetadataPublisher>();
        metadata.SetReturnsDefault(Task.FromResult(true));

        var publisher = new Mock<IConnectorPublisher>();
        publisher.SetupGet(p => p.IsAvailable).Returns(true);
        publisher.SetupGet(p => p.Treatments).Returns(treatments.Object);
        publisher.SetupGet(p => p.Device).Returns(device.Object);
        publisher.SetupGet(p => p.Metadata).Returns(metadata.Object);

        _service = new TestConnectorService(publisher.Object, _clock);
    }

    [Fact]
    public async Task A_record_with_no_identity_is_stamped_with_a_fresh_id_one_instant_and_the_connector_source()
    {
        List<Bolus> batch = [new() { LegacyId = "test_1" }, new() { LegacyId = "test_2" }];

        await _service.Publish(batch);

        _publishedBoluses.Should().HaveCount(2).And.OnlyContain(b =>
            b.Id != Guid.Empty
            && b.CreatedAt == Instant.UtcDateTime
            && b.ModifiedAt == Instant.UtcDateTime
            && b.DataSource == Source);
        _publishedBoluses.Select(b => b.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_set_id_and_the_legacy_id_are_kept()
    {
        var id = Guid.CreateVersion7();
        List<Bolus> batch = [new() { Id = id, LegacyId = "test_1" }];

        await _service.Publish(batch);

        _publishedBoluses.Single().Id.Should().Be(id);
        _publishedBoluses.Single().LegacyId.Should().Be("test_1");
    }

    [Fact]
    public async Task The_data_source_is_always_the_connector_source()
    {
        List<Bolus> batch = [new() { DataSource = "elsewhere" }];

        await _service.Publish(batch);

        _publishedBoluses.Single().DataSource.Should().Be(Source);
    }

    [Fact]
    public async Task The_published_record_is_the_one_the_connector_holds()
    {
        var bolus = new Bolus { LegacyId = "test_1" };

        await _service.Publish([bolus]);

        bolus.Id.Should().Be(_publishedBoluses.Single().Id).And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_second_sync_of_the_same_vendor_record_keeps_its_legacy_id()
    {
        await _service.Publish([new Bolus { LegacyId = "test_1" }]);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _service.Publish([new Bolus { LegacyId = "test_1" }]);

        _publishedBoluses.Select(b => b.LegacyId).Should().Equal("test_1", "test_1");
        _publishedBoluses[1].CreatedAt.Should().Be(Instant.UtcDateTime.AddMinutes(5));
    }

    [Fact]
    public async Task A_patient_device_keeps_its_deterministic_id_and_gets_a_creation_time()
    {
        var id = Guid.CreateVersion7();
        var device = new PatientDevice { Id = id };

        await _service.Publish([device]);

        device.Id.Should().Be(id);
        device.CreatedAt.Should().Be(Instant.UtcDateTime);
    }

    [Fact]
    public async Task A_system_event_and_a_heart_rate_carry_the_connector_source()
    {
        var systemEvent = new SystemEvent { Source = "elsewhere" };
        var heartRate = new HeartRate();

        await _service.Publish([systemEvent]);
        await _service.Publish([heartRate]);

        systemEvent.Source.Should().Be(Source);
        heartRate.DataSource.Should().Be(Source);
    }

    [Fact]
    public async Task A_device_status_keeps_what_its_connector_set()
    {
        var status = new DeviceStatus();

        await _service.Publish([status]);

        status.Id.Should().BeNull();
    }
}
