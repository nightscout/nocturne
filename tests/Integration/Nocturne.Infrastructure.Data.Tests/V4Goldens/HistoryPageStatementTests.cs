using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// A v3 history page reuses the statement the pool prepared only when its window bounds are
/// parameters: a bound written into the SQL makes every cursor a new statement text.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public partial class HistoryPageStatementTests(V4GoldenFixture fixture)
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex DateLiteral();

    [Fact]
    public async Task Pages_with_different_cursors_send_one_statement_each_for_the_page_and_the_millisecond_extension()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var repository = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        for (var i = 0; i < 3; i++)
        {
            await repository.CreateAsync(
                new SensorGlucose { Timestamp = T0.AddMinutes(5 * i), Mgdl = 120, DataSource = "xdrip", Device = "xdrip" },
                WriteOrigin.Backfill);
        }

        var recorder = new StatementRecorder();
        var options = TestContexts.Options(fixture.AppConnectionString, [recorder]);
        await Eventually.UntilAsync(
            "the writes to reach history",
            async () => (await PageAsync(options, tenant, 0, 100)).Count == 3,
            step: TimeSpan.FromMilliseconds(100));
        recorder.Clear();

        var cursor = 0L;
        for (var run = 0; run < 2; run++)
        {
            var page = await PageAsync(options, tenant, cursor, 1);
            page.Should().NotBeEmpty($"run {run} from cursor {cursor} must reach the millisecond-extension read");
            cursor = page.Max(e => e.SysUpdatedAt).ToMills();
        }

        var reads = recorder.All
            .Where(s => s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && s.Contains("FROM sensor_glucose", StringComparison.Ordinal)
                && s.Contains("ORDER BY", StringComparison.Ordinal))
            .ToList();

        reads.Should().HaveCount(4, "each page is one read for the page and one for its last millisecond");
        reads.Where((_, i) => i % 2 == 0).Distinct().Should().ContainSingle("the page read has the same text for both cursors");
        reads.Where((_, i) => i % 2 == 1).Distinct().Should().ContainSingle("the extension read has the same text for both cursors");
        reads.Should().OnlyContain(s => !DateLiteral().IsMatch(s), "a window bound is a parameter, not a literal");
    }

    private static async Task<List<SensorGlucoseEntity>> PageAsync(
        DbContextOptions<NocturneDbContext> options, Guid tenant, long cursor, int limit)
    {
        await using var ctx = new NocturneDbContext(options) { TenantId = tenant };
        return await HistoryPage.GetAsync(
            ctx, ctx.SensorGlucose.IncludingDeleted().AsNoTracking(), e => e.SysUpdatedAt, e => e.Id,
            cursor, limit, NullLogger.Instance, "sensor_glucose", CancellationToken.None);
    }
}
