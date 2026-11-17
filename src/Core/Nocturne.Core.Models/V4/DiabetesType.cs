using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Classification of a patient's diabetes diagnosis.
/// </summary>
/// <seealso cref="PatientRecord"/>
[JsonConverter(typeof(JsonStringEnumConverter<DiabetesType>))]
public enum DiabetesType
{
    /// <summary>Type 1 diabetes (autoimmune).</summary>
    Type1,

    /// <summary>Type 2 diabetes (insulin resistance).</summary>
    Type2,

    /// <summary>Latent Autoimmune Diabetes in Adults.</summary>
    LADA,

    /// <summary>Maturity-Onset Diabetes of the Young.</summary>
    MODY,

    Gestational,

    /// <summary>Other type; see <see cref="PatientRecord.DiabetesTypeOther"/> for details.</summary>
    Other
}
