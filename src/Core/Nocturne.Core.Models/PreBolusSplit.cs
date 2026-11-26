using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nocturne.Core.Constants;

namespace Nocturne.Core.Models;

/// <summary>
/// Splits a meal posted to the v1 API with a <c>preBolus</c> as Nightscout's POST does
/// (<c>lib/server/treatments.js</c>): the bolus stays at its own time with its <c>preBolus</c> and
/// without its carbs, and the carbs become a treatment of their own at <c>created_at + preBolus</c>
/// minutes, with the same <c>eventType</c> and the meal's <c>notes</c>.
/// </summary>
/// <remarks>
/// The carbs take an id derived from the bolus's identity, which is settled on the document as sent,
/// so a re-send upserts both halves. Each half is decomposed under its own id and correlation, so a
/// read serves them as two treatments, and neither half's re-send drops the other's rows.
/// <para>
/// NIGHTSCOUT-COMPAT: Nightscout splits any event type, and writes the second document with empty
/// carbs when there are none; only a Meal, Snack or Combo Bolus with carbs is split here. A meal with
/// no insulin is not split: Careportal's Carb Time is applied to its carbs, which move to the offset
/// under the meal's own id without the preBolus, where Nightscout keeps an empty first document and
/// the preBolus on it. Served with the preBolus, the carbs would move again when posted back. The
/// <c>notes</c> go with the carbs alone, where Nightscout keeps them on both documents, so the meal
/// holds one note. <c>foodType</c> and <c>absorptionTime</c>, which Nightscout leaves on the bolus,
/// go with the carbs they describe, since a bolus here cannot hold them. A preBolus beyond a day is
/// not applied at all.
/// </para>
/// </remarks>
public static class PreBolusSplit
{
    private const string CarbsIdPrefix = "prebolus-carbs-";
    private const string WriteBackCarbsIdPrefix = "prebolus-written-carbs-";
    private const double MaxPreBolusMinutes = 24 * 60;
    private static readonly long MaxMills = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <param name="treatment">The meal as sent.</param>
    /// <param name="identify">Settles the identity of a meal that is split.</param>
    /// <returns>The treatment alone, or the bolus and then its carbs.</returns>
    public static IReadOnlyList<Treatment> Split(Treatment treatment, Action<Treatment> identify) =>
        Split(treatment, t =>
        {
            identify(t);
            return t.Id is { Length: > 0 } id ? id : null;
        }, CarbsId);

    /// <summary>
    /// What Nightscout write-back sends for a stored treatment: <see cref="Split"/> of a copy of it,
    /// keyed by its legacy id as a v1 POST keys the meal, so a meal stored as one record goes upstream
    /// as the two documents a v1 POST of it stores, and its carbs come back under an id the pull
    /// resolves to it (<see cref="WriteBackCarbsId"/> of that legacy id). Anything else goes as it
    /// is: a record with no legacy id, since no id of its carbs would resolve, and a meal with no
    /// insulin, whose carbs Nocturne holds at its own time and whose copy upstream is already there.
    /// </summary>
    public static IReadOnlyList<Treatment> ForWriteBack(Treatment treatment) =>
        treatment.LegacyId is { Length: > 0 } legacyId
        && Split(treatment.ShallowCopy(), _ => legacyId, WriteBackCarbsId) is [_, _] halves
            ? halves
            : [treatment];

    /// <summary>How far the carbs of a meal split on <paramref name="preBolus"/> minutes lie from its bolus.</summary>
    public static long OffsetMillis(double preBolus) => (long)Math.Round(preBolus * 60_000);

