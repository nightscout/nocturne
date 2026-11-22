using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// The connection a record belongs to, as the connections list attributes them
/// (<see cref="ConnectionService"/>). One app is one connection however many credentials, logins and
/// device strings it has: a verified app's grants fold under its publisher's software id, any other
/// app's under its client, and a token is its own. A record no credential wrote belongs to the
/// connector its data source names, else to that data source or device, as one key, where the list
/// draws each data source and device of such rows as its own stream.
/// </summary>
public static class ConnectionKeys
{
    /// <summary>Whether a record's writer names a connection: an app or a token grant.</summary>
    public static bool NamesConnection(OAuthGrantRecord grant) =>
        grant.GrantType is OAuthGrantTypes.App or OAuthGrantTypes.Direct;

    /// <summary>The connection a credential's records belong to.</summary>
    public static string Of(OAuthGrantRecord grant) =>
        grant.GrantType == OAuthGrantTypes.App
            ? $"app:{(grant.ClientIdentity == OAuthClientIdentity.Verified ? grant.Client?.SoftwareId : null)
                ?? grant.ClientEntityId?.ToString() ?? grant.Id.ToString()}"
            : $"token:{grant.Id}";

    /// <summary>
    /// Who owns a record that <paramref name="writer"/> inserted with
    /// <paramref name="dataSource"/> and <paramref name="device"/>: the writer's grant when
    /// <paramref name="grants"/> holds it, else the connector its data source names, else that data
    /// source or device, else no one.
    /// </summary>
    /// <param name="grants">The grants that name a connection (<see cref="NamesConnection"/>), by id.</param>
    public static RecordOwner OwnerOf(
        Guid? writer, string? dataSource, string? device, IReadOnlyDictionary<Guid, OAuthGrantRecord> grants)
    {
        if (writer is { } id && grants.TryGetValue(id, out var grant))
            return new RecordOwner(grant, null, null);
        if (ConnectorMetadataService.GetByDataSourceId(dataSource ?? device) is { } connector)
            return new RecordOwner(null, connector, null);
        return (dataSource ?? device) is { Length: > 0 } source
            ? new RecordOwner(null, null, source)
            : default;
    }
}

/// <summary>
/// One of: the <see cref="Grant"/> whose connection a record belongs to, the <see cref="Connector"/>
/// its data source names, the <see cref="Source"/> it reports, or none of them.
/// </summary>
public readonly record struct RecordOwner(
    OAuthGrantRecord? Grant, ConnectorMetadataService.ConnectorDisplayInfo? Connector, string? Source)
{
    /// <summary>A key naming the connection, unique across the three kinds; null for no owner.</summary>
    public string? Key =>
        Grant is { } grant ? ConnectionKeys.Of(grant)
        : Connector is { } connector ? $"connector:{connector.ConnectorId}"
        : Source is { } source ? $"source:{source}"
        : null;
}
