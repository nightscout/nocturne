using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The grouped reads the connections list is built from, against real Postgres: the grouping by a
/// nullable writer and both nullable origin handles is the SQL under test.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class WriterBucketsTests(RlsCompletenessFixture fx)
{
    [Fact]
    public async Task Rows_are_bucketed_by_writer_and_both_origin_handles()
    {
        var tenant = Guid.NewGuid();
        var token = Guid.NewGuid();
        var app = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var conn = await fx.OpenMigratorConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES ('{tenant}', 'writer-{tenant:N}', 'writer-buckets-test', true, now(), now())
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var provider = TenantReaderData.BuildProvider(fx.AppConnectionString);
        await using var scope = provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, tenant);
        var contexts = scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

        await using (var db = await contexts.CreateAsync())
        {
            db.SensorGlucose.AddRange(
                Glucose(token, null, "Unknown", now.AddMinutes(-5)),
                Glucose(token, null, "Unknown", now.AddHours(-30)),
                Glucose(app, "prelude:companion", "Unknown", now.AddMinutes(-2)),
                Glucose(null, "glooko-connector", "glooko-connector", now.AddDays(-2)),
                Glucose(token, null, "Unknown", now.AddDays(-45)));
            db.Boluses.Add(new BolusEntity { Id = Guid.CreateVersion7(), Timestamp = now.AddHours(-1), Insulin = 1, WriterGrantId = token });
            db.StateSpans.Add(new StateSpanEntity
            {
                Id = Guid.CreateVersion7(), Category = "TemporaryTarget", State = "Active", Source = "AAPS",
                StartTimestamp = now.AddHours(-3), WriterGrantId = token,
            });
            await db.SaveChangesAsync();
        }

        var buckets = await new SourceOwnedDataRepository(contexts, SystemAuditContext.ForService("test"))
            .WriterBucketsAsync(now.AddDays(-30), now.AddHours(-24), CancellationToken.None);

        buckets.Should().HaveCount(5);
        var tokenGlucose = buckets.Single(b => b.DataType == SourceDataTypes.Glucose && b.WriterGrantId == token);
        tokenGlucose.Should().BeEquivalentTo(
            new { DataSource = (string?)null, Device = "Unknown", Count = 2L, CountLast24Hours = 1 });
        buckets.Single(b => b.WriterGrantId == app).DataSource.Should().Be("prelude:companion");
        buckets.Single(b => b.WriterGrantId is null).Device.Should().Be("glooko-connector");
        buckets.Single(b => b.DataType == SourceDataTypes.Boluses).Should().BeEquivalentTo(
            new { WriterGrantId = (Guid?)token, DataSource = (string?)null, Device = (string?)null, Count = 1L });
        buckets.Single(b => b.DataType == SourceDataTypes.StateSpans).Should().BeEquivalentTo(
            new { WriterGrantId = (Guid?)token, DataSource = (string?)null, Device = "AAPS" });
    }

    private static SensorGlucoseEntity Glucose(Guid? writer, string? dataSource, string? device, DateTime at) =>
        new()
        {
            Id = Guid.CreateVersion7(), Timestamp = at, Mgdl = 100, DataSource = dataSource, Device = device,
            WriterGrantId = writer,
        };
}
