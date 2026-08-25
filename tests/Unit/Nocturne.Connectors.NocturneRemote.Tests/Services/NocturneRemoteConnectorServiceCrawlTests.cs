using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.NocturneRemote.Tests.Services;

public class NocturneRemoteConnectorServiceCrawlTests
{
    /// <summary>
    /// A page that never arrives is not the end of the range. Ending the crawl there publishes the
    /// newest pages and reports a green sync, and because the next lower bound is derived from the
    /// newest record then stored locally, the pages below the failure are never asked for again.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAPageMidCrawlIsRejected_FailsTheRunAndPublishesNothing()
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
        fixture.PublishedGlucose.Should().BeEmpty(
            "publishing the pages above the failure would put the ones below it out of reach");
    }

    /// <summary>
    /// The same swallow by a different route: a 200 whose envelope carries no page is a fetch that
    /// failed, not a range that ran out. A range that ran out answers an empty array.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAPageCarriesNoData_FailsTheRunAndPublishesNothing()
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
        fixture.PublishedGlucose.Should().BeEmpty();
    }

    /// <summary>
    /// A body that will not parse — a captive portal's HTML, a truncated response — reaches the
    /// crawl as an exception rather than a null, and has to reach the same conclusion. This is the
    /// route by which most "carried no page" failures actually arrive: an envelope that parses at
    /// all supplies an empty <c>Data</c> rather than a null one.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenAPageIsUnparseable_FailsTheRunAndPublishesNothing()
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
        fixture.PublishedGlucose.Should().BeEmpty();
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
    /// A run carrying no lower bound imports this remote's full history, and no family's resume
    /// point may narrow it back — the remote is another Nocturne instance, so the first sync is
    /// meant to take everything it holds.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheCallerSuppliesNoLowerBound_AsksEveryFamilyForEverything()
    {
        var handler = new RemoteFakeHandler();
        var fixture = new ServiceFixture(
            handler, latestTreatment: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await fixture.Service.SyncDataAsync(
            new SyncRequest { From = null, To = null, DataTypes = [SyncDataType.Boluses] },
            fixture.Config,
            CancellationToken.None);

        handler.CrawlOf(NocturneRemoteConstants.Boluses).Should().NotContain("from=");
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
        manual.Token = ManualRunToken;

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
        result.Errors.Should().ContainSingle()
            .Which.Should().Be($"Authentication failed for {DataSources.NocturneRemoteConnector}");
        handler.Requests.Should().NotContain(url => url.Contains(NocturneRemoteConstants.Boluses),
            "a rejected credential fails the run before the enabled types are reached");
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
                        $"upstream refused; sent authorization: Bearer {config.Token}"),
                });
        var fixture = new ServiceFixture(handler, config: config);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            fixture.Config,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().NotContain(config.Token).And.Contain("HTTP 502 BadGateway");
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
        Token = "direct-grant-token",
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

        internal ServiceFixture(
            RemoteFakeHandler handler,
            NocturneRemoteConnectorConfiguration? config = null,
            DateTime? latestTreatment = null,
            bool treatmentWatermarkFails = false)
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

            var treatments = new Mock<ITreatmentPublisher>();
            var watermark = treatments.Setup(p => p.GetLatestTreatmentTimestampAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()));
            if (treatmentWatermarkFails)
                watermark.ThrowsAsync(new HttpRequestException("the API did not answer"));
            else
                watermark.ReturnsAsync(latestTreatment);
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
            publisher.Setup(p => p.Device).Returns(Mock.Of<IDevicePublisher>());
            publisher.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());

            Service = new NocturneRemoteConnectorService(
                new HttpClient(handler),
                Mock.Of<IConnectorServerResolver<NocturneRemoteConnectorConfiguration>>(),
                NullLogger<NocturneRemoteConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                publisher.Object);
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

        /// <summary>Every request made, so a test can assert on the range each crawl asked for.</summary>
        internal List<string> Requests { get; } = [];

        /// <summary>
        /// The Authorization header each request carried, positionally aligned with
        /// <see cref="Requests"/>.
        /// </summary>
        internal List<string?> Tokens { get; } = [];

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

        internal static HttpResponseMessage Status(HttpStatusCode status) => new(status);

        internal static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        internal static HttpResponseMessage GlucosePage(int total, params string[] timestamps) =>
            Page(total, timestamps.Select(t =>
                $$"""{"id":"{{Guid.NewGuid()}}","timestamp":"{{t}}","mgdl":120}"""));

        internal static HttpResponseMessage BolusPage(int total, params string[] timestamps) =>
            Page(total, timestamps.Select(t =>
                $$"""{"id":"{{Guid.NewGuid()}}","timestamp":"{{t}}","insulin":1.5}"""));

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
            Tokens.Add(request.Headers.TryGetValues("Authorization", out var authorization)
                ? authorization.FirstOrDefault()
                : null);

            if (_broken.TryGetValue(path, out var status))
                return Task.FromResult(Status(status));

            if (IsAuthCheck(request))
                return Task.FromResult(GlucosePage(total: 0));

            if (_pages.TryGetValue(path, out var queue) && queue.Count > 0)
                return Task.FromResult(queue.Dequeue());

            return Task.FromResult(
                path == V1DeviceStatus || path == NocturneRemoteConstants.Foods
                    ? Json("[]")
                    : GlucosePage(total: 0));
        }

        private static bool IsAuthCheck(HttpRequestMessage request) =>
            request.RequestUri!.AbsolutePath == NocturneRemoteConstants.SensorGlucose
            && !request.RequestUri.Query.Contains("offset=", StringComparison.Ordinal);
    }
}
