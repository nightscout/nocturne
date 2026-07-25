using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Core.Extensions;

public static class ConnectorSecretExtensions
{
    /// <summary>
    ///     Merges updates into a connector's stored secrets.
    /// </summary>
    /// <remarks>
    ///     <see cref="IConnectorConfigurationService.SaveSecretsAsync"/> replaces the whole secrets
    ///     document, so a connector that saves only the keys it rotates at runtime would drop the
    ///     credentials the user configured. Reading first and writing the merged result keeps them.
    /// </remarks>
    /// <param name="updates">
    ///     Keys to write. A null or empty value removes the key rather than storing a blank.
    /// </param>
    /// <returns>True when the stored secrets changed and were saved.</returns>
    public static async Task<bool> MergeSecretsAsync(
        this IConnectorConfigurationService configService,
        string connectorName,
        IReadOnlyDictionary<string, string?> updates,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(updates);

        var stored = await configService.GetSecretsAsync(connectorName, ct);

        var changed = false;
        foreach (var (key, value) in updates)
            changed |= ApplySecret(stored, key, value);

        if (!changed)
            return false;

        await configService.SaveSecretsAsync(connectorName, stored, modifiedBy, ct);
        return true;
    }

    /// <summary>
    ///     Applies one secret to the document, removing the key when the value is gone.
    ///     Returns whether the document changed.
    /// </summary>
    public static bool ApplySecret(Dictionary<string, string> secrets, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        if (string.IsNullOrEmpty(value))
            return secrets.Remove(key);

        if (secrets.GetValueOrDefault(key) == value)
            return false;

        secrets[key] = value;
        return true;
    }
}
