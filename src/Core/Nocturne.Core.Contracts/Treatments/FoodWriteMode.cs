namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// The rules a food create follows.
/// </summary>
public enum FoodWriteMode
{
    /// <summary>
    /// HTML in the text fields is sanitized and trimmed, and a document that repeats an earlier one in the batch is dropped.
    /// </summary>
    Native,

    /// <summary>
    /// Legacy Nightscout rules: text is stored as sent and every document in the batch is kept.
    /// </summary>
    Legacy,
}
