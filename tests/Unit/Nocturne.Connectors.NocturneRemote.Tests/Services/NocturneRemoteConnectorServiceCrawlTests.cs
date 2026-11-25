using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.NocturneRemote.Tests.Services;

public class NocturneRemoteConnectorServiceCrawlTests
{
    /// <summary>
    /// A page that never arrives is not the end of the range. The pages above it stay published, and
    /// the oldest of them is marked, because the next lower bound is derived from the newest record
    /// then stored locally and would otherwise never ask for the pages below the failure again.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAPageMidCrawlIsRejected_KeepsThePagesAboveItAndMarksWhereItStopped()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.BadGateway));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse("a crawl that lost a page has not synced the range");
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.Glucose}");
        result.ItemsSynced.Should().BeEmpty(
            "a range the sync could not read through must stay unreported rather than claim a count");
        fixture.PublishedGlucose.Should().HaveCount(2);
        fixture.Marks.Should().Contain(nameof(SyncDataType.Glucose), new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// The same swallow by a different route: a 200 whose envelope carries no page is a fetch that
    /// failed, not a range that ran out. A range that ran out answers an empty array.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAPageCarriesNoData_FailsTheRun()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Json("""{"data":null,"pagination":{"limit":2,"offset":2,"total":6}}"""));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ItemsSynced.Should().BeEmpty();
    }

    /// <summary>
    /// A body that will not parse — a captive portal's HTML, a truncated response — reaches the
    /// crawl as an exception rather than a null, and has to reach the same conclusion. This is the
    /// route by which most "carried no page" failures actually arrive: an envelope that parses at
    /// all supplies an empty <c>Data</c> rather than a null one.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAPageIsUnparseable_FailsTheRun()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Json("<html>upstream is down</html>"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ItemsSynced.Should().BeEmpty();
        result.Errors.Should().ContainSingle()
            .Which.Should().NotContain("see preceding connector logs",
                "the response parsing cause must be visible in the connector status");
    }

    /// <summary>
    /// The distinction the failure path must not erase: a range the remote genuinely has nothing
    /// left in answers with a short page, and that is a successful sync of everything there was.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRangeIsExhausted_ReportsSuccessAndPublishesEveryPage()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 3, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.GlucosePage(total: 3, "2026-01-03T08:10:00Z"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 3,
        });
        fixture.PublishedGlucose.Should().HaveCount(3);
    }

    /// <summary>
    /// A remote that omits <c>pagination.total</c> cannot say where the range ends, so a last page that
    /// is exactly full costs one more request, which comes back empty and ends the crawl.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenPagesCarryNoTotalAndTheLastPageIsFull_EndsOnTheEmptyPageAndCollectsEverything()
    {
        static HttpResponseMessage Page(params string[] timestamps) =>
            RemoteFakeHandler.Json("{\"data\":[" + string.Join(",", timestamps.Select(t =>
                $$"""{"id":"{{Guid.NewGuid()}}","timestamp":"{{t}}","mgdl":120}""")) + "],\"pagination\":{\"limit\":2,\"offset\":0}}");

        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                Page("2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                Page("2026-01-03T08:10:00Z", "2026-01-03T08:15:00Z"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 4,
        });
        fixture.PublishedGlucose.Should().HaveCount(4);
    }

    /// <summary>
    /// A window the remote holds nothing for still records a count: the tenant's sync card renders a
    /// badge per key, so "checked, found nothing" must not be reported the way "could not check" is.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteHasNoRecords_ReportsAnExplicitZero()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose, RemoteFakeHandler.GlucosePage(total: 0));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 0,
        });
    }

    /// <summary>
    /// Discarding the range is only the right trade because the page was asked for until the retry
    /// budget ran out. Failing on one unlucky 502 would leave a remote with ordinary page-level
    /// flakiness permanently red and syncing nothing for that type — worse than the truncation this
    /// change exists to remove.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenATransientFailureClearsWithinTheRetryBudget_CompletesTheCrawl()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 3, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.BadGateway),
                RemoteFakeHandler.Status(HttpStatusCode.ServiceUnavailable),
                RemoteFakeHandler.GlucosePage(total: 3, "2026-01-03T08:10:00Z"));
        var fixture = new ServiceFixture(handler, config: NewConfig(maxRetryAttempts: 3));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue("the page arrived within the attempt budget");
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 3,
        });
        fixture.PublishedGlucose.Should().HaveCount(3);
    }

    /// <summary>
    /// Each data type crawls its own endpoint, so one endpoint failing must cost only that type. A
    /// failed run withholds the connector's last-successful-sync stamp and shows the tenant a red
    /// connector, which has to name what actually broke. The glucose crawl fails on its second page,
    /// so the failure is reached past the auth check rather than by it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenOneTypesCrawlFails_LeavesTheOtherTypeSynced()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.BadGateway))
            .Serve(NocturneRemoteConstants.Boluses,
                RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.Glucose}");
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Boluses] = 1,
        }, "a rejected glucose crawl must not cost the boluses, nor claim a glucose count");
        fixture.PublishedBoluses.Should().ContainSingle();
    }

    /// <summary>
    /// A type the tenant switched off is never crawled, so a remote that rejects its endpoint cannot
    /// mark the connector red while everything the tenant enabled synced.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenASwitchedOffTypesEndpointIsBroken_ReportsSuccess()
    {
        var handler = new RemoteFakeHandler()
            .Break(NocturneRemoteConstants.Activity, HttpStatusCode.BadGateway)
            .Serve(NocturneRemoteConstants.Boluses,
                RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
        var fixture = new ServiceFixture(handler, config: NewConfig(syncActivity: false));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue("no type the tenant enabled failed to sync");
        handler.Requests.Where(url => url.Contains(NocturneRemoteConstants.SensorGlucose)).Should().ContainSingle();
        result.Errors.Should().BeEmpty();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Boluses] = 1,
        });
        handler.Requests.Should().NotContain(url => url.Contains(NocturneRemoteConstants.Activity),
            "a switched-off type is not crawled at all, which is why its endpoint cannot fail the run");
    }

    /// <summary>
    /// Foods are a single flat fetch rather than a crawl, and used to answer a rejection with an
    /// empty list — which <c>RecordPublishOutcome</c> records as a confident zero, telling the
    /// tenant in green that the remote was reached and had no foods.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheFoodsFetchIsRejected_FailsTheRunAndRecordsNoCount()
    {
        var handler = new RemoteFakeHandler().Break(NocturneRemoteConstants.Foods, HttpStatusCode.Forbidden);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Food] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.Food}")
            .And.Contain("HTTP 403 Forbidden",
                "a refused scope is the failure a tenant can act on, and they cannot read the logs");
        result.ItemsSynced.Should().BeEmpty(
            "a rejected fetch never reached the remote, so it cannot report that there were no foods");
    }

    /// <summary>The foods equivalent of an exhausted range: a remote with no foods is still a success.</summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteHasNoFoods_ReportsAnExplicitZero()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.Foods, RemoteFakeHandler.Json("[]"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Food] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Food] = 0,
        });
    }

    /// <summary>
    /// Device status crawls the remote's v1 endpoint on a time cursor rather than an offset, and
    /// carried the same swallow — worse, it read a rejected page and a range with nothing left in it
    /// through one condition. A grant the remote will keep refusing is not retried, so it arrives as
    /// the empty result the walk backwards through history used to stop on.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheDeviceStatusCrawlIsRejectedMidRange_FailsTheRun()
    {
        var handler = new RemoteFakeHandler()
            .Serve(RemoteFakeHandler.V1DeviceStatus,
                RemoteFakeHandler.DeviceStatusPage("2026-01-03T08:05:00Z", "2026-01-03T08:00:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.Forbidden));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.DeviceStatus] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.DeviceStatus}");
        result.ItemsSynced.Should().BeEmpty();
    }

    /// <summary>A remote with no device statuses in range answers an empty array, and that succeeds.</summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteHasNoDeviceStatuses_ReportsAnExplicitZero()
    {
        var handler = new RemoteFakeHandler()
            .Serve(RemoteFakeHandler.V1DeviceStatus, RemoteFakeHandler.Json("[]"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.DeviceStatus] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.DeviceStatus] = 0,
        });
    }

    /// <summary>
    /// On an open-ended catch-up a family that has fallen behind widens the bound back to its own
    /// resume point. Leaving it on the glucose-derived bound strands it: this run's glucose publish
    /// moves that bound past the range a failed treatment crawl still owes, so the gap cannot be
    /// repaired next cycle.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAFamilyHasFallenBehind_WidensToItsOwnResumePoint()
    {
        var latestTreatment = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var glucoseFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(handler, latestTreatment: latestTreatment);

        await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                From = glucoseFrom,
                To = null,
                DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
            },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.SensorGlucose).Should()
            .Contain($"from={glucoseFrom:o}");
        handler.CrawlOf(NocturneRemoteConstants.Boluses).Should()
            .Contain($"from={latestTreatment.AddMinutes(-5):o}",
                "the treatment family resumes from its own newest stored record, not glucose's");
    }

    /// <summary>
    /// Each treatment type is read from its own endpoint, so one can fail for a day while its
    /// siblings publish. Resuming it from the treatment family's newest record would let a sibling's
    /// fresher data carry the bound past the day it still owes.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenOneTreatmentTypeLagsItsSiblings_ResumesItFromItsOwnWatermark()
    {
        var latestBolus = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);
        var latestCarbs = new DateTime(2026, 5, 31, 23, 0, 0, DateTimeKind.Utc);
        var glucoseFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(
            handler,
            latestTreatment: latestCarbs,
            latestByType: new Dictionary<SyncDataType, DateTime>
            {
                [SyncDataType.Boluses] = latestBolus,
                [SyncDataType.CarbIntake] = latestCarbs,
            });

        await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                From = glucoseFrom,
                To = null,
                DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.CarbIntake],
            },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.Boluses).Should()
            .Contain($"from={latestBolus.AddMinutes(-5):o}",
                "boluses resume from their own newest record, not the carbs that published past them");
        handler.CrawlOf(NocturneRemoteConstants.CarbIntake).Should()
            .Contain($"from={latestCarbs.AddMinutes(-5):o}");
    }

    /// <summary>
    /// State spans are read from their own endpoint like every other type, so the glucose cursor
    /// advancing past a range their crawl failed to read must not take them with it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenStateSpansLagGlucose_ResumesThemFromTheirOwnWatermark()
    {
        var latestStateSpan = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);
        var glucoseFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(handler, latestStateSpan: latestStateSpan);

        await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                From = glucoseFrom,
                To = null,
                DataTypes = [SyncDataType.Glucose, SyncDataType.StateSpans],
            },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.SensorGlucose).Should()
            .Contain($"from={glucoseFrom:o}");
        handler.CrawlOf(NocturneRemoteConstants.StateSpans).Should()
            .Contain($"from={latestStateSpan.AddMinutes(-5):o}",
                "state spans resume from their own newest record, not glucose's");
    }

    [Fact]
    public async Task SyncDataAsync_ReplicatesTheRemotesAppliedProfileActivationsKeyedByTheirIds()
    {
        var remoteId = Guid.NewGuid();
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.ProfileActivations, RemoteFakeHandler.Json($$"""
                [{"id":"{{remoteId}}","baseProfileName":"Sick","percentage":120,"timeshiftHours":1.5,
                  "phase":"Effective","origin":"Uploader","app":"AAPS","isTemporary":true,
                  "startMills":1767427200000,"endMills":1767430800000}]
                """));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.StateSpans] }, fixture.Config, CancellationToken.None);

        handler.Requests.Should().Contain(u => u.Contains(NocturneRemoteConstants.ProfileActivations + "?applied=true"));
        var activation = fixture.PublishedActivations.Should().ContainSingle().Subject;
        activation.SyncIdentifier.Should().Be(remoteId.ToString());
        activation.BaseProfileName.Should().Be("Sick");
        activation.Percentage.Should().Be(120);
        activation.TimeshiftMs.Should().Be(5_400_000);
        activation.RequestedDurationMs.Should().Be(3_600_000);
        activation.EndMills.Should().Be(1767430800000);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncDataAsync_FromARemoteWithoutProfileActivations_ReplaysOnlyItsSpans()
    {
        var handler = new RemoteFakeHandler()
            .Break(NocturneRemoteConstants.ProfileActivations, HttpStatusCode.NotFound);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.StateSpans] }, fixture.Config, CancellationToken.None);

        result.Errors.Should().BeEmpty("a remote that predates activations sends its switches as Profile spans");
        fixture.PublishedActivations.Should().BeEmpty();
    }

    /// <summary>
    /// A caller's lower bound is never narrowed by a family's resume point. An explicit <c>from</c>
    /// with no <c>to</c> is a legitimate request shape and the one an admin repairing a months-old
    /// gap sends; answering it from the watermark fetches nothing and reports the run as a success
    /// with a zero count, which is the failure this branch exists to stop.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenGivenALowerBoundBelowTheResumePoint_HonoursTheCallersBound()
    {
        var askedFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(
            handler, latestTreatment: new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc));

        await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                From = askedFrom,
                To = null,
                DataTypes = [SyncDataType.Boluses],
            },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.Boluses).Should()
            .Contain($"from={askedFrom:o}", "the caller asked for this lower bound");
    }

    /// <summary>
    /// A run carrying no lower bound — the tenant's sync button — resumes each family from what it
    /// has stored, and a family with nothing stored takes everything the remote holds: the remote is
    /// another Nocturne instance, so its first sync is meant to.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheCallerSuppliesNoLowerBound_ResumesEachFamilyFromItsOwnPoint()
    {
        var latestBolus = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(
            handler, latestByType: new Dictionary<SyncDataType, DateTime> { [SyncDataType.Boluses] = latestBolus });

        await fixture.Service.SyncDataAsync(
            new SyncRequest { From = null, To = null, DataTypes = [SyncDataType.Boluses, SyncDataType.CarbIntake] },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.Boluses).Should().Contain($"from={latestBolus.AddMinutes(-5):o}");
        handler.CrawlOf(NocturneRemoteConstants.CarbIntake).Should().NotContain("from=");
    }

    /// <summary>
    /// A background run's lower bound is glucose's resume point, which says nothing about any other
    /// family. Widening them to it ties each to glucose: a remote whose glucose endpoint fails every
    /// run keeps that point at the start of history, and every run re-imports every family from there.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_OnABackgroundRun_ResumesEachFamilyIndependentlyOfGlucose(bool glucoseStored)
    {
        var latestGlucose = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var latestTreatment = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var latestStatus = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(
            handler,
            latestGlucose: glucoseStored ? latestGlucose : null,
            latestTreatment: latestTreatment,
            latestDeviceStatus: latestStatus);

        await fixture.Service.SyncDataAsync(fixture.Config, CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.Boluses).Should()
            .Contain($"from={latestTreatment.AddMinutes(-5):o}");
        handler.Requests.First(u => u.Contains(RemoteFakeHandler.V1DeviceStatus)).Should()
            .Contain($"find[created_at][$gte]={latestStatus.AddMinutes(-5):o}");
    }

    [Fact]
    public async Task ScheduledSyncTwice_SecondRunCrawlsGlucoseOnlyFromTheFirstRunsNewestReading()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 2, "2026-01-03T08:05:00Z", "2026-01-03T08:00:00Z"));
        var fixture = new ServiceFixture(handler, glucoseWatermarkFollowsPublishes: true);

        await fixture.Service.SyncDataAsync(fixture.Config, CancellationToken.None);
        handler.CrawlOf(NocturneRemoteConstants.SensorGlucose).Should().NotContain("from=",
            "nothing was stored, so the first run reads the whole history");
        await fixture.Service.SyncDataAsync(fixture.Config, CancellationToken.None);

        var newest = new DateTime(2026, 1, 3, 8, 5, 0, DateTimeKind.Utc);
        fixture.PublishedGlucose.Max(g => g.Timestamp).Should().Be(newest);
        handler.LastCrawlOf(NocturneRemoteConstants.SensorGlucose).Should().Contain($"from={newest.AddMinutes(-5):o}");
    }

    /// <summary>
    /// A run cut off mid-crawl — the per-tenant timeout — keeps the pages it published, and the next
    /// run crawls only the range it still owes: between the catch-up's floor and those pages.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenARunIsCancelledMidCrawl_TheNextRunResumesTheRangeItOwes()
    {
        var latestStatus = new DateTime(2026, 1, 3, 7, 0, 0, DateTimeKind.Utc);
        var floor = latestStatus.AddMinutes(-5);
        var oldestPublished = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc);
        var newestPublished = new DateTime(2026, 1, 3, 8, 5, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler()
            .Serve(RemoteFakeHandler.V1DeviceStatus,
                RemoteFakeHandler.DeviceStatusPage("2026-01-03T08:05:00Z", "2026-01-03T08:00:00Z"));
        using var timeout = new CancellationTokenSource();
        var fixture = new ServiceFixture(handler, latestDeviceStatus: latestStatus, onDeviceStatusPublished: timeout.Cancel);
        var request = new SyncRequest { DataTypes = [SyncDataType.DeviceStatus] };

        var cut = () => fixture.Service.SyncDataAsync(request, fixture.Config, timeout.Token);

        await cut.Should().ThrowAsync<OperationCanceledException>();
        fixture.PublishedStatuses.Should().HaveCount(2);
        fixture.Marks.Should().BeEquivalentTo(new Dictionary<string, DateTime>
        {
            ["DeviceStatus.GapFrom"] = floor,
            ["DeviceStatus.GapTo"] = oldestPublished,
            ["DeviceStatus.Through"] = newestPublished,
        }, "a bounded catch-up owes a bounded range, not the history below it, and records how far it reached");

        handler.Requests.Clear();
        await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        handler.Requests.Should().Contain(u => u.Contains(RemoteFakeHandler.V1DeviceStatus)
            && u.Contains($"find[created_at][$gte]={floor:o}")
            && u.Contains($"find[created_at][$lte]={oldestPublished:o}"));
        fixture.Marks.Should().Equal(
            new Dictionary<string, DateTime> { ["DeviceStatus.Through"] = newestPublished },
            "the owed range was crawled to its floor");
    }

    /// <summary>
    /// A failed catch-up owes its own range only. Recording it in the collection's mark would replace
    /// a deeper backfill's progress and turn a minutes-wide gap into a crawl of the whole history.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenACatchUpFails_KeepsADeeperBackfillMark()
    {
        var latestBolus = new DateTime(2026, 1, 3, 7, 0, 0, DateTimeKind.Utc);
        var deepMark = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.Boluses,
                RemoteFakeHandler.BolusPage(total: 6, "2026-01-03T08:05:00Z", "2026-01-03T08:00:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.BadGateway));
        var fixture = new ServiceFixture(handler, latestTreatment: latestBolus);
        fixture.Marks["Boluses"] = deepMark;

        await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Boluses] }, fixture.Config, CancellationToken.None);

        fixture.Marks.Should().BeEquivalentTo(new Dictionary<string, DateTime>
        {
            ["Boluses"] = deepMark,
            ["Boluses.GapFrom"] = latestBolus.AddMinutes(-5),
            ["Boluses.GapTo"] = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc),
            ["Boluses.Through"] = new DateTime(2026, 1, 3, 8, 5, 0, DateTimeKind.Utc),
        });
    }

    /// <summary>
    /// A catch-up whose first page is full records what it owes before it asks for the next, so a
    /// process killed during that fetch does not strand the range below what it published. A
    /// catch-up of one short page, the common case, owes nothing and writes once, to record how far
    /// it reached.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_ACatchUpRecordsItsRangeBeforeFetchingPastAFullPage()
    {
        var latestStatus = new DateTime(2026, 1, 3, 7, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler()
            .Serve(RemoteFakeHandler.V1DeviceStatus,
                RemoteFakeHandler.DeviceStatusPage("2026-01-03T08:05:00Z", "2026-01-03T08:00:00Z"),
                RemoteFakeHandler.DeviceStatusPage("2026-01-03T07:30:00Z"));
        var fixture = new ServiceFixture(handler, latestDeviceStatus: latestStatus);
        Dictionary<string, DateTime>? atSecondFetch = null;
        handler.OnRequest = url =>
        {
            if (url.Contains(RemoteFakeHandler.V1DeviceStatus) && fixture.PublishedStatuses.Count == 2)
                atSecondFetch ??= new(fixture.Marks);
        };

        await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.DeviceStatus] }, fixture.Config, CancellationToken.None);

        atSecondFetch.Should().ContainKey("DeviceStatus.GapTo");
        fixture.Marks.Should().Equal(new Dictionary<string, DateTime>
        {
            ["DeviceStatus.Through"] = new(2026, 1, 3, 8, 5, 0, DateTimeKind.Utc),
        });

        var single = new ServiceFixture(
            new RemoteFakeHandler().Serve(RemoteFakeHandler.V1DeviceStatus,
                RemoteFakeHandler.DeviceStatusPage("2026-01-03T08:05:00Z")),
            latestDeviceStatus: latestStatus);
        await single.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.DeviceStatus] }, single.Config, CancellationToken.None);

        single.MarkWrites.Should().Be(1);
        single.Marks.Keys.Should().Equal("DeviceStatus.Through");
    }

    /// <summary>
    /// A family with nothing stored owes its whole history, whatever another family has stored. Bounding
    /// it at the glucose cursor instead — after a first run that stored glucose and then timed out
    /// before device status ran — would put everything older than that run out of reach for good.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAFamilyHasNothingStored_CrawlsItsWholeHistoryWhateverGlucoseHolds()
    {
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(handler, latestGlucose: new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc));

        await fixture.Service.SyncDataAsync(fixture.Config, CancellationToken.None);

        handler.Requests.First(u => u.Contains(RemoteFakeHandler.V1DeviceStatus)).Should().NotContain("$gte");
        handler.LastCrawlOf(NocturneRemoteConstants.Activity).Should().NotContain("from=");
    }

    /// <summary>
    /// A background run with activity stored up to a moment asks the remote for the window from just
    /// before it, and reads that one page, instead of walking the remote's whole activity history.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenActivityIsStored_CrawlsOnlyTheWindowFromItsNewestRecord()
    {
        var latestActivity = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.Activity,
                RemoteFakeHandler.ActivityPage(total: 1, "2026-01-03T08:02:00Z"));
        var fixture = new ServiceFixture(handler, latestActivity: latestActivity);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.CrawlOf(NocturneRemoteConstants.Activity).Should()
            .Contain($"from={latestActivity.AddMinutes(-5):o}");
        handler.Requests.Count(u => u.Contains(NocturneRemoteConstants.Activity)).Should().Be(1);
        fixture.PublishedActivities.Should().ContainSingle();
        result.ItemsSynced.Should().Contain(SyncDataType.Activity, 1);
    }

    /// <summary>
    /// A remote that predates the activity window ignores its bounds and answers newest first from its
    /// whole history. The page that reaches below the window holds the last record the window asked
    /// for, so the read ends there in success and the older pages are not fetched.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteIgnoresTheActivityWindow_StopsAtThePageThatPassesTheWindow()
    {
        var latestActivity = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.Activity,
                RemoteFakeHandler.ActivityPage(total: 3, "2026-01-03T08:02:00Z", "2026-01-03T07:00:00Z"),
                RemoteFakeHandler.ActivityPage(total: 3, "2026-01-01T07:00:00Z"));
        var fixture = new ServiceFixture(handler, latestActivity: latestActivity);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedActivities.Should().HaveCount(2);
        handler.Requests.Count(u => u.Contains(NocturneRemoteConstants.Activity)).Should().Be(1);
        fixture.Marks.Keys.Should().Equal($"{SyncDataType.Activity}.Through");
    }

    /// <summary>
    /// The activity crawl publishes page by page, so a run the budget or a failure cuts off keeps what
    /// it published. A crawl that read everything before publishing would publish one batch.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenActivityTakesTwoPages_PublishesEachPageAsItsOwnBatch()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.Activity,
                RemoteFakeHandler.ActivityPage(total: 3, "2026-01-03T08:02:00Z", "2026-01-03T07:00:00Z"),
                RemoteFakeHandler.ActivityPage(total: 3, "2026-01-01T07:00:00Z"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedActivityBatchSizes.Should().Equal(2, 1);
    }

    /// <summary>
    /// A crawl cut off after a page marks where it reached under the activity type, so the next run
    /// does not claim the range below it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAnActivityPageIsRejected_MarksWhereTheCrawlStopped()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.Activity,
                RemoteFakeHandler.ActivityPage(total: 6, "2026-01-03T08:05:00Z", "2026-01-03T08:00:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.BadGateway));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        fixture.PublishedActivityBatchSizes.Should().Equal(2);
        fixture.Marks.Should().Contain(nameof(SyncDataType.Activity), new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// A merged read stops serving at its window, so a range holding more than that is read again
    /// from its first page under the oldest time served. Every record arrives, none past the cap is
    /// lost, and the crawl records the range as covered only because it read all of it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheActivityWindowHoldsMoreThanTheRemoteServes_ContinuesBelowTheOldestServed()
    {
        var store = new ActivityStore(count: V4ApiLimits.MaxMergedPageWindow + 5);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedActivities.Select(a => a.Id).Distinct().Should().BeEquivalentTo(store.Ids);
        fixture.Marks.Should().NotContainKey(nameof(SyncDataType.Activity), "the crawl reached the oldest record");
    }

    /// <summary>
    /// Records sharing the instant a read stepped to are served by the next read too, because the
    /// bound is inclusive. The one past the cap is imported rather than dropped, and the one served
    /// twice arrives twice, which the idempotent publish absorbs.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenRecordsShareTheInstantTheActivityReadSteppedTo_ImportsThemAll()
    {
        var tiedAt = V4ApiLimits.MaxMergedPageWindow - 1;
        var store = new ActivityStore(count: V4ApiLimits.MaxMergedPageWindow + 3, tiedAt: tiedAt);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedActivities.Select(a => a.Id).Distinct().Should().BeEquivalentTo(store.Ids);
        fixture.PublishedActivities.Count(a => a.Id == store.Ids[tiedAt])
            .Should().Be(2, "the record the read stepped to is served again at the new bound");
    }

    /// <summary>
    /// A remote that ignores the window and holds more than it serves answers the same top page after
    /// the step. The crawl fails instead of ending as though the range were read, and keeps only the
    /// mark of what it read, so no later run takes the range below it as covered.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteIgnoresTheWindowAndHoldsMoreThanItServes_FailsWithoutClaimingCoverage()
    {
        var store = new ActivityStore(count: V4ApiLimits.MaxMergedPageWindow + 5, honoursWindow: false);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().StartWith($"Failed to sync {SyncDataType.Activity}");
        fixture.PublishedActivities.Should().HaveCount(V4ApiLimits.MaxMergedPageWindow);
        fixture.Marks.Should().Contain(
            nameof(SyncDataType.Activity), fixture.PublishedActivities.Min(a => a.Mills).ToUtcDateTime());
        handler.Requests.Count(u => u.Contains(NocturneRemoteConstants.Activity))
            .Should().Be(V4ApiLimits.MaxMergedPageWindow / RemoteFakeHandler.PageSize + 2,
                "the pages under the cap, the empty page at it, and the read after the step");
    }

    /// <summary>
    /// A remote older than the cap on merged pages serves past it, and is paged on rather than
    /// stepped, so a first import reads its whole history although it ignores the window.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteServesPastTheCap_PagesOnAndReadsItInFull()
    {
        var store = new ActivityStore(count: V4ApiLimits.MaxMergedPageWindow + 5, honoursWindow: false, capped: false);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedActivities.Select(a => a.Id).Should().BeEquivalentTo(store.Ids);
        fixture.Marks.Should().NotContainKey(nameof(SyncDataType.Activity));
    }

    /// <summary>
    /// A first import from a remote that takes the window and holds fewer records than its list
    /// serves reads them all and leaves nothing marked.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteTakesTheWindowAndHoldsLessThanItServes_ReadsItInFull()
    {
        var store = new ActivityStore(count: 7);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedActivities.Select(a => a.Id).Should().BeEquivalentTo(store.Ids);
        fixture.Marks.Keys.Should().Equal($"{SyncDataType.Activity}.Through");
    }

    /// <summary>
    /// A catch-up whose window holds more than the remote's list serves steps below the cap and reads
    /// the whole window, so it succeeds and owes nothing.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheCatchUpWindowHoldsMoreThanTheRemoteServes_ReadsTheWholeWindow()
    {
        var from = ActivityStore.At(V4ApiLimits.MaxMergedPageWindow + 5);
        var store = new ActivityStore(count: V4ApiLimits.MaxMergedPageWindow + 20);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler, latestActivity: from.AddMinutes(5));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedActivities.Select(a => a.Id).Distinct().Should().BeEquivalentTo(store.IdsFrom(from));
        fixture.Marks.Keys.Should().Equal($"{SyncDataType.Activity}.Through");
    }

    /// <summary>
    /// A catch-up from a remote that holds more than its list serves, but fewer inside the window,
    /// reads the window and stops there, whether or not the remote takes the window. A remote that
    /// ignores it has served everything above the lower bound before the first record below it, so
    /// the window was read in full and the run succeeds.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_WhenTheRemoteHoldsMoreThanItServesButTheCatchUpWindowFewer_ReadsTheWindowAndSucceeds(
        bool honoursWindow)
    {
        var store = new ActivityStore(count: V4ApiLimits.MaxMergedPageWindow + 5, honoursWindow: honoursWindow);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler, latestActivity: ActivityStore.Newest);
        var window = store.IdsFrom(ActivityStore.Newest.AddMinutes(-5));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        fixture.PublishedActivities.Select(a => a.Id).Should().Contain(window);
        fixture.PublishedActivities.Should().HaveCountLessThanOrEqualTo(window.Count + 1);
        handler.Requests.Count(u => u.Contains(NocturneRemoteConstants.Activity))
            .Should().BeLessThanOrEqualTo(window.Count / RemoteFakeHandler.PageSize + 1);
        fixture.Marks.Keys.Should().Equal($"{SyncDataType.Activity}.Through");
    }

    /// <summary>
    /// A catch-up from a remote that ignores the window, after an outage longer than its list serves,
    /// cannot reach the window's oldest records. It publishes the newest the remote serves, fails, and
    /// owes the range from its lower bound up to the oldest record it published.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteIgnoresTheWindowAndTheCatchUpWindowHoldsMoreThanItServes_FailsAndOwesTheRest()
    {
        var from = ActivityStore.At(V4ApiLimits.MaxMergedPageWindow + 5);
        var store = new ActivityStore(count: V4ApiLimits.MaxMergedPageWindow + 20, honoursWindow: false);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler, latestActivity: from.AddMinutes(5));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().StartWith($"Failed to sync {SyncDataType.Activity}");
        fixture.PublishedActivities.Select(a => a.Id).Should()
            .BeEquivalentTo(store.Ids.Take(V4ApiLimits.MaxMergedPageWindow));
        fixture.Marks.Should().BeEquivalentTo(new Dictionary<string, DateTime>
        {
            [$"{SyncDataType.Activity}.GapFrom"] = from,
            [$"{SyncDataType.Activity}.GapTo"] = ActivityStore.At(V4ApiLimits.MaxMergedPageWindow - 1),
            [$"{SyncDataType.Activity}.Through"] = ActivityStore.Newest,
        });
    }

    /// <summary>
    /// After a first import that could not reach past what a remote that ignores the window serves,
    /// a later run catches up the recent window and then fails on the range below the mark, which
    /// stays where it was: the remote cannot serve that range until it takes the window.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteIgnoresTheWindowAndAMarkIsHeldBelowWhatItServes_FailsAndKeepsTheMark()
    {
        var mark = ActivityStore.At(V4ApiLimits.MaxMergedPageWindow - 1);
        var store = new ActivityStore(count: V4ApiLimits.MaxMergedPageWindow + 5, honoursWindow: false);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler, latestActivity: ActivityStore.Newest);
        fixture.Marks[nameof(SyncDataType.Activity)] = mark;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().StartWith($"Failed to sync {SyncDataType.Activity}");
        fixture.PublishedActivities.Select(a => a.Id).Should()
            .Contain(store.IdsFrom(ActivityStore.Newest.AddMinutes(-5)));
        fixture.Marks.Should().Contain(nameof(SyncDataType.Activity), mark)
            .And.ContainKey($"{SyncDataType.Activity}.Through", "the catch-up of the recent window succeeded")
            .And.HaveCount(2);
    }

    /// <summary>
    /// The remote stores times finer than the millisecond a record carries, so a record past the cap
    /// that shares the oldest served record's millisecond, but lies later in it, is still inside the
    /// stepped bound and is imported.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenARecordPastTheCapLiesLaterInTheOldestServedMillisecond_ImportsIt()
    {
        var tiedAt = V4ApiLimits.MaxMergedPageWindow - 1;
        var store = new ActivityStore(
            count: V4ApiLimits.MaxMergedPageWindow + 3, tiedAt: tiedAt, tieOffset: TimeSpan.FromMicroseconds(700));
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedActivities.Select(a => a.Id).Distinct().Should().BeEquivalentTo(store.Ids);
    }

    /// <summary>
    /// A list the remote serves in full is read once and never stepped, whether or not the remote
    /// takes the window, and leaves nothing marked.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteIgnoresTheWindowAndHoldsLessThanItServes_ReadsItInFull()
    {
        var store = new ActivityStore(count: 7, honoursWindow: false);
        var handler = new RemoteFakeHandler().ServeComputed(NocturneRemoteConstants.Activity, store.Respond);
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Activity] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedActivities.Should().HaveCount(7);
        fixture.Marks.Should().NotContainKey(nameof(SyncDataType.Activity));
    }

    /// <summary>
    /// A remote's stored activity, newest first, served the way its merged list serves it: each source
    /// compares the inclusive bounds with the time it stores, to the tick here, and each record carries
    /// its time truncated to the millisecond.
    /// </summary>
    private sealed class ActivityStore
    {
        internal static readonly DateTime Newest = new(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc);

        private readonly List<(string Id, DateTime At)> _records;
        private readonly bool _honoursWindow;
        private readonly bool _capped;

        internal IReadOnlyList<string> Ids => _records.Select(r => r.Id).ToList();

        internal IReadOnlyList<string> IdsFrom(DateTime from) =>
            _records.Where(r => r.At >= from).Select(r => r.Id).ToList();

        /// <summary>The time of the record at <paramref name="index"/>, one second below the one above it.</summary>
        internal static DateTime At(int index) => Newest.AddSeconds(-index);

        /// <param name="tiedAt">An index whose record shares its millisecond with the one below it.</param>
        /// <param name="tieOffset">How far into that millisecond the record below it lies.</param>
        /// <param name="capped">Whether the list serves nothing past <see cref="V4ApiLimits.MaxMergedPageWindow"/>, as a remote since the cap does.</param>
        internal ActivityStore(
            int count, int? tiedAt = null, bool honoursWindow = true, TimeSpan tieOffset = default, bool capped = true)
        {
            _honoursWindow = honoursWindow;
            _capped = capped;
            _records = Enumerable.Range(0, count)
                .Select(i => ($"activity-{i}", tiedAt is { } tie && i > tie
                    ? At(i - 1) + (i == tie + 1 ? tieOffset : TimeSpan.Zero)
                    : At(i)))
                .ToList();
        }

        internal HttpResponseMessage Respond(Uri uri)
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var offset = int.Parse(query["offset"]!);
            var limit = int.Parse(query["limit"]!);
            var from = Bound(query["from"]);
            var to = Bound(query["to"]);

            var inWindow = _records
                .Where(r => !_honoursWindow || ((from is null || r.At >= from) && (to is null || r.At <= to)))
                .ToList();
            var served = inWindow
                .Skip(offset)
                .Take(_capped
                    ? Math.Clamp(limit, 0, V4ApiLimits.MaxMergedPageWindow - Math.Min(offset, V4ApiLimits.MaxMergedPageWindow))
                    : limit);

            var records = string.Join(",", served.Select(r =>
                $$"""{"_id":"{{r.Id}}","mills":{{new DateTimeOffset(r.At).ToUnixTimeMilliseconds()}},"type":"Walking"}"""));
            return RemoteFakeHandler.Json(
                $$$"""{"data":[{{{records}}}],"pagination":{"limit":{{{limit}}},"offset":{{{offset}}},"total":{{{inWindow.Count}}}}}""");
        }

        private static DateTime? Bound(string? value) =>
            value is null
                ? null
                : DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal).UtcDateTime;
    }

    /// <summary>
    /// A backfill stops taking pages once half the per-tenant timeout has passed, and the type still
    /// reports success: a run the timeout cuts off fails every type after it and backs the connector
    /// off, which would leave live glucose waiting on a backfill.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenABackfillOutrunsTheBudget_StopsBetweenPagesAndLetsTheRestSync()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero));
        var handler = new RemoteFakeHandler()
            .Serve(RemoteFakeHandler.V1DeviceStatus,
                RemoteFakeHandler.DeviceStatusPage("2026-01-03T08:05:00Z", "2026-01-03T08:00:00Z"),
                RemoteFakeHandler.DeviceStatusPage("2026-01-03T07:55:00Z", "2026-01-03T07:50:00Z"));
        var budget = TimeSpan.FromSeconds(30);
        var fixture = new ServiceFixture(handler, time: time, onDeviceStatusPublished: () => time.Advance(budget));
        fixture.Service.BackfillBudget = budget;

        var result = await fixture.Service.SyncDataAsync(fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.PublishedStatuses.Should().HaveCount(2);
        fixture.Marks.Should().Contain(nameof(SyncDataType.DeviceStatus), new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc));
        handler.Requests.Should().Contain(u => u.Contains(NocturneRemoteConstants.Foods),
            "the types after the backfill still sync");
    }

    /// <summary>
    /// A type can publish without leaving the stored watermark its resume point reads — a device
    /// status with no APS, pump or uploader fields writes no snapshot. Its next run resumes from the
    /// newest record its full crawl published rather than crawling the whole history again; a cursor
    /// reset still crawls everything.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_AfterAFullCrawlOfATypeThatStoresNoWatermark_ResumesFromWhereItReached()
    {
        var handler = new RemoteFakeHandler()
            .Serve(RemoteFakeHandler.V1DeviceStatus,
                RemoteFakeHandler.DeviceStatusPage("2026-01-03T08:05:00Z", "2026-01-03T08:00:00Z"));
        var fixture = new ServiceFixture(handler);
        var request = new SyncRequest { DataTypes = [SyncDataType.DeviceStatus] };

        await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);
        DeviceStatusCrawls(handler).First().Should().NotContain("$gte");

        handler.Requests.Clear();
        await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);
        DeviceStatusCrawls(handler).First().Should()
            .Contain($"find[created_at][$gte]={new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc):o}");

        handler.Requests.Clear();
        await fixture.Service.SyncDataAsync(
            new SyncRequest { To = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), DataTypes = [SyncDataType.DeviceStatus] },
            fixture.Config, CancellationToken.None);
        DeviceStatusCrawls(handler).First().Should().NotContain("$gte");
    }

    private static List<string> DeviceStatusCrawls(RemoteFakeHandler handler) =>
        handler.Requests.Where(u => u.Contains(RemoteFakeHandler.V1DeviceStatus)).ToList();

    /// <summary>
    /// A V4 list refuses a range wider than its span limit, so an owed range wider than that is read
    /// a window at a time. Asked for in one request, every run's first page of it would fail and the
    /// deep backfill behind it would never be reached.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_AnOwedRangeWiderThanTheV4SpanLimit_IsCrawledInWindows()
    {
        var gapFrom = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var gapTo = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(handler, latestTreatment: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        fixture.Marks["Boluses.GapFrom"] = gapFrom;
        fixture.Marks["Boluses.GapTo"] = gapTo;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Boluses] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        var windows = handler.Requests
            .Where(u => u.Contains(NocturneRemoteConstants.Boluses) && u.Contains("&to="))
            .Select(u => (From: DateTime.Parse(Query(u, "from")).ToUniversalTime(), To: DateTime.Parse(Query(u, "to")).ToUniversalTime()))
            .ToList();
        windows.Should().HaveCountGreaterThan(1);
        windows.Should().OnlyContain(w => (w.To - w.From).TotalDays <= V4ApiLimits.MaxDateSpanDays);
        windows.Max(w => w.To).Should().Be(gapTo);
        windows.Min(w => w.From).Should().Be(gapFrom);
        windows.Zip(windows.Skip(1)).Should().OnlyContain(pair => pair.Second.To < pair.First.From,
            "a record on a boundary is read in one window only");
        fixture.Marks.Should().BeEmpty();
    }

    private static string Query(string url, string name) =>
        Uri.UnescapeDataString(System.Text.RegularExpressions.Regex.Match(url, $"[?&]{name}=([^&]+)").Groups[1].Value);

    /// <summary>A full crawl of a type the remote holds nothing of writes no mark, on every run.</summary>
    [Fact]
    public async Task SyncDataAsync_WhenAFullCrawlFindsNothing_WritesNoMark()
    {
        var fixture = new ServiceFixture(new RemoteFakeHandler());

        await fixture.Service.SyncDataAsync(fixture.Config, CancellationToken.None);

        fixture.MarkWrites.Should().Be(0);
    }

    /// <summary>
    /// The attempt budget the run was handed governs. A budget of three would reach the third
    /// scripted response and complete the crawl; one stops at the 502.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_SpendsTheBudgetTheRunWasHanded()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                RemoteFakeHandler.Status(HttpStatusCode.BadGateway),
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:10:00Z"));
        var fixture = new ServiceFixture(handler, config: NewConfig(maxRetryAttempts: 1));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse("the run was allowed one attempt");
        result.ItemsSynced.Should().BeEmpty();
    }

    /// <summary>The page size the run was handed governs for the same reason, and on the same paths.</summary>
    [Fact]
    public async Task SyncDataAsync_UsesThePageSizeTheRunWasHanded()
    {
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(handler);

        await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.SensorGlucose).Should()
            .Contain($"limit={RemoteFakeHandler.PageSize}");
    }

    /// <summary>
    /// A tripwire for the cached-configuration shape this connector used to carry: a field seeded at
    /// construction from the frozen startup defaults and assigned by the background entry point
    /// alone, so a run that never assigned it crawled with a page size and credential it had not
    /// been given. That is no longer constructible — the service takes no registration, and the
    /// registration is transient anyway, so each run resolves its own instance — which is why the
    /// arrangement here is two runs through one instance: the cheapest one that still goes red the
    /// moment any configuration starts outliving the run it belongs to.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAManualRunFollowsABackgroundRun_CrawlsWithTheManualRunsConfiguration()
    {
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(handler);

        await fixture.Service.SyncDataAsync(fixture.Config, CancellationToken.None);

        var manual = NewConfig();
        manual.MaxCount = ManualRunPageSize;
        manual.AccessToken = ManualRunToken;

        await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            manual,
            CancellationToken.None);

        handler.LastCrawlOf(NocturneRemoteConstants.SensorGlucose).Should()
            .Contain($"limit={ManualRunPageSize}");
        handler.Tokens.Last().Should().Be($"Bearer {ManualRunToken}");
    }

    /// <summary>
    /// The credential check reads the sensor-glucose endpoint, so a remote that is merely unwell
    /// there must not be reported as a rejected credential: the tenant would be sent to re-authorize
    /// a grant that was never the problem, and would lose every other type with it. A 403 is the same
    /// answer by a different route — a grant scoped to treatments only, which is a configuration the
    /// remote supports.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SyncDataAsync_WhenTheProbedEndpointRefusesButTheTypeIsSwitchedOff_ReportsSuccess(
        HttpStatusCode refusal)
    {
        var handler = new RemoteFakeHandler()
            .Break(NocturneRemoteConstants.SensorGlucose, refusal)
            .Serve(NocturneRemoteConstants.Boluses,
                RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
        var fixture = new ServiceFixture(handler, config: NewConfig(syncGlucose: false));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeTrue("no type the tenant enabled failed to sync");
        result.Errors.Should().BeEmpty();
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Boluses] = 1,
        });
    }

    /// <summary>
    /// And when the tenant did enable the type, the refusal is still that type's rather than the
    /// run's: the crawl reaches the same endpoint under the tenant's retry budget and reports what it
    /// found, leaving every other type synced.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheProbedEndpointIsUnwell_CostsOnlyTheTypeThatReadsIt()
    {
        var handler = new RemoteFakeHandler()
            .Break(NocturneRemoteConstants.SensorGlucose, HttpStatusCode.BadGateway)
            .Serve(NocturneRemoteConstants.Boluses,
                RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.Glucose}");
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Boluses] = 1,
        });
        fixture.PublishedBoluses.Should().ContainSingle();
    }

    /// <summary>
    /// The one answer that is the connector's own to report. A credential the remote rejects will be
    /// rejected by every crawl too, so the run ends at the check rather than repeating the rejection
    /// once per type, and the tenant is told the thing they can act on.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteRejectsTheCredential_FailsBeforeAnyTypeIsCrawled()
    {
        var handler = new RemoteFakeHandler()
            .Break(NocturneRemoteConstants.SensorGlucose, HttpStatusCode.Unauthorized)
            .Serve(NocturneRemoteConstants.Boluses,
                RemoteFakeHandler.BolusPage(total: 1, "2026-01-03T09:00:00Z"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Authentication failed");
        handler.Requests.Should().ContainSingle("the auth probe has one attempt and a 401 ends the run");
        result.Errors.Should().ContainSingle()
            .Which.Should().Be($"Authentication failed for {DataSources.NocturneRemoteConnector}");
        handler.Requests.Should().NotContain(url => url.Contains(NocturneRemoteConstants.Boluses),
            "a rejected credential fails the run before the enabled types are reached");
    }

    [Fact]
    public async Task SyncDataAsync_WhenRetryReturnsMalformedJson_ReportsTheTerminalFailure()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.Status(HttpStatusCode.ServiceUnavailable),
                RemoteFakeHandler.Json("invalid-json"));
        var fixture = new ServiceFixture(handler, config: NewConfig(maxRetryAttempts: 2));

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().NotContain("HTTP 503");
        result.Errors.Single().Should().Contain("invalid start");
    }

    [Fact]
    public async Task SyncDataAsync_WhenTheRemoteTransportFails_ReportsTheTransportCause()
    {
        var handler = new RemoteFakeHandler()
            .BreakTransport(NocturneRemoteConstants.SensorGlucose,
                new HttpRequestException("TLS handshake failed"));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("The remote Nocturne instance at https://remote.example did not answer: TLS handshake failed");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    [Fact]
    public async Task SyncDataAsync_WhenACrawlRequestTimesOut_ReportsTheTimeoutAsTheCause()
    {
        var handler = new RemoteFakeHandler()
            .BreakCrawl(NocturneRemoteConstants.SensorGlucose,
                new TaskCanceledException("The crawl request timed out."));
        var fixture = new ServiceFixture(handler);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("The crawl request timed out.",
                "the tenant reads the sync card, so a timeout has to leave its text there");
    }

    [Fact]
    public async Task SyncDataAsync_WhenTheCallerCancelsDuringACrawlRequest_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RemoteFakeHandler()
            .BreakCrawl(NocturneRemoteConstants.SensorGlucose,
                new TaskCanceledException("The crawl request was cancelled."));
        handler.OnRequest = url =>
        {
            if (url.Contains("offset=", StringComparison.Ordinal))
                cancellation.Cancel();
        };
        var fixture = new ServiceFixture(handler);

        var run = () => fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// An explicit range is honoured as given for every family — that is how a cursor reset re-pulls
    /// history the per-family catch-up bounds would otherwise skip.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenGivenAnExplicitRange_AsksEveryFamilyForIt()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(
            handler, latestTreatment: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                From = from,
                To = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
            },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.SensorGlucose).Should().Contain($"from={from:o}");
        handler.CrawlOf(NocturneRemoteConstants.Boluses).Should().Contain($"from={from:o}");
    }

    /// <summary>
    /// A watermark the publisher cannot answer fails only the families it would have bounded. It is
    /// resolved inside the per-type error boundary for that reason: resolving it up front would take
    /// down glucose, profiles and food as well, none of which needed it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAFamilysWatermarkCannotBeRead_FailsOnlyThatFamily()
    {
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 1, "2026-01-03T08:00:00Z"));
        var fixture = new ServiceFixture(handler, treatmentWatermarkFails: true);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().StartWith($"Failed to sync {SyncDataType.Boluses}");
        result.ItemsSynced.Should().BeEquivalentTo(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 1,
        }, "glucose never needed the treatment watermark");
    }

    /// <summary>
    /// The remote is the tenant's own instance behind their own proxy, and the failed response's
    /// body is quoted into the connector's last-error message and its logs. A proxy error page that
    /// echoes the request would otherwise put the tenant's bearer token in both.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAFailedResponseEchoesTheRequest_KeepsTheTokenOutOfTheError()
    {
        var config = NewConfig();
        var handler = new RemoteFakeHandler()
            .Serve(NocturneRemoteConstants.SensorGlucose,
                RemoteFakeHandler.GlucosePage(total: 6, "2026-01-03T08:00:00Z", "2026-01-03T08:05:00Z"),
                new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent(
                        $"upstream refused; sent authorization: Bearer {config.AccessToken}"),
                });
        var fixture = new ServiceFixture(handler, config: config);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().NotContain(config.AccessToken).And.Contain("HTTP 502 BadGateway");
    }

    /// <summary>
    /// Page size and credential belonging to a second run only, so a run crawled with the first
    /// run's configuration cannot answer to them.
    /// </summary>
    private const int ManualRunPageSize = 7;

    /// <inheritdoc cref="ManualRunPageSize"/>
    private const string ManualRunToken = "manual-run-token";

    private static NocturneRemoteConnectorConfiguration NewConfig(
        bool syncGlucose = true,
        bool syncActivity = true,
        int maxRetryAttempts = 1) => new()
    {
        Url = RemoteFakeHandler.BaseUrl,
        AccessToken = "direct-grant-token",
        MaxCount = RemoteFakeHandler.PageSize,
        // One attempt unless a test is about the budget, so each scripted response answers exactly
        // one request and a crawl script reads in the order the crawl makes them.
        MaxRetryAttempts = maxRetryAttempts,
        SyncGlucose = syncGlucose,
        SyncActivity = syncActivity,
    };

    /// <summary>Wires the connector service and a recording publisher onto one fake handler.</summary>
    private sealed class ServiceFixture
    {
        internal NocturneRemoteConnectorService Service { get; }
        internal NocturneRemoteConnectorConfiguration Config { get; }
        internal List<SensorGlucose> PublishedGlucose { get; } = [];
        internal List<Bolus> PublishedBoluses { get; } = [];
        internal List<ProfileActivation> PublishedActivations { get; } = [];
        internal List<DeviceStatus> PublishedStatuses { get; } = [];
        internal List<Activity> PublishedActivities { get; } = [];
        internal List<int> PublishedActivityBatchSizes { get; } = [];
        internal InMemoryConnectorStateStore State { get; }
        internal Dictionary<string, DateTime> Marks => State.Resume;
        internal int MarkWrites { get; private set; }

        internal ServiceFixture(
            RemoteFakeHandler handler,
            NocturneRemoteConnectorConfiguration? config = null,
            DateTime? latestTreatment = null,
            bool treatmentWatermarkFails = false,
            IReadOnlyDictionary<SyncDataType, DateTime>? latestByType = null,
            DateTime? latestStateSpan = null,
            DateTime? latestGlucose = null,
            DateTime? latestDeviceStatus = null,
            DateTime? latestActivity = null,
            Action? onDeviceStatusPublished = null,
            TimeProvider? time = null,
            bool glucoseWatermarkFollowsPublishes = false)
        {
            Config = config ?? NewConfig();

            var glucose = new Mock<IGlucosePublisher>();
            glucose
                .Setup(p => p.PublishSensorGlucoseAsync(
                    It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<SensorGlucose>, string, WriteOrigin, CancellationToken>(
                    (batch, _, _, _) => PublishedGlucose.AddRange(batch))
                .ReturnsAsync(true);
            glucose
                .Setup(p => p.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => glucoseWatermarkFollowsPublishes && PublishedGlucose.Count > 0
                    ? PublishedGlucose.Max(g => g.Timestamp)
                    : latestGlucose);

            var device = new Mock<IDevicePublisher>();
            device
                .Setup(p => p.GetLatestDeviceStatusTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(latestDeviceStatus);
            device
                .Setup(p => p.PublishDeviceStatusAsync(
                    It.IsAny<IEnumerable<DeviceStatus>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<DeviceStatus>, string, WriteOrigin, CancellationToken>((batch, _, _, _) =>
                {
                    PublishedStatuses.AddRange(batch);
                    onDeviceStatusPublished?.Invoke();
                })
                .ReturnsAsync(true);

            var treatments = new Mock<ITreatmentPublisher>();
            var watermark = treatments.Setup(p => p.GetLatestTreatmentTimestampAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()));
            var typeWatermark = treatments.Setup(p => p.GetLatestTreatmentTimestampAsync(
                It.IsAny<SyncDataType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));
            if (treatmentWatermarkFails)
            {
                watermark.ThrowsAsync(new HttpRequestException("the API did not answer"));
                typeWatermark.ThrowsAsync(new HttpRequestException("the API did not answer"));
            }
            else
            {
                watermark.ReturnsAsync(latestTreatment);
                typeWatermark.ReturnsAsync((SyncDataType type, string _, CancellationToken _) =>
                    latestByType is not null && latestByType.TryGetValue(type, out var latest)
                        ? latest
                        : latestTreatment);
            }

            var metadata = new Mock<IMetadataPublisher>();
            State = new InMemoryConnectorStateStore { BeforeWrite = _ => MarkWrites++ };
            metadata
                .Setup(p => p.GetLatestActivityTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(latestActivity);
            metadata
                .Setup(p => p.PublishActivityAsync(
                    It.IsAny<IEnumerable<Activity>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Activity>, string, WriteOrigin, CancellationToken>(
                    (batch, _, _, _) =>
                    {
                        var records = batch.ToList();
                        PublishedActivities.AddRange(records);
                        PublishedActivityBatchSizes.Add(records.Count);
                    })
                .ReturnsAsync(true);
            metadata
                .Setup(p => p.GetLatestStateSpanTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(latestStateSpan);
            metadata
                .Setup(p => p.PublishStateSpansAsync(
                    It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            metadata
                .Setup(p => p.PublishProfileActivationsAsync(
                    It.IsAny<IEnumerable<ProfileActivation>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<ProfileActivation>, string, WriteOrigin, CancellationToken>(
                    (batch, _, _, _) => PublishedActivations.AddRange(batch))
                .ReturnsAsync(true);

            treatments
                .Setup(p => p.PublishBolusesAsync(
                    It.IsAny<IEnumerable<Bolus>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Bolus>, string, WriteOrigin, CancellationToken>(
                    (batch, _, _, _) => PublishedBoluses.AddRange(batch))
                .ReturnsAsync(true);

            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);
            publisher.Setup(p => p.Glucose).Returns(glucose.Object);
            publisher.Setup(p => p.Treatments).Returns(treatments.Object);
            publisher.Setup(p => p.Device).Returns(device.Object);
            publisher.Setup(p => p.Metadata).Returns(metadata.Object);
            publisher.Setup(p => p.State).Returns(State);

            Service = new NocturneRemoteConnectorService(
                new HttpClient(handler),
                Mock.Of<IConnectorServerResolver<NocturneRemoteConnectorConfiguration>>(),
                NullLogger<NocturneRemoteConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                publisher.Object,
                time);
        }
    }

    /// <summary>
    /// Serves the remote instance's endpoints, one scripted response per request in the order the
    /// connector makes them.
    /// </summary>
    /// <remarks>
    /// <see cref="Break"/> models an endpoint that is down and answers every request to it — the
    /// auth check included, because it reads the sensor-glucose endpoint like any other caller.
    /// <see cref="Serve"/> models the responses to successive pages of a working endpoint, which the
    /// auth check is deliberately not served from: it asks for one record before the crawl starts,
    /// so letting it consume the crawl's first page would misdescribe every script.
    /// </remarks>
    private sealed class RemoteFakeHandler : HttpMessageHandler
    {
        internal const string BaseUrl = "https://remote.example";
        internal const string V1DeviceStatus = "/api/v1/devicestatus.json";

        /// <summary>Page size the fixture configures, small enough to script a multi-page crawl.</summary>
        internal const int PageSize = 2;

        private readonly Dictionary<string, Queue<HttpResponseMessage>> _pages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HttpStatusCode> _broken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _transportFailures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _crawlFailures = new(StringComparer.Ordinal);

        /// <summary>Every request made, so a test can assert on the range each crawl asked for.</summary>
        internal List<string> Requests { get; } = [];

        internal Action<string>? OnRequest { get; set; }

        /// <summary>
        /// The Authorization header each request carried, positionally aligned with
        /// <see cref="Requests"/>.
        /// </summary>
        internal List<string?> Tokens { get; } = [];

        private readonly Dictionary<string, Func<Uri, HttpResponseMessage>> _computed = new(StringComparer.Ordinal);

        /// <summary>Answers every request to <paramref name="path"/> from its query, as a stored remote would.</summary>
        internal RemoteFakeHandler ServeComputed(string path, Func<Uri, HttpResponseMessage> respond)
        {
            _computed[path] = respond;
            return this;
        }

        internal RemoteFakeHandler Serve(string path, params HttpResponseMessage[] responses)
        {
            _pages[path] = new Queue<HttpResponseMessage>(responses);
            return this;
        }

        /// <summary>The first crawl request made to <paramref name="path"/>, excluding the auth check.</summary>
        internal string CrawlOf(string path) => Crawls(path).First();

        /// <inheritdoc cref="CrawlOf"/>
        internal string LastCrawlOf(string path) => Crawls(path).Last();

        private IEnumerable<string> Crawls(string path) =>
            Requests.Where(u => u.Contains(path, StringComparison.Ordinal)
                                && u.Contains("offset=", StringComparison.Ordinal));

        internal RemoteFakeHandler Break(string path, HttpStatusCode status)
        {
            _broken[path] = status;
            return this;
        }

        internal RemoteFakeHandler BreakTransport(string path, Exception failure)
        {
            _transportFailures[path] = failure;
            return this;
        }

        /// <summary>Fails the crawl of <paramref name="path"/> but not the auth check that precedes it.</summary>
        internal RemoteFakeHandler BreakCrawl(string path, Exception failure)
        {
            _crawlFailures[path] = failure;
            return this;
        }

        internal static HttpResponseMessage Status(HttpStatusCode status) => new(status);

        internal static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        internal static HttpResponseMessage GlucosePage(int total, params string[] timestamps) =>
            Page(total, timestamps.Select(t =>
                $$"""{"id":"{{Guid.NewGuid()}}","timestamp":"{{t}}","mgdl":120}"""));

        internal static HttpResponseMessage BolusPage(int total, params string[] timestamps) =>
            Page(total, timestamps.Select(t =>
                $$"""{"id":"{{Guid.NewGuid()}}","timestamp":"{{t}}","insulin":1.5}"""));

        internal static HttpResponseMessage ActivityPage(int total, params string[] timestamps) =>
            Page(total, timestamps.Select(t =>
                $$"""{"_id":"{{Guid.NewGuid()}}","mills":{{DateTimeOffset.Parse(t).ToUnixTimeMilliseconds()}},"type":"Walking"}"""));

        internal static HttpResponseMessage DeviceStatusPage(params string[] createdAt) =>
            Json("[" + string.Join(",", createdAt.Select(t =>
                $$"""{"_id":"{{Guid.NewGuid()}}","created_at":"{{t}}"}""")) + "]");

        private static HttpResponseMessage Page(int total, IEnumerable<string> records) =>
            Json("{\"data\":[" + string.Join(",", records)
                 + "],\"pagination\":{\"limit\":" + PageSize
                 + ",\"offset\":0,\"total\":" + total + "}}");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.RequestUri.ToString());
            OnRequest?.Invoke(request.RequestUri.ToString());
            Tokens.Add(request.Headers.TryGetValues("Authorization", out var authorization)
                ? authorization.FirstOrDefault()
                : null);

            if (_broken.TryGetValue(path, out var status))
                return Task.FromResult(Status(status));

            if (_transportFailures.TryGetValue(path, out var failure))
                throw failure;

            if (_crawlFailures.TryGetValue(path, out var crawlFailure) && !IsAuthCheck(request))
                throw crawlFailure;

            if (IsAuthCheck(request))
                return Task.FromResult(GlucosePage(total: 0));

            if (_computed.TryGetValue(path, out var respond))
                return Task.FromResult(respond(request.RequestUri));

            if (_pages.TryGetValue(path, out var queue) && queue.Count > 0)
                return Task.FromResult(queue.Dequeue());

            return Task.FromResult(
                path == V1DeviceStatus || path == NocturneRemoteConstants.Foods
                    || path == NocturneRemoteConstants.ProfileActivations
                    ? Json("[]")
                    : GlucosePage(total: 0));
        }

        private static bool IsAuthCheck(HttpRequestMessage request) =>
            request.RequestUri!.AbsolutePath == NocturneRemoteConstants.SensorGlucose
            && !request.RequestUri.Query.Contains("offset=", StringComparison.Ordinal);
    }
}
