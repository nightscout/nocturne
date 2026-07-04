using FluentAssertions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class EfFirstPartyTokenRepositoryTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly EfFirstPartyTokenRepository _repository;
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _otherSubjectId = Guid.CreateVersion7();

    public EfFirstPartyTokenRepositoryTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = TenantId;
        _repository = new EfFirstPartyTokenRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private RefreshTokenEntity AddToken(
        Guid subjectId,
        string? oidcSessionId,
        DateTime? revokedAt = null,
        DateTime? expiresAt = null,
        DateTime? issuedAt = null)
    {
        var entity = new RefreshTokenEntity
        {
            Id = Guid.CreateVersion7(),
            TokenHash = Guid.NewGuid().ToString("N"),
            SubjectId = subjectId,
            OidcSessionId = oidcSessionId,
            IssuedAt = issuedAt ?? DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(7),
            RevokedAt = revokedAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _context.RefreshTokens.Add(entity);
        _context.SaveChanges();
        return entity;
    }

    [Fact]
    public async Task RevokeSessionForSubject_revokes_all_tokens_of_that_session_only()
    {
        var sessionA = "session-a";
        var a1 = AddToken(_subjectId, sessionA);
        var a2 = AddToken(_subjectId, sessionA);
        var b1 = AddToken(_subjectId, "session-b");

        var count = await _repository.RevokeSessionForSubjectAsync(_subjectId, sessionA, "test");

        count.Should().Be(2);
        _context.RefreshTokens.Single(t => t.Id == a1.Id).RevokedAt.Should().NotBeNull();
        _context.RefreshTokens.Single(t => t.Id == a2.Id).RevokedAt.Should().NotBeNull();
        _context.RefreshTokens.Single(t => t.Id == b1.Id).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task RevokeSessionForSubject_does_not_touch_other_subjects_sessions()
    {
        var sessionId = "shared-session-id";
        var theirs = AddToken(_otherSubjectId, sessionId);

        var count = await _repository.RevokeSessionForSubjectAsync(_subjectId, sessionId, "test");

        count.Should().Be(0);
        _context.RefreshTokens.Single(t => t.Id == theirs.Id).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task RevokeSessionForSubject_legacy_token_without_session_id_matches_by_token_id()
    {
        var legacy = AddToken(_subjectId, oidcSessionId: null);

        var count = await _repository.RevokeSessionForSubjectAsync(
            _subjectId, legacy.Id.ToString(), "test");

        count.Should().Be(1);
        _context.RefreshTokens.Single(t => t.Id == legacy.Id).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeOtherSessionsForSubject_keeps_current_session_and_other_subjects()
    {
        var current = "current-session";
        var mine1 = AddToken(_subjectId, current);
        var mine2 = AddToken(_subjectId, "other-session");
        var legacy = AddToken(_subjectId, oidcSessionId: null);
        var theirs = AddToken(_otherSubjectId, "their-session");

        var count = await _repository.RevokeOtherSessionsForSubjectAsync(_subjectId, current, "test");

        count.Should().Be(2);
        _context.RefreshTokens.Single(t => t.Id == mine1.Id).RevokedAt.Should().BeNull();
        _context.RefreshTokens.Single(t => t.Id == mine2.Id).RevokedAt.Should().NotBeNull();
        _context.RefreshTokens.Single(t => t.Id == legacy.Id).RevokedAt.Should().NotBeNull();
        _context.RefreshTokens.Single(t => t.Id == theirs.Id).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task RevokeOtherSessionsForSubject_with_legacy_current_keeps_that_token()
    {
        var legacyCurrent = AddToken(_subjectId, oidcSessionId: null);
        var other = AddToken(_subjectId, "some-session");

        var count = await _repository.RevokeOtherSessionsForSubjectAsync(
            _subjectId, legacyCurrent.Id.ToString(), "test");

        count.Should().Be(1);
        _context.RefreshTokens.Single(t => t.Id == legacyCurrent.Id).RevokedAt.Should().BeNull();
        _context.RefreshTokens.Single(t => t.Id == other.Id).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetActiveSessions_projects_oidc_session_id_and_skips_revoked_and_expired()
    {
        var active = AddToken(_subjectId, "session-a");
        AddToken(_subjectId, "session-b", revokedAt: DateTime.UtcNow);
        AddToken(_subjectId, "session-c", expiresAt: DateTime.UtcNow.AddMinutes(-1));

        var sessions = await _repository.GetActiveSessionsAsync(_subjectId);

        sessions.Should().ContainSingle();
        sessions[0].Id.Should().Be(active.Id);
        sessions[0].OidcSessionId.Should().Be("session-a");
    }
}
