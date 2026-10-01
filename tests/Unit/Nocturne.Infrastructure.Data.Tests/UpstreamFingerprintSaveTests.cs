using Microsoft.AspNetCore.Http;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// How <see cref="NocturneDbContext"/>'s save applies an open <see cref="UpstreamFingerprintScope"/>:
/// only to the source and legacy id it names, never into an audit record, and without moving the
/// update stamp v3 history clients page on.
/// </summary>
[Trait("Category", "Unit")]
public class UpstreamFingerprintSaveTests : IDisposable
{
    private const string Connector = "nightscout-connector";

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public UpstreamFingerprintSaveTests()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns((HttpContext)null!);
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            _tenantId, "test", new MutationAuditInterceptor(accessor.Object));
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_connector_upsert_audits_its_change_but_not_the_fingerprint()
    {
        var id = await SeedCarbAsync(Connector);

        await using (var ctx = _db.CreateContext())
        {
            ctx.AuditContext = new UserAuditContext();
            (await ctx.CarbIntakes.SingleAsync(c => c.Id == id)).Carbs = 50;
            using (Scope(Connector, "fingerprint-value"))
                await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        (await verify.CarbIntakes.SingleAsync(c => c.Id == id)).UpstreamFingerprint.Should().Be("fingerprint-value");
        var log = await verify.MutationAuditLog.SingleAsync(l => l.Action == "update");
        log.ChangesJson.Should().Contain("50");
        log.ChangesJson.Should().NotContain("fingerprint", "the audit log is served over the API");
    }

    [Fact]
    public async Task A_fingerprint_only_write_leaves_the_update_stamp_alone()
    {
        var id = await SeedCarbAsync(Connector);
        DateTime stamped;
        await using (var ctx = _db.CreateContext())
            stamped = (await ctx.CarbIntakes.SingleAsync(c => c.Id == id)).SysUpdatedAt;

        await using (var ctx = _db.CreateContext())
        {
            await ctx.CarbIntakes.SingleAsync(c => c.Id == id);
            using (Scope(Connector, "fingerprint-value"))
                await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var row = await verify.CarbIntakes.SingleAsync(c => c.Id == id);
        row.UpstreamFingerprint.Should().Be("fingerprint-value");
        row.SysUpdatedAt.Should().Be(stamped);
    }

    [Fact]
    public async Task A_scope_leaves_another_sources_row_under_the_same_id_alone()
    {
        var id = await SeedCarbAsync("another-uploader", fingerprint: "kept");

        await using (var ctx = _db.CreateContext())
        {
            (await ctx.CarbIntakes.SingleAsync(c => c.Id == id)).Carbs = 50;
            using (Scope(Connector, null))
                await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        (await verify.CarbIntakes.SingleAsync(c => c.Id == id)).UpstreamFingerprint.Should().Be("kept");
    }

    private static IDisposable Scope(string source, string? fingerprint) =>
        UpstreamFingerprintScope.Open(new Dictionary<(string?, string), string?> { [(source, "t-1")] = fingerprint });

    private async Task<Guid> SeedCarbAsync(string source, string? fingerprint = null)
    {
        var id = Guid.CreateVersion7();
        await using var ctx = _db.CreateContext();
        ctx.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = id, TenantId = _tenantId, LegacyId = "t-1", DataSource = source, Carbs = 35,
            Timestamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), UpstreamFingerprint = fingerprint,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "PUT /api/v4/carbs";
        public bool IsSystem => false;
    }
}
