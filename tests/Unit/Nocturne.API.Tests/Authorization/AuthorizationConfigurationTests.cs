using System;
using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the default-deny authorization wiring: the fallback policy must stay in place, the
/// tenant-admin controllers must keep their explicit gate (the fallback is too weak for them),
/// the genuinely-public controllers must stay anonymous, and the dev-only controllers must be
/// dropped outside development.
/// </summary>
public class AuthorizationConfigurationTests
{
    private static System.Reflection.Assembly ApiAssembly => typeof(AuthorizationConfiguration).Assembly;

    [Fact]
    public void AddNocturneAuthorization_SetsFallbackPolicy_RequiringPermissionTrie()
    {
        var services = new ServiceCollection();
        services.AddNocturneAuthorization();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        options.FallbackPolicy.Should().NotBeNull(
            "every endpoint without an explicit authorization attribute must be gated by default");
        options.FallbackPolicy!.Requirements.Should().ContainSingle()
            .Which.Should().BeOfType<HasPermissionsRequirement>();

        var hasPermissions = options.GetPolicy(PolicyNames.HasPermissions);
        hasPermissions.Should().NotBeNull();
        hasPermissions!.Requirements.Should().ContainItemsAssignableTo<HasPermissionsRequirement>();
    }

    [Theory]
    [InlineData("Nocturne.API.Controllers.V4.TenantAdmin.MigrationController")]
    [InlineData("Nocturne.API.Controllers.V4.TenantAdmin.ProcessingController")]
    [InlineData("Nocturne.API.Controllers.V4.TenantAdmin.NightscoutTransitionController")]
    [InlineData("Nocturne.API.Controllers.V4.TenantAdmin.DeduplicationController")]
    public void TenantAdminController_RequiresAdmin(string typeName)
    {
        var type = ApiAssembly.GetType(typeName);
        type.Should().NotBeNull($"{typeName} should exist");

        type!.GetCustomAttributes(inherit: true).OfType<RequireAdminAttribute>()
            .Should().NotBeEmpty(
                $"{typeName} exposes tenant-admin operations that the HasPermissions fallback cannot gate");
    }

    [Theory]
    [InlineData("Nocturne.API.Controllers.V4.Platform.StatusController")]
    [InlineData("Nocturne.API.Controllers.V1.StatusController")]
    [InlineData("Nocturne.API.Controllers.V3.StatusController")]
    [InlineData("Nocturne.API.Controllers.MetadataController")]
    [InlineData("Nocturne.API.Controllers.V1.VersionsController")]
    [InlineData("Nocturne.API.Controllers.V3.VersionController")]
    [InlineData("Nocturne.API.Controllers.V3.LastModifiedController")]
    [InlineData("Nocturne.API.Controllers.V1.AuthenticationController")]
    public void PublicController_AllowsAnonymous(string typeName)
    {
        var type = ApiAssembly.GetType(typeName);
        type.Should().NotBeNull($"{typeName} should exist");

        type!.GetCustomAttributes(inherit: true).OfType<AllowAnonymousAttribute>()
            .Should().NotBeEmpty(
                $"{typeName} must stay reachable for unauthenticated callers under the fallback policy");
    }

    [Fact]
    public void DiscrepancyIngest_IsAnonymous_WhileReadsRequireAdmin()
    {
        var type = ApiAssembly.GetType("Nocturne.API.Controllers.V4.TenantAdmin.DiscrepancyController");
        type.Should().NotBeNull();

        // Inter-instance ingestion validates a shared bearer key itself, so it opts out of the
        // user-oriented policies.
        var ingest = type!.GetMethod("IngestDiscrepancy");
        ingest.Should().NotBeNull();
        ingest!.GetCustomAttributes(inherit: true).OfType<AllowAnonymousAttribute>()
            .Should().NotBeEmpty();

        // The dashboard reads remain admin-only.
        var metrics = type.GetMethod("GetCompatibilityMetrics");
        metrics.Should().NotBeNull();
        metrics!.GetCustomAttributes(inherit: true).OfType<RequireAdminAttribute>()
            .Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("Nocturne.API.Hubs.DataHub")]
    [InlineData("Nocturne.API.Hubs.AlarmHub")]
    [InlineData("Nocturne.API.Hubs.AlertHub")]
    [InlineData("Nocturne.API.Hubs.HomeAssistantHub")]
    public void RealtimeHub_AllowsAnonymous(string typeName)
    {
        var type = ApiAssembly.GetType(typeName);
        type.Should().NotBeNull($"{typeName} should exist");

        // These hubs authenticate in-band via the internal realtime bridge; the HTTP fallback
        // policy must not gate their connection handshake or the bridge cannot connect.
        type!.GetCustomAttributes(inherit: true).OfType<AllowAnonymousAttribute>()
            .Should().NotBeEmpty(
                $"{typeName} authenticates in-band and must stay exempt from the fallback policy");
    }

    [Theory]
    [InlineData(false, false)] // outside development: dev-only controllers are excluded
    [InlineData(true, true)]   // in development: dev-only controllers remain registered
    public void ConfigureControllerDiscovery_TogglesDevOnlyControllers(bool isDevelopment, bool expectedPresent)
    {
        var manager = new ApplicationPartManager();
        manager.ApplicationParts.Add(new AssemblyPart(ApiAssembly));
        // Mimic the default provider that AddControllers registers.
        manager.FeatureProviders.Add(new ControllerFeatureProvider());

        AuthorizationConfiguration.ConfigureControllerDiscovery(manager, isDevelopment);

        var feature = new ControllerFeature();
        manager.PopulateFeature(feature);

        var devOnlyPresent = feature.Controllers.Any(
            c => c.Namespace?.Contains(".DevOnly", StringComparison.Ordinal) == true);
        devOnlyPresent.Should().Be(expectedPresent);
    }
}
