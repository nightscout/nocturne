using System.Reflection;
using System.Text.Json;
using Nocturne.Connectors.Core.Extensions;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     The configuration changes only a person at their own sign-in who may change the site's
///     settings may make, because an app holding a delegated credential, and a member who may only set
///     up data sources, neither owns the stored secret nor decides where the site's records go:
///     <list type="bullet">
///         <item>
///             a change to where the connector sends requests (<see cref="ChoosesDestination"/>)
///             while it carries something there: a secret, or the tenant's records through a
///             <see cref="ConnectorPropertyAttribute.SendsRecords"/> property that is on before or
///             after the save;
///         </item>
///         <item>turning a <see cref="ConnectorPropertyAttribute.SendsRecords"/> property on.</item>
///     </list>
///     Setting the destination of a connector that carries nothing is first-time setup and stays
///     open. Emptying the secrets first does not reopen it while records would still be sent.
/// </summary>
/// <seealso cref="ConnectorCredentialVerifier{TConfig}"/>
public static class DelegatedConfigurationGuard
{
    /// <summary>
    ///     The refused key for a connector whose configuration type is unknown, so a save the guard
    ///     cannot judge is refused whole.
    /// </summary>
    public const string WholeConfiguration = "configuration";

    /// <summary>
    ///     Whether <paramref name="property"/> decides the host the connector sends requests to. Only a
    ///     free-text URL does: a region or server choice selects among the vendor's own hosts.
    /// </summary>
    public static bool ChoosesDestination(PropertyInfo property) =>
        property.GetCustomAttribute<ConnectorPropertyAttribute>()?.Format == "uri";

    /// <summary>Whether turning <paramref name="property"/> on sends the tenant's records out.</summary>
    public static bool SendsRecords(PropertyInfo property) =>
        property.GetCustomAttribute<ConnectorPropertyAttribute>()?.SendsRecords == true;

    /// <summary>
    ///     The keys of <paramref name="proposed"/> a delegated credential may not save over
    ///     <paramref name="stored"/>. A save replaces the stored document, so both are bound over
    ///     <paramref name="baseline"/> and compared value by value.
    /// </summary>
    /// <param name="baseline">
    ///     The configuration every tenant's document is bound over: the type's defaults with the
    ///     instance's own settings applied. Not changed.
    /// </param>
    /// <param name="stored">The stored configuration, or null when there is none.</param>
    /// <param name="proposed">The configuration the caller is saving.</param>
    /// <param name="holdsStoredSecret">Whether the tenant has stored a secret for the connector.</param>
    public static IReadOnlyList<string> RefusedKeys(
        object baseline, JsonDocument? stored, JsonDocument proposed, bool holdsStoredSecret)
    {
        var properties = Properties(baseline);
        var before = Bind(baseline, stored);
        var after = Bind(baseline, proposed);

        var carries = Carries(properties, before, holdsStoredSecret)
                      || properties.Any(p => SendsRecords(p) && p.GetValue(after) is true);

        return properties
            .Where(p =>
                (carries && ChoosesDestination(p) && !Equals(p.GetValue(before), p.GetValue(after)))
                || (SendsRecords(p) && p.GetValue(after) is true && p.GetValue(before) is not true))
            .Select(ConnectorConfigurationBinder.GetBoundKeyName)
            .ToList();
    }

    /// <summary>
    ///     The keys of <paramref name="stored"/> a guarded caller may not change, whatever they save:
    ///     every destination while the connector carries something there, and every
    ///     <see cref="ConnectorPropertyAttribute.SendsRecords"/> property that is off.
    ///     <see cref="RefusedKeys"/> refuses a save that changes one of them.
    /// </summary>
    /// <param name="baseline">As for <see cref="RefusedKeys"/>.</param>
    /// <param name="stored">The stored configuration, or null when there is none.</param>
    /// <param name="holdsStoredSecret">Whether the tenant has stored a secret for the connector.</param>
    public static IReadOnlyList<string> LockedKeys(object baseline, JsonDocument? stored, bool holdsStoredSecret)
    {
        var properties = Properties(baseline);
        var before = Bind(baseline, stored);
        var carries = Carries(properties, before, holdsStoredSecret);

        return properties
            .Where(p => (carries && ChoosesDestination(p)) || (SendsRecords(p) && p.GetValue(before) is not true))
            .Select(ConnectorConfigurationBinder.GetBoundKeyName)
            .ToList();
    }

    private static List<PropertyInfo> Properties(object baseline) =>
        baseline.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToList();

    private static bool Carries(List<PropertyInfo> properties, object before, bool holdsStoredSecret) =>
        holdsStoredSecret
        || properties.Any(p => IsSecret(p) && p.GetValue(before) is string { Length: > 0 })
        || properties.Any(p => SendsRecords(p) && p.GetValue(before) is true);

    private static bool IsSecret(PropertyInfo property) =>
        property.GetCustomAttribute<ConnectorPropertyAttribute>()?.Secret == true;

    private static object Bind(object baseline, JsonDocument? configuration)
    {
        var type = baseline.GetType();
        var config = JsonSerializer.Deserialize(JsonSerializer.Serialize(baseline, type), type)
                     ?? throw new InvalidOperationException($"{type.Name} could not be copied.");
        if (configuration is { RootElement.ValueKind: JsonValueKind.Object })
            ConnectorConfigurationBinder.ApplyJsonToConfig(configuration, config);
        return config;
    }
}
