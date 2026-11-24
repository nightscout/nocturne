using System.Text.Json.Serialization;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>One person's last 24 hours, read from the apex for the People page panel.</summary>
/// <param name="Person">The item the overview builds for the person, under the same access and clamp.</param>
/// <param name="Readings">Canonical readings of the window, oldest first, at most one per 5-minute
/// bucket. Empty when there are none.</param>
/// <param name="Entries">The newest manual boluses and carb intakes of the window, newest first, at most 10 in
/// total. Null when the caller's effective permissions on the tenant do not satisfy treatments read.</param>
/// <param name="From">Start of the window the readings and entries cover, inclusive, UTC.</param>
/// <param name="To">End of the window, UTC.</param>
public record TenantPersonDetail(
    TenantOverviewItem Person,
    List<TenantOverviewPoint> Readings,
    List<TenantPersonEntry>? Entries,
    DateTime From,
    DateTime To);

/// <summary>A bolus or a carb intake. The value field the kind does not use is null.</summary>
/// <param name="Units">Insulin units, for <see cref="TenantPersonEntryKind.Bolus"/>.</param>
/// <param name="Grams">Carbohydrate grams, for <see cref="TenantPersonEntryKind.Carbs"/>.</param>
public record TenantPersonEntry(
    TenantPersonEntryKind Kind,
    DateTime Timestamp,
    double? Units,
    double? Grams);

/// <summary>What a <see cref="TenantPersonEntry"/> records.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TenantPersonEntryKind>))]
public enum TenantPersonEntryKind
{
    /// <summary>A manual insulin dose. Algorithm boluses are not entries.</summary>
    Bolus,

    /// <summary>A carbohydrate intake.</summary>
    Carbs,
}
