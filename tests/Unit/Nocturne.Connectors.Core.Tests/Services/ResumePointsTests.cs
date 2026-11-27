using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class ResumePointsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Floor = Now.UtcDateTime.AddMonths(-6);

    public class TestConfig : BaseConnectorConfiguration;

    /// <summary>
    ///     Records where each declared family of a run started, as a single-window source would read
    ///     it, after running <see cref="Body"/>.
    /// </summary>
    private sealed class ProbeConnector(
        IConnectorPublisher publisher,
        Func<ResumePoints, Func<ResumePoints, Task<SyncResult>>, Task<SyncResult>>? policy = null)
        : BaseConnectorService<TestConfig>(
            new HttpClient(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            NullLogger<ProbeConnector>.Instance,
            new RecordingRetryDelayStrategy(),
            publisher,
            new FakeTimeProvider(Now))
    {
        public Dictionary<ResumeFamily, DateTime?> Starts { get; } = [];
        public Dictionary<ResumeFamily, DateTime?> Points { get; } = [];
        public ResumeFamily[] Families { get; init; } = [ResumeFamily.Glucose];
        public Action<SyncRequest> Body { get; init; } = _ => { };
        public bool BodyRan { get; private set; }
        public Dictionary<ResumeFamily, HttpRequestException> Faults { get; } = [];

        protected override string ConnectorSource => "probe-connector";
        public override string ServiceName => "Probe";

        protected override IEnumerable<ResumeKey> ResumesFrom(SyncRequest request, TestConfig config) =>
            Families.Select(family => (ResumeKey)family);

        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConfig config, CancellationToken cancellationToken)
        {
            BodyRan = true;
            Body(request);
            foreach (var family in Families)
            {
                try
                {
                    Starts[family] = WindowStart(request, family);
                    Points[family] = request.Resume.Point(family);
                }
                catch (HttpRequestException ex)
                {
                    Faults[family] = ex;
                }
            }

            return Task.FromResult(new SyncResult { Success = true });
        }

        protected override Task<SyncResult> RunScheduledAsync(
            SyncRequest request,
            ResumePoints resume,
            Func<ResumePoints, Task<SyncResult>> run,
            TestConfig config,
            CancellationToken cancellationToken) =>
            policy is null ? base.RunScheduledAsync(request, resume, run, config, cancellationToken) : policy(resume, run);
    }

    private static Mock<IConnectorPublisher> Publisher(DateTime? glucose = null, DateTime? treatments = null)
    {
        var publisher = new Mock<IConnectorPublisher>();
        publisher.SetupGet(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose.GetLatestEntryTimestampAsync("probe-connector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(glucose);
        publisher.Setup(p => p.Treatments.GetLatestTreatmentTimestampAsync("probe-connector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);
        return publisher;
    }

    [Fact]
    public async Task FirstScheduledRun_StartsEveryFamilyAtTheInitialFloor()
    {
        var probe = new ProbeConnector(Publisher().Object)
        {
            Families = [ResumeFamily.Glucose, ResumeFamily.Treatments],
        };

        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        probe.Starts.Should().BeEquivalentTo(new Dictionary<ResumeFamily, DateTime?>
        {
            [ResumeFamily.Glucose] = Floor,
            [ResumeFamily.Treatments] = Floor,
        });
    }

    [Fact]
    public async Task ResumedScheduledRun_StartsEachFamilyAtItsOwnNewestRecordLessTheOverlap()
    {
        var glucose = Now.UtcDateTime.AddHours(-1);
        var treatments = Now.UtcDateTime.AddDays(-3);
        var probe = new ProbeConnector(Publisher(glucose, treatments).Object)
        {
            Families = [ResumeFamily.Glucose, ResumeFamily.Treatments],
        };

        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        probe.Starts[ResumeFamily.Glucose].Should().Be(glucose.AddMinutes(-5));
        probe.Starts[ResumeFamily.Treatments].Should().Be(treatments.AddMinutes(-5),
            "a family resumes from its own records, not from where glucose resumes");
    }

    [Fact]
    public async Task ScheduledRun_ReadsOnlyTheFamiliesItDeclares_AndEachOnce()
    {
        var publisher = Publisher(Now.UtcDateTime.AddHours(-1), Now.UtcDateTime.AddDays(-1));
        var probe = new ProbeConnector(publisher.Object) { Families = [ResumeFamily.Treatments] };

        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        publisher.Verify(p => p.Glucose.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "a run that does not declare where glucose resumes must not depend on reading it");
        publisher.Verify(p => p.Treatments.GetLatestTreatmentTimestampAsync("probe-connector", It.IsAny<CancellationToken>()),
            Times.Once, "the start and the point of one family are one read");
    }

    [Fact]
    public async Task ScheduledRun_WhoseUndeclaredFamilyCannotBeRead_StillSucceeds()
    {
        var publisher = Publisher(treatments: Now.UtcDateTime.AddDays(-1));
        publisher.Setup(p => p.Glucose.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));
        var probe = new ProbeConnector(publisher.Object) { Families = [ResumeFamily.Treatments] };

        var result = await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ResetRun_StartsAtTheCallersFrom_WhateverIsStored()
    {
        var from = Now.UtcDateTime.AddYears(-2);
        var probe = new ProbeConnector(Publisher(Now.UtcDateTime.AddHours(-1), Now.UtcDateTime.AddHours(-2)).Object)
        {
            Families = [ResumeFamily.Glucose, ResumeFamily.Treatments],
        };

        await probe.SyncDataAsync(
            new SyncRequest { From = from, To = Now.UtcDateTime }, new TestConfig(), CancellationToken.None);

        probe.Starts.Values.Should().AllBeEquivalentTo(from);
    }

    [Fact]
    public async Task ResetRun_NamingNoLowerBound_HasNone()
    {
        var probe = new ProbeConnector(Publisher(Now.UtcDateTime.AddHours(-1)).Object);

        await probe.SyncDataAsync(new SyncRequest { To = Now.UtcDateTime }, new TestConfig(), CancellationToken.None);

        probe.Starts[ResumeFamily.Glucose].Should().BeNull();
        probe.Points[ResumeFamily.Glucose].Should().Be(Now.UtcDateTime.AddHours(-1).AddMinutes(-5),
            "the family's own point stays readable for a source that widens a caller's bound to it");
    }

    [Fact]
    public async Task BackgroundRunWithACallersSince_StartsThere()
    {
        var since = Now.UtcDateTime.AddDays(-10);
        var probe = new ProbeConnector(Publisher(Now.UtcDateTime.AddHours(-1)).Object);

        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None, since);

        probe.Starts[ResumeFamily.Glucose].Should().Be(since);
    }

    [Fact]
    public async Task WindowPolicy_SetsOneStartForEveryFamily_AndLeavesTheirPoints()
    {
        var policyStart = Now.UtcDateTime.AddDays(-14);
        var glucose = Now.UtcDateTime.AddHours(-1);
        var probe = new ProbeConnector(
            Publisher(glucose, Now.UtcDateTime.AddDays(-2)).Object,
            (resume, run) => run(resume.StartingAt(policyStart)))
        {
            Families = [ResumeFamily.Glucose, ResumeFamily.Treatments],
        };

        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        probe.Starts.Values.Should().AllBeEquivalentTo(policyStart);
        probe.Points[ResumeFamily.Glucose].Should().Be(glucose.AddMinutes(-5));
    }

    [Fact]
    public async Task WindowPolicy_IsNotConsultedOnARunACallerBounded()
    {
        var consulted = false;
        var probe = new ProbeConnector(Publisher().Object, (resume, run) =>
        {
            consulted = true;
            return run(resume);
        });

        await probe.SyncDataAsync(
            new SyncRequest { From = Now.UtcDateTime.AddDays(-1) }, new TestConfig(), CancellationToken.None);
        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None, Now.UtcDateTime.AddDays(-1));

        consulted.Should().BeFalse();
    }

    [Fact]
    public void ARequestNoSyncRan_HoldsNoPoints()
    {
        var read = () => new SyncRequest().Resume.Point(ResumeFamily.Glucose);

        read.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task APointReadLateInTheRun_IsTheOneTheRunStartedWith_ThoughTheRunPublishedNewer()
    {
        var stored = Now.UtcDateTime.AddHours(-3);
        var latest = stored;
        var publisher = Publisher();
        publisher.Setup(p => p.Glucose.GetLatestEntryTimestampAsync("probe-connector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => latest);
        var probe = new ProbeConnector(publisher.Object) { Body = _ => latest = Now.UtcDateTime };

        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        probe.Points[ResumeFamily.Glucose].Should().Be(stored.AddMinutes(-5),
            "a meter reading the run published before it asked must not move its glucose start past readings it never fetched");
        publisher.Verify(p => p.Glucose.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ANonGlucosePointReadLateInTheRun_IsTheOneTheRunStartedWith_ThoughTheRunPublishedNewer()
    {
        var stored = Now.UtcDateTime.AddHours(-3);
        var latest = stored;
        var publisher = Publisher();
        publisher.Setup(p => p.Treatments.GetLatestTreatmentTimestampAsync("probe-connector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => latest);
        var probe = new ProbeConnector(publisher.Object)
        {
            Families = [ResumeFamily.Treatments],
            Body = _ => latest = Now.UtcDateTime,
        };

        await probe.SyncDataAsync(new TestConfig(), CancellationToken.None);

        probe.Points[ResumeFamily.Treatments].Should().Be(stored.AddMinutes(-5),
            "a treatment the run published before it asked must not move its treatment start past records it never fetched");
        publisher.Verify(
            p => p.Treatments.GetLatestTreatmentTimestampAsync("probe-connector", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AScheduledRunsGlucosePointThatCannotBeRead_FailsTheRunBeforeTheSyncRuns()
    {
        var publisher = Publisher(treatments: Now.UtcDateTime.AddDays(-1));
        publisher.Setup(p => p.Glucose.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("watermark store down"));
        var probe = new ProbeConnector(publisher.Object) { Families = [ResumeFamily.Glucose, ResumeFamily.Treatments] };
        var (reporter, reported) = Reporter();

        var result = await probe.SyncDataAsync(new TestConfig(), CancellationToken.None, progressReporter: reporter);

        probe.BodyRan.Should().BeFalse("the scheduled window starts at the glucose point, so nothing may be fetched without it");
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("watermark store down");
        reported.Should().ContainSingle().Which.Phase.Should().Be(SyncPhase.Failed);
    }

    [Theory]
    [InlineData(true, ResumeFamily.Treatments)]
    [InlineData(false, ResumeFamily.Treatments)]
    [InlineData(false, ResumeFamily.Glucose)]
    public async Task AnyOtherPointThatCannotBeRead_FailsWhereTheSyncAsksForIt(bool scheduled, ResumeFamily failing)
    {
        var publisher = Publisher(Now.UtcDateTime.AddHours(-1), Now.UtcDateTime.AddDays(-1));
        if (failing == ResumeFamily.Glucose)
            publisher.Setup(p => p.Glucose.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("watermark store down"));
        else
            publisher.Setup(p => p.Treatments.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("watermark store down"));
        var probe = new ProbeConnector(publisher.Object) { Families = [ResumeFamily.Glucose, ResumeFamily.Treatments] };

        var result = scheduled
            ? await probe.SyncDataAsync(new TestConfig(), CancellationToken.None)
            : await probe.SyncDataAsync(
                new SyncRequest { From = Now.UtcDateTime.AddDays(-1) }, new TestConfig(), CancellationToken.None);

        result.Success.Should().BeTrue("the probe's own boundary caught the failure");
        probe.Faults.Should().ContainSingle().Which.Key.Should().Be(failing);
        probe.Points.Should().ContainSingle().Which.Key.Should().NotBe(failing,
            "a family whose point was read is synced although another's failed");
    }

    private static (ISyncProgressReporter Reporter, List<SyncProgressEvent> Reported) Reporter()
    {
        var reported = new List<SyncProgressEvent>();
        var reporter = new Mock<ISyncProgressReporter>();
        reporter.Setup(r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SyncProgressEvent, CancellationToken>((e, _) => reported.Add(e))
            .Returns(Task.CompletedTask);
        return (reporter.Object, reported);
    }

    [Fact]
    public async Task APointTheConnectorDidNotDeclare_IsAProgrammingError()
    {
        var probe = new ProbeConnector(Publisher(Now.UtcDateTime.AddHours(-1), Now.UtcDateTime.AddDays(-1)).Object)
        {
            Families = [ResumeFamily.Glucose],
            Body = request => request.Resume.Point(ResumeFamily.Treatments),
        };

        var run = () => probe.SyncDataAsync(
            new SyncRequest { From = Now.UtcDateTime.AddDays(-1) }, new TestConfig(), CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Treatments*");
    }
}
