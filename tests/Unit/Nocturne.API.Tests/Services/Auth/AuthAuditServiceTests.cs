using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Covers how <see cref="AuthAuditService"/> resolves the actor, target tenant and request trace
/// of an event when the caller does not name them, over a real database rather than argument
/// matchers.
/// </summary>
public class AuthAuditServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _adminSubjectId = Guid.CreateVersion7();

    public AuthAuditServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var seed = new NocturneDbContext(_dbOptions) { TenantId = _tenantId };
        seed.Database.EnsureCreated();
        seed.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "default",
            DisplayName = "Default",
            IsActive = true,
        });
        seed.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Member", IsActive = true });
        seed.Subjects.Add(new SubjectEntity
        {
            Id = _adminSubjectId,
            Name = "Platform Admin",
            IsActive = true,
            IsPlatformAdmin = true,
        });
        seed.SaveChanges();
    }

    [Fact]
    public async Task Log_AttributesTheEventToTheCallerWhenTheyAreNotTheSubject()
    {
        var row = await LogAndReadAsync(
            AuthAuditEventType.SubjectDeleted,
            _subjectId,
            caller: new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.SessionCookie,
                SubjectId = _adminSubjectId,
            });

        Assert.Equal(_adminSubjectId, row.ActorSubjectId);
        Assert.NotEqual(row.SubjectId, row.ActorSubjectId);
        Assert.Null(row.ActorCredential);
    }

    [Fact]
    public async Task Log_NamesTheCredentialWhenTheCallerHasNoSubjectOfItsOwn()
    {
        var row = await LogAndReadAsync(
            AuthAuditEventType.SubjectCreated,
            _subjectId,
            caller: new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.InstanceKey,
                CredentialFingerprint = "0123456789abcdef",
            });

        Assert.Equal("InstanceKey:0123456789abcdef", row.ActorCredential);
        Assert.Null(row.ActorSubjectId);
    }

    [Fact]
    public async Task Log_NamesTheCredentialOnAnEventWithNoSubjectOfItsOwn()
    {
        var row = await LogAndReadAsync(
            AuthAuditEventType.PermissionDenied,
            subjectId: null,
            caller: new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.InstanceKey,
                CredentialFingerprint = "0123456789abcdef",
            });

        Assert.Equal("InstanceKey:0123456789abcdef", row.ActorCredential);
        Assert.Null(row.ActorSubjectId);
    }

    [Fact]
    public async Task Log_LeavesTheSubjectAsItsOwnActorOnAnUnauthenticatedRequest()
    {
        var row = await LogAndReadAsync(
            AuthAuditEventType.Login,
            _subjectId,
            caller: AuthContext.Unauthenticated());

        Assert.Equal(_subjectId, row.ActorSubjectId);
        Assert.Null(row.ActorCredential);
    }

    [Fact]
    public async Task Log_LeavesTheSubjectAsItsOwnActorWhenTheCallerIsThatSubject()
    {
        var row = await LogAndReadAsync(
            AuthAuditEventType.TokenIssued,
            _subjectId,
            caller: new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.SessionCookie,
                SubjectId = _subjectId,
            });

        Assert.Equal(_subjectId, row.ActorSubjectId);
        Assert.Null(row.ActorCredential);
    }

    [Fact]
    public void FromCallerOtherThan_YieldsNoActorOnlyWhenTheCallerIsTheSubject()
    {
        var caller = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _subjectId,
        };

        Assert.Null(AuthAuditActor.FromCallerOtherThan(caller, _subjectId));
        Assert.Equal(
            new AuthAuditActor(_subjectId, null),
            AuthAuditActor.FromCallerOtherThan(caller, _adminSubjectId));
    }

    [Fact]
    public async Task Log_PrefersAnExplicitActorOverTheRequestCaller()
    {
        var row = await LogAndReadAsync(
            AuthAuditEventType.TokenRevoked,
            _subjectId,
            caller: new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.SessionCookie,
                SubjectId = _adminSubjectId,
            },
            actor: new AuthAuditActor(null, "InstanceKey:fedcba9876543210"));

        Assert.Equal("InstanceKey:fedcba9876543210", row.ActorCredential);
        Assert.Null(row.ActorSubjectId);
    }

    [Fact]
    public async Task Log_RecordsTheTenantTheRequestIsPinnedTo()
    {
        var row = await LogAndReadAsync(
            AuthAuditEventType.Login, _subjectId, caller: null, pinnedTenantId: _tenantId);

        Assert.Equal(_tenantId, row.TenantId);
    }

    [Fact]
    public async Task Log_LeavesTheTenantNullOnAnUnpinnedRequest()
    {
        var row = await LogAndReadAsync(AuthAuditEventType.Login, _subjectId, caller: null);

        Assert.Null(row.TenantId);
    }

    [Fact]
    public async Task Log_KeepsAnExplicitTenantOnAnUnpinnedRequest()
    {
        var row = await LogAndReadAsync(
            AuthAuditEventType.PlatformAdminGrantIssued,
            _subjectId,
            caller: null,
            tenantId: _tenantId);

        Assert.Equal(_tenantId, row.TenantId);
    }

    [Fact]
    public async Task Log_JoinsTheEventToTheTraceTheAuditMiddlewareStamped()
    {
        var row = await LogAndReadAsync(
            AuthAuditEventType.Login,
            _subjectId,
            caller: null,
            requestTrace: "0HN7GKQ8V1J2K:00000003");

        Assert.Equal("0HN7GKQ8V1J2K:00000003", row.TraceId);
    }

    [Fact]
    public async Task Log_LeavesTheTraceNullOutsideARequest()
    {
        var row = await LogAndReadAsync(AuthAuditEventType.Logout, _subjectId, caller: null);

        Assert.Null(row.TraceId);
    }

    /// <summary>
    /// Writes one event through the real service on a context pinned to
    /// <paramref name="pinnedTenantId"/>, with <paramref name="caller"/> on the ambient request,
    /// and reads the row back. A non-null <paramref name="requestTrace"/> makes the request one
    /// <see cref="AuditContextMiddleware"/> has already run over, as it has for every caller of
    /// the service.
    /// </summary>
    private async Task<AuthAuditLogEntity> LogAndReadAsync(
        string eventType,
        Guid? subjectId,
        AuthContext? caller,
        Guid? pinnedTenantId = null,
        AuthAuditActor? actor = null,
        Guid? tenantId = null,
        string? requestTrace = null)
    {
        var httpContext = new DefaultHttpContext();
        if (caller is not null)
        {
            httpContext.Items["AuthContext"] = caller;
        }

        var auditContext = new AuditContext();
        if (requestTrace is not null)
        {
            httpContext.TraceIdentifier = requestTrace;
            await new AuditContextMiddleware(_ => Task.CompletedTask)
                .InvokeAsync(httpContext, auditContext);
        }

        await using var dbContext = new NocturneDbContext(_dbOptions)
        {
            TenantId = pinnedTenantId ?? Guid.Empty,
        };

        var service = new AuthAuditService(
            dbContext,
            new HttpContextAccessor { HttpContext = httpContext },
            auditContext,
            new Mock<ILogger<AuthAuditService>>().Object);

        await service.LogAsync(
            eventType, subjectId, success: true, actor: actor, tenantId: tenantId);

        await using var reader = new NocturneDbContext(_dbOptions);
        return await reader.AuthAuditLog.SingleAsync(a => a.EventType == eventType);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
