using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// An activity batch runs the same statements against PostgreSQL whatever its size: one read of the
/// ids and one of the sync keys per table, and one save for both tables.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class ActivityBatchStatementsTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const long Start = 1_780_000_000_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABatchOfFiftyRunsWhatABatchOfFourRuns(bool resend)
    {
        var four = await StatementsAsync(4, resend);
        var fifty = await StatementsAsync(48, resend);

        fifty.Should().Equal(four);
        four.Should().NotBeEmpty();
    }

    private async Task<List<string>> StatementsAsync(int size, bool resend)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await DecomposedRows.TruncateAsync(conn, ["heart_rates", "step_counts"]);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
            cmd.Parameters.AddWithValue("tid", Fixture.TenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        var recorder = new StatementRecorder();
        await using var context = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(recorder)
            .Options) { TenantId = Fixture.TenantId };
        var decomposer = new ActivityDecomposer(
            new TestNocturneDbContextFactory(context), Mock.Of<IStateSpanRepository>(), TimeProvider.System,
            NullLogger<ActivityDecomposer>.Instance);

        if (resend)
            await decomposer.DecomposeBatchAsync(Batch(size, 60), WriteOrigin.Live);
        recorder.Clear();

        await decomposer.DecomposeBatchAsync(Batch(size, 90), WriteOrigin.Live);

        return [.. recorder.All.Select(s => s.Split(' ', 2)[0])];
    }

    /// <summary>Heart rates and step counts by id and by xDrip sync key, in equal parts.</summary>
    private static List<Activity> Batch(int size, int value) =>
    [
        .. Enumerable.Range(0, size).Select(i => (i % 4) switch
        {
            0 => Of($"5b00{i:x4}0000000000000000", "heartrate", i, "bpm", value),
            1 => Of(null, "hr-bpm", i, "bpm", value),
            2 => Of($"5b00{i:x4}0000000000000000", "steps", i, "metric", value),
            _ => Of(null, "steps-total", i, "steps", value),
        }),
    ];

    private static Activity Of(string? id, string type, int i, string key, int value) => new()
    {
        Id = id,
        Type = type,
        Mills = Start + i * 60_000L,
        AdditionalProperties = new() { [key] = JsonSerializer.SerializeToElement(value) },
    };
}
