using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Covers which SignalR groups a credential may join. Scopes decide which data categories a
/// connection sees; <see cref="HubCredentialKind"/> decides whether it may join a group that carries
/// more than one category, or another subject's payloads.
/// </summary>
/// <remarks>
/// The tenant-wide groups are gated on <c>glucose.read</c> but carry tracker state, device action
/// intents and arbitrary <c>dataUpdate</c> payloads, and the per-subject broadcasts are published to
/// them as well. A guest link holds glucose read, so without the kind check it would receive every
/// member's in-app notifications and device notification mirrors.
/// </remarks>
[Trait("Category", "Unit")]
public class HubCredentialKindTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OtherSubject = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string ConnectionId = "conn-1";

    // ── credential classification ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The classification is asserted for every <see cref="AuthType"/>, so a new authentication type
    /// cannot reach a tenant-wide group without a deliberate decision recorded here.
    /// </summary>
    public static TheoryData<AuthType, HubCredentialKind> Classifications => new()
    {
        { AuthType.None, HubCredentialKind.Restricted },
        { AuthType.Guest, HubCredentialKind.Restricted },
        { AuthType.InstanceKey, HubCredentialKind.Infrastructure },
        { AuthType.OidcToken, HubCredentialKind.Subject },
        { AuthType.LegacyJwt, HubCredentialKind.Subject },
        { AuthType.LegacyAccessToken, HubCredentialKind.Subject },
        { AuthType.ApiKey, HubCredentialKind.Subject },
        { AuthType.SessionCookie, HubCredentialKind.Subject },
        { AuthType.OAuthAccessToken, HubCredentialKind.Subject },
        { AuthType.DirectGrant, HubCredentialKind.Subject },
        { AuthType.PlatformAccess, HubCredentialKind.Subject },
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void Every_auth_type_classifies_as_expected(AuthType authType, HubCredentialKind expected)
    {
        HubAuthorization.Classify(authType).Should().Be(expected);
    }

    [Fact]
    public void Classifications_cover_every_auth_type()
    {
        var classified = Classifications
            .Select(row => (AuthType)((object?[])row)[0]!)
            .ToHashSet();

        classified.Should().BeEquivalentTo(Enum.GetValues<AuthType>(),
            "a new AuthType must be classified deliberately, not default into a tenant-wide group");
    }

    [Fact]
    public void An_unclassified_credential_is_restricted()
    {
        // Fail closed: the zero value is Restricted, so a default-constructed or unmapped kind
        // cannot join a tenant-wide group.
        default(HubCredentialKind).Should().Be(HubCredentialKind.Restricted);
        HubAuthorization.Classify((AuthType)9999).Should().Be(HubCredentialKind.Restricted);
    }

    [Theory]
    [InlineData(HubCredentialKind.Restricted, false)]
    [InlineData(HubCredentialKind.Subject, true)]
    [InlineData(HubCredentialKind.Infrastructure, true)]
    public void CanJoinTenantRelay_follows_the_kind(HubCredentialKind kind, bool expected)
    {
        Authorization(kind).CanJoinTenantRelay.Should().Be(expected);
    }

    [Fact]
    public void A_restricted_credential_owns_no_subject_group()
    {
        // Held on the kind alone, so it does not rest on GuestSessionHandler continuing to resolve a
        // guest session with SubjectId null (the data owner it acts for is carried separately, on
        // AuthContext.ActingAsSubjectId). A subject group carries that subject's in-app
        // notifications, which no guest scope covers.
        Authorization(HubCredentialKind.Restricted).OwnSubjectId.Should().BeNull();
        Authorization(HubCredentialKind.Subject).OwnSubjectId.Should().Be(Subject);
    }

    // ── which groups DataHub.Authorize joins ──────────────────────────────────────────────────

    [Fact]
    public async Task A_guest_credential_joins_no_tenant_group()
    {
        var (hub, groups) = CreateHub(Authorization(HubCredentialKind.Restricted));

        var result = await hub.Authorize(new AuthorizeRequest { Token = "guest" });

        System.Text.Json.JsonSerializer.Serialize(result).Should().Contain("\"success\":true");
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a guest link reaches data through Subscribe's category groups, never a tenant-wide group");
    }

    [Fact]
    public async Task A_member_credential_joins_the_data_group_and_its_own_subject_group()
    {
        var (hub, groups) = CreateHub(Authorization(HubCredentialKind.Subject));

        await hub.Authorize(new AuthorizeRequest { Token = "member" });

        VerifyJoined(groups, RealtimeGroups.Authorized);
        VerifyJoined(groups, RealtimeGroups.ForSubject(Subject));
        VerifyNotJoined(groups, RealtimeGroups.Relay);
        VerifyNotJoined(groups, RealtimeGroups.ForSubject(OtherSubject));
    }

    [Fact]
    public async Task The_bridge_joins_the_relay_group()
    {
        var (hub, groups) = CreateHub(new HubAuthorization(
            Tenant,
            Scope.Normalize([Scope.FullAccess]),
            HubCredentialKind.Infrastructure,
            SubjectId: null));

        await hub.Authorize(new AuthorizeRequest { Secret = "instance-key" });

        VerifyJoined(groups, RealtimeGroups.Authorized);
        VerifyJoined(groups, RealtimeGroups.Relay);
    }

    // ── who the per-subject broadcasts reach ──────────────────────────────────────────────────

    [Fact]
    public async Task A_notification_never_goes_to_the_tenant_data_group()
    {
        var (broadcast, sends) = CreateBroadcastService();

        await broadcast.BroadcastNotificationCreatedAsync(Subject.ToString(), new InAppNotificationDto());

        sends.Should().Contain(Group(RealtimeGroups.ForSubject(Subject)));
        sends.Should().Contain(Group(RealtimeGroups.Relay));
        sends.Should().NotContain(Group(RealtimeGroups.Authorized),
            "the tenant data group is reachable by any member holding glucose read, so a "
            + "notification addressed to one subject must not be published to it");
    }

    [Fact]
    public async Task A_device_notification_mirror_goes_only_to_its_own_subject()
    {
        var (broadcast, sends) = CreateBroadcastService();

        await broadcast.BroadcastDeviceNotificationAsync(new DeviceNotificationMirror
        {
            UserId = Subject.ToString(),
            Notification = new InAppNotificationDto(),
        });

        sends.Should().Contain(Group(RealtimeGroups.ForSubject(Subject)));
        sends.Should().Contain(Group(RealtimeGroups.Relay));
        sends.Should().NotContain(Group(RealtimeGroups.Authorized));
        sends.Should().NotContain(Group(RealtimeGroups.ForSubject(OtherSubject)));
    }

    [Fact]
    public void The_subject_group_name_is_stable_across_guid_and_string_callers()
    {
        // The hub has a Guid, the payloads carry a string. SignalR compares group names byte for
        // byte, so an uppercase or braced identifier would silently deliver to nobody.
        RealtimeGroups.ForSubject(Subject)
            .Should().Be(RealtimeGroups.ForSubject(Subject.ToString().ToUpperInvariant()))
            .And.Be(RealtimeGroups.ForSubject(Subject.ToString("B")));
    }

    // ── registration ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// SignalR reads <c>HubOptions.HubFilters</c> and never resolves <see cref="IHubFilter"/> from the
    /// container, so a filter registered only in DI is silently inert — which is how the hub methods
    /// went unauthorized in the first place.
    /// </summary>
    [Fact]
    public void The_hub_filters_are_registered_in_HubOptions()
    {
        var provider = BuildRealtimeServices();

        RegisteredFilters(provider.GetRequiredService<IOptions<HubOptions>>().Value)
            .Should().BeEquivalentTo([typeof(TenantHubFilter), typeof(HubAuthorizationFilter)],
                options => options.WithStrictOrdering(),
                "the tenant accessor must be populated before anything inside resolves a "
                + "tenant-scoped service");

        provider.GetService<HubAuthorizationFilter>().Should().NotBeNull(
            "the filter type is resolved from the container when the pipeline is built");
        provider.GetService<TenantHubFilter>().Should().NotBeNull();
    }

    /// <summary>
    /// Per-hub filters replace the global list rather than adding to it, so a per-hub
    /// <c>HubFilters</c> collection would drop the authorization filter for that hub.
    /// </summary>
    [Fact]
    public void No_hub_overrides_the_global_filter_list()
    {
        var provider = BuildRealtimeServices();

        foreach (var hubOptionsType in new[]
                 {
                     typeof(HubOptions<DataHub>), typeof(HubOptions<AlarmHub>), typeof(HubOptions<AlertHub>),
                     typeof(HubOptions<ConfigHub>), typeof(HubOptions<HomeAssistantHub>),
                 })
        {
            var options = provider.GetRequiredService(
                typeof(IOptions<>).MakeGenericType(hubOptionsType));
            var value = options.GetType().GetProperty("Value")!.GetValue(options)!;

            RegisteredFilters(value).Should().BeNull(
                $"{hubOptionsType.GenericTypeArguments[0].Name} must inherit the global filters");
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildRealtimeServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRealTimeAndNotifications(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static HubAuthorization Authorization(HubCredentialKind kind) => new(
        Tenant, Scope.Normalize([Scope.GlucoseRead]), kind, Subject);

    private static string Group(string name) => TenantAwareHub.FormatTenantGroup(Tenant.ToString(), name);

    private static void VerifyJoined(Mock<IGroupManager> groups, string group) =>
        groups.Verify(
            g => g.AddToGroupAsync(ConnectionId, Group(group), It.IsAny<CancellationToken>()),
            Times.Once);

    private static void VerifyNotJoined(Mock<IGroupManager> groups, string group) =>
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), Group(group), It.IsAny<CancellationToken>()),
            Times.Never);

    /// <summary>Reads the internal <c>HubOptions.HubFilters</c> list.</summary>
    private static List<Type>? RegisteredFilters(object hubOptions)
    {
        var property = hubOptions.GetType().GetProperty("HubFilters",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);

        if (property?.GetValue(hubOptions) is not System.Collections.IEnumerable filters)
            return null;

        // AddFilter<T>() stores an internal factory holding the filter type; AddFilter(instance)
        // stores the instance itself.
        return filters.Cast<object>().Select(FilterType).ToList();
    }

    private static Type FilterType(object filter)
    {
        if (filter is Type type)
            return type;

        var held = filter.GetType()
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.GetValue(filter))
            .OfType<Type>()
            .FirstOrDefault();

        return held ?? filter.GetType();
    }

    private static (DataHub Hub, Mock<IGroupManager> Groups) CreateHub(HubAuthorization authorization)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantContext"] =
            new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false);

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new StubHttpContextFeature { HttpContext = httpContext });

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.ConnectionId).Returns(ConnectionId);
        callerContext.SetupGet(c => c.Features).Returns(features);
        callerContext.SetupGet(c => c.Items).Returns(new Dictionary<object, object?>());

        var authorizer = new Mock<IHubTokenAuthorizer>();
        authorizer
            .Setup(a => a.AuthorizeTokenAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync(authorization);
        authorizer
            .Setup(a => a.AuthorizeInstanceKey(It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(authorization);

        var groups = new Mock<IGroupManager>();
        var hub = new DataHub(Mock.Of<ILogger<DataHub>>(), authorizer.Object)
        {
            Context = callerContext.Object,
            Groups = groups.Object,
        };

        return (hub, groups);
    }

    private sealed class StubHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>A broadcast service recording the group name of every send.</summary>
    private static (ISignalRBroadcastService Service, List<string> Sends) CreateBroadcastService()
    {
        var sends = new List<string>();

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>()))
            .Returns((string group) =>
            {
                sends.Add(group);
                return Mock.Of<IClientProxy>();
            });

        var dataHub = new Mock<IHubContext<DataHub>>();
        dataHub.SetupGet(h => h.Clients).Returns(clients.Object);

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.Context)
            .Returns(new TenantContext(Tenant, "default", "Default", IsActive: true, IsDemo: false));

        var service = new SignalRBroadcastService(
            dataHub.Object,
            Mock.Of<IHubContext<AlarmHub>>(),
            Mock.Of<IHubContext<ConfigHub>>(),
            Mock.Of<IHubContext<AlertHub>>(),
            Mock.Of<IHubContext<HomeAssistantHub>>(),
            Mock.Of<IHubContext<OverviewHub>>(),
            tenantAccessor.Object,
            Options.Create(new JsonHubProtocolOptions()),
            Mock.Of<ILogger<SignalRBroadcastService>>());

        return (service, sends);
    }
}
