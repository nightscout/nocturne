using System.Buffers.Text;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// A spec carried in a link to the web app's <c>/reports/custom/new?spec=</c>: the normalised spec's
/// JSON, raw-deflated, as base64url. The link is not signed, so whatever it carries is read as any
/// caller's spec: decoded within <see cref="MaxSpecBytes"/>, then validated before it runs or is saved.
/// </summary>
internal static class ReportSpecLink
{
    /// <summary>The path the web app opens a linked spec at.</summary>
    public const string NewReportPath = "/reports/custom/new";

    /// <summary>The largest spec, as UTF-8 JSON, a link or a request may carry.</summary>
    public const int MaxSpecBytes = 16 * 1024;

    /// <summary>
    /// The longest encoded spec a link carries. Longer specs get no link, so the URL stays within
    /// what a browser, the gateway and the web server accept in a request line.
    /// </summary>
    public const int MaxEncodedLength = 6 * 1024;

    /// <summary>The web app's address for <paramref name="spec"/> on <paramref name="origin"/>, or null when it would be too long.</summary>
    public static string? NewReportUrl(string origin, ReportSpec spec, JsonSerializerOptions options)
    {
        var encoded = Encode(JsonSerializer.SerializeToUtf8Bytes(spec, options));
        return encoded is null ? null : $"{origin.TrimEnd('/')}{NewReportPath}?spec={encoded}";
    }

    /// <summary>The encoded form of <paramref name="json"/>, or null when it is over either limit.</summary>
    public static string? Encode(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxSpecBytes)
            return null;

        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json);

        var encoded = Base64Url.EncodeToString(compressed.ToArray());
        return encoded.Length <= MaxEncodedLength ? encoded : null;
    }

    /// <summary>
    /// The spec JSON <paramref name="encoded"/> carries. Reads at most <see cref="MaxSpecBytes"/>
    /// out of the deflate stream, so a small link that inflates to a large document is refused
    /// without inflating it.
    /// </summary>
    public static ReportSpecLinkRead Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return ReportSpecLinkRead.Fail("The link has no report.");
        if (encoded.Length > MaxEncodedLength)
            return ReportSpecLinkRead.Fail(TooLarge);

        byte[] compressed;
        try
        {
            compressed = Base64Url.DecodeFromChars(encoded.Trim());
        }
        catch (FormatException)
        {
            return ReportSpecLinkRead.Fail(Damaged);
        }

        var json = new byte[MaxSpecBytes + 1];
        var read = 0;
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
            while (read < json.Length && deflate.Read(json, read, json.Length - read) is var n and > 0)
                read += n;
        }
        catch (InvalidDataException)
        {
            return ReportSpecLinkRead.Fail(Damaged);
        }

        if (read > MaxSpecBytes)
            return ReportSpecLinkRead.Fail(TooLarge);

        try
        {
            using var document = JsonDocument.Parse(json.AsMemory(0, read), new JsonDocumentOptions { MaxDepth = 32 });
            return new ReportSpecLinkRead(document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return ReportSpecLinkRead.Fail(Damaged);
        }
    }

    /// <summary>Whether <paramref name="spec"/>, as its JSON text, is within <see cref="MaxSpecBytes"/>.</summary>
    public static bool WithinLimit(JsonElement spec) =>
        Encoding.UTF8.GetByteCount(spec.GetRawText()) <= MaxSpecBytes;

    internal const string TooLarge = "The report is too large. A report spec can be at most 16 kB.";

    internal const string Damaged = "The link is damaged. The report in it cannot be read. Copy the full link again.";
}

/// <summary>A decoded link: the spec JSON, or why there is none.</summary>
internal sealed record ReportSpecLinkRead(JsonElement? Spec, string? Error)
{
    public static ReportSpecLinkRead Fail(string error) => new(null, error);
}
