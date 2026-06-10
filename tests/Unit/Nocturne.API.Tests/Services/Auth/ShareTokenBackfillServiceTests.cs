using System.Linq;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

public sealed class ShareTokenBackfillServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _sp;
    private readonly IDbContextFactory<NocturneDbContext> _factory;
    private readonly Guid _publicTenantId = Guid.CreateVersion7();
    private readonly Guid _privateTenantId = Guid.CreateVersion7();
    private readonly Guid _publicSubjectId = Guid.CreateVersion7();

    public ShareTokenBackfillServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContextFactory<NocturneDbContext>(o => o
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<IShareTokenGenerator, ShareTokenGenerator>();
        _sp = services.BuildServiceProvider();
        _factory = _sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        Seed(db);
    }

    private void Seed(NocturneDbContext db)
    {
        db.Subjects.Add(new SubjectEntity
        {
            Id = _publicSubjectId,
            Name = "Public",
            IsActive = true,
            IsSystemSubject = true,
        });

        // Public tenant: Public subject holds a Viewer role => was public => should be backfilled.
        db.Tenants.Add(new TenantEntity { Id = _publicTenantId, Slug = "pubco", DisplayName = "Pub" });
        var pubMemberId = Guid.CreateVersion7();
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = pubMemberId,
            TenantId = _publicTenantId,
            SubjectId = _publicSubjectId,
        });
        var viewerRoleId = Guid.CreateVersion7();
        db.TenantRoles.Add(new TenantRoleEntity
        {
            Id = viewerRoleId,
            TenantId = _publicTenantId,
            Name = "Viewer",
            Slug = TenantPermissions.SeedRoles.Viewer,
            Permissions = TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Viewer],
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = pubMemberId,
            TenantRoleId = viewerRoleId,
            SysCreatedAt = DateTime.UtcNow,
        });

        // Private tenant: Public subject has no roles => never public => must not be backfilled.
        db.Tenants.Add(new TenantEntity { Id = _privateTenantId, Slug = "privco", DisplayName = "Priv" });
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _privateTenantId,
            SubjectId = _publicSubjectId,
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _sp.Dispose();
        _connection.Dispose();
    }

    private Task RunAsync() =>
        new ShareTokenBackfillService(_sp, NullLogger<ShareTokenBackfillService>.Instance).StartAsync(default);

    [Fact]
    public async Task Backfills_token_for_previously_public_tenant_only()
    {
        await RunAsync();

        using var db = _factory.CreateDbContext();
        var pub = db.Tenants.Single(t => t.Id == _publicTenantId);
        var priv = db.Tenants.Single(t => t.Id == _privateTenantId);

        pub.ShareToken.Should().NotBeNullOrEmpty();
        pub.ShareTokenSetAt.Should().NotBeNull();
        priv.ShareToken.Should().BeNull("a tenant that was never public must not get a link");
    }

    [Fact]
    public async Task Is_idempotent_and_does_not_rotate_existing_tokens()
    {
        await RunAsync();
        string firstToken;
        using (var db = _factory.CreateDbContext())
            firstToken = db.Tenants.Single(t => t.Id == _publicTenantId).ShareToken!;

        await RunAsync();

        using var db2 = _factory.CreateDbContext();
        db2.Tenants.Single(t => t.Id == _publicTenantId).ShareToken.Should().Be(firstToken);
    }

    [Fact]
    public async Task Does_not_overwrite_a_tenant_that_already_has_a_token()
    {
        var id = Guid.CreateVersion7();
        using (var db = _factory.CreateDbContext())
        {
            db.Tenants.Add(new TenantEntity
            {
                Id = id,
                Slug = "hastoken",
                DisplayName = "Has",
                ShareToken = "existingtok00",
            });
            db.SaveChanges();
        }

        await RunAsync();

        using var db2 = _factory.CreateDbContext();
        db2.Tenants.Single(t => t.Id == id).ShareToken.Should().Be("existingtok00");
    }

    [Fact]
    public async Task Backfills_tenant_with_direct_permissions_only()
    {
        var id = Guid.CreateVersion7();
        using (var db = _factory.CreateDbContext())
        {
            db.Tenants.Add(new TenantEntity { Id = id, Slug = "directco", DisplayName = "Direct" });
            db.TenantMembers.Add(new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = id,
                SubjectId = _publicSubjectId,
                DirectPermissions = ["glucose.read"],
            });
            db.SaveChanges();
        }

        await RunAsync();

        using var db2 = _factory.CreateDbContext();
        db2.Tenants.Single(t => t.Id == id).ShareToken.Should().NotBeNullOrEmpty();
    }
}
