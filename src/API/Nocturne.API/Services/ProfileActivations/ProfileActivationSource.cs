using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

/// <summary>
/// The source family of a <see cref="ProfileActivation"/>: the activations one uploader or connector
/// wrote, which supersede and confirm one another and nothing else.
/// </summary>
/// <remarks>
/// AAPS names itself by <c>app</c> on v3 and by <c>enteredBy</c> on v1, and an effective switch can
/// arrive through one while its Profile Switch came through the other, so both read as one family.
/// </remarks>
internal static class ProfileActivationSource
{
    internal const string Aaps = "AAPS";

    private const string AapsEnteredBy = "openaps://AndroidAPS";

    private static readonly string[] UploaderEnteredByPrefixes =
        ["openaps://", "loop://", "trio", "iaps", "freeaps"];

    public static string KeyOf(ProfileActivation activation) =>
        KeyOf(activation.DataSource, activation.App, activation.EnteredBy);

    public static string KeyOf(string? dataSource, string? app, string? enteredBy) =>
        !string.IsNullOrEmpty(dataSource) ? dataSource
        : IsAaps(app, enteredBy) ? Aaps
        : app ?? enteredBy ?? string.Empty;

    public static bool IsAaps(string? app, string? enteredBy) =>
        string.Equals(app, Aaps, StringComparison.OrdinalIgnoreCase)
        || enteredBy?.StartsWith(AapsEnteredBy, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether a person recorded <paramref name="activation"/> through Nocturne
    /// (<see cref="NativeProfileSwitches"/>) rather than a treatment upload or a connector.
    /// </summary>
    public static bool IsNative(ProfileActivation activation) =>
        activation is { Origin: ProfileActivationOrigin.User, LegacyId: null, DataSource: DataSources.ManualEntry };

    /// <summary>
    /// <see cref="ProfileActivationOrigin.Uploader"/> for an AID uploader's switch, else
    /// <see cref="ProfileActivationOrigin.User"/>: a careportal switch names the person in <c>enteredBy</c>.
    /// </summary>
    public static ProfileActivationOrigin OriginOf(string? app, string? enteredBy, string? originalProfileName) =>
        IsAaps(app, enteredBy)
        || !string.IsNullOrEmpty(originalProfileName)
        || UploaderEnteredByPrefixes.Any(p => enteredBy?.StartsWith(p, StringComparison.OrdinalIgnoreCase) == true)
            ? ProfileActivationOrigin.Uploader
            : ProfileActivationOrigin.User;
}
