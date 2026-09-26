using System.Net;

namespace Nocturne.Core.Constants;

/// <summary>
/// What a person is told when Nocturne cannot read their Nightscout site, in the words of the
/// thing they have to go and fix.
/// </summary>
/// <remarks>
/// Both the setup-time import and the connector's sync reach a user's Nightscout, and must not
/// describe the same failure differently. The shared wording lives here because a connector
/// cannot reference the API project.
/// </remarks>
public static class NightscoutMessages
{
    /// <summary>The site never answered: DNS, a refused connection, dropped packets or a timeout.</summary>
    public const string Unreachable =
        "Could not reach your Nightscout server. Check it is online and that it allows connections "
        + "from Nocturne.";

    /// <summary>The site answered 401, how Nightscout turns down a secret it does not recognise.</summary>
    public const string ApiSecretRejected =
        "Nightscout rejected the API secret. Check it matches your Nightscout API_SECRET exactly.";

    /// <summary>
    /// The site answered 403. Nightscout sends 401 for a secret it does not recognise. A 403 is as
    /// likely to come from an IP rule, proxy or firewall in front of the site. Naming the API
    /// secret here would be a guess.
    /// </summary>
    public const string Refused =
        "Nightscout refused the request. That can come from Nightscout itself, or from a firewall, "
        + "proxy or IP rule in front of your site — check what is allowed to reach it from Nocturne.";

    /// <summary>The check could not be completed, for a reason that is in the log rather than in the tenant's hands.</summary>
    public const string CheckFailed = "Nocturne could not check your Nightscout server.";

    /// <summary>
    /// A 404 answering a probe. Both probes ask for a route every Nightscout serves, so a 404
    /// there says the address reached something that is not one.
    /// </summary>
    public const string NotNightscout =
        "Nothing at that address answered as Nightscout. Check the site address.";

    /// <summary>
    /// Connector-only: the import reads without a secret, the connector requires one. A scheduled
    /// poll never gets this far, because ConnectorConfigurationLoader switches an incomplete
    /// connector off first. A manual "Sync now" does: ConnectorSyncExecutor does not read Enabled.
    /// </summary>
    public const string ApiSecretMissing =
        "Nocturne needs your Nightscout API secret before it can sync. Add it in this connector's "
        + "settings, using the same value as your Nightscout API_SECRET.";

    /// <summary>What to tell the tenant about a status their Nightscout answered with.</summary>
    /// <param name="read">
    ///     Two answers turn on this, along different axes. Only the import reads without a secret,
    ///     so only its reads offer a blank one after a 401. Only a probe asks for a route every
    ///     Nightscout serves, so only a probe may read a 404 as a wrong address.
    /// </param>
    public static string ForStatus(HttpStatusCode status, string reading, NightscoutRead read) =>
        status switch
        {
            HttpStatusCode.Unauthorized => read is NightscoutRead.ImportProbe or NightscoutRead.ImportCollection
                ? ApiSecretRejected + " If your site allows reading without one, leave it blank."
                : ApiSecretRejected,
            HttpStatusCode.Forbidden => Refused,
            HttpStatusCode.NotFound when read is NightscoutRead.ImportProbe or NightscoutRead.ConnectorProbe => NotNightscout,
            // The number is kept for whoever reads a support thread. The fix is not the tenant's
            // to make, so the sentence says what they can do instead.
            _ when (int)status is >= 500 and <= 599 =>
                $"Nightscout answered with a server error ({(int)status}). It may be down or "
                + "restarting; try again shortly.",
            _ => $"Nightscout answered {(int)status} for {reading}.",
        };
}

/// <summary>Which read of a user's Nightscout a message is being written for.</summary>
public enum NightscoutRead
{
    /// <summary>The setup wizard's connection test, which asks for /api/v1/status.</summary>
    ImportProbe,

    /// <summary>The import walking collections by name, any of which an older site may lack.</summary>
    ImportCollection,

    /// <summary>The connector's hand-shake, which asks for /api/v1/entries.json.</summary>
    ConnectorProbe,
}
