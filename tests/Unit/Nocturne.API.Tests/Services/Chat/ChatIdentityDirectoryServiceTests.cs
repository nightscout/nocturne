using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Chat;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Chat;

[Trait("Category", "Unit")]
public class ChatIdentityDirectoryServiceTests : IDisposable
{
    private const string Platform = "discord";
    private const string UserA = "discord-user-a";
    private const string UserB = "discord-user-b";

    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly TestDbContextFactory _factory;
    private readonly ChatIdentityDirectoryService _service;

    public ChatIdentityDirectoryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var db = new NocturneDbContext(_options))
        {
            db.Database.EnsureCreated();
        }

        _factory = new TestDbContextFactory(_options);
        _service = new ChatIdentityDirectoryService(
            _factory,
            Mock.Of<ILogger<ChatIdentityDirectoryService>>());
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// Inserts a tenant and returns its id. chat_identity_directory.tenant_id is a real FK with
    /// ON DELETE CASCADE, so a link cannot point at a tenant that was never created.
    /// </summary>
    private Guid NewTenant()
    {
        var id = Guid.CreateVersion7();
        using var db = new NocturneDbContext(_options);
        db.Tenants.Add(new TenantEntity
        {
            Id = id,
            Slug = $"t-{id:n}"[..20],
            DisplayName = "Test Tenant",
        });
        db.SaveChanges();
        return id;
    }

    /// <summary>
    /// Inserts a subject and returns its id. chat_identity_directory.nocturne_user_id is a real FK
    /// with ON DELETE CASCADE, so a link cannot point at a subject that was never created.
    /// </summary>
    private Guid NewSubject()
    {
        var id = Guid.CreateVersion7();
        using var db = new NocturneDbContext(_options);
        db.Subjects.Add(new SubjectEntity { Id = id, Name = $"s-{id:n}"[..20] });
        db.SaveChanges();
        return id;
    }

    private sealed class TestDbContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Context that lets another writer commit just before its own insert, reproducing a concurrent
    /// create landing between the service's label read and its <c>SaveChangesAsync</c>. The real
    /// unique index then rejects the insert, so the retry path runs against SQLite's enforcement
    /// rather than a stubbed exception. The interloper is handed the row the service is about to
    /// write so it can steal exactly the label the service picked.
    /// </summary>
    /// <remarks>
    /// SQLite reports a unique violation as a <see cref="SqliteException"/> carrying no SQLSTATE,
    /// so the rejection is re-presented as Npgsql would report it. The index still did the
    /// rejecting; only the error code is translated, which is what lets the service's own
    /// unique-violation test run here rather than a test-only stand-in for it.
    /// </remarks>
    private sealed class InterloperDbContext(
        DbContextOptions<NocturneDbContext> options,
        Action<ChatIdentityDirectoryEntry> interlope) : NocturneDbContext(options)
    {
        public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            var pending = ChangeTracker.Entries<ChatIdentityDirectoryEntry>()
                .FirstOrDefault(e => e.State == EntityState.Added)?.Entity;
            if (pending is not null)
            {
                interlope(pending);
            }

            try
            {
                return await base.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                throw new DbUpdateException(
                    ex.Message,
                    new PostgresException(
                        ex.InnerException.Message,
                        "ERROR",
                        "ERROR",
                        PostgresErrorCodes.UniqueViolation));
            }
        }
    }

    /// <summary>
    /// Hands out an <see cref="InterloperDbContext"/> for the first <paramref name="interlopeCount"/>
    /// requests, then plain contexts. <paramref name="beforeRetry"/> runs before every context after
    /// the first, which is the window between a failed insert and the retry's re-read.
    /// </summary>
    private sealed class InterloperDbContextFactory(
        DbContextOptions<NocturneDbContext> options,
        Action<ChatIdentityDirectoryEntry> interlope,
        int interlopeCount,
        Action? beforeRetry = null) : IDbContextFactory<NocturneDbContext>
    {
        private int _interloped;

        public int ContextsCreated { get; private set; }

        public NocturneDbContext CreateDbContext()
        {
            if (ContextsCreated++ > 0)
            {
                beforeRetry?.Invoke();
            }

            if (_interloped >= interlopeCount)
            {
                return new NocturneDbContext(options);
            }

            _interloped++;
            return new InterloperDbContext(options, interlope);
        }

        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Context whose insert always fails with something other than a unique violation, standing in
    /// for a rejection no re-read can turn into a success (a foreign key, a check, an over-long
    /// label).
    /// </summary>
    private sealed class BarrenFailureDbContext(DbContextOptions<NocturneDbContext> options)
        : NocturneDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
            => Task.FromException<int>(new DbUpdateException("insert rejected"));
    }

    /// <summary>Hands out contexts that always fail their insert.</summary>
    private sealed class BarrenFailureDbContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public int ContextsCreated { get; private set; }

        public NocturneDbContext CreateDbContext()
        {
            ContextsCreated++;
            return new BarrenFailureDbContext(options);
        }

        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>Hard-deletes a directory row by label, bypassing the service.</summary>
    private void DeleteLink(string platformUserId, string label)
    {
        using var db = new NocturneDbContext(_options);
        db.ChatIdentityDirectory
            .Where(d => d.Platform == Platform && d.PlatformUserId == platformUserId && d.Label == label)
            .ExecuteDelete();
    }

    /// <summary>Writes a directory row directly, bypassing the service.</summary>
    private void InsertLink(string platformUserId, Guid tenantId, string label, bool isDefault)
    {
        using var db = new NocturneDbContext(_options);
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = Guid.CreateVersion7(),
            Platform = Platform,
            PlatformUserId = platformUserId,
            TenantId = tenantId,
            NocturneUserId = NewSubject(),
            Label = label,
            DisplayName = label,
            IsDefault = isDefault,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    // ---- GetCandidatesAsync ----

    [Fact]
    public async Task GetCandidatesAsync_returns_empty_when_no_links()
    {
        var result = await _service.GetCandidatesAsync(Platform, UserA, default);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCandidatesAsync_returns_single_link_when_one_exists()
    {
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        var result = await _service.GetCandidatesAsync(Platform, UserA, default);
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetCandidatesAsync_returns_all_links_when_multiple_exist()
    {
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "oliver", "Oliver", default);
        var result = await _service.GetCandidatesAsync(Platform, UserA, default);
        result.Should().HaveCount(2);
    }

    /// <summary>
    /// The directory row is the only thing binding a chat account to a tenant, so deleting the
    /// person it was issued for must stop it resolving — for every platform user holding a link to
    /// them, and only for them.
    /// </summary>
    [Fact]
    public async Task GetCandidatesAsync_stops_returning_a_link_whose_subject_is_deleted()
    {
        var subjectId = NewSubject();
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), subjectId, "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "oliver", "Oliver", default);
        await _service.CreateLinkAsync(Platform, UserB, NewTenant(), subjectId, "lily-b", "Lily", default);

        await using (var db = new NocturneDbContext(_options))
        {
            await db.Subjects.Where(s => s.Id == subjectId).ExecuteDeleteAsync();
        }

        (await _service.GetCandidatesAsync(Platform, UserA, default))
            .Select(l => l.Label).Should().BeEquivalentTo(["oliver"]);
        (await _service.GetCandidatesAsync(Platform, UserB, default)).Should().BeEmpty();
    }

    // ---- CreateLinkAsync ----

    [Fact]
    public async Task CreateLinkAsync_marks_first_link_as_default()
    {
        var link = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        link.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task CreateLinkAsync_marks_subsequent_link_as_non_default()
    {
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        var second = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "oliver", "Oliver", default);
        second.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task CreateLinkAsync_is_idempotent_on_same_platform_user_tenant()
    {
        var tenantId = NewTenant();
        var userId = NewSubject();
        var first = await _service.CreateLinkAsync(Platform, UserA, tenantId, userId, "lily", "Lily", default);
        var second = await _service.CreateLinkAsync(Platform, UserA, tenantId, userId, "different", "Different", default);
        second.Id.Should().Be(first.Id);
        second.Label.Should().Be("lily");
        second.DisplayName.Should().Be("Lily");
    }

    [Fact]
    public async Task CreateLinkAsync_auto_suffixes_label_collision()
    {
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily 1", default);
        var second = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily 2", default);
        second.Label.Should().Be("lily-2");
    }

    [Fact]
    public async Task CreateLinkAsync_auto_suffixes_multiple_collisions()
    {
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily 1", default);
        var b = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily 2", default);
        var c = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily 3", default);
        b.Label.Should().Be("lily-2");
        c.Label.Should().Be("lily-3");
    }

    [Fact]
    public async Task CreateLinkAsync_retries_with_next_suffix_when_label_is_taken_mid_insert()
    {
        var owned = NewTenant();
        var stolen = NewTenant();
        var ours = NewTenant();
        await _service.CreateLinkAsync(Platform, UserA, owned, NewSubject(), "other", "Other", default);

        var factory = new InterloperDbContextFactory(
            _options,
            pending => InsertLink(UserA, stolen, pending.Label, isDefault: false),
            interlopeCount: 1);
        var service = new ChatIdentityDirectoryService(
            factory, Mock.Of<ILogger<ChatIdentityDirectoryService>>());

        var entry = await service.CreateLinkAsync(
            Platform, UserA, ours, NewSubject(), "lily", "Ours", default);

        entry.Label.Should().Be("lily-2");
        entry.TenantId.Should().Be(ours);
        factory.ContextsCreated.Should().Be(2, "the first attempt lost the label and had to re-read");

        var all = await _service.GetCandidatesAsync(Platform, UserA, default);
        all.Select(l => l.Label).Should().BeEquivalentTo(["other", "lily", "lily-2"]);
    }

    [Fact]
    public async Task CreateLinkAsync_returns_the_winning_row_when_same_tenant_link_lands_mid_insert()
    {
        var tenantId = NewTenant();

        var factory = new InterloperDbContextFactory(
            _options,
            _ => InsertLink(UserA, tenantId, "winner", isDefault: true),
            interlopeCount: 1);
        var service = new ChatIdentityDirectoryService(
            factory, Mock.Of<ILogger<ChatIdentityDirectoryService>>());

        var entry = await service.CreateLinkAsync(
            Platform, UserA, tenantId, NewSubject(), "loser", "Loser", default);

        entry.Label.Should().Be("winner");

        var all = await _service.GetCandidatesAsync(Platform, UserA, default);
        all.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateLinkAsync_drops_the_default_flag_when_another_first_link_lands_mid_insert()
    {
        var theirs = NewTenant();
        var ours = NewTenant();

        // Two chat users linking their very first tenant at once: both compute IsDefault = true and
        // collide on ux_directory_user_one_default alone — different tenants, different labels.
        var factory = new InterloperDbContextFactory(
            _options,
            _ => InsertLink(UserA, theirs, "theirs", isDefault: true),
            interlopeCount: 1);
        var service = new ChatIdentityDirectoryService(
            factory, Mock.Of<ILogger<ChatIdentityDirectoryService>>());

        var entry = await service.CreateLinkAsync(
            Platform, UserA, ours, NewSubject(), "ours", "Ours", default);

        entry.Label.Should().Be("ours", "the label never collided");
        entry.IsDefault.Should().BeFalse("the concurrent first link already claimed the default");
        factory.ContextsCreated.Should().Be(2);

        var all = await _service.GetCandidatesAsync(Platform, UserA, default);
        all.Where(l => l.IsDefault).Select(l => l.Label).Should().BeEquivalentTo(["theirs"]);
    }

    /// <summary>
    /// The interloper's row is hard-deleted before the retry re-reads, so the label set is exactly
    /// what the failed attempt saw. The rejection was still a lost race and retrying still resolves
    /// it — which is why the retry turns on what the database rejected rather than on whether the
    /// re-read can spot the other writer.
    /// </summary>
    [Fact]
    public async Task CreateLinkAsync_retries_when_the_row_that_beat_it_is_deleted_before_the_re_read()
    {
        var owned = NewTenant();
        var stolen = NewTenant();
        var ours = NewTenant();
        await _service.CreateLinkAsync(Platform, UserA, owned, NewSubject(), "other", "Other", default);

        var factory = new InterloperDbContextFactory(
            _options,
            pending => InsertLink(UserA, stolen, pending.Label, isDefault: false),
            interlopeCount: 1,
            beforeRetry: () => DeleteLink(UserA, "lily"));
        var service = new ChatIdentityDirectoryService(
            factory, Mock.Of<ILogger<ChatIdentityDirectoryService>>());

        var entry = await service.CreateLinkAsync(
            Platform, UserA, ours, NewSubject(), "lily", "Ours", default);

        entry.Label.Should().Be("lily", "the freed label is available again");
        factory.ContextsCreated.Should().Be(2);

        var all = await _service.GetCandidatesAsync(Platform, UserA, default);
        all.Select(l => l.Label).Should().BeEquivalentTo(["other", "lily"]);
    }

    [Fact]
    public async Task CreateLinkAsync_surfaces_a_rejection_a_re_read_cannot_fix()
    {
        var factory = new BarrenFailureDbContextFactory(_options);
        var service = new ChatIdentityDirectoryService(
            factory, Mock.Of<ILogger<ChatIdentityDirectoryService>>());

        var act = async () => await service.CreateLinkAsync(
            Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);

        await act.Should().ThrowAsync<DbUpdateException>().WithMessage("insert rejected");
        factory.ContextsCreated.Should().Be(
            1,
            "only a unique violation is a lost race, so anything else surfaces on the first attempt "
            + "instead of burning MaxCreateAttempts saves");
    }

    [Fact]
    public async Task CreateLinkAsync_surfaces_the_rejection_when_every_attempt_loses()
    {
        var factory = new InterloperDbContextFactory(
            _options,
            pending => InsertLink(UserA, NewTenant(), pending.Label, isDefault: false),
            interlopeCount: int.MaxValue);
        var service = new ChatIdentityDirectoryService(
            factory, Mock.Of<ILogger<ChatIdentityDirectoryService>>());

        var act = async () => await service.CreateLinkAsync(
            Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);

        await act.Should().ThrowAsync<DbUpdateException>();
        factory.ContextsCreated.Should().Be(5, "retries are bounded, not unlimited");
    }

    // ---- SetDefaultAsync ----

    [Fact]
    public async Task SetDefaultAsync_promotes_target_and_clears_other_defaults()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        var b = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "oliver", "Oliver", default);
        a.IsDefault.Should().BeTrue();
        b.IsDefault.Should().BeFalse();

        await _service.SetDefaultAsync(b.Id, tenantScope: null, ct: default);

        var aAfter = await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default);
        var bAfter = await _service.GetByIdAsync(b.Id, tenantScope: null, ct: default);
        aAfter!.IsDefault.Should().BeFalse();
        bAfter!.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task SetDefaultAsync_throws_when_link_not_found()
    {
        var act = async () => await _service.SetDefaultAsync(Guid.CreateVersion7(), tenantScope: null, ct: default);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ---- RenameLabelAsync ----

    [Fact]
    public async Task RenameLabelAsync_updates_label()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        await _service.RenameLabelAsync(a.Id, tenantScope: null, "rose", ct: default);
        var after = await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default);
        after!.Label.Should().Be("rose");
    }

    [Fact]
    public async Task RenameLabelAsync_throws_on_collision()
    {
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        var b = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "oliver", "Oliver", default);

        var act = async () => await _service.RenameLabelAsync(b.Id, tenantScope: null, "lily", ct: default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- UpdateDisplayNameAsync ----

    [Fact]
    public async Task UpdateDisplayNameAsync_updates_display_name()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        await _service.UpdateDisplayNameAsync(a.Id, tenantScope: null, "Lily Renamed", ct: default);
        var after = await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default);
        after!.DisplayName.Should().Be("Lily Renamed");
    }

    // ---- RevokeAsync ----

    [Fact]
    public async Task RevokeAsync_hard_deletes_row()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        await _service.RevokeAsync(a.Id, tenantScope: null, ct: default);
        var after = await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default);
        after.Should().BeNull();
    }

    // ---- GetByTenantAsync ----

    [Fact]
    public async Task GetByTenantAsync_returns_only_links_for_that_tenant()
    {
        var t1 = NewTenant();
        var t2 = NewTenant();
        await _service.CreateLinkAsync(Platform, UserA, t1, NewSubject(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserB, t2, NewSubject(), "oliver", "Oliver", default);

        var result = await _service.GetByTenantAsync(t1, default);
        result.Should().HaveCount(1);
        result[0].TenantId.Should().Be(t1);
    }

    // ---- GetByPlatformAndUserAsync ----

    [Fact]
    public async Task GetByPlatformAndUserAsync_returns_exact_match_when_label_provided()
    {
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "oliver", "Oliver", default);

        var result = await _service.GetByPlatformAndUserAsync(Platform, UserA, "oliver", default);
        result.Should().NotBeNull();
        result!.Label.Should().Be("oliver");
    }

    [Fact]
    public async Task GetByPlatformAndUserAsync_returns_default_when_label_null_and_multiple_exist()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "oliver", "Oliver", default);

        var result = await _service.GetByPlatformAndUserAsync(Platform, UserA, null, default);
        result.Should().NotBeNull();
        result!.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task GetByPlatformAndUserAsync_returns_single_when_only_one_exists()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        // Force IsDefault=false to simulate "no default"
        using (var db = _factory.CreateDbContext())
        {
            var entity = await db.ChatIdentityDirectory.FirstAsync(d => d.Id == a.Id);
            entity.IsDefault = false;
            await db.SaveChangesAsync();
        }

        var result = await _service.GetByPlatformAndUserAsync(Platform, UserA, null, default);
        result.Should().NotBeNull();
        result!.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task GetByPlatformAndUserAsync_returns_null_when_label_null_and_ambiguous()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "oliver", "Oliver", default);
        // Clear default flag without setting another
        using (var db = _factory.CreateDbContext())
        {
            var entity = await db.ChatIdentityDirectory.FirstAsync(d => d.Id == a.Id);
            entity.IsDefault = false;
            await db.SaveChangesAsync();
        }

        var result = await _service.GetByPlatformAndUserAsync(Platform, UserA, null, default);
        result.Should().BeNull();
    }

    // ---- GetByIdAsync ----

    [Fact]
    public async Task GetByIdAsync_returns_link_or_null()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, NewTenant(), NewSubject(), "lily", "Lily", default);
        (await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default)).Should().NotBeNull();
        (await _service.GetByIdAsync(Guid.CreateVersion7(), tenantScope: null, ct: default)).Should().BeNull();
    }
}
