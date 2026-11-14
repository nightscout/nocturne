namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// The rules a food create or update follows.
/// </summary>
public enum FoodWriteMode
{
    /// <summary>
    /// HTML in the text fields is sanitized and trimmed, a name that sanitizes to empty is refused, and a created document that repeats an earlier one in the batch is dropped.
    /// </summary>
    Native,

    /// <summary>
    /// Legacy Nightscout rules: text is stored as sent and every created document in the batch is kept.
    /// </summary>
    Legacy,
}
