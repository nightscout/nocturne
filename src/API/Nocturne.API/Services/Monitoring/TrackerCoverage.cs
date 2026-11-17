using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// What a tracker definition's <see cref="TrackerDefinitionEntity.Covers"/> says its hardware
/// provides, and what follows from it.
/// </summary>
public static class TrackerCoverage
{
    /// <summary>
    /// The capabilities in <paramref name="json"/>. Names that are not a <see cref="PumpCapability"/>
    /// are skipped, so a definition is read as covering less, never more.
    /// </summary>
    public static IReadOnlySet<PumpCapability> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new HashSet<PumpCapability>();
        List<string>? names;
        try
        {
            names = JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return new HashSet<PumpCapability>();
        }
        return (names ?? [])
            .Select(NameOf)
            .OfType<PumpCapability>()
            .ToHashSet();
    }

    /// <summary>
    /// The capability spelled <paramref name="name"/>, ignoring case. Unlike
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>, a number or a comma list is not one.
    /// </summary>
    private static PumpCapability? NameOf(string? name) =>
        Enum.GetValues<PumpCapability>()
            .Select(c => (PumpCapability?)c)
            .FirstOrDefault(c => string.Equals(c.ToString(), name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>What a pod provides, which a <see cref="TrackerCategory.Pod"/> tracker covers unless it says otherwise.</summary>
    public static readonly IReadOnlySet<PumpCapability> PodCapabilities =
        new HashSet<PumpCapability> { PumpCapability.Site, PumpCapability.Reservoir, PumpCapability.Power };

    /// <summary>
    /// What <paramref name="definition"/> covers: its <see cref="TrackerDefinitionEntity.Covers"/>,
    /// else <see cref="PodCapabilities"/> for a pod tracker, else nothing known.
    /// </summary>
    public static IReadOnlySet<PumpCapability> Of(TrackerDefinitionEntity definition) =>
        Of(definition.Category, Parse(definition.Covers));

    /// <summary>What a definition of <paramref name="category"/> stating <paramref name="covers"/> covers.</summary>
    public static IReadOnlySet<PumpCapability> Of(TrackerCategory category, IReadOnlySet<PumpCapability> covers) =>
        covers.Count == 0 && category == TrackerCategory.Pod ? PodCapabilities : covers;

    /// <summary>The stored form of <paramref name="capabilities"/>, in <see cref="PumpCapability"/> order.</summary>
    public static string Serialize(IEnumerable<PumpCapability> capabilities) =>
        JsonSerializer.Serialize(capabilities.Distinct().Order().Select(c => c.ToString()));

    /// <summary>
    /// Canonicalises a requested capability list. Blank entries are dropped, since a form posts an
    /// empty list as one blank value; null stays null.
    /// </summary>
    /// <returns>False, with <paramref name="error"/>, when a name is not a capability.</returns>
    public static bool TryNormalise(
        IEnumerable<string>? requested, out IReadOnlyList<PumpCapability>? capabilities, out string? error)
    {
        capabilities = null;
        error = null;
        if (requested is null)
            return true;

        var parsed = new List<PumpCapability>();
        foreach (var name in requested.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            if (NameOf(name) is not { } capability)
            {
                error = $"'{name}' is not something a tracker can cover";
                return false;
            }
            parsed.Add(capability);
        }
        capabilities = [.. parsed.Distinct().Order()];
        return true;
    }

    /// <summary>
    /// Whether a definition of <paramref name="category"/> stating <paramref name="covers"/> holds
    /// insulin, so a low-reservoir level rule applies to it.
    /// </summary>
    public static bool HoldsInsulin(TrackerCategory category, IReadOnlySet<PumpCapability> covers) =>
        category == TrackerCategory.Reservoir || Of(category, covers).Contains(PumpCapability.Reservoir);

    /// <inheritdoc cref="HoldsInsulin(TrackerCategory, IReadOnlySet{PumpCapability})"/>
    public static bool HoldsInsulin(TrackerDefinitionEntity definition) =>
        HoldsInsulin(definition.Category, Parse(definition.Covers));
}
