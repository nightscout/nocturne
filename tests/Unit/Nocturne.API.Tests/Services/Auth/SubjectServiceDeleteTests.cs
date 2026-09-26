using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Covers what <see cref="SubjectService.DeleteSubjectAsync"/> leaves of the deleted subject's auth
/// audit trail, over SQLite so the scrub's bulk updates and the foreign keys both run for real.
/// </summary>
public class SubjectServiceDeleteTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Guid _deletedId = Guid.CreateVersion7();
    private readonly Guid _adminId = Guid.CreateVersion7();
    private readonly Guid _bystanderId = Guid.CreateVersion7();

    private readonly Guid _selfLoginRow = Guid.CreateVersion7();
    private readonly Guid _actedOnOtherRow = Guid.CreateVersion7();
    private readonly Guid _actedOnByAdminRow = Guid.CreateVersion7();
    private readonly Guid _unrelatedRow = Guid.CreateVersion7();

    public SubjectServiceDeleteTests()
    {
        using var seed = _db.CreateContext();
        seed.Subjects.AddRange(
            new SubjectEntity { Id = _deletedId, Name = "Leaver", IsActive = true },
            new SubjectEntity { Id = _adminId, Name = "Admin", IsActive = true },
            new SubjectEntity { Id = _bystanderId, Name = "Bystander", IsActive = true });
        seed.AuthAuditLog.AddRange(
            Row(_selfLoginRow, AuthAuditEventType.Login, subject: _deletedId, actor: _deletedId, "10.0.0.1"),
            Row(_actedOnOtherRow, AuthAuditEventType.RoleAssigned, subject: _bystanderId, actor: _deletedId, "10.0.0.2"),
            Row(_actedOnByAdminRow, AuthAuditEventType.RoleAssigned, subject: _deletedId, actor: _adminId, "10.0.0.3"),
            Row(_unrelatedRow, AuthAuditEventType.Login, subject: _bystanderId, actor: _bystanderId, "10.0.0.4"));
        seed.SaveChanges();
    }

    [Fact]
    public async Task Delete_KeepsTheSubjectAndActorIdsOnEveryRow()
    {
        await DeleteAsLeaverAsync();

        var rows = await ReadRowsAsync();
        rows[_selfLoginRow].SubjectId.Should().Be(_deletedId);
        rows[_selfLoginRow].ActorSubjectId.Should().Be(_deletedId);
        rows[_actedOnOtherRow].ActorSubjectId.Should().Be(_deletedId);
        rows[_actedOnByAdminRow].SubjectId.Should().Be(_deletedId);
    }

    [Fact]
    public async Task Delete_ClearsTheClientOfEveryRowTheSubjectActedOn()
    {
        await DeleteAsLeaverAsync();

        var rows = await ReadRowsAsync();
        foreach (var id in new[] { _selfLoginRow, _actedOnOtherRow })
        {
            rows[id].IpAddress.Should().BeNull();
            rows[id].UserAgent.Should().BeNull();
        }
    }

    [Fact]
    public async Task Delete_ClearsTheDetailsOfEveryRowTheSubjectIsPartyTo()
    {
        await DeleteAsLeaverAsync();

        var rows = await ReadRowsAsync();
        rows[_selfLoginRow].DetailsJson.Should().BeNull();
        rows[_actedOnOtherRow].DetailsJson.Should().BeNull();
        rows[_actedOnByAdminRow].DetailsJson.Should().BeNull();
    }

    [Fact]
    public async Task Delete_KeepsAnotherActorsClientOnARowAboutTheSubject()
    {
        await DeleteAsLeaverAsync();

        var row = (await ReadRowsAsync())[_actedOnByAdminRow];
        row.IpAddress.Should().Be("10.0.0.3");
        row.UserAgent.Should().Be("agent-10.0.0.3");
    }

    [Fact]
    public async Task Delete_LeavesRowsTheSubjectIsNoPartyToUntouched()
    {
        await DeleteAsLeaverAsync();

        var row = (await ReadRowsAsync())[_unrelatedRow];
        row.IpAddress.Should().Be("10.0.0.4");
        row.UserAgent.Should().Be("agent-10.0.0.4");
        row.DetailsJson.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_RecordsTheDeletionAgainstTheDeletedSubjectAndTheCaller()
    {
        await DeleteAsLeaverAsync();

        await using var reader = _db.CreateContext();
        var deleted = await reader.AuthAuditLog.SingleAsync(a => a.EventType == AuthAuditEventType.SubjectDeleted);
        deleted.SubjectId.Should().Be(_deletedId);
        deleted.ActorSubjectId.Should().Be(_adminId);
        (await reader.Subjects.AnyAsync(s => s.Id == _deletedId)).Should().BeFalse();
    }

    private async Task DeleteAsLeaverAsync()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _adminId,
        };

        await using var dbContext = _db.CreateContext();
        var audit = new AuthAuditService(
            dbContext,
            new HttpContextAccessor { HttpContext = httpContext },
            new AuditContext(),
            NullLogger<AuthAuditService>.Instance);
        var service = new SubjectService(
            dbContext, audit, Mock.Of<IRecoveryCodeService>(), NullLogger<SubjectService>.Instance);

        (await service.DeleteSubjectAsync(_deletedId)).Should().BeTrue();
    }

    private async Task<Dictionary<Guid, AuthAuditLogEntity>> ReadRowsAsync()
    {
        await using var reader = _db.CreateContext();
        return await reader.AuthAuditLog.AsNoTracking().ToDictionaryAsync(a => a.Id);
    }

    private static AuthAuditLogEntity Row(Guid id, string eventType, Guid subject, Guid actor, string ip) => new()
    {
        Id = id,
        EventType = eventType,
        SubjectId = subject,
        ActorSubjectId = actor,
        IpAddress = ip,
        UserAgent = $"agent-{ip}",
        DetailsJson = """{"email":"someone@example.invalid"}""",
    };

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
