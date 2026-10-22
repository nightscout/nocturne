using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Hubs;

/// <summary>
/// The <see cref="TenantReader"/> a SignalR connection reads as, kept on the connection for its
/// lifetime: the one its upgrade request recorded (<see cref="HttpContextExtensions.RecordReader"/>),
/// narrowed when an in-band credential is clamped (<see cref="HubAuthorizationState.Grant"/>).
/// </summary>
/// <remarks>
/// It is not read off the upgrade request's scope, because for long polling that scope is a copy
/// SignalR made which never ran the middleware. A connection whose request recorded no reader has
/// none, and its scopes are left reading as no one with no tenant.
/// </remarks>
public static class HubConnectionReader
{
    private const string ItemKey = "TenantReader";

    /// <summary>The connection's reader, or null when its request recorded none.</summary>
    public static TenantReader? Of(HubCallerContext connection)
    {
        if (connection.Items.TryGetValue(ItemKey, out var held) && held is TenantReader reader)
            return reader;

        if (connection.GetHttpContext()?.GetRecordedReader() is not { } recorded)
            return null;

        connection.Items[ItemKey] = recorded;
        return recorded;
    }

    /// <summary>Limits the connection to the last 24 hours from its next scope on.</summary>
    public static void ClampToDay(HubCallerContext connection)
    {
        if (Of(connection) is { } reader)
            connection.Items[ItemKey] = reader.ClampedToDay();
    }
}
