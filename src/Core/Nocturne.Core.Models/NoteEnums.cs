using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Category of note for organizing and filtering
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoteCategory>))]
public enum NoteCategory
{
    /// <summary>
    /// Something noticed or observed about diabetes management
    /// </summary>
    Observation,

    /// <summary>
    /// Question to ask healthcare provider
    /// </summary>
    Question,

    /// <summary>
    /// Action item or task to complete
    /// </summary>
    Task,

    /// <summary>
    /// Contextual annotation or marker
    /// </summary>
    Marker
}
