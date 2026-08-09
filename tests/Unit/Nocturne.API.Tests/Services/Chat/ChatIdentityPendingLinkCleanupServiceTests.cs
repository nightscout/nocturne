using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Chat;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Chat;

[Trait("Category", "Unit")]
public class ChatIdentityPendingLinkCleanupServiceTests : IDisposable
{
    private const string Platform = "discord";
    private const string UserA = "discord-user-a";

    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly ServiceProvider _serviceProvider;
    private readonly ChatIdentityPendingLinkCleanupService _sut;

    public ChatIdentityPendingLinkCleanupServiceTests()
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

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(
            new TestDbContextFactory(_options));
        services.AddScoped<ChatIdentityPendingLinkService>();
        _serviceProvider = services.BuildServiceProvider();

        _sut = new ChatIdentityPendingLinkCleanupService(
            _serviceProvider,
            NullLogger<ChatIdentityPendingLinkCleanupService>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TestDbContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Context that signals once it has been disposed, which for
    /// <see cref="ChatIdentityPendingLinkService.CleanupExpiredAsync"/> is after its delete has
    /// committed. Lets a test wait for the sweep to finish instead of polling the same SQLite
    /// connection the sweep is using from another thread.
    /// </summary>
    private sealed class SignallingDbContext(
        DbContextOptions<NocturneDbContext> options,
        Action onDisposed) : NocturneDbContext(options)
    {
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            onDisposed();
        }
    }

    private sealed class SignallingDbContextFactory(
        DbContextOptions<NocturneDbContext> options,
        Action onContextDisposed) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
            => new SignallingDbContext(options, onContextDisposed);

        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private void SeedToken(string token, TimeSpan expiresIn)
    {
        using var db = new NocturneDbContext(_options);
        db.ChatIdentityPendingLinks.Add(new ChatIdentityPendingLinkEntity
        {
            Token = token,
            Platform = Platform,
            PlatformUserId = UserA,
            Source = "connect-slash",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            ExpiresAt = DateTime.UtcNow.Add(expiresIn),
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task SweepAsync_deletes_expired_tokens_and_leaves_live_ones()
    {
        SeedToken("EXPIRED1", TimeSpan.FromMinutes(-20));
        SeedToken("EXPIRED2", TimeSpan.FromMinutes(-1));
        SeedToken("LIVE", TimeSpan.FromMinutes(5));

        var deleted = await _sut.SweepAsync(CancellationToken.None);

        deleted.Should().Be(2);

        using var db = new NocturneDbContext(_options);
        var remaining = await db.ChatIdentityPendingLinks.Select(p => p.Token).ToListAsync();
        remaining.Should().BeEquivalentTo(["LIVE"]);
    }

    [Fact]
    public async Task SweepAsync_is_a_no_op_when_nothing_has_expired()
    {
        SeedToken("LIVE", TimeSpan.FromMinutes(5));

        (await _sut.SweepAsync(CancellationToken.None)).Should().Be(0);
    }

    /// <summary>
    /// Builds a service under test whose sweep signals the returned task once its context has been
    /// disposed, so a test can wait for the loop to reach the sweep instead of polling the same
    /// SQLite connection the sweep is using from another thread. The caller disposes the provider.
    /// </summary>
    private (ServiceProvider Provider, ChatIdentityPendingLinkCleanupService Sut, Task Swept)
        CreateSweepSignallingService()
    {
        var swept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(
            new SignallingDbContextFactory(_options, () => swept.TrySetResult()));
        services.AddScoped<ChatIdentityPendingLinkService>();
        var provider = services.BuildServiceProvider();

        var sut = new ChatIdentityPendingLinkCleanupService(
            provider, NullLogger<ChatIdentityPendingLinkCleanupService>.Instance)
        {
            InitialDelay = TimeSpan.Zero,
        };

        return (provider, sut, swept.Task);
    }

    [Fact]
    public async Task ExecuteAsync_sweeps_once_before_waiting_for_the_next_tick()
    {
        SeedToken("EXPIRED1", TimeSpan.FromMinutes(-20));

        var (provider, sut, swept) = CreateSweepSignallingService();
        await using var _ = provider;

        await sut.StartAsync(CancellationToken.None);

        // Wait for the sweep's context to be disposed, then stop the loop, so the connection is
        // only ever touched by one thread at a time.
        var finished = await Task.WhenAny(swept, Task.Delay(TimeSpan.FromSeconds(10)));
        await sut.StopAsync(CancellationToken.None);

        finished.Should().Be(swept, "starting the hosted service must reach CleanupExpiredAsync");

        using var db = new NocturneDbContext(_options);
        (await db.ChatIdentityPendingLinks.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// Cancellation is an ordinary shutdown, not a fault. The wait for the first sweep is what makes
    /// this test say anything: the host schedules <c>ExecuteAsync</c> on the thread pool, so stopping
    /// straight after <c>StartAsync</c> can cancel the loop before its body ever runs, leaving a
    /// cancelled task that proves nothing about how the loop handles cancellation.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_stops_cleanly_on_cancellation()
    {
        var (provider, sut, swept) = CreateSweepSignallingService();
        await using var _ = provider;

        await sut.StartAsync(CancellationToken.None);
        (await Task.WhenAny(swept, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should().Be(swept, "the loop must be running before its cancellation means anything");

        await sut.StopAsync(CancellationToken.None);

        sut.ExecuteTask!.Status.Should().Be(
            TaskStatus.RanToCompletion,
            "a running loop absorbs the cancellation instead of letting it escape");
    }
}
