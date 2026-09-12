using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Nocturne.API.Tests.GoldenFiles.V2;

public class AuthorizationGoldenTests : GoldenFileTestBase
{
    public AuthorizationGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    #region GET /api/v2/authorization/permissions

    [Fact]
    public async Task GetPermissions_ReturnsPermissionsResponseShape()
    {
        var response = await Client.GetAsync("/api/v2/authorization/permissions");
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("firstSeen", "lastSeen");
    }

    #endregion

    #region GET /api/v2/authorization/subjects

    [Fact]
    public async Task GetSubjects_ReturnsSubjectsListResponseShape()
    {
        // A Nightscout "subject" is a direct grant now, so the list is empty until one exists.
        await SeedDirectGrant("Pump uploader", [Scope.GlucoseRead]);

        var response = await Client.GetAsync("/api/v2/authorization/subjects");
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("created", "modified");
    }

    /// <summary>Seeds the grant the subjects endpoint reports.</summary>
    private async Task SeedDirectGrant(string label, List<string> scopes)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        db.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        // The facade reads the device subject's grants, which is who it issues to.
        var deviceSubjectId = await db.DeviceSubjectOf(db.TenantId);

        db.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = db.TenantId,
            SubjectId = deviceSubjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = scopes,
            Label = label,
            CreatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    #endregion
}
