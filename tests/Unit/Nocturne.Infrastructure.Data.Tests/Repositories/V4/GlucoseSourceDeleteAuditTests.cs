using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The delete contract every glucose repository owes: the source predicate reaches both origin
/// handles a row can carry (<see cref="Nocturne.Infrastructure.Data.Extensions.SourceFilter"/>),
/// and the delete runs through the audited path so a user delete stamps <c>deleted_by_user</c> —
/// the discriminator that stops the next connector sync re-importing the rows
/// (<see cref="Nocturne.Infrastructure.Data.Extensions.SoftDeleteDedupExtensions"/>).
/// In-memory SQLite because <c>ExecuteUpdateAsync</c> and the audit transaction need a relational
/// provider.
/// </summary>
public abstract class GlucoseSourceDeleteAuditTests<TEntity> : IDisposable
    where TEntity : class, ISoftDeletable, ISourcedEntity
{
    protected static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly NocturneDbContext _context;

    protected GlucoseSourceDeleteAuditTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using (var seedContext = new NocturneDbContext(_options) { TenantId = TestTenantId })
        {
            seedContext.Database.EnsureCreated();
            seedContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
            seedContext.SaveChanges();
        }

        _context = new NocturneDbContext(_options) { TenantId = TestTenantId };
        UseAuditContext(new UserAuditContext());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds the repository under test against <paramref name="auditContext"/>.</summary>
    protected abstract void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext);

    /// <summary>A row of the repository's type, unsaved.</summary>
    protected abstract TEntity NewRow(Guid id, DateTime timestamp, string? dataSource, string? device);

    protected abstract Task<int> DeleteBySourceAsync(string source);

    protected abstract Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to);

    /// <summary>The name <c>mutation_audit_log</c> records for this entity type.</summary>
    protected abstract string AuditEntityType { get; }

    protected static ILogger<T> Logger<T>() => NullLogger<T>.Instance;

    protected void UseAuditContext(IAuditContext auditContext) =>
        CreateRepository(new TestTenantDbContextFactory(_context), auditContext);

    private Guid Seed(DateTime timestamp, string? dataSource = null, string? device = null)
    {
        var id = Guid.CreateVersion7();
        _context.Set<TEntity>().Add(NewRow(id, timestamp, dataSource, device));
        _context.SaveChanges();
        return id;
    }

    private NocturneDbContext Verify() => new(_options) { TenantId = TestTenantId };

    private async Task<(DateTime? DeletedAt, bool DeletedByUser)> ReadDeleteStateAsync(Guid id)
    {
        await using var verify = Verify();
        var row = await verify.Set<TEntity>()
            .IgnoreQueryFilters()
            .SingleAsync(e => EF.Property<Guid>(e, "Id") == id);
        return (row.DeletedAt, (bool)verify.Entry(row).Property("DeletedByUser").CurrentValue!);
    }

    private async Task<List<MutationAuditLogEntity>> ReadAuditLogAsync()
    {
        await using var verify = Verify();
        return await verify.Set<MutationAuditLogEntity>().ToListAsync();
    }

    private static (int Count, string Scope) ReadSummary(string? changesJson)
    {
        using var doc = JsonDocument.Parse(changesJson!);
        return (doc.RootElement.GetProperty("count").GetInt32(),
            doc.RootElement.GetProperty("scope").GetString()!);
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "DELETE /api/v4/data-sources/dexcom";
        public bool IsSystem => false;
    }

    [Fact]
    public async Task DeleteBySourceAsync_DeletesRowsUnderEitherOriginHandle_AndSparesOtherSources()
    {
        var byDataSource = Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        var byDevice = Seed(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), dataSource: "other", device: "dexcom");
        var unrelated = Seed(new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), dataSource: "libre", device: "Libre3");

        var deleted = await DeleteBySourceAsync("dexcom");

        deleted.Should().Be(2);
        (await ReadDeleteStateAsync(byDataSource)).DeletedAt.Should().NotBeNull();
        (await ReadDeleteStateAsync(byDevice)).DeletedAt.Should().NotBeNull();
        (await ReadDeleteStateAsync(unrelated)).DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteBySourceAsync_UserContext_StampsDeletedByUserAndWritesSummaryRow()
    {
        Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        var byDevice = Seed(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), device: "dexcom");

        var deleted = await DeleteBySourceAsync("dexcom");

        deleted.Should().Be(2);
        (await ReadDeleteStateAsync(byDevice)).DeletedByUser.Should().BeTrue();

        var log = (await ReadAuditLogAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be("bulk_delete");
        log.EntityType.Should().Be(AuditEntityType);
        log.EntityId.Should().BeNull();
        log.SubjectName.Should().Be("tester");
        ReadSummary(log.ChangesJson).Should().Be((2, "data_source=dexcom"));
    }

    [Fact]
    public async Task DeleteBySourceAsync_SystemContext_LeavesRowsReimportable()
    {
        var id = Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        UseAuditContext(SystemAuditContext.ForService("connector:dexcom"));

        var deleted = await DeleteBySourceAsync("dexcom");

        deleted.Should().Be(1);
        var state = await ReadDeleteStateAsync(id);
        state.DeletedAt.Should().NotBeNull();
        state.DeletedByUser.Should().BeFalse();
        (await ReadAuditLogAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteByTimeRangeAsync_UserContext_StampsDeletedByUserAndWritesSummaryRow()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
        var beforeFrom = Seed(from.AddHours(-1), dataSource: "dexcom");
        var atLowerBound = Seed(from, dataSource: "dexcom");
        var inRange = Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        var atUpperBound = Seed(to, dataSource: "dexcom");

        var deleted = await DeleteByTimeRangeAsync(from, to);

        deleted.Should().Be(2);
        (await ReadDeleteStateAsync(inRange)).DeletedByUser.Should().BeTrue();
        (await ReadDeleteStateAsync(atLowerBound)).DeletedAt.Should().NotBeNull("the lower bound is inclusive");
        (await ReadDeleteStateAsync(beforeFrom)).DeletedAt.Should().BeNull("rows before the window are out of scope");
        (await ReadDeleteStateAsync(atUpperBound)).DeletedAt.Should().BeNull("the upper bound is exclusive");

        var log = (await ReadAuditLogAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be("bulk_delete");
        log.EntityType.Should().Be(AuditEntityType);
        ReadSummary(log.ChangesJson).Should().Be((2, $"timestamp={from:O}..{to:O}"));
    }
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class CalibrationRepositorySourceDeleteTests : GlucoseSourceDeleteAuditTests<CalibrationEntity>
{
    private CalibrationRepository _repository = null!;

    protected override string AuditEntityType => "Calibration";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new CalibrationRepository(contextFactory, auditContext, Logger<CalibrationRepository>());

    protected override CalibrationEntity NewRow(Guid id, DateTime timestamp, string? dataSource, string? device) =>
        new()
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            DataSource = dataSource,
            Device = device,
            Slope = 1.0,
        };

    protected override Task<int> DeleteBySourceAsync(string source) => _repository.DeleteBySourceAsync(source);

    protected override Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to) =>
        _repository.DeleteByTimeRangeAsync(from, to);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class MeterGlucoseRepositorySourceDeleteTests : GlucoseSourceDeleteAuditTests<MeterGlucoseEntity>
{
    private MeterGlucoseRepository _repository = null!;

    protected override string AuditEntityType => "MeterGlucose";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new MeterGlucoseRepository(contextFactory, auditContext, Logger<MeterGlucoseRepository>());

    protected override MeterGlucoseEntity NewRow(Guid id, DateTime timestamp, string? dataSource, string? device) =>
        new()
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            DataSource = dataSource,
            Device = device,
            Mgdl = 120,
        };

    protected override Task<int> DeleteBySourceAsync(string source) => _repository.DeleteBySourceAsync(source);

    protected override Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to) =>
        _repository.DeleteByTimeRangeAsync(from, to);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseRepositorySourceDeleteTests : GlucoseSourceDeleteAuditTests<SensorGlucoseEntity>
{
    private SensorGlucoseRepository _repository = null!;

    protected override string AuditEntityType => "SensorGlucose";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new SensorGlucoseRepository(
            contextFactory,
            new Mock<IDeduplicationService>().Object,
            auditContext,
            Logger<SensorGlucoseRepository>());

    protected override SensorGlucoseEntity NewRow(Guid id, DateTime timestamp, string? dataSource, string? device) =>
        new()
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            DataSource = dataSource,
            Device = device,
            Mgdl = 120,
        };

    protected override Task<int> DeleteBySourceAsync(string source) => _repository.DeleteBySourceAsync(source);

    protected override Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to) =>
        _repository.DeleteByTimeRangeAsync(from, to);
}
