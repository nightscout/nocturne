using Nocturne.Connectors.Core.Extensions;

namespace Nocturne.Connectors.Core.Utilities;

/// <summary>
///     Builds the keys a connector stores its records under: its registered
///     <see cref="ConnectorRegistrationAttribute.LegacyIdPrefix"/>, then the vendor key parts the
///     connector chooses, joined by underscores. The repository skips a record whose key a stored
///     row already carries, so a key that changes stores every record again.
/// </summary>
/// <remarks>
///     Parts are formatted as string interpolation formats them, in the current culture. Only the
///     Glooko keys that carry a <see cref="double"/> depend on it, because the stored keys were built
///     that way. A caller adding a numeric or date part to any other key formats it invariantly first.
/// </remarks>
public readonly record struct ConnectorLegacyIds(string Prefix)
{
    /// <exception cref="InvalidOperationException">
    ///     <typeparamref name="TConfig"/> declares no registration, or one with neither a data source
    ///     id nor a legacy id prefix.
    /// </exception>
    public static ConnectorLegacyIds For<TConfig>() =>
        ConnectorRegistrationAttribute.DeclaredOn(typeof(TConfig)).LegacyIdPrefix is { Length: > 0 } prefix
            ? new ConnectorLegacyIds(prefix)
            : throw new InvalidOperationException(
                $"{typeof(TConfig).Name} declares no {nameof(ConnectorRegistrationAttribute.LegacyIdPrefix)}.");

    /// <param name="parts">
    ///     Vendor key parts, already formatted: a part is written as <c>ToString()</c> gives it in the
    ///     current culture.
    /// </param>
    public string Of(params object?[] parts) => $"{Prefix}_{string.Join('_', parts.Select(part => $"{part}"))}";

    /// <summary>
    ///     The prefix and a SHA-1 of <see cref="Of"/>, for a record the vendor gives no id of its own,
    ///     whose key is built from its time and content.
    /// </summary>
    /// <param name="parts">Formatted as <see cref="Of"/> formats them.</param>
    public string Hashed(params object?[] parts) => $"{Prefix}_{HashUtils.Sha1Hex(Of(parts))}";
}
