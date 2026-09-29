using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Drives <see cref="SetPatientRelationshipRequest"/> and <see cref="SetUnitsAndTimezoneRequest"/>
/// through MVC model binding and validation, which a direct call on the controller skips; see the
/// requests' remarks for the shape MVC needs.
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

    private const string UnitsRoute = "/api/v4/tenant-settings/units-and-timezone";

    [Fact]
    public async Task SavesUnitsAndATimezone()
    {
        using var host = BuildHost();

        var response = await host.GetTestClient().PutAsJsonAsync(
            UnitsRoute, new { glucoseUnits = "mmol", timezone = "Australia/Sydney" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<UnitsAndTimezoneDto>())
            .Should().Be(new UnitsAndTimezoneDto("mmol", "Australia/Sydney"));
    }

    public static TheoryData<object> RefusedUnitsRequests => new()
    {
        new { glucoseUnits = "mmol/L", timezone = "Australia/Sydney" },
        new { glucoseUnits = "MG/DL", timezone = "Australia/Sydney" },
        new { timezone = "Australia/Sydney" },
        new { glucoseUnits = "mmol" },
        new { glucoseUnits = "mmol", timezone = new string('a', 65) },
        new { glucoseUnits = "mmol", timezone = "Middle/Earth" },
    };

    [Fact]
    public async Task StoresTheIanaIdForATimezoneGivenInAnotherForm()
    {
        var units = new Mock<IUnitsAndTimezoneService>();
        using var host = BuildHost(units: units);

        var response = await host.GetTestClient().PutAsJsonAsync(
            UnitsRoute, new { glucoseUnits = "mmol", timezone = "ETC/GMT-2" });

        response.IsSuccessStatusCode.Should().BeTrue();
        units.Verify(u => u.SetAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), "mmol", "Etc/GMT-2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BindsTheLocaleAndNightscoutQueryOnTheRead()
    {
        var units = new Mock<IUnitsAndTimezoneService>();
        units.Setup(u => u.GetAsync(It.IsAny<Guid>(), "sv-SE", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnitsAndTimezoneDto("mmol", "Europe/Stockholm"));
        using var host = BuildHost(units: units);

        var answer = await host.GetTestClient().GetFromJsonAsync<UnitsAndTimezoneDto>(
            UnitsRoute + "?locale=sv-SE&fromNightscout=true");

        answer.Should().Be(new UnitsAndTimezoneDto("mmol", "Europe/Stockholm"));
    }

    [Fact]
    public async Task RefusesAnAdministratorWhoIsNotTheOwner()
    {
        var units = new Mock<IUnitsAndTimezoneService>(MockBehavior.Strict);
        using var host = BuildHost(scopes: [Scope.TenantSettings, Scope.TherapyReadWrite], units: units);
        var client = host.GetTestClient();

        (await client.PutAsJsonAsync(UnitsRoute, new { glucoseUnits = "mmol", timezone = "Australia/Sydney" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(UnitsRoute)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RefusesTheDemoAccount()
    {
        var units = new Mock<IUnitsAndTimezoneService>(MockBehavior.Strict);
        using var host = BuildHost(demoSubject: true, units: units);

        var response = await host.GetTestClient().PutAsJsonAsync(
            UnitsRoute, new { glucoseUnits = "mmol", timezone = "Australia/Sydney" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(RefusedUnitsRequests))]
    public async Task RefusesUnitsOrATimezoneItDoesNotKnow(object body)
    {
        using var host = BuildHost();

        var response = await host.GetTestClient().PutAsJsonAsync(UnitsRoute, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static IHost BuildHost(
        string[]? scopes = null, bool demoSubject = false, Mock<IUnitsAndTimezoneService>? units = null)
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

        var subjectId = Guid.CreateVersion7();
        var database = Guid.NewGuid().ToString();
        using (var seed = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>().UseInMemoryDatabase(database).Options))
        {
            seed.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "Owner", IsDemoSubject = demoSubject });
            seed.SaveChanges();
        }
        if (units is null)
        {
            units = new Mock<IUnitsAndTimezoneService>();
            units.Setup(u => u.SetAsync(tenantId, subjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, Guid _, string glucoseUnits, string timezone, CancellationToken _) =>
                    new UnitsAndTimezoneDto(glucoseUnits, timezone));
        }

        // Forbid() needs an authentication service to answer with.
        var authentication = new Mock<IAuthenticationService>();
        authentication.Setup(a => a.ForbidAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<AuthenticationProperties?>()))
            .Callback((HttpContext context, string? _, AuthenticationProperties? _) =>
                context.Response.StatusCode = StatusCodes.Status403Forbidden)
            .Returns(Task.CompletedTask);

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
                    services.AddSingleton(units.Object);
                    services.AddSingleton(authentication.Object);
                    services.AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(database));
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
                            (IReadOnlySet<string>)new HashSet<string>(scopes ?? [Scope.FullAccess]);
                        context.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = subjectId };
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
