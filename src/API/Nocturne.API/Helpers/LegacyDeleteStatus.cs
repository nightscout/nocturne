namespace Nocturne.API.Helpers;

/// <summary>
/// The body Nightscout's v1 DELETE answers with, found or not: its <c>normalizeDeleteStatus</c>
/// over a MongoDB delete result, which carries <c>n</c> and <c>deletedCount</c> alike, plus the
/// older driver's <c>ok</c> and <c>result</c>.
/// </summary>
/// <remarks>
/// NightscoutKit (Loop) fails any DELETE that is not a 200 with a JSON body, so a retried delete
/// of a record already gone must still answer this with zeros.
/// </remarks>
public static class LegacyDeleteStatus
{
    /// <summary>
    /// The path id Nightscout reads as no id filter, so the request's <c>find</c> alone selects
    /// what a delete by id removes.
    /// </summary>
    public const string AnyId = "*";

    /// <summary>
    /// A dictionary rather than an anonymous type so the zero counts survive the serialiser's
    /// <c>WhenWritingDefault</c>.
    /// </summary>
    public static Dictionary<string, object> For(long deletedCount) => new()
    {
        ["acknowledged"] = true,
        ["deletedCount"] = deletedCount,
        ["n"] = deletedCount,
        ["ok"] = 1,
        ["result"] = new Dictionary<string, object> { ["n"] = deletedCount, ["ok"] = 1 },
    };
}
