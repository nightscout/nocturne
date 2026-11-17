using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Queries;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Nocturne.Core.Contracts.Entries;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Covers the 24-hour history clamp on <see cref="DataHub.LoadRetro"/>: whatever route the
/// connection's credential arrived by, the read runs on services that carry the credential's clamp
/// to Row-Level Security, and an unclamped credential's read does not.
/// </summary>
[Trait("Category", "Unit")]
public class DataHubHistoryClampTests
{
    private static readonly Guid Tenant = DataHubTestHarness.Tenant;
    private static readonly Guid Subject = DataHubTestHarness.Subject;
    private static readonly long TwoDaysAgoMills =
        DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();

    /// <summary>What the read context and a context of the handshake scope said at the moment entries were read.</summary>
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
        var deviceStatusService = new Mock<IDeviceStatusService>();
        deviceStatusService
            .Setup(s => s.GetDeviceStatusesAsync(
                It.IsAny<FindQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<ReadSortField>()))
            .ReturnsAsync([]);
        var authorizer = new Mock<IHubTokenAuthorizer>();

        var (hub, httpContext) = DataHubTestHarness.CreateHub(
            entryService.Object, new Mock<ITreatmentService>().Object, deviceStatusService.Object, authorizer.Object);
        var requestServices = httpContext.RequestServices;

        // A context the handshake request created before any credential was known.
        var db = requestServices.GetRequiredService<INocturneDbContextFactory>().CreateAsync().AsTask().GetAwaiter().GetResult();
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
    public async Task A_clamped_credential_subscribes_to_no_category()
    {
        var (hub, _, authorizer, _) = CreateHub();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync("tok", Tenant, Scope.GlucoseRead))
            .ReturnsAsync(InBand(historyClamped: true));
        await hub.Authorize(new AuthorizeRequest { Token = "tok" });
        var groups = Mock.Get(hub.Groups);
        groups.Invocations.Clear();

        var result = await hub.Subscribe(new StorageSubscribeRequest());

        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        System.Text.Json.JsonSerializer.Serialize(result).Should().Contain("\"collections\":[]");
    }

    [Fact]
    public async Task An_unclamped_credential_subscribes_to_the_categories_it_holds()
    {
        var (hub, _, authorizer, _) = CreateHub();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync("tok", Tenant, Scope.GlucoseRead))
            .ReturnsAsync(InBand(historyClamped: false));
        await hub.Authorize(new AuthorizeRequest { Token = "tok" });

        var result = await hub.Subscribe(new StorageSubscribeRequest());

        System.Text.Json.JsonSerializer.Serialize(result)
            .Should().Contain("\"collections\":[\"entries\",\"glucose\"]");
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
        httpContext.RequestServices.GetRequiredService<TenantReadContext>()
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
