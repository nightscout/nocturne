using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>An entry PUT or PATCH edits the stored reading it names, as the user who made it.</summary>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class EntryEditIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private static readonly DateTime At = new(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);

    /// <remarks>
    /// A reading Nocturne stored with no legacy id is served under its uuid or the 24-hex prefix of
    /// it; an edit by either names that row, not a new legacy id.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_edit_of_a_reading_with_no_legacy_id_updates_it_in_place(bool patch, bool byPrefix)
    {
        var stored = await InScopeAsync(Guid.CreateVersion7(), async services =>
            (await services.GetRequiredService<ISensorGlucoseRepository>().BulkCreateAsync(
                [new SensorGlucose { Timestamp = At, Mgdl = 120, Device = "native" }], WriteOrigin.Live)).Single());
        var servedId = byPrefix ? MongoObjectId.FromGuid(stored.Id) : stored.Id.ToString();

        var edited = await InScopeAsync(Guid.CreateVersion7(), services =>
        {
            var entries = services.GetRequiredService<IEntryService>();
            return patch
                ? entries.PatchEntryAsync(servedId, JsonSerializer.SerializeToElement(new { sgv = 150 }))
                : entries.UpdateEntryAsync(servedId, new Entry
                {
                    Type = "sgv", Sgv = 150, Mills = new DateTimeOffset(At).ToUnixTimeMilliseconds(), Device = "native",
                });
        });

        edited.Should().NotBeNull();
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var rows = await db.Set<SensorGlucoseEntity>().ToListAsync();
        rows.Should().ContainSingle("the edit writes onto the stored reading rather than beside it")
            .Which.Should().Match<SensorGlucoseEntity>(row => row.Id == stored.Id && row.Mgdl == 150);
    }

    [Fact]
    public async Task An_entry_put_is_audited_as_the_user_who_made_it()
    {
        await InScopeAsync(Guid.CreateVersion7(), services => services.GetRequiredService<IEntryService>().CreateEntriesAsync(
            [new Entry { Id = "5e0000aa0000000000000001", Type = "sgv", Sgv = 120, Mills = new DateTimeOffset(At).ToUnixTimeMilliseconds() }]));

        var editor = Guid.CreateVersion7();
        await InScopeAsync(editor, services => services.GetRequiredService<IEntryService>().UpdateEntryAsync(
            "5e0000aa0000000000000001",
            new Entry { Type = "sgv", Sgv = 135, Mills = new DateTimeOffset(At).ToUnixTimeMilliseconds() }));

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var row = await db.Set<SensorGlucoseEntity>().SingleAsync(s => s.LegacyId == "5e0000aa0000000000000001");
        (await db.Set<MutationAuditLogEntity>().IgnoreQueryFilters()
                .Where(a => a.EntityId == row.Id && a.Action == "update").Select(a => a.ActorSubjectId).ToListAsync())
            .Should().Equal([editor], "a user's edit is their trail, unlike an uploader's readings");
    }

    private async Task<T> InScopeAsync<T>(Guid subject, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        var audit = (AuditContext)scope.ServiceProvider.GetRequiredService<IAuditContext>();
        audit.SubjectId = subject;
        audit.SubjectName = "editor";
        audit.AuthType = "Session";
        return await action(scope.ServiceProvider);
    }
}
