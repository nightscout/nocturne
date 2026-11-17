using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Chat;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Chat;

[Trait("Category", "Unit")]
public class ChatIdentityPendingLinkServiceTests : IDisposable
{
    private const string Platform = "discord";
    private const string UserA = "discord-user-a";

    private readonly SqliteTestDatabase _db;
    private readonly IDbContextFactory<NocturneDbContext> _factory;
    private readonly ChatIdentityPendingLinkService _service;

    public ChatIdentityPendingLinkServiceTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        _factory = _db.ContextFactory;
        _service = new ChatIdentityPendingLinkService(
            _factory.AsNocturneFactory(),
            TimeProvider.System, Mock.Of<ILogger<ChatIdentityPendingLinkService>>());
    }

    public void Dispose() => _db.Dispose();

    // ---- CreateAsync ----

    [Fact]
    public async Task CreateAsync_stores_the_chat_account_name_without_control_or_bidi_characters()
    {
        var token = await _service.CreateAsync(
            Platform, UserA, "ali‮ce​\u0007", null, "connect-slash", default);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.ChatIdentityPendingLinks.SingleAsync(p => p.Token == token);
        row.PlatformUserName.Should().Be("alice");
    }

    [Theory]
    [InlineData("‮​")]
    [InlineData("   ")]
    public void A_name_with_nothing_printable_left_is_stored_as_none(string name)
    {
        ChatIdentityPendingLinkService.SanitizePlatformUserName(name).Should().BeNull();
    }

    [Fact]
    public void A_long_name_is_cut_on_a_whole_character()
    {
        ChatIdentityPendingLinkService.SanitizePlatformUserName(new string('a', 255) + "\U0001F600")
            .Should().Be(new string('a', 255));
        ChatIdentityPendingLinkService.SanitizePlatformUserName(new string('a', 254) + "\U0001F600")
            .Should().EndWith("\U0001F600").And.HaveLength(256);
    }

    [Fact]
    public async Task CreateAsync_returns_64_char_hex_token()
    {
        var token = await _service.CreateAsync(Platform, UserA, null, null, "connect-slash", default);
        token.Should().HaveLength(64);
        Regex.IsMatch(token, "^[0-9A-F]{64}$").Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_persists_row_with_correct_fields()
    {
        var before = DateTime.UtcNow;
        var token = await _service.CreateAsync(Platform, UserA, null, "lily-tenant", "connect-slash", default);
        var after = DateTime.UtcNow;

        using var db = _factory.CreateDbContext();
        var row = await db.ChatIdentityPendingLinks.FirstOrDefaultAsync(p => p.Token == token);
        row.Should().NotBeNull();
        row!.Platform.Should().Be(Platform);
        row.PlatformUserId.Should().Be(UserA);
        row.TenantSlug.Should().Be("lily-tenant");
        row.Source.Should().Be("connect-slash");
        row.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        (row.ExpiresAt - row.CreatedAt).Should().Be(ChatIdentityPendingLinkService.TokenLifetime);
    }

    [Fact]
    public async Task CreateAsync_allows_null_tenant_slug()
    {
        var token = await _service.CreateAsync(Platform, UserA, null, null, "oauth2-finalize", default);
        using var db = _factory.CreateDbContext();
        var row = await db.ChatIdentityPendingLinks.FirstAsync(p => p.Token == token);
        row.TenantSlug.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_generates_distinct_tokens()
    {
        var a = await _service.CreateAsync(Platform, UserA, null, null, "connect-slash", default);
        var b = await _service.CreateAsync(Platform, UserA, null, null, "connect-slash", default);
        a.Should().NotBe(b);
    }

    // ---- TryConsumeAsync ----

    [Fact]
    public async Task TryConsumeAsync_returns_null_when_token_missing()
    {
        var result = await _service.TryConsumeAsync("DEADBEEF", default);
        result.Should().BeNull();
    }

    [Fact]
    public async Task TryConsumeAsync_returns_row_and_deletes_it()
    {
        var token = await _service.CreateAsync(Platform, UserA, null, "lily", "connect-slash", default);
        var result = await _service.TryConsumeAsync(token, default);

        result.Should().NotBeNull();
        result!.Token.Should().Be(token);
        result.Platform.Should().Be(Platform);
        result.PlatformUserId.Should().Be(UserA);
        result.TenantSlug.Should().Be("lily");

        using var db = _factory.CreateDbContext();
        (await db.ChatIdentityPendingLinks.FirstOrDefaultAsync(p => p.Token == token)).Should().BeNull();
    }

    [Fact]
    public async Task TryConsumeAsync_returns_null_when_called_twice()
    {
        var token = await _service.CreateAsync(Platform, UserA, null, null, "connect-slash", default);
        (await _service.TryConsumeAsync(token, default)).Should().NotBeNull();
        (await _service.TryConsumeAsync(token, default)).Should().BeNull();
    }

    [Fact]
    public async Task TryConsumeAsync_returns_null_when_expired_and_leaves_row()
    {
        const string token = "EXPIREDTOKEN";
        using (var db = _factory.CreateDbContext())
        {
            db.ChatIdentityPendingLinks.Add(new ChatIdentityPendingLinkEntity
            {
                Token = token,
                Platform = Platform,
                PlatformUserId = UserA,
                TenantSlug = null,
                Source = "connect-slash",
                CreatedAt = DateTime.UtcNow.AddMinutes(-20),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-10),
            });
            await db.SaveChangesAsync();
        }

        var result = await _service.TryConsumeAsync(token, default);
        result.Should().BeNull();

        using var db2 = _factory.CreateDbContext();
        (await db2.ChatIdentityPendingLinks.FirstOrDefaultAsync(p => p.Token == token)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_token_is_consumable_until_the_lifetime_after_the_clock_has_passed()
    {
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var service = new ChatIdentityPendingLinkService(
            _factory.AsNocturneFactory(), clock, Mock.Of<ILogger<ChatIdentityPendingLinkService>>());
        var inTime = await service.CreateAsync(Platform, UserA, null, null, "connect-slash", default);
        var tooLate = await service.CreateAsync(Platform, UserA, null, null, "connect-slash", default);

        using (var db = _factory.CreateDbContext())
        {
            var row = await db.ChatIdentityPendingLinks.SingleAsync(p => p.Token == inTime);
            row.CreatedAt.Should().Be(start.UtcDateTime);
            row.ExpiresAt.Should().Be(start.UtcDateTime + ChatIdentityPendingLinkService.TokenLifetime);
        }

        clock.Advance(ChatIdentityPendingLinkService.TokenLifetime - TimeSpan.FromSeconds(1));
        (await service.TryConsumeAsync(inTime, default)).Should().NotBeNull();

        clock.Advance(TimeSpan.FromSeconds(2));
        (await service.TryConsumeAsync(tooLate, default)).Should().BeNull();
    }

    // ---- CleanupExpiredAsync ----

    [Fact]
    public async Task CleanupExpiredAsync_removes_only_expired_rows()
    {
        var freshToken = await _service.CreateAsync(Platform, UserA, null, null, "connect-slash", default);

        using (var db = _factory.CreateDbContext())
        {
            db.ChatIdentityPendingLinks.Add(new ChatIdentityPendingLinkEntity
            {
                Token = "EXPIRED1",
                Platform = Platform,
                PlatformUserId = UserA,
                Source = "connect-slash",
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-20),
            });
            db.ChatIdentityPendingLinks.Add(new ChatIdentityPendingLinkEntity
            {
                Token = "EXPIRED2",
                Platform = Platform,
                PlatformUserId = UserA,
                Source = "oauth2-finalize",
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-15),
            });
            await db.SaveChangesAsync();
        }

        var deleted = await _service.CleanupExpiredAsync(default);
        deleted.Should().Be(2);

        using var db2 = _factory.CreateDbContext();
        var remaining = await db2.ChatIdentityPendingLinks.ToListAsync();
        remaining.Should().HaveCount(1);
        remaining[0].Token.Should().Be(freshToken);
    }

    [Fact]
    public async Task CleanupExpiredAsync_returns_zero_when_nothing_expired()
    {
        await _service.CreateAsync(Platform, UserA, null, null, "connect-slash", default);
        var deleted = await _service.CleanupExpiredAsync(default);
        deleted.Should().Be(0);
    }

    [Fact]
    public async Task TryConsumeAsync_returns_the_row_when_the_delete_landed_but_its_commit_reported_failure()
    {
        var token = await _service.CreateAsync(Platform, UserA, null, null, "connect-slash", default);
        var fault = new FirstTransactionFault(TransactionFault.AfterCommit);
        var retrying = new ChatIdentityPendingLinkService(
            (new RetryingFactory(TransactionFaultOptions.RetryingSqlite(_db.Connection, fault))).AsNocturneFactory(),
            TimeProvider.System, Mock.Of<ILogger<ChatIdentityPendingLinkService>>());

        var consumed = await retrying.TryConsumeAsync(token, default);

        fault.Fired.Should().BeTrue();
        using var db = _factory.CreateDbContext();
        (await db.ChatIdentityPendingLinks.AnyAsync(p => p.Token == token)).Should().BeFalse();
        consumed.Should().NotBeNull();
        consumed!.Token.Should().Be(token);
    }

    private sealed class RetryingFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
