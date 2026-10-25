using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Treatments;

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
internal static class PreBolusSplit
{
    private const string CarbsIdPrefix = "prebolus-carbs-";
    private const double MaxPreBolusMinutes = 24 * 60;
    private static readonly long MaxMills = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <returns>The treatment alone, or the bolus and then its carbs.</returns>
    public static IReadOnlyList<Treatment> Split(Treatment treatment)
    {
        if (!TreatmentDecomposer.IsMealEventType(treatment.EventType?.Trim())
            || treatment.Carbs is not > 0
            || treatment.PreBolus is not { } preBolus || preBolus == 0
            || !(Math.Abs(preBolus) <= MaxPreBolusMinutes)
            || treatment.Mills <= 0)
            return [treatment];

        var carbsAt = treatment.Mills + (long)Math.Round(preBolus * 60_000);
        if (carbsAt <= 0 || carbsAt > MaxMills)
            return [treatment];

        TreatmentDecomposer.NormalizeIdentity(treatment);

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
            Id = CarbsId(treatment.Id!),
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
    internal static string CarbsId(string bolusId) =>
        CarbsIdPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bolusId)))[..48];
}
