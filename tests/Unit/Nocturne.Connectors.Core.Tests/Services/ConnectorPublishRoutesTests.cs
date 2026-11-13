using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Pins the pairing every record type owes the shared publish path: the sync data types it
///     counts under, the origin family its broadcast origin comes from, and the one publisher member
///     it reaches. The expectations are written out here, apart from the table they check.
/// </summary>
public class ConnectorPublishRoutesTests
{
    private sealed class TestConfig : BaseConnectorConfiguration;

    private sealed class UnroutedRecord;

    private sealed class TestConnectorService(IConnectorPublisher? publisher)
        : BaseConnectorService<TestConfig>(
            new HttpClient(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            NullLogger<TestConnectorService>.Instance,
            publisher)
    {
        protected override string ConnectorSource => "test";
        public override string ServiceName => "Test";

        public override Task<bool> AuthenticateAsync() => Task.FromResult(true);

        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> ByRoute<T>(IEnumerable<T> records, TestConfig? config = null) where T : class
            => PublishByRouteAsync(records, config ?? new TestConfig(), CancellationToken.None);

        public Task<bool> Counted<T>(
            SyncResult result, HashSet<SyncDataType> active, List<T> records, SyncDataType? dataType = null)
            where T : class
            => PublishRecordTypeAsync(result, active, records, new TestConfig(), CancellationToken.None,
                dataType: dataType);
    }

    private sealed record Expected(
        Type RecordType,
        SyncDataType[] DataTypes,
        PublishOriginFamily Origin,
        string Member);

    private static readonly Expected[] Expectations =
    [
        new(typeof(Entry), [SyncDataType.Glucose], PublishOriginFamily.Glucose, "PublishEntriesAsync"),
        new(typeof(SensorGlucose), [SyncDataType.Glucose], PublishOriginFamily.Glucose, "PublishSensorGlucoseAsync"),
        new(typeof(Treatment), [], PublishOriginFamily.Treatment, "PublishTreatmentsAsync"),
        new(typeof(Bolus), [SyncDataType.Boluses], PublishOriginFamily.Treatment, "PublishBolusesAsync"),
        new(typeof(CarbIntake), [SyncDataType.CarbIntake], PublishOriginFamily.Treatment, "PublishCarbIntakesAsync"),
        new(typeof(BGCheck), [SyncDataType.ManualBG], PublishOriginFamily.Treatment, "PublishBGChecksAsync"),
        new(typeof(BolusCalculation), [SyncDataType.BolusCalculations], PublishOriginFamily.Treatment, "PublishBolusCalculationsAsync"),
        new(typeof(TempBasal), [SyncDataType.TempBasals, SyncDataType.StateSpans], PublishOriginFamily.Treatment, "PublishTempBasalsAsync"),
        new(typeof(BasalInjection), [SyncDataType.BasalInjections], PublishOriginFamily.Treatment, "PublishBasalInjectionsAsync"),
        new(typeof(Note), [SyncDataType.Notes], PublishOriginFamily.Treatment, "PublishNotesAsync"),
        new(typeof(DeviceStatus), [SyncDataType.DeviceStatus], PublishOriginFamily.Device, "PublishDeviceStatusAsync"),
        new(typeof(DeviceEvent), [SyncDataType.DeviceEvents], PublishOriginFamily.Treatment, "PublishDeviceEventsAsync"),
        new(typeof(SystemEvent), [SyncDataType.DeviceEvents], PublishOriginFamily.Live, "PublishSystemEventsAsync"),
        new(typeof(Profile), [SyncDataType.Profiles], PublishOriginFamily.Live, "PublishProfilesAsync"),
        new(typeof(Food), [SyncDataType.Food], PublishOriginFamily.Live, "PublishFoodAsync"),
        new(typeof(ConnectorFoodEntryImport), [SyncDataType.Food], PublishOriginFamily.Live, "PublishConnectorFoodEntriesAsync"),
        new(typeof(Activity), [SyncDataType.Activity], PublishOriginFamily.Live, "PublishActivityAsync"),
        new(typeof(StateSpan), [SyncDataType.StateSpans], PublishOriginFamily.Live, "PublishStateSpansAsync"),
        new(typeof(ProfileActivation), [SyncDataType.StateSpans], PublishOriginFamily.Live, "PublishProfileActivationsAsync"),
        new(typeof(BodyWeight), [SyncDataType.BodyWeight], PublishOriginFamily.Live, "PublishBodyWeightsAsync"),
        new(typeof(StepCount), [SyncDataType.Steps], PublishOriginFamily.Live, "PublishStepCountsAsync"),
        new(typeof(HeartRate), [SyncDataType.HeartRate], PublishOriginFamily.Live, "PublishHeartRatesAsync"),
    ];

    /// <summary>Accepts every publish and answers every resume watermark with no prior data.</summary>
    private sealed class RecordingPublisher
    {
        public Mock<IGlucosePublisher> Glucose { get; } = new();
        public Mock<ITreatmentPublisher> Treatments { get; } = new();
        public Mock<IDevicePublisher> Device { get; } = new();
        public Mock<IMetadataPublisher> Metadata { get; } = new();

        public RecordingPublisher()
        {
            Glucose.SetReturnsDefault(Task.FromResult(true));
            Glucose.SetReturnsDefault(Task.FromResult<DateTime?>(null));
            Treatments.SetReturnsDefault(Task.FromResult(true));
            Treatments.SetReturnsDefault(Task.FromResult<DateTime?>(null));
            Device.SetReturnsDefault(Task.FromResult(true));
            Device.SetReturnsDefault(Task.FromResult<DateTime?>(null));
            Metadata.SetReturnsDefault(Task.FromResult(true));
            Metadata.SetReturnsDefault(Task.FromResult<DateTime?>(null));

            Metadata.Setup(m => m.PublishConnectorFoodEntriesAsync(
                    It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ConnectorFoodEntry>());

            Root.SetupGet(p => p.IsAvailable).Returns(true);
            Root.SetupGet(p => p.Glucose).Returns(Glucose.Object);
            Root.SetupGet(p => p.Treatments).Returns(Treatments.Object);
            Root.SetupGet(p => p.Device).Returns(Device.Object);
            Root.SetupGet(p => p.Metadata).Returns(Metadata.Object);
        }

        public Mock<IConnectorPublisher> Root { get; } = new();

        public IConnectorPublisher Object => Root.Object;

        public IEnumerable<(string Surface, string Member, object?[] Arguments)> Calls() =>
            new (string, Mock)[]
                {
                    ("Glucose", Glucose), ("Treatments", Treatments), ("Device", Device), ("Metadata", Metadata),
                }
                .SelectMany(pair => pair.Item2.Invocations
                    .Select(i => (pair.Item1, i.Method.Name, i.Arguments.ToArray())));
    }

    private static readonly Dictionary<string, string> WatermarkReaders = new()
    {
        [nameof(PublishOriginFamily.Glucose)] = "Glucose.GetLatestEntryTimestampAsync",
        [nameof(PublishOriginFamily.Treatment)] = "Treatments.GetLatestTreatmentTimestampAsync",
        [nameof(PublishOriginFamily.Device)] = "Device.GetLatestDeviceStatusTimestampAsync",
    };

    private static object Batch(Type recordType)
    {
        var batch = Array.CreateInstance(recordType, 1);
        batch.SetValue(Activator.CreateInstance(recordType), 0);
        return batch;
    }

    private static Task<bool> PublishByRoute(TestConnectorService service, Type recordType) =>
        (Task<bool>)typeof(TestConnectorService).GetMethod(nameof(TestConnectorService.ByRoute))!
            .MakeGenericMethod(recordType)
            .Invoke(service, [Batch(recordType), null])!;

    [Fact]
    public void TheTableRoutesExactlyTheRecordTypesTheExpectationsName()
    {
        PublishRoutes.All.Select(route => route.RecordType)
            .Should().BeEquivalentTo(Expectations.Select(e => e.RecordType));
    }

    [Fact]
    public async Task EveryRoute_ReachesItsPublisherMemberUnderItsOriginFamily()
    {
        foreach (var expected in Expectations)
        {
            // Arrange
            var publisher = new RecordingPublisher();
            var service = new TestConnectorService(publisher.Object);

            // Act
            var published = await PublishByRoute(service, expected.RecordType);

            // Assert
            var because = $"{expected.RecordType.Name} routes to {expected.Member}";
            published.Should().BeTrue(because);

            var route = PublishRoutes.All.Single(r => r.RecordType == expected.RecordType);
            route.DataTypes.Should().Equal(expected.DataTypes, because);
            route.Origin.Should().Be(expected.Origin, because);

            var calls = publisher.Calls().ToList();
            var publishes = calls.Where(c => c.Member.StartsWith("Publish")).ToList();
            publishes.Should().ContainSingle(because).Which.Member.Should().Be(expected.Member, because);

            var origins = publishes[0].Arguments.OfType<WriteOrigin>().ToList();
            if (expected.Member is "PublishBodyWeightsAsync" or "PublishStepCountsAsync" or "PublishHeartRatesAsync")
                origins.Should().BeEmpty($"{because}, a member that takes no origin");
            else
                origins.Should().Equal(
                    [expected.Origin == PublishOriginFamily.Live ? WriteOrigin.Live : WriteOrigin.Backfill],
                    $"{because}: a family reads its watermark, and no prior data reads as a backfill");

            var watermarkReads = calls.Where(c => c.Member.StartsWith("GetLatest"))
                .Select(c => $"{c.Surface}.{c.Member}").ToList();
            if (expected.Origin == PublishOriginFamily.Live)
                watermarkReads.Should().BeEmpty(because);
            else
                watermarkReads.Should().Equal([WatermarkReaders[expected.Origin.ToString()]], because);
        }
    }

    [Fact]
    public async Task AnUnroutedRecordType_Throws()
    {
        var service = new TestConnectorService(new RecordingPublisher().Object);
        var result = new SyncResult();
        var active = new HashSet<SyncDataType> { SyncDataType.Glucose };

        var counted = () => service.Counted(result, active, [new UnroutedRecord()]);
        var direct = () => service.ByRoute([new UnroutedRecord()]);

        await counted.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UnroutedRecord*");
        await direct.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UnroutedRecord*");
        result.ItemsSynced.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnroutedRecordType_ThrowsEvenWithNoPublisher()
    {
        var service = new TestConnectorService(null);

        var direct = () => service.ByRoute([new UnroutedRecord()]);

        await direct.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task APublisherThatIsNotAvailable_PublishesNothingAndReportsFalse()
    {
        var recording = new RecordingPublisher();
        recording.Root.SetupGet(p => p.IsAvailable).Returns(false);
        var service = new TestConnectorService(recording.Object);

        var published = await service.ByRoute([new Bolus()]);

        published.Should().BeFalse();
        recording.Calls().Should().BeEmpty();
    }

    [Fact]
    public async Task ARecordTypeThatSeveralSyncDataTypesGovern_CountsUnderTheOneTheCallerNames()
    {
        var service = new TestConnectorService(new RecordingPublisher().Object);
        var active = new HashSet<SyncDataType> { SyncDataType.TempBasals, SyncDataType.StateSpans };

        var byDefault = new SyncResult();
        await service.Counted(byDefault, active, [new TempBasal()]);
        var named = new SyncResult();
        await service.Counted(named, active, [new TempBasal()], SyncDataType.StateSpans);

        byDefault.ItemsSynced.Should().Equal(new Dictionary<SyncDataType, int> { [SyncDataType.TempBasals] = 1 });
        named.ItemsSynced.Should().Equal(new Dictionary<SyncDataType, int> { [SyncDataType.StateSpans] = 1 });
    }

    [Fact]
    public async Task ASyncDataTypeTheRouteDoesNotClaim_Throws()
    {
        var service = new TestConnectorService(new RecordingPublisher().Object);
        var active = new HashSet<SyncDataType> { SyncDataType.Boluses };

        var wrong = () => service.Counted(new SyncResult(), active, [new Bolus()], SyncDataType.Glucose);

        await wrong.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ARecordTypeNoSingleSyncDataTypeGoverns_NeedsTheCallerToNameOne()
    {
        var service = new TestConnectorService(new RecordingPublisher().Object);
        var active = new HashSet<SyncDataType> { SyncDataType.Boluses };

        var unnamed = () => service.Counted(new SyncResult(), active, [new Treatment()]);

        await unnamed.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(GlucoseProcessing.Smoothed, GlucoseProcessing.Smoothed, 110.0)]
    [InlineData(GlucoseProcessing.Unsmoothed, GlucoseProcessing.Unsmoothed, null)]
    public async Task SensorGlucose_IsStampedWithTheConfigsProcessingBeforeItIsPublished(
        GlucoseProcessing configured, GlucoseProcessing expected, double? expectedSmoothed)
    {
        var publisher = new RecordingPublisher();
        IReadOnlyList<SensorGlucose>? seen = null;
        publisher.Glucose
            .Setup(g => g.PublishSensorGlucoseAsync(
                It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(),
                It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<SensorGlucose> records, string _, WriteOrigin _, CancellationToken _) =>
                seen = records.Select(r => new SensorGlucose
                {
                    GlucoseProcessing = r.GlucoseProcessing,
                    SmoothedMgdl = r.SmoothedMgdl,
                }).ToList())
            .ReturnsAsync(true);
        var service = new TestConnectorService(publisher.Object);

        await service.ByRoute([new SensorGlucose { Mgdl = 110 }], new TestConfig { GlucoseProcessing = configured });

        seen.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            GlucoseProcessing = expected,
            SmoothedMgdl = expectedSmoothed,
        });
    }

    [Fact]
    public async Task SensorGlucose_KeepsASmoothedValueTheMapperAlreadySet()
    {
        var publisher = new RecordingPublisher();
        double? smoothed = null;
        publisher.Glucose
            .Setup(g => g.PublishSensorGlucoseAsync(
                It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(),
                It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<SensorGlucose> records, string _, WriteOrigin _, CancellationToken _) =>
                smoothed = records.Single().SmoothedMgdl)
            .ReturnsAsync(true);
        var service = new TestConnectorService(publisher.Object);

        await service.ByRoute([new SensorGlucose { Mgdl = 110, SmoothedMgdl = 104 }]);

        smoothed.Should().Be(104);
    }

    [Fact]
    public void SensorGlucoseIsTheRouteWithAPreparationStep()
    {
        PublishRoutes.For<SensorGlucose>().Prepare.Should().NotBeNull();
        PublishRoutes.For<Entry>().Prepare.Should().BeNull();
        PublishRoutes.For<Bolus>().Prepare.Should().BeNull();
        PublishRoutes.For<DeviceStatus>().Prepare.Should().BeNull();
        PublishRoutes.For<Profile>().Prepare.Should().BeNull();
    }
}
