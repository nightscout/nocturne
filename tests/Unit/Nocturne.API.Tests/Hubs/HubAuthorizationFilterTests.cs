using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Hubs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Covers <see cref="HubAuthorizationFilter"/>, the gate that makes hub methods fail closed. The hubs
/// accept an anonymous connection (in-band authentication needs the connection first), so every hub
/// method other than an explicit authentication entry point has to be denied until the connection has
/// proven a credential, and has to satisfy the scope it declares.
/// </summary>
[Trait("Category", "Unit")]
public class HubAuthorizationFilterTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>
    /// Builds an invocation context for <paramref name="hubType"/>.<paramref name="methodName"/> on a
    /// connection carrying <paramref name="authorization"/> (null = anonymous).
    /// </summary>
    private static HubInvocationContext CreateInvocation(
        Type hubType, string methodName, HubAuthorization? authorization)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantContext"] =
            new TenantContext(Tenant, "default", "Default", IsActive: true);

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature { HttpContext = httpContext });

        var items = new Dictionary<object, object?>();

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.ConnectionId).Returns("conn-1");
        callerContext.SetupGet(c => c.Features).Returns(features);
        callerContext.SetupGet(c => c.Items).Returns(items);

        if (authorization is not null)
        {
            HubAuthorizationState.Grant(callerContext.Object, authorization);
        }

        var method = hubType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)!;

        // The filter decides on the MethodInfo and the connection only; the hub instance is never
        // touched, so a stub stands in for hubs whose constructors take services.
        return new HubInvocationContext(
            callerContext.Object,
            Mock.Of<IServiceProvider>(),
            hub: new StubHub(),
            hubMethod: method,
            hubMethodArguments: []);
    }

    private sealed class StubHub : Hub;

    /// <summary>A member credential on the connection's own tenant carrying <paramref name="scopes"/>.</summary>
    private static HubAuthorization Member(params string[] scopes) => new(
        Tenant, OAuthScopes.Normalize(scopes), HubCredentialKind.Subject, Guid.NewGuid());

    /// <summary>
    /// A share-style credential — a guest link — carrying <paramref name="scopes"/>.
    /// </summary>
    /// <remarks>
    /// A subject id is passed deliberately even though <c>GuestSessionHandler</c> resolves a guest
    /// session with none (the data owner it acts for goes on <c>AuthContext.ActingAsSubjectId</c>):
    /// the refusal must hold on the kind alone, not on the subject id happening to be absent.
    /// </remarks>
    private static HubAuthorization Guest(params string[] scopes) => new(
        Tenant, OAuthScopes.Normalize(scopes), HubCredentialKind.Restricted, Guid.NewGuid());

    private static async Task<bool> InvokeAsync(HubInvocationContext invocation)
    {
        var filter = new HubAuthorizationFilter();
        var reached = false;
        await filter.InvokeMethodAsync(invocation, _ =>
        {
            reached = true;
            return ValueTask.FromResult<object?>(null);
        });
        return reached;
    }

    private static Func<Task> Attempt(
        Type hubType, string methodName, HubAuthorization? authorization) =>
        () => InvokeAsync(CreateInvocation(hubType, methodName, authorization));

    public static TheoryData<Type, string> GatedMethods => new()
    {
        { typeof(DataHub), nameof(DataHub.LoadRetro) },
        { typeof(DataHub), nameof(DataHub.Subscribe) },
        { typeof(AlertHub), nameof(AlertHub.Subscribe) },
        { typeof(AlertHub), nameof(AlertHub.Acknowledge) },
        { typeof(AlarmHub), nameof(AlarmHub.Ack) },
        { typeof(HomeAssistantHub), nameof(HomeAssistantHub.Subscribe) },
        { typeof(HomeAssistantHub), nameof(HomeAssistantHub.Acknowledge) },
        { typeof(ConfigHub), nameof(ConfigHub.Subscribe) },
        { typeof(ConfigHub), nameof(ConfigHub.Unsubscribe) },
        { typeof(ConfigHub), nameof(ConfigHub.SubscribeAll) },
        { typeof(ConfigHub), nameof(ConfigHub.UnsubscribeAll) },
    };

    [Fact]
    public async Task Config_changes_are_not_readable_by_a_read_only_credential()
    {
        // ConfigurationChangeEvent names the member who made each change, and connector
        // configuration is tenant administration with no narrower scope in the vocabulary.
        var readOnly = Member(OAuthScopes.GlucoseRead, OAuthScopes.AlertsRead);

        var attempt = Attempt(typeof(ConfigHub), nameof(ConfigHub.SubscribeAll), readOnly);

        await attempt.Should().ThrowAsync<HubException>()
            .Where(e => e.Message.Contains(OAuthScopes.FullAccess));
    }

    [Theory]
    [MemberData(nameof(GatedMethods))]
    public async Task Unauthorized_connection_is_denied(Type hubType, string methodName)
    {
        var attempt = Attempt(hubType, methodName, authorization: null);

        await attempt.Should().ThrowAsync<HubException>()
            .Where(e => e.Message.Contains("requires an authorized connection"));
    }

    [Theory]
    [MemberData(nameof(GatedMethods))]
    public async Task Full_access_connection_is_allowed(Type hubType, string methodName)
    {
        var authorization = Member(OAuthScopes.FullAccess);

        var reached = await InvokeAsync(CreateInvocation(hubType, methodName, authorization));

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Authorized_connection_without_the_declared_scope_is_denied()
    {
        // A read-only credential must not be able to silence alarms.
        var readOnly = Member(OAuthScopes.GlucoseRead, OAuthScopes.AlertsRead);

        var attempt = Attempt(typeof(AlarmHub), nameof(AlarmHub.Ack), readOnly);

        await attempt.Should().ThrowAsync<HubException>()
            .Where(e => e.Message.Contains(OAuthScopes.AlertsReadWrite));
    }

    [Fact]
    public async Task LoadRetro_requires_glucose_read()
    {
        var therapyOnly = Member(OAuthScopes.TherapyRead);

        var attempt = Attempt(typeof(DataHub), nameof(DataHub.LoadRetro), therapyOnly);

        await attempt.Should().ThrowAsync<HubException>()
            .Where(e => e.Message.Contains(OAuthScopes.GlucoseRead));
    }

    [Fact]
    public async Task Home_assistant_acknowledge_requires_alerts_readwrite()
    {
        var readOnly = Member(OAuthScopes.AlertsRead);

        var attempt = Attempt(
            typeof(HomeAssistantHub), nameof(HomeAssistantHub.Acknowledge), readOnly);

        await attempt.Should().ThrowAsync<HubException>()
            .Where(e => e.Message.Contains(OAuthScopes.AlertsReadWrite));
    }

    [Fact]
    public async Task Authentication_entry_points_are_reachable_anonymously()
    {
        // In-band authentication cannot work otherwise: the credential arrives in the invocation.
        foreach (var (hubType, methodName) in new (Type, string)[]
                 {
                     (typeof(DataHub), nameof(DataHub.Authorize)),
                     (typeof(AlarmHub), nameof(AlarmHub.Subscribe)),
                 })
        {
            var reached = await InvokeAsync(
                CreateInvocation(hubType, methodName, authorization: null));
            reached.Should().BeTrue($"{hubType.Name}.{methodName} is an authentication entry point");
        }
    }

    /// <summary>
    /// The methods the filter gates that join a group carrying the whole tenant's payloads. Scope alone
    /// does not constrain them: a guest link's scopes are the data owner's own read scopes, so the
    /// credential kind is the only thing separating a share of one patient's glucose from the tenant's
    /// alert stream.
    /// </summary>
    public static TheoryData<Type, string> TenantWideMethods => new()
    {
        { typeof(AlertHub), nameof(AlertHub.Subscribe) },
        { typeof(HomeAssistantHub), nameof(HomeAssistantHub.Subscribe) },
        { typeof(ConfigHub), nameof(ConfigHub.Subscribe) },
        { typeof(ConfigHub), nameof(ConfigHub.SubscribeAll) },
    };

    [Theory]
    [MemberData(nameof(TenantWideMethods))]
    public async Task A_share_scoped_credential_is_refused_a_tenant_wide_group(
        Type hubType, string methodName)
    {
        // Full access on purpose: the refusal must not depend on the guest scope set happening to
        // exclude alerts.read, because a grant update can widen a guest link's scopes.
        var attempt = Attempt(hubType, methodName, Guest(OAuthScopes.FullAccess));

        await attempt.Should().ThrowAsync<HubException>()
            .Where(e => e.Message.Contains("requires a credential belonging to the tenant"));
    }

    [Theory]
    [MemberData(nameof(TenantWideMethods))]
    public async Task A_tenant_credential_reaches_a_tenant_wide_group(Type hubType, string methodName)
    {
        // The bridge (instance key) and any subject credential holding the declared scope must still
        // subscribe.
        foreach (var authorization in new[]
                 {
                     Member(OAuthScopes.FullAccess),
                     new HubAuthorization(
                         Tenant,
                         OAuthScopes.Normalize([OAuthScopes.FullAccess]),
                         HubCredentialKind.Infrastructure,
                         SubjectId: null),
                 })
        {
            var reached = await InvokeAsync(CreateInvocation(hubType, methodName, authorization));
            reached.Should().BeTrue($"{hubType.Name}.{methodName} with {authorization.Kind}");
        }
    }

    /// <summary>
    /// Every hub method that joins a tenant-wide group declares
    /// <see cref="HubTenantGroupAttribute"/>, so the credential-kind check is not left to whoever writes
    /// the next hub method.
    /// </summary>
    /// <remarks>
    /// The joins are found by scanning the compiled bodies for a call to
    /// <see cref="IGroupManager.AddToGroupAsync"/> rather than listed here, so a method added later is
    /// covered without this test being edited. <c>DataHub.Subscribe</c> is the one join that is not
    /// tenant-wide: it joins the per-data-category groups and gates each on the read scope governing
    /// that category, which is how a guest link receives the categories it was shared.
    /// </remarks>
    [Fact]
    public void Every_hub_method_that_joins_a_tenant_wide_group_declares_it()
    {
        var declared = HubMethods()
            .Where(m => m.GetCustomAttribute<HubTenantGroupAttribute>() is not null)
            .Select(Describe)
            .ToList();

        HubMethods().Where(JoinsAGroup).Select(Describe).Should().BeEquivalentTo(
            [.. declared, $"{nameof(DataHub)}.{nameof(DataHub.Subscribe)}"],
            "a hub method that joins a tenant-wide group must declare [HubTenantGroup], and the "
            + "per-data-category groups DataHub.Subscribe joins are gated per category instead");
    }

    [Fact]
    public void Every_invocable_hub_method_either_authenticates_or_is_gated()
    {
        // The guarantee this filter exists for: a method added to a hub later is denied by default.
        // A method that opts out with [HubAuthenticationMethod] must be doing in-band authentication.
        var entryPoints = HubMethods()
            .Where(m => m.GetCustomAttribute<HubAuthenticationMethodAttribute>() is not null)
            .Select(Describe)
            .ToList();

        entryPoints.Should().BeEquivalentTo(["DataHub.Authorize", "AlarmHub.Subscribe"]);
    }

    /// <summary>
    /// Every method a client can invoke on a hub: declared on the hub itself and not an override of a
    /// <see cref="Hub"/> lifetime member.
    /// </summary>
    /// <remarks>
    /// The lifetime overrides (<c>OnConnectedAsync</c>, <c>OnDisconnectedAsync</c>) are excluded
    /// because <see cref="IHubFilter.InvokeMethodAsync"/> never sees them — SignalR routes them through
    /// <see cref="IHubFilter.OnConnectedAsync"/> and <see cref="IHubFilter.OnDisconnectedAsync"/>
    /// instead — so an attribute declared on one would be inert. Neither of the tests below can say
    /// anything about them.
    /// </remarks>
    private static IEnumerable<MethodInfo> HubMethods() =>
        typeof(DataHub).Assembly.GetTypes()
            .Where(t => t.IsAssignableTo(typeof(TenantAwareHub)) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetBaseDefinition().DeclaringType == m.DeclaringType);

    private static string Describe(MethodInfo method) => $"{method.DeclaringType!.Name}.{method.Name}";

    /// <summary>
    /// Whether <paramref name="method"/> reaches <see cref="IGroupManager.AddToGroupAsync"/>, following
    /// the async state machine the compiler moved the body into and any helper the method calls on its
    /// own hub — including the ones the compiler moved into a type nested inside that hub (state
    /// machines, closure display classes, local-function holders).
    /// </summary>
    private static bool JoinsAGroup(MethodInfo method)
    {
        var pending = new Queue<MethodBase>();
        pending.Enqueue(method);
        var seen = new HashSet<MethodBase>();

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            if (current.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType is { } machine)
            {
                foreach (var moveNext in machine
                             .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             .Where(m => m.Name.EndsWith("MoveNext", StringComparison.Ordinal)))
                {
                    pending.Enqueue(moveNext);
                }
            }

            foreach (var called in CalledMethods(current))
            {
                if (called.Name == nameof(IGroupManager.AddToGroupAsync))
                {
                    return true;
                }

                if (DeclaredWithin(called.DeclaringType, method.DeclaringType!))
                {
                    pending.Enqueue(called);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="hubType"/> or a type nested inside it.
    /// </summary>
    /// <remarks>
    /// An async body's calls come out of a <c>MoveNext</c> on a compiler-generated type nested in the
    /// hub, and its lambdas and closures out of further nested display classes, so an exact match on
    /// the hub type alone stops the walk at the first such boundary and leaves only direct calls
    /// followed.
    /// </remarks>
    private static bool DeclaredWithin(Type? candidate, Type hubType)
    {
        for (var type = candidate; type is not null; type = type.DeclaringType)
        {
            if (type == hubType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The methods called from <paramref name="method"/>'s IL, found by resolving the token after every
    /// <c>call</c> and <c>callvirt</c> opcode byte.
    /// </summary>
    /// <remarks>
    /// A byte that is not an instruction boundary yields a token that does not resolve and is skipped,
    /// so the result is a superset of the real calls. That errs toward reporting a group join, which is
    /// the direction that makes the caller's assertion fail rather than silently pass.
    /// </remarks>
    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        const byte Call = 0x28;
        const byte CallVirt = 0x6F;

        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (Call or CallVirt))
            {
                continue;
            }

            MethodBase? called = null;
            try
            {
                called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
            }
            catch (Exception)
            {
                // Not an instruction boundary, or a token needing generic context to resolve.
            }

            if (called is not null)
            {
                yield return called;
            }
        }
    }
}
