using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;

namespace Nocturne.API.Hubs;

/// <summary>
/// Who a SignalR connection's writes are audited as, kept on the connection for its lifetime: the
/// actor <c>AuditContextMiddleware</c> recorded for the upgrade request, replaced by the credential an
/// in-band authentication proves (<see cref="HubAuthorizationState.Grant"/>).
/// </summary>
/// <remarks>
/// Hub methods write through the upgrade request's scope or the invocation scope, and neither has
/// run <c>AuditContextMiddleware</c> when it is the long-polling copy of the request or an invocation
/// scope; an in-band credential arrives after the middleware ran for an anonymous request.
/// </remarks>
public static class HubConnectionActor
{
    private const string ItemKey = "AuditActor";

    /// <summary>The connection's actor, or null when its request recorded none.</summary>
    public static IAuditContext? Of(HubCallerContext connection)
    {
        if (connection.Items.TryGetValue(ItemKey, out var held) && held is IAuditContext actor)
            return actor;

        if (connection.GetHttpContext() is not { } upgrade
            || !upgrade.Items.TryGetValue(AuditContext.HttpItemKey, out var recorded)
            || recorded is not IAuditContext upgradeActor)
        {
            return null;
        }

        connection.Items[ItemKey] = upgradeActor;
        return upgradeActor;
    }

    /// <summary>
    /// Makes <paramref name="authorization"/>'s principal the connection's actor, keeping the upgrade
    /// request's address and trace, and sets it on the upgrade request's scope at once.
    /// </summary>
    public static void Record(HubCallerContext connection, HubAuthorization authorization)
    {
        var upgradeActor = Of(connection);
        var actor = new AuditContext();
        if (upgradeActor is not null)
            actor.CopyFrom(upgradeActor);

        if (upgradeActor?.SubjectId != authorization.SubjectId)
            actor.SubjectName = null;
        actor.SubjectId = authorization.SubjectId;
        actor.AuthType = authorization.Source?.AuthType.ToString() ?? actor.AuthType;
        actor.TokenId = authorization.Source?.GrantId ?? actor.TokenId;
        actor.IsSystem = false;

        connection.Items[ItemKey] = actor;
        if (connection.GetHttpContext()?.RequestServices is { } upgradeScope)
            CarryInto(connection, upgradeScope);
    }

    /// <summary>Sets the connection's actor on <paramref name="scope"/>.</summary>
    public static void CarryInto(HubCallerContext connection, IServiceProvider scope)
    {
        if (scope.GetService<IAuditContext>() is AuditContext audit && Of(connection) is { } actor)
            audit.CopyFrom(actor);
    }
}
