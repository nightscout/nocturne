using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// A connector's readings of the pump's active program publish as effective, connector-origin
/// <see cref="ProfileActivation"/>s, keyed by their upstream id; a Profile span another instance
/// replays publishes as one too.
/// </summary>
public sealed class MetadataPublisherProfileActivationTests : IDisposable
{
    private const string Source = "glooko-connector";
    private static readonly DateTime T0 = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly NocturneDbContext _context;
    private readonly Mock<IStateSpanService> _stateSpans = new();
    private readonly MetadataPublisher _publisher;

    public MetadataPublisherProfileActivationTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = TestTenantCache.DefaultTenantId;
        _publisher = new MetadataPublisher(
            Mock.Of<IProfileWriteService>(),
            Mock.Of<IFoodService>(),
            Mock.Of<IConnectorFoodEntryService>(),
            Mock.Of<IActivityService>(),
            _stateSpans.Object,
            Mock.Of<ISystemEventRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<IBodyWeightService>(),
            Mock.Of<IStepCountService>(),
            Mock.Of<IHeartRateService>(),
            Mock.Of<ITenantOwnerResolver>(),
            Mock.Of<ITenantAccessor>(),
            _context.AsFactory(),
            ProfileActivationTestDeps.Repository(_context),
            ProfileActivationTestDeps.Writer(_context),
            Mock.Of<IAuditContext>(),
            new PublishSkipTally(),
            NullLogger<MetadataPublisher>.Instance);
    }

    public void Dispose() => _context.Dispose();

    private static ProfileActivation Reading(string syncIdentifier, DateTime start, string program) => new()
    {
        SyncIdentifier = syncIdentifier,
        StartTimestamp = start,
        BaseProfileName = program,
    };

    [Fact]
    public async Task AnIdenticalResendWritesNoRowVersion()
    {
        ProfileActivation[] Readings() => [Reading("p-1", T0, "A"), Reading("p-2", T0.AddHours(1), "B")];
        await _publisher.PublishProfileActivationsAsync(Readings(), Source, WriteOrigin.Live);
        var before = await _context.ProfileActivations.AsNoTracking()
            .ToDictionaryAsync(a => a.SyncIdentifier!, a => (a.SysUpdatedAt, a.EndTimestamp));
        _context.ChangeTracker.Clear();

        await _publisher.PublishProfileActivationsAsync(Readings(), Source, WriteOrigin.Live);

        before["p-1"].EndTimestamp.Should().Be(T0.AddHours(1));
        (await _context.ProfileActivations.AsNoTracking()
                .ToDictionaryAsync(a => a.SyncIdentifier!, a => (a.SysUpdatedAt, a.EndTimestamp)))
            .Should().Equal(before, "an end settled before the write leaves nothing to write after it");
    }

    [Fact]
    public async Task AReadingIsAnEffectiveConnectorActivationKeyedByItsUpstreamId()
    {
        (await _publisher.PublishProfileActivationsAsync([Reading("p-1", T0, "A")], Source, WriteOrigin.Live))
            .Should().BeTrue();

        var stored = await _context.ProfileActivations.SingleAsync();
        stored.Phase.Should().Be(nameof(ProfileActivationPhase.Effective));
        stored.Origin.Should().Be(nameof(ProfileActivationOrigin.Connector));
        stored.DataSource.Should().Be(Source);
        stored.SyncIdentifier.Should().Be("p-1");
        stored.LegacyId.Should().BeNull("a connector's reading is not a treatment");
    }

    [Fact]
    public async Task AResyncUpdatesInPlaceAndALaterReadingEndsTheOpenOne()
    {
        await _publisher.PublishProfileActivationsAsync([Reading("p-1", T0, "A")], Source, WriteOrigin.Live);
        await _publisher.PublishProfileActivationsAsync(
            [Reading("p-1", T0, "A"), Reading("p-2", T0.AddHours(2), "B")], Source, WriteOrigin.Live);

        var stored = await _context.ProfileActivations.OrderBy(a => a.Timestamp).ToListAsync();
        stored.Should().HaveCount(2);
        stored[0].EndTimestamp.Should().Be(T0.AddHours(2), "the next reading ends the program that was running");
        stored[1].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task AReplayedProfileSpanPublishesAsAnActivationAndWritesNoSpan()
    {
        var span = new StateSpan
        {
            OriginalId = "glooko_profile_1",
            Category = StateSpanCategory.Profile,
            State = "Active",
            StartTimestamp = T0,
            Metadata = new Dictionary<string, object> { ["profileName"] = "Weekday" },
        };

        (await _publisher.PublishStateSpansAsync([span], Source, WriteOrigin.Live)).Should().BeTrue();

        (await _context.ProfileActivations.SingleAsync()).BaseProfileName.Should().Be("Weekday");
        _stateSpans.Verify(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
