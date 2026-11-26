using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// A watermark that cannot be read fails the run, whatever failed it: a null watermark means no prior
/// data, which is a crawl of the whole history, so the run waits for the next cycle instead.
/// </summary>
public class ConnectorWatermarkReadTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Earlier = Now.UtcDateTime.AddDays(-3);

    public class TestConfig : BaseConnectorConfiguration;

    private sealed class ResumeProbe(IConnectorPublisher publisher, ResumeFamily family)
        : BaseConnectorService<TestConfig>(
            new HttpClient(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            NullLogger<ResumeProbe>.Instance,
            new RecordingRetryDelayStrategy(),
            publisher,
            new FakeTimeProvider(Now))
    {
        public bool Read { get; private set; }
        public DateTime? Start { get; private set; }

        protected override string ConnectorSource => "probe-connector";
        public override string ServiceName => "Probe";
        public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

        protected override IEnumerable<ResumeKey> ResumesFrom(SyncRequest request, TestConfig config) =>
            [(ResumeKey)family];

        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConfig config, CancellationToken cancellationToken)
        {
            Start = WindowStart(request, family);
            Read = true;
            return Task.FromResult(new SyncResult { Success = true });
        }
    }

    private static Mock<IConnectorPublisher> Publisher(ResumeFamily family, Func<Task<DateTime?>> latest)
    {
        var publisher = new Mock<IConnectorPublisher>();
        publisher.SetupGet(p => p.IsAvailable).Returns(true);
        if (family == ResumeFamily.DeviceStatus)
            publisher.Setup(p => p.Device.GetLatestDeviceStatusTimestampAsync("probe-connector", It.IsAny<CancellationToken>()))
                .Returns(latest);
        else
            publisher.Setup(p => p.Metadata.GetLatestActivityTimestampAsync("probe-connector", It.IsAny<CancellationToken>()))
                .Returns(latest);
        return publisher;
    }

    public static TheoryData<ResumeFamily> Families => new() { ResumeFamily.DeviceStatus, ResumeFamily.Activity };

    public static TheoryData<ResumeFamily, Exception> Failures
    {
        get
        {
            var data = new TheoryData<ResumeFamily, Exception>();
            foreach (var family in new[] { ResumeFamily.DeviceStatus, ResumeFamily.Activity })
            {
                data.Add(family, new ConnectorStoreUnavailableException("connection refused", new InvalidOperationException()));
                data.Add(family, new TaskCanceledException("The store timed out."));
                data.Add(family, new NullReferenceException("a defect in the read"));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_watermark_read_that_fails_fails_the_run_and_starts_no_read(ResumeFamily family, Exception failure)
    {
        var probe = new ResumeProbe(Publisher(family, () => Task.FromException<DateTime?>(failure)).Object, family);

        var result = await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be(failure.Message);
        probe.Read.Should().BeFalse("a run that cannot read its watermark must not read the whole history");
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_watermark_read_that_lands_resumes_from_it(ResumeFamily family)
    {
        var probe = new ResumeProbe(Publisher(family, () => Task.FromResult<DateTime?>(Earlier)).Object, family);

        var result = await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
        probe.Start.Should().NotBeNull().And.BeBefore(Earlier);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_family_with_nothing_stored_reads_its_whole_history(ResumeFamily family)
    {
        var probe = new ResumeProbe(Publisher(family, () => Task.FromResult<DateTime?>(null)).Object, family);

        var result = await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
        probe.Read.Should().BeTrue();
        probe.Start.Should().BeNull();
    }
}
