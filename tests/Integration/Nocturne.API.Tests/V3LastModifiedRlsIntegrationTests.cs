using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Platform;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A last-modified stamp is read under the same Row Level Security as the rows it summarises, so a
/// share or a history-clamped member learns nothing of a row it cannot read, not even when it changed.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class V3LastModifiedRlsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    [Fact]
    public async Task A_treatments_share_does_not_see_a_note_move_the_treatments_stamp()
    {
        var now = DateTime.UtcNow;
        var bolusStamp = now.AddMinutes(-30);
        var noteStamp = now.AddMinutes(-5);
        await SeedAsync(
            new BolusEntity { Id = Guid.CreateVersion7(), Timestamp = now.AddHours(-1), Insulin = 1 }, bolusStamp,
            new NoteEntity { Id = Guid.CreateVersion7(), Timestamp = now.AddHours(-1) }, noteStamp);

        var member = await ReadAsync([Scope.TreatmentsRead]);
        var share = await ReadAsync([Scope.TreatmentsRead], share: true);

        member.Treatments.Should().BeCloseTo(noteStamp, TimeSpan.FromMilliseconds(1));
        share.Treatments.Should().BeCloseTo(bolusStamp, TimeSpan.FromMilliseconds(1));
        share.Entries.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_clamped_caller_does_not_see_an_old_row_edit_move_the_stamp(bool share)
    {
        var now = DateTime.UtcNow;
        var recentStamp = now.AddMinutes(-30);
        var oldRowEditStamp = now.AddMinutes(-5);
        await SeedAsync(
            new BolusEntity { Id = Guid.CreateVersion7(), Timestamp = now.AddHours(-1), Insulin = 1 }, recentStamp,
            new BolusEntity { Id = Guid.CreateVersion7(), Timestamp = now.AddDays(-3), Insulin = 1 }, oldRowEditStamp);

        var unclamped = await ReadAsync([Scope.TreatmentsRead], share: share);
        var clamped = await ReadAsync([Scope.TreatmentsRead], share: share, clamped: true);

        unclamped.Treatments.Should().BeCloseTo(oldRowEditStamp, TimeSpan.FromMilliseconds(1));
        clamped.Treatments.Should().BeCloseTo(recentStamp, TimeSpan.FromMilliseconds(1));
    }

    private async Task SeedAsync(object first, DateTime firstStamp, object second, DateTime secondStamp)
    {
        await WithTenantScopeAsync(async services =>
        {
            await using var context = await services.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            context.AddRange(first, second);
            await context.SaveChangesAsync();
            context.Entry(first).Property("SysUpdatedAt").CurrentValue = firstStamp;
            context.Entry(second).Property("SysUpdatedAt").CurrentValue = secondStamp;
            await context.SaveChangesAsync();
            return true;
        });
    }

    private async Task<LastModifiedResponse> ReadAsync(string[] scopes, bool share = false, bool clamped = false)
    {
        return await WithTenantScopeAsync(async services =>
        {
            var readContext = services.GetRequiredService<ICategoryReadContext>();
            if (share)
            {
                readContext.MarkShare();
                readContext.SetVisibleCategories(string.Join(',', scopes));
                readContext.SetFullHistory(!clamped);
            }
            else if (clamped)
                readContext.ClampMemberHistory();

            var httpContext = new DefaultHttpContext();
            httpContext.SetAuthContext(new AuthContext { IsAuthenticated = !share });
            httpContext.SetGrantedScopes(Scope.Normalize(scopes));
            var accessor = new HttpContextAccessor { HttpContext = httpContext };

            return await ActivatorUtilities.CreateInstance<StatusService>(services, accessor).GetLastModifiedAsync();
        });
    }
}
