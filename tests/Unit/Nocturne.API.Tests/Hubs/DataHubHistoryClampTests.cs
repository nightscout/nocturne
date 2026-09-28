using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Covers the 24-hour history clamp on <see cref="DataHub.LoadRetro"/>: whatever route the
/// connection's credential arrived by, the read runs on services that carry the credential's clamp
/// to Row-Level Security, and an unclamped credential's read does not.
/// </summary>
[Trait("Category", "Unit")]
public class DataHubHistoryClampTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly long TwoDaysAgoMills =
        DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>What the read context and the scoped DbContext said at the moment entries were read.</summary>
    private sealed class ReadObservation
    {
        public bool? CategoryReadContextClamped { get; set; }
        public bool? DbContextClamped { get; set; }
    }

    private static (DataHub hub, HttpContext httpContext, Mock<IHubTokenAuthorizer> authorizer, ReadObservation seen)
        CreateHub()
    {
        var seen = new ReadObservation();
        var entryService = new Mock<IEntryService>();
        var treatmentService = new Mock<ITreatmentService>();

        var services = new ServiceCollection();
        services.AddScoped<ICategoryReadContext, CategoryReadContext>();
        services.AddDbContext<NocturneDbContext>(options =>
            options.UseInMemoryDatabase($"DataHubHistoryClamp_{Guid.NewGuid()}"));
        services.AddSingleton(entryService.Object);
        services.AddSingleton(treatmentService.Object);
        services.AddSingleton(new DeviceStatusProjectionService(
            Mock.Of<IApsSnapshotRepository>(),
            Mock.Of<IPumpSnapshotRepository>(),
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IStateSpanRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(),
            NullLogger<DeviceStatusProjectionService>.Instance));
        var requestServices = services.BuildServiceProvider().CreateScope().ServiceProvider;

        // The handshake request pinned its scoped DbContext before any credential was known.
        var db = requestServices.GetRequiredService<NocturneDbContext>();
        var readContext = requestServices.GetRequiredService<ICategoryReadContext>();

        entryService
            .Setup(s => s.GetEntriesAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                seen.CategoryReadContextClamped = readContext.IsHistoryClamped;
                seen.DbContextClamped = db.HistoryClamped;
                return [];
            });

        var httpContext = new DefaultHttpContext { RequestServices = requestServices };
        httpContext.Items["TenantContext"] =
            new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false);

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.ConnectionId).Returns("conn-1");
        callerContext.SetupGet(c => c.Features).Returns(features);
        callerContext.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());

        var clients = new Mock<IHubCallerClients>();
        clients.SetupGet(c => c.Caller).Returns(Mock.Of<ISingleClientProxy>());

        var authorizer = new Mock<IHubTokenAuthorizer>();
        var hub = new DataHub(NullLogger<DataHub>.Instance, authorizer.Object)
        {
            Context = callerContext.Object,
            Groups = Mock.Of<IGroupManager>(),
            Clients = clients.Object,
        };

        return (hub, httpContext, authorizer, seen);
    }

    private static HubAuthorization InBand(bool historyClamped) => new(
        Tenant, Scope.Normalize([Scope.GlucoseRead]), HubCredentialKind.Subject, Subject, historyClamped);

    private static void AuthenticateUpgrade(HttpContext httpContext, bool limitTo24Hours)
    {
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = Subject,
            TenantId = Tenant,
            LimitTo24Hours = limitTo24Hours,
        };
        httpContext.SetGrantedScopes(Scope.Normalize([Scope.GlucoseRead]));
    }

    [Fact]
    public async Task Retro_load_after_in_band_authorization_with_a_clamped_credential_is_clamped()
    {
        var (hub, _, authorizer, seen) = CreateHub();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync("tok", Tenant, Scope.GlucoseRead))
            .ReturnsAsync(InBand(historyClamped: true));

        await hub.Authorize(new AuthorizeRequest { Token = "tok" });
        await hub.LoadRetro(new RetroLoadRequest { LoadedMills = TwoDaysAgoMills });

        seen.CategoryReadContextClamped.Should().BeTrue();
        seen.DbContextClamped.Should().BeTrue();
    }

    [Fact]
    public async Task Retro_load_after_in_band_authorization_with_an_unclamped_credential_reads_full_history()
    {
        var (hub, _, authorizer, seen) = CreateHub();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync("tok", Tenant, Scope.GlucoseRead))
            .ReturnsAsync(InBand(historyClamped: false));

        await hub.Authorize(new AuthorizeRequest { Token = "tok" });
        await hub.LoadRetro(new RetroLoadRequest { LoadedMills = TwoDaysAgoMills });

        seen.CategoryReadContextClamped.Should().BeFalse();
        seen.DbContextClamped.Should().BeFalse();
    }

    [Fact]
    public async Task Retro_load_on_a_connection_whose_upgrade_carried_a_clamped_credential_is_clamped()
    {
        var (hub, httpContext, _, seen) = CreateHub();
        AuthenticateUpgrade(httpContext, limitTo24Hours: true);

        await hub.Authorize(new AuthorizeRequest());
        await hub.LoadRetro(new RetroLoadRequest { LoadedMills = TwoDaysAgoMills });

        seen.CategoryReadContextClamped.Should().BeTrue();
        seen.DbContextClamped.Should().BeTrue();
    }

    [Fact]
    public async Task Retro_load_on_a_connection_whose_upgrade_carried_an_unclamped_credential_reads_full_history()
    {
        var (hub, httpContext, _, seen) = CreateHub();
        AuthenticateUpgrade(httpContext, limitTo24Hours: false);

        await hub.Authorize(new AuthorizeRequest());
        await hub.LoadRetro(new RetroLoadRequest { LoadedMills = TwoDaysAgoMills });

        seen.CategoryReadContextClamped.Should().BeFalse();
        seen.DbContextClamped.Should().BeFalse();
    }

    [Fact]
    public async Task A_later_in_band_authorization_does_not_replace_a_clamped_grant()
    {
        var (hub, _, authorizer, seen) = CreateHub();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync("clamped", Tenant, Scope.GlucoseRead))
            .ReturnsAsync(InBand(historyClamped: true));
        authorizer
            .Setup(a => a.AuthorizeTokenAsync("unclamped", Tenant, Scope.GlucoseRead))
            .ReturnsAsync(InBand(historyClamped: false));

        await hub.Authorize(new AuthorizeRequest { Token = "clamped" });
        await hub.Authorize(new AuthorizeRequest { Token = "unclamped" });
        await hub.LoadRetro(new RetroLoadRequest { LoadedMills = TwoDaysAgoMills });

        seen.CategoryReadContextClamped.Should().BeTrue();
        seen.DbContextClamped.Should().BeTrue();
    }

    [Fact]
    public void Granting_an_unclamped_credential_does_not_lift_a_clamp_already_on_the_connection()
    {
        var (hub, httpContext, _, _) = CreateHub();

        HubAuthorizationState.Grant(hub.Context, InBand(historyClamped: true));
        HubAuthorizationState.Grant(hub.Context, InBand(historyClamped: false));

        httpContext.RequestServices.GetRequiredService<ICategoryReadContext>()
            .IsHistoryClamped.Should().BeTrue();
        httpContext.RequestServices.GetRequiredService<NocturneDbContext>()
            .HistoryClamped.Should().BeTrue();
    }

    [Fact]
    public void A_clamp_that_fails_leaves_the_connection_unauthorized()
    {
        var (hub, httpContext, _, _) = CreateHub();
        var failing = new Mock<IServiceProvider>();
        failing
            .Setup(s => s.GetService(It.IsAny<Type>()))
            .Throws(new ObjectDisposedException("handshake scope"));
        httpContext.RequestServices = failing.Object;

        var grant = () => HubAuthorizationState.Grant(hub.Context, InBand(historyClamped: true));

        grant.Should().Throw<ObjectDisposedException>();
        hub.Context.Items.Should().BeEmpty();
    }
}
