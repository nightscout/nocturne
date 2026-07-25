using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Exercises legacy Nightscout token resolution on <see cref="SubjectService"/> against a real EF
/// InMemory DbContext. Vector: digest = 318030bcdc470b9d05518755491a80239a640400, canonical token
/// phone-318030bcdc470b9d (see <see cref="LegacyNightscoutTokenTests"/>).
/// </summary>
public class SubjectServiceLegacyTokenTests : IDisposable
{
    private const string Digest = "318030bcdc470b9d05518755491a80239a640400";
    private const string CanonicalToken = "phone-318030bcdc470b9d";

    private readonly NocturneDbContext _db;
    private readonly SubjectService _service;
    private readonly Mock<IAuthAuditService> _audit = new();

    public SubjectServiceLegacyTokenTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext();
        _service = new SubjectService(_db, _audit.Object, NullLogger<SubjectService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Guid> SeedMigratedSubjectAsync(bool isActive = true)
    {
        var id = Guid.CreateVersion7();
        _db.Subjects.Add(new SubjectEntity
        {
            Id = id,
            Name = "Phone",
            AccessTokenHash = "unused-sha256",
            LegacyTokenDigest = Digest,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task FindSubjectByLegacyTokenAsync_resolves_migrated_token()
    {
        var id = await SeedMigratedSubjectAsync();

        var subject = await _service.FindSubjectByLegacyTokenAsync(CanonicalToken);

        subject.Should().NotBeNull();
        subject!.Id.Should().Be(id);
    }

    [Fact]
    public async Task FindSubjectByLegacyTokenAsync_ignores_inactive_subjects()
    {
        await SeedMigratedSubjectAsync(isActive: false);

        var subject = await _service.FindSubjectByLegacyTokenAsync(CanonicalToken);

        subject.Should().BeNull();
    }

    [Fact]
    public async Task FindSubjectByLegacyTokenAsync_returns_null_for_non_matching_token()
    {
        await SeedMigratedSubjectAsync();

        var subject = await _service.FindSubjectByLegacyTokenAsync("phone-aaaaaaaaaaaaaaaa");

        subject.Should().BeNull();
    }

    [Fact]
    public async Task RegenerateAccessTokenAsync_revokes_the_legacy_token()
    {
        var id = await SeedMigratedSubjectAsync();

        var newToken = await _service.RegenerateAccessTokenAsync(id);

        newToken.Should().NotBeNull();
        // The digest must be cleared so the old (possibly leaked) legacy token stops authenticating.
        (await _service.FindSubjectByLegacyTokenAsync(CanonicalToken)).Should().BeNull();
        (await _db.Subjects.AsNoTracking().FirstAsync(s => s.Id == id)).LegacyTokenDigest.Should().BeNull();
    }
}
