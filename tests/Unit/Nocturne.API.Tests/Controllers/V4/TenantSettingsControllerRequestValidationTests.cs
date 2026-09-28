using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Drives <see cref="SetPatientRelationshipRequest"/> through MVC model binding and validation,
/// which a direct call on the controller skips; see the request's remarks for the shape MVC needs.
/// </summary>
public class TenantSettingsControllerRequestValidationTests
{
    private const string Route = "/api/v4/tenant-settings/patient-relationship";

    [Fact]
    public async Task AcceptsACaregiverNamingThePatient()
    {
        using var host = BuildHost();

        var response = await host.GetTestClient().PutAsJsonAsync(
            Route, new { relationship = "Caregiver", patientName = "Sam" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PatientRelationshipDto>())
            .Should().Be(new PatientRelationshipDto(PatientRelationship.Caregiver, "Sam"));
    }

    [Fact]
    public async Task RejectsAnUndefinedRelationship()
    {
        using var host = BuildHost();

        var response = await host.GetTestClient().PutAsJsonAsync(
            Route, new { relationship = 7, patientName = "Sam" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RejectsANameLongerThan256Characters()
    {
        using var host = BuildHost();

        var response = await host.GetTestClient().PutAsJsonAsync(
            Route, new { relationship = "Caregiver", patientName = new string('a', 300) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static IHost BuildHost()
    {
        var tenantId = Guid.CreateVersion7();
        var answer = (PatientRelationship?)null;
        var record = new PatientRecord();

        var tenants = new Mock<ITenantService>();
        tenants.Setup(t => t.SetPatientRelationshipAsync(tenantId, It.IsAny<PatientRelationship>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, PatientRelationship, CancellationToken>((_, value, _) => answer = value)
            .Returns(Task.CompletedTask);
        tenants.Setup(t => t.GetPatientRelationshipAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => answer);

        var records = new Mock<IPatientRecordRepository>();
        records.Setup(r => r.GetOrCreateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(record);
        records.Setup(r => r.UpdateAsync(record, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        records.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => record);

        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(tenantId);

        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton(tenants.Object);
                    services.AddSingleton(records.Object);
                    services.AddSingleton(accessor.Object);
                    services.Configure<RouteOptions>(o => o.SuppressCheckForUnhandledSecurityMetadata = true);
                    services.AddControllers().ConfigureApplicationPartManager(manager =>
                    {
                        manager.ApplicationParts.Clear();
                        manager.ApplicationParts.Add(new AssemblyPart(typeof(TenantSettingsController).Assembly));
                        manager.FeatureProviders.Add(new OnlyTenantSettingsController());
                    });
                });
                web.Configure(app =>
                {
                    app.Use((context, next) =>
                    {
                        context.Items["GrantedScopes"] =
                            (IReadOnlySet<string>)new HashSet<string> { Scope.FullAccess };
                        return next(context);
                    });
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();
    }

    private sealed class OnlyTenantSettingsController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(TenantSettingsController).GetTypeInfo());
        }
    }
}
