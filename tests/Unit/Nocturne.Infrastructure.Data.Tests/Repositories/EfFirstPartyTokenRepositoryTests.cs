using FluentAssertions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Demo;
using Nocturne.Infrastructure.Data;
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
        DateTime? issuedAt = null,
        string? tokenHash = null,
        Guid? id = null)
    {
        var entity = new RefreshTokenEntity
        {
            Id = id ?? Guid.CreateVersion7(),
            TokenHash = tokenHash ?? Guid.NewGuid().ToString("N"),
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

    #region Demo subject IP/user-agent scrubbing

    // A demo tenant's visitor account is shared: anyone can obtain a session for it without
    // signing up, and GET /api/v4/account/sessions lists every session for the subject —
    // IpAddress included — to any member of it. Recording the caller's address would show each
    // visitor where every other current visitor connects from, and let them revoke each other.
    //
    // Enforced at this sink rather than at the callers because the callers do not agree: the
    // sign-in endpoints pass no address on purpose, but RotateRefreshTokenAsync carries the old
    // row's values forward, and POST /api/auth/oidc/refresh is [AllowAnonymous] — so the web
    // app's first automatic refresh put the real client address straight back. Testing the
    // rotate shape here, not just the issue shape, is the point.

    [Fact]
    public async Task CreateAsync_scrubs_ip_and_user_agent_for_a_demo_subject()
    {
        var demoSubjectId = await AddSubjectAsync(isDemoSubject: true);

        await _repository.CreateAsync(NewRecord(
            demoSubjectId, ipAddress: "203.0.113.44", userAgent: "Mozilla/5.0 (visitor)"));

        var stored = _context.RefreshTokens.Single(t => t.SubjectId == demoSubjectId);
        stored.IpAddress.Should().BeNull("a shared account must not accumulate visitor addresses");
        stored.UserAgent.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_scrubs_a_rotated_row_carrying_the_previous_values_forward()
    {
        // The exact shape RotateRefreshTokenAsync produces: IpAddress = ipAddress ?? old.IpAddress.
        // The issue path having passed nulls does not help once rotation supplies real ones.
        var demoSubjectId = await AddSubjectAsync(isDemoSubject: true);

        await _repository.CreateAsync(NewRecord(demoSubjectId, ipAddress: null, userAgent: null));
        await _repository.CreateAsync(NewRecord(
            demoSubjectId, ipAddress: "198.51.100.9", userAgent: "Mozilla/5.0 (refresh)"));

        _context.RefreshTokens
            .Where(t => t.SubjectId == demoSubjectId)
            .Should().OnlyContain(t => t.IpAddress == null && t.UserAgent == null,
                "no path may repopulate them, including rotation");
    }

    [Fact]
    public async Task CreateAsync_keeps_ip_and_user_agent_for_an_ordinary_subject()
    {
        // Real members rely on these: the session list is how someone spots a login they do not
        // recognise. Scrubbing everyone would be a privacy regression, not a fix.
        var subjectId = await AddSubjectAsync(isDemoSubject: false);

        await _repository.CreateAsync(NewRecord(
            subjectId, ipAddress: "203.0.113.44", userAgent: "Mozilla/5.0 (real)"));

        var stored = _context.RefreshTokens.Single(t => t.SubjectId == subjectId);
        stored.IpAddress.Should().Be("203.0.113.44");
        stored.UserAgent.Should().Be("Mozilla/5.0 (real)");
    }

    [Fact]
    public async Task CreateAsync_keeps_ip_and_user_agent_when_the_subject_row_is_absent()
    {
        // A missing subject is not a demo subject, and the row still has to be written — the
        // scrub must not become a silent data-loss path for tokens whose subject this context
        // cannot see.
        await _repository.CreateAsync(NewRecord(
            _otherSubjectId, ipAddress: "203.0.113.7", userAgent: "cli"));

        var stored = _context.RefreshTokens.Single(t => t.SubjectId == _otherSubjectId);
        stored.IpAddress.Should().Be("203.0.113.7");
        stored.UserAgent.Should().Be("cli");
    }

    /// <summary>
    /// Rotation is the path the sign-in-time cap never sees, and it is driven by an anonymous
    /// endpoint with no rate limit of its own, so the cap has to hold here or it holds nowhere.
    /// </summary>
    [Fact]
    public async Task CreateAsync_caps_a_demo_subjects_live_sessions_across_rotation()
    {
        var demoSubjectId = await AddSubjectAsync(isDemoSubject: true);

        // Far more rotations than the cap, none of them going near the sign-in path.
        for (var i = 0; i < DemoSessionLimits.MaxLiveSessions + 40; i++)
        {
            await _repository.CreateAsync(NewRecord(demoSubjectId, ipAddress: null, userAgent: null));
        }

        _context.RefreshTokens.Count(t => t.SubjectId == demoSubjectId)
            .Should().Be(DemoSessionLimits.MaxLiveSessions,
                "an anonymous account anyone can obtain must not be able to grow the table without bound");
    }

    /// <summary>
    /// Which rows the cap displaces, not just how many survive it. The inverse ordering evicts
    /// whoever just signed in, on every subsequent sign-in, while the oldest rows never leave.
    /// </summary>
    [Fact]
    public async Task CreateAsync_displaces_a_demo_subjects_oldest_sessions_and_keeps_the_newest()
    {
        var demoSubjectId = await AddSubjectAsync(isDemoSubject: true);

        // Distinct IssuedAt values, so the expected retention is a total order and nothing here
        // rests on the tiebreaker.
        const int seeded = DemoSessionLimits.MaxLiveSessions + 5;
        var issuedAt = DateTime.UtcNow.AddMinutes(-seeded);
        for (var i = 0; i < seeded; i++)
        {
            AddToken(demoSubjectId, $"session-{i}", issuedAt: issuedAt.AddMinutes(i), tokenHash: $"seed-{i}");
        }

        await _repository.CreateAsync(NewRecord(demoSubjectId, ipAddress: null, userAgent: null));

        var surviving = _context.RefreshTokens
            .Where(t => t.SubjectId == demoSubjectId && t.TokenHash.StartsWith("seed-"))
            .Select(t => t.TokenHash)
            .ToList();

        // 5 + 1 seeded rows go: the cap, less the slot the new row takes.
        var expected = Enumerable
            .Range(seeded - (DemoSessionLimits.MaxLiveSessions - 1), DemoSessionLimits.MaxLiveSessions - 1)
            .Select(i => $"seed-{i}");

        surviving.Should().BeEquivalentTo(expected,
            "the cap displaces the oldest sessions and keeps the newest");
        surviving.Should().NotContain("seed-0", "the oldest session is the first to go");
        surviving.Should().Contain($"seed-{seeded - 1}", "the newest session is the last to go");
    }

    /// <summary>
    /// Visitors arriving together are issued rows in the same instant, so which of them the cap
    /// displaces must not be left to the provider. The token id settles it.
    /// </summary>
    /// <remarks>
    /// The two ids differ only in their final byte, which .NET's <see cref="Guid"/> comparison and
    /// PostgreSQL's bytewise <c>uuid</c> comparison order the same way, so the row expected to
    /// survive does not depend on which of them runs the sort.
    /// </remarks>
    [Fact]
    public async Task CreateAsync_breaks_a_demo_subjects_issued_at_tie_by_token_id()
    {
        var demoSubjectId = await AddSubjectAsync(isDemoSubject: true);

        var tied = DateTime.UtcNow.AddHours(-1);
        var lowerId = Guid.Parse("11111111-1111-1111-1111-111111111110");
        var higherId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        // Seeded lower id first: without the tiebreaker, the InMemory provider's stable sort leaves
        // them in this order and displaces the wrong one, so dropping it fails here. Against real
        // PostgreSQL an untied order is unspecified, so this pins the tiebreaker, not its absence.
        AddToken(demoSubjectId, "tie-low", issuedAt: tied, tokenHash: "tie-low", id: lowerId);
        AddToken(demoSubjectId, "tie-high", issuedAt: tied, tokenHash: "tie-high", id: higherId);

        // Fill the rest of the cap with strictly newer rows, so the tied pair is the boundary and
        // exactly one of the two has to go.
        for (var i = 0; i < DemoSessionLimits.MaxLiveSessions - 2; i++)
        {
            AddToken(demoSubjectId, $"newer-{i}", issuedAt: tied.AddMinutes(i + 1), tokenHash: $"newer-{i}");
        }

        await _repository.CreateAsync(NewRecord(demoSubjectId, ipAddress: null, userAgent: null));

        var surviving = _context.RefreshTokens
            .Where(t => t.SubjectId == demoSubjectId)
            .Select(t => t.TokenHash)
            .ToList();

        surviving.Should().Contain("tie-high", "the higher id sorts first and is kept");
        surviving.Should().NotContain("tie-low", "the lower id is the one the tiebreaker displaces");
    }

    [Fact]
    public async Task CreateAsync_leaves_an_ordinary_subjects_sessions_alone()
    {
        // Positive control: a real member accumulating sessions across many devices must not have
        // the oldest silently deleted, which is what makes the trim safe to run at this sink.
        var subjectId = await AddSubjectAsync(isDemoSubject: false);

        for (var i = 0; i < DemoSessionLimits.MaxLiveSessions + 5; i++)
        {
            await _repository.CreateAsync(NewRecord(subjectId, ipAddress: null, userAgent: null));
        }

        _context.RefreshTokens.Count(t => t.SubjectId == subjectId)
            .Should().Be(DemoSessionLimits.MaxLiveSessions + 5);
    }

    [Fact]
    public async Task CreateAsync_clears_a_demo_subjects_dead_rows_before_displacing_live_ones()
    {
        var demoSubjectId = await AddSubjectAsync(isDemoSubject: true);
        var live = AddToken(demoSubjectId, "live-session", issuedAt: DateTime.UtcNow.AddDays(-30));
        AddToken(demoSubjectId, "revoked", revokedAt: DateTime.UtcNow.AddMinutes(-1));
        AddToken(demoSubjectId, "expired", expiresAt: DateTime.UtcNow.AddMinutes(-1));

        await _repository.CreateAsync(NewRecord(demoSubjectId, ipAddress: null, userAgent: null));

        _context.RefreshTokens.Count(t => t.SubjectId == demoSubjectId).Should().Be(2);
        _context.RefreshTokens.Any(t => t.Id == live.Id).Should().BeTrue(
            "the oldest live session is only displaced once nothing dead is left to clear");
    }

    private async Task<Guid> AddSubjectAsync(bool isDemoSubject)
    {
        var subject = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = isDemoSubject ? "Demo Visitor" : "Real Person",
            IsActive = true,
            IsDemoSubject = isDemoSubject,
        };
        _context.Subjects.Add(subject);
        await _context.SaveChangesAsync();
        return subject.Id;
    }

    private static RefreshTokenRecord NewRecord(
        Guid subjectId, string? ipAddress, string? userAgent) => new(
            Id: Guid.CreateVersion7(),
            TokenHash: Guid.NewGuid().ToString("N"),
            SubjectId: subjectId,
            OidcSessionId: null,
            DeviceDescription: "demo-visitor",
            IpAddress: ipAddress,
            UserAgent: userAgent,
            IssuedAt: DateTime.UtcNow,
            ExpiresAt: DateTime.UtcNow.AddDays(30),
            RevokedAt: null,
            RevokedReason: null,
            ReplacedByTokenId: null,
            LastUsedAt: null);

    #endregion
}
