using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// What <see cref="V4RepositoryBase{TModel,TEntity}"/> reads return, against a real relational store.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class V4RepositoryBaseReadTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly DateTime At = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly BGCheckRepository _bgChecks;
    private readonly BasalInjectionRepository _injections;

    public V4RepositoryBaseReadTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        var factory = new TestNocturneDbContextFactory(_context);
        _bgChecks = new BGCheckRepository(
            factory, new Mock<IDuplicateLinker>().Object, new Mock<IAuditContext>().Object,
            NullLogger<BGCheckRepository>.Instance);
        _injections = new BasalInjectionRepository(
            factory, new Mock<IAuditContext>().Object, NullLogger<BasalInjectionRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<BGCheck> CreateCheckAsync(string source, Guid? correlationId = null) =>
        _bgChecks.CreateAsync(
            new BGCheck { Timestamp = At, DataSource = source, Glucose = 100, CorrelationId = correlationId },
            WriteOrigin.Live);

    [Fact]
    public async Task GetAsync_OfADuplicateFlaggedType_HidesTheNonPrimaryCopy()
    {
        var primary = await CreateCheckAsync("mylife-connector");
        var copy = await CreateCheckAsync("glooko-connector");
        await DuplicateGroups.LinkAsync(_context, RecordType.BGCheck, primary.Id, copy.Id);

        var read = await _bgChecks.GetAsync(null, null, null, null);

        read.Should().ContainSingle().Which.Id.Should().Be(primary.Id);
    }

    [Fact]
    public async Task GetAsync_OfATypeThatIsNotDuplicateFlagged_ReturnsEveryRow()
    {
        await _injections.CreateAsync(
            new BasalInjection { Timestamp = At, DataSource = "a", SyncIdentifier = "1", Units = 10 }, WriteOrigin.Live);
        await _injections.CreateAsync(
            new BasalInjection { Timestamp = At, DataSource = "b", SyncIdentifier = "1", Units = 10 }, WriteOrigin.Live);

        var read = await _injections.GetAsync(null, null, null, null);

        read.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByCorrelationIdAsync_ReturnsOnlyTheRecordsSharingTheId()
    {
        var shared = Guid.NewGuid();
        var first = await CreateCheckAsync("a", shared);
        var second = await CreateCheckAsync("b", shared);
        await CreateCheckAsync("c", Guid.NewGuid());
        await CreateCheckAsync("d");

        var read = await _bgChecks.GetByCorrelationIdAsync(shared);

        read.Select(r => r.Id).Should().BeEquivalentTo([first.Id, second.Id]);
    }

    [Fact]
    public async Task GetByCorrelationIdsAsync_ReturnsTheRecordsOfEveryIdOnce_AndNoneForNoIds()
    {
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        var inOne = await CreateCheckAsync("a", one);
        var inTwo = await CreateCheckAsync("b", two);
        await CreateCheckAsync("c", Guid.NewGuid());

        var read = await _bgChecks.GetByCorrelationIdsAsync([one, two, one]);

        read.Select(r => r.Id).Should().BeEquivalentTo([inOne.Id, inTwo.Id]);
        (await _bgChecks.GetByCorrelationIdsAsync([])).Should().BeEmpty();
    }
}
