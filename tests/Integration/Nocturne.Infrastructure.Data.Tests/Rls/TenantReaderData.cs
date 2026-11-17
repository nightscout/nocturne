using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// One tenant's rows across the four kinds of table a reader is narrowed on, and the counts a
/// reader reaches through Row Level Security alone (raw SQL, so the EF tenant filter plays no part).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>step_counts</c>: categorised (<c>stepcount.read</c>), one row now and one 30 hours old.</item>
/// <item><c>foods</c>: catalogue (<c>food.read</c>), no recency column, one row.</item>
/// <item><c>notes</c>: hidden from shares, clamped by recency, one row now and one 30 hours old.</item>
/// <item><c>state_spans</c>: hidden from shares, clamped by overlap: one span still running since
/// three days ago, one that ended two days ago, one that ended an hour ago.</item>
/// </list>
/// </remarks>
internal sealed record ReaderCounts(long Steps, long Foods, long Notes, long Spans)
{
    public static readonly ReaderCounts Owner = new(2, 1, 2, 3);
    public static readonly ReaderCounts Clamped = new(1, 1, 1, 2);
    public static readonly ReaderCounts Nothing = new(0, 0, 0, 0);
}

internal static class TenantReaderData
{
    public static ServiceProvider BuildProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgreSqlInfrastructure(connectionString, configuration: null);
        services.AddScoped<ITenantAccessor, SettableTenantAccessor>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public static async Task<Guid> SeedTenantAsync(RlsCompletenessFixture fx, IServiceProvider root)
    {
        var tenant = Guid.NewGuid();

        await using (var conn = await fx.OpenMigratorConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'reader-matrix', true, now(), now())
                """;
            AddParam(cmd, "@id", tenant);
            AddParam(cmd, "@slug", $"reader-{tenant:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        var now = DateTime.UtcNow;
        await using var db = await root.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        db.StepCounts.AddRange(
            new StepCountEntity { Id = Guid.NewGuid(), Timestamp = now },
            new StepCountEntity { Id = Guid.NewGuid(), Timestamp = now.AddHours(-30) });
        db.Foods.Add(new FoodEntity { Id = Guid.NewGuid(), Name = "toast" });
        db.Notes.AddRange(
            new NoteEntity { Id = Guid.NewGuid(), Timestamp = now, Text = "now" },
            new NoteEntity { Id = Guid.NewGuid(), Timestamp = now.AddHours(-30), Text = "old" });
        db.StateSpans.AddRange(
            new StateSpanEntity { Id = Guid.NewGuid(), Category = "Profile", State = "running", StartTimestamp = now.AddDays(-3) },
            new StateSpanEntity
            {
                Id = Guid.NewGuid(), Category = "Profile", State = "ended-2-days-ago",
                StartTimestamp = now.AddDays(-4), EndTimestamp = now.AddDays(-2),
            },
            new StateSpanEntity
            {
                Id = Guid.NewGuid(), Category = "Override", State = "ended-1-hour-ago",
                StartTimestamp = now.AddHours(-30), EndTimestamp = now.AddHours(-1),
            });
        await db.SaveChangesAsync();

        return tenant;
    }

    /// <summary>What <paramref name="context"/> reaches of <paramref name="tenant"/>'s rows.</summary>
    public static async Task<ReaderCounts> CountAsync(NocturneDbContext context, Guid tenant)
    {
        var wasOpen = context.Database.GetDbConnection().State == System.Data.ConnectionState.Open;
        if (!wasOpen)
            await context.Database.OpenConnectionAsync();
        try
        {
            var connection = context.Database.GetDbConnection();
            return new ReaderCounts(
                await CountAsync(connection, "step_counts", tenant),
                await CountAsync(connection, "foods", tenant),
                await CountAsync(connection, "notes", tenant),
                await CountAsync(connection, "state_spans", tenant));
        }
        finally
        {
            if (!wasOpen)
                await context.Database.CloseConnectionAsync();
        }
    }

    public static async Task<string?> SettingAsync(NocturneDbContext context, string name)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = context.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT current_setting(@name, true)";
            AddParam(cmd, "@name", name);
            return await cmd.ExecuteScalarAsync() as string;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    public static void Resolve(IServiceProvider scope, Guid tenant) =>
        scope.GetRequiredService<ITenantAccessor>().SetTenant(new TenantContext(tenant, "reader", "Reader", true, false));

    public static void MarkShare(IServiceProvider scope, string? csv, bool fullHistory)
    {
        var reader = scope.GetRequiredService<ICategoryReadContext>();
        reader.MarkShare();
        if (csv is not null)
            reader.SetVisibleCategories(csv);
        reader.SetFullHistory(fullHistory);
    }

    private static async Task<long> CountAsync(DbConnection connection, string table, Guid tenant)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE tenant_id = @tid";
        AddParam(cmd, "@tid", tenant);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private sealed class SettableTenantAccessor : ITenantAccessor
    {
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public TenantContext? Context { get; private set; }
        public void SetTenant(TenantContext context) => Context = context;
    }
}
