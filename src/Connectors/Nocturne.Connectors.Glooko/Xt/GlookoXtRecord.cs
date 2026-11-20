using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     One Glooko XT record (a "moment") as the <c>GET_COLLECTED_DATA</c> event returns it. A
///     single record can carry several kinds of data at once — a glucose, a bolus and carbs share
///     one row when they were logged together — so the mapper fans one record out into several
///     Nocturne records. Fields the server never filled are null; every field is optional except
///     <see cref="RecordedAt"/>.
/// </summary>
public class GlookoXtRecord
{
    /// <summary>Server-assigned id; the only stable identity a record has.</summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(GlookoXtLenientLongConverter))]
    public long? Id { get; set; }

    /// <summary>ISO-8601 UTC timestamp of the moment.</summary>
    [JsonPropertyName("recorded_at")]
    public string? RecordedAt { get; set; }

    /// <summary>Meter or manually entered glucose, in the account's unit.</summary>
    [JsonPropertyName("glycemia")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? Glycemia { get; set; }

    /// <summary>Sensor glucose, in the account's unit. Populated when a CGM product wrote the record.</summary>
    [JsonPropertyName("glycemia_cgm")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? GlycemiaCgm { get; set; }

    [JsonPropertyName("product_glycemia_id")]
    [JsonConverter(typeof(GlookoXtLenientLongConverter))]
    public long? ProductGlycemiaId { get; set; }

    /// <summary>Rapid-acting bolus, in units.</summary>
    [JsonPropertyName("fast_insulin")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? FastInsulin { get; set; }

    /// <summary>
    ///     Correction bolus, in units, as a client sends it. The server stores it as
    ///     <see cref="FastInsulin"/> with <see cref="IsInjectionCorrection"/> set, so a read-back
    ///     never carries this field; it is kept for an acknowledgement echo.
    /// </summary>
    [JsonPropertyName("correction_insulin")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? CorrectionInsulin { get; set; }

    /// <summary>Long-acting (basal) injection, in units.</summary>
    [JsonPropertyName("slow_insulin")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? SlowInsulin { get; set; }

    /// <summary>Absolute basal rate, in units per hour.</summary>
    [JsonPropertyName("basal_rate")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? BasalRate { get; set; }

    /// <summary>
    ///     Temporary basal as a percentage of the scheduled rate (100 = unchanged, 0 = suspended).
    ///     A record carrying only a percentage is not returned by the read API at all, so in
    ///     practice this only ever arrives beside <see cref="BasalRate"/>.
    /// </summary>
    [JsonPropertyName("rate_percentage")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? RatePercentage { get; set; }

    /// <summary>Duration of a basal change or extended bolus, in minutes.</summary>
    [JsonPropertyName("duration")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? Duration { get; set; }

    /// <summary>Extended (square) bolus rate.</summary>
    [JsonPropertyName("bolus_rate")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? BolusRate { get; set; }

    [JsonPropertyName("product_pump_id")]
    [JsonConverter(typeof(GlookoXtLenientLongConverter))]
    public long? ProductPumpId { get; set; }

    /// <summary>Carbohydrates, in grams.</summary>
    [JsonPropertyName("carbs")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? Carbs { get; set; }

    [JsonPropertyName("meal_tag")]
    public string? MealTag { get; set; }

    [JsonPropertyName("meal_description")]
    public string? MealDescription { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("memo")]
    public string? Memo { get; set; }

    /// <summary>Pump suspended at this moment. The server pairs it with a <see cref="BasalRate"/> of zero and no duration.</summary>
    [JsonPropertyName("pump_stop")]
    [JsonConverter(typeof(GlookoXtLenientBoolConverter))]
    public bool? PumpStop { get; set; }

    /// <summary>Pump resumed at this moment.</summary>
    [JsonPropertyName("pump_resume")]
    [JsonConverter(typeof(GlookoXtLenientBoolConverter))]
    public bool? PumpResume { get; set; }

    /// <summary>Infusion set / cannula priming.</summary>
    [JsonPropertyName("is_prime")]
    [JsonConverter(typeof(GlookoXtLenientBoolConverter))]
    public bool? IsPrime { get; set; }

    [JsonPropertyName("is_injection_correction")]
    [JsonConverter(typeof(GlookoXtLenientBoolConverter))]
    public bool? IsInjectionCorrection { get; set; }

    [JsonPropertyName("medication_name")]
    public string? MedicationName { get; set; }

    [JsonPropertyName("medication_value")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? MedicationValue { get; set; }

    [JsonPropertyName("activity_step")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? ActivityStep { get; set; }

    [JsonPropertyName("activity_minute")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? ActivityMinute { get; set; }

    [JsonPropertyName("weight")]
    [JsonConverter(typeof(GlookoXtLenientDoubleConverter))]
    public double? Weight { get; set; }

    [JsonPropertyName("serial_number")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("device_uuid")]
    public string? DeviceUuid { get; set; }

    /// <summary>Fields this model does not name, kept so a probe log can show what the server sent.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>The acknowledgement body of <c>GET_COLLECTED_DATA</c>.</summary>
public class GlookoXtCollectedDataResponse
{
    [JsonPropertyName("collected_data")]
    public List<GlookoXtRecord>? CollectedData { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