    public static bool IsMealEventType(string? eventType) =>
        string.Equals(eventType, TreatmentTypes.MealBolus, StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventType, TreatmentTypes.SnackBolus, StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventType, TreatmentTypes.ComboBolus, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <see cref="Split"/> changes <paramref name="treatment"/> once it has an id.</summary>
    public static bool Applies(Treatment treatment) => CarbsAt(treatment) is not null;

    private static long? CarbsAt(Treatment treatment)
    {
        if (!IsMealEventType(treatment.EventType?.Trim())
            || treatment.Carbs is not > 0
            || treatment.PreBolus is not { } preBolus || preBolus == 0
            || !(Math.Abs(preBolus) <= MaxPreBolusMinutes)
            || treatment.Mills <= 0)
            return null;

        var carbsAt = treatment.Mills + OffsetMillis(preBolus);
        return carbsAt <= 0 || carbsAt > MaxMills ? null : carbsAt;
    }

    private static IReadOnlyList<Treatment> Split(
        Treatment treatment, Func<Treatment, string?> mealIdOf, Func<string, string> carbsIdOf)
    {
        if (CarbsAt(treatment) is not { } carbsAt || mealIdOf(treatment) is not { } mealId)
            return [treatment];

        if (treatment.Insulin is not > 0)
        {
            treatment.Mills = carbsAt;
            treatment.CreatedAt = null;
            treatment.RawTimestamp = treatment.RawTimestamp?.ValueKind switch
            {
                JsonValueKind.Number => JsonSerializer.SerializeToElement(carbsAt),
                JsonValueKind.String => JsonSerializer.SerializeToElement(treatment.Created_at),
                _ => null,
            };
            treatment.PreBolus = null;
            return [treatment];
        }

        var carbs = new Treatment
        {
            Id = carbsIdOf(mealId),
            PreBolusMealId = mealId,
            EventType = treatment.EventType,
            Mills = carbsAt,
            Carbs = treatment.Carbs,
            Notes = treatment.Notes,
            FoodType = treatment.FoodType,
            AbsorptionTime = treatment.AbsorptionTime,
            CarbTime = treatment.CarbTime,
            DataSource = treatment.DataSource,
        };

        treatment.Carbs = null;
        treatment.Notes = null;
        treatment.FoodType = null;
        treatment.AbsorptionTime = null;
        treatment.CarbTime = null;

        return [treatment, carbs];
    }

    /// <summary>
    /// Hex after a prefix with non-hex letters, so it cannot be read as an ObjectId, a UUID or a
    /// content-derived id.
    /// </summary>
    public static string CarbsId(string bolusId) => Derived(CarbsIdPrefix, bolusId);

    /// <summary>
    /// As <see cref="CarbsId"/>, for the carbs write-back splits off a meal stored as one record
    /// (<see cref="ForWriteBack"/>). Apart from the id of a v1 POST's carbs record, so what write-back
    /// removes when the meal stops splitting is only ever its own document.
    /// </summary>
    public static string WriteBackCarbsId(string legacyId) => Derived(WriteBackCarbsIdPrefix, legacyId);

    /// <summary>
    /// Whether <paramref name="name"/> is an id the carbs <see cref="ForWriteBack"/> splits off the
    /// meal <paramref name="legacyId"/> names go upstream or come back under: <see cref="WriteBackCarbsId"/>
    /// of it, or that id's <see cref="MongoObjectId.Coerce"/>.
    /// </summary>
    public static bool NamesWriteBackCarbs(string name, string legacyId)
    {
        var carbsId = WriteBackCarbsId(legacyId);
        return name == carbsId || name == MongoObjectId.Coerce(carbsId);
    }

    /// The key a carb intake keeps <see cref="Treatment.PreBolusMealId"/> under in its additional
    /// properties, so the carbs record is announced with it when read back.
    /// </summary>
    public const string MealIdField = "preBolusMealId";

    /// <summary>
    /// <paramref name="record"/> with the <see cref="Treatment.PreBolusMealId"/> of
    /// <paramref name="treatment"/> added, or unchanged when it carries none.
    /// </summary>
    public static Dictionary<string, object?>? AddMealId(Dictionary<string, object?>? record, Treatment treatment) =>
        KeptField.Add(record, MealIdField, treatment.PreBolusMealId is { Length: > 0 } mealId ? mealId : null);

    /// <summary>The meal's legacy id a record's additional properties hold, or null.</summary>
    public static string? MealIdOf(IReadOnlyDictionary<string, object?>? record) =>
        KeptField.String(record, MealIdField);

    private static string Derived(string prefix, string id) =>
        prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..48];
}
