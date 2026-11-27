using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Configuration;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Telemetry;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Telemetry;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.API.Tests.Services.Telemetry;

/// <summary>
/// <c>page_view_counts</c> over a migrated PostgreSQL under the runtime role: what a flush stores,
/// what the retention deletes, and that the table has no column that could name anyone.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PageViewCountsPostgresTests(PageViewCountsPostgresTests.Database database)
    : IClassFixture<PageViewCountsPostgresTests.Database>, IAsyncLifetime
{
    public sealed class Database() : MigratedDatabase("page_view_counts");

    private const string Agp = "/(authenticated)/reports/agp";
    private const string Calendar = "/(authenticated)/calendar";

    private static readonly DateOnly Today = new(2026, 11, 27);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider? _provider;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var reset = new NpgsqlCommand("TRUNCATE page_view_counts;", conn);
        await reset.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
    }

    [Fact]
    public async Task The_table_holds_a_day_a_route_id_and_a_count_and_nothing_else()
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var columns = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'page_view_counts' ORDER BY column_name",
            conn);
        var names = new List<string>();
        await using (var reader = await columns.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));

        names.Should().Equal("count", "date", "route_id");
    }

    [Fact]
    public async Task A_flush_stores_each_day_and_route_and_adds_to_what_is_stored()
    {
        var (counter, flusher, store) = Build(enabled: true);

        counter.Record(Agp);
        counter.Record(Agp);
        counter.Record(Calendar);
        await flusher.RunOnceAsync(default);
        counter.Record(Agp);
        await flusher.FlushAsync(default);

        (await store.ReadAsync(Today, Today, default)).Should().Equal(
            new PageViewCount(Today, Calendar, 1),
            new PageViewCount(Today, Agp, 3));
    }

    [Fact]
    public async Task While_page_counts_are_off_a_beacon_stores_nothing()
    {
        var (counter, flusher, store) = Build(enabled: false);

        counter.Record(Agp);
        await flusher.RunOnceAsync(default);

        (await store.ReadAsync(Today.AddDays(-400), Today, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Days_older_than_the_retention_are_deleted_even_while_page_counts_are_off()
    {
        var (_, flusher, store) = Build(enabled: false);
        var oldestKept = Today.AddDays(-(PageViewCountFlusher.RetentionDays - 1));
        await store.AddAsync(
            [
                new PageViewCount(oldestKept.AddDays(-1), Agp, 5),
                new PageViewCount(oldestKept, Agp, 7),
                new PageViewCount(Today, Agp, 1),
            ],
            default);

        await flusher.RunOnceAsync(default);

        (await store.ReadAsync(Today.AddDays(-1000), Today, default)).Should().Equal(
            new PageViewCount(oldestKept, Agp, 7),
            new PageViewCount(Today, Agp, 1));
    }

    [Fact]
    public async Task A_range_reads_both_of_its_ends()
    {
        var (_, _, store) = Build(enabled: true);
        await store.AddAsync(
            [
                new PageViewCount(Today.AddDays(-3), Agp, 1),
                new PageViewCount(Today.AddDays(-2), Agp, 2),
                new PageViewCount(Today.AddDays(-1), Agp, 3),
                new PageViewCount(Today, Agp, 4),
            ],
            default);

        (await store.ReadAsync(Today.AddDays(-2), Today.AddDays(-1), default))
            .Select(c => c.Count).Should().Equal(2, 3);
    }

    private (PageViewCounter Counter, PageViewCountFlusher Flusher, IPageViewCountStore Store) Build(bool enabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContextFactory<NocturneDbContext>(o => o.UseTenantInterceptor(database.AppConnectionString));
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddDataServices();
        services.AddTransient<IPageViewCountStore, PageViewCountRepository>();
        _provider = services.BuildServiceProvider();

        var store = _provider.GetRequiredService<IPageViewCountStore>();
        var counter = new PageViewCounter(Options.Create(new PageCountsOptions { Enabled = enabled }), _clock);
        var flusher = new PageViewCountFlusher(counter, store, _clock, NullLogger<PageViewCountFlusher>.Instance);
        return (counter, flusher, store);
    }
}
