using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Legacy;

/// <summary>
/// Static helpers for formatting <see cref="Entry"/> and <see cref="Treatment"/> collections into
/// plain-text export formats (CSV, TSV, TXT). All methods are pure functions with no external dependencies.
/// </summary>
public static class DataFormatService
{
    private static readonly JsonSerializerOptions RelaxedEscaping = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly (string MediaType, string? Format)[] Offered =
    [
        ("text/plain", "txt"),
        ("text/tab-separated-values", "tsv"),
        ("text/csv", "csv"),
        ("application/json", null),
    ];

    /// <summary>
    /// The text format a request asks for, or <see langword="null"/> when the response is JSON. A
    /// <paramref name="format"/> of csv, tsv or txt wins; any other value is ignored. Otherwise
    /// offered types are ranked by quality, specificity, then position in the <c>Accept</c> header,
    /// as Express's <c>res.format</c> does. A <c>q=0</c> range refuses a type.
    /// </summary>
    /// <remarks>
    /// NIGHTSCOUT-COMPAT: Nightscout answers <c>*/*</c> and a missing <c>Accept</c> with the text
    /// layout; Nocturne answers JSON, so only an explicit text type or <c>text/*</c> selects text.
    /// </remarks>
    public static string? NegotiateFormat(string? format, IList<MediaTypeHeaderValue> accept)
    {
        if (format?.ToLowerInvariant() is ("csv" or "tsv" or "txt") and { } requested)
        {
            return requested;
        }

        (string? Format, double Quality, int Specificity, int Index)? best = null;
        foreach (var (mediaType, offeredFormat) in Offered)
        {
            if (BestMatch(accept, mediaType) is not { } match || match.Quality <= 0)
                continue;

            if (
                best is null
                || (match.Quality, match.Specificity, -match.Index).CompareTo(
                    (best.Value.Quality, best.Value.Specificity, -best.Value.Index)
                ) > 0
            )
            {
                best = (offeredFormat, match.Quality, match.Specificity, match.Index);
            }
        }

        return best?.Format;
    }

    private static (double Quality, int Specificity, int Index)? BestMatch(
        IList<MediaTypeHeaderValue> accept,
        string mediaType
    )
    {
        var offered = new MediaTypeHeaderValue(mediaType);
        (double Quality, int Specificity, int Index)? best = null;
        for (var index = 0; index < accept.Count; index++)
        {
            var range = accept[index];
            var specificity =
                range.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase) ? 2
                : range.MatchesAllSubTypes && range.Type.Equals(offered.Type, StringComparison.OrdinalIgnoreCase) ? 1
                : 0;
            if (specificity > 0 && (best is null || specificity > best.Value.Specificity))
            {
                best = (range.Quality ?? 1, specificity, index);
            }
        }

        return best;
    }

    public static string FormatEntries(Entry[] entries, string format)
    {
        return format.ToLowerInvariant() switch
        {
            "csv" => FormatEntriesSeparated(entries, ","),
            "tsv" or "txt" => FormatEntriesSeparated(entries, "\t"),
            _ => throw new ArgumentException($"Unsupported format: {format}"),
        };
    }

    /// <inheritdoc />
    public static string FormatTreatments(Treatment[] treatments, string format)
    {
        return format.ToLowerInvariant() switch
        {
            "csv" => FormatTreatmentsAsCsv(treatments),
            "tsv" => FormatTreatmentsAsTsv(treatments),
            "txt" => FormatTreatmentsAsText(treatments),
            _ => throw new ArgumentException($"Unsupported format: {format}"),
        };
    }

    /// <inheritdoc />
    public static string FormatDeviceStatus(DeviceStatus[] deviceStatus, string format)
    {
        return format.ToLowerInvariant() switch
        {
            "csv" => FormatDeviceStatusAsCsv(deviceStatus),
            "tsv" => FormatDeviceStatusAsTsv(deviceStatus),
            "txt" => FormatDeviceStatusAsText(deviceStatus),
            _ => throw new ArgumentException($"Unsupported format: {format}"),
        };
    }

    /// <inheritdoc />
    public static string GetContentType(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "csv" => "text/csv",
            "tsv" => "text/tab-separated-values",
            "txt" => "text/plain",
            _ => "application/json",
        };
    }

    private static string FormatEntriesSeparated(Entry[] entries, string separator) =>
        string.Join(
            "\r\n",
            entries.Select(entry =>
                string.Join(
                    separator,
                    JsonString(entry.DateString),
                    entry.Mills.ToString(CultureInfo.InvariantCulture),
                    entry.Sgv?.ToString(CultureInfo.InvariantCulture),
                    JsonString(entry.Direction),
                    JsonString(entry.Device)
                )
            )
        );

    private static string JsonString(string? value) =>
        value is null ? "" : JsonSerializer.Serialize(value, RelaxedEscaping);

    private static string FormatTreatmentsAsCsv(Treatment[] treatments)
    {
        var sb = new StringBuilder();

        // CSV Header
        sb.AppendLine(
            "_id,timestamp,created_at,eventType,insulin,carbs,glucose,glucoseType,notes,enteredBy"
        );

        foreach (var treatment in treatments)
        {
            sb.AppendLine(FormatTreatmentAsCsvRow(treatment));
        }

        return sb.ToString();
    }

    private static string FormatTreatmentsAsTsv(Treatment[] treatments)
    {
        var sb = new StringBuilder();

        // TSV Header
        sb.AppendLine(
            "_id\ttimestamp\tcreated_at\teventType\tinsulin\tcarbs\tglucose\tglucoseType\tnotes\tenteredBy"
        );

        foreach (var treatment in treatments)
        {
            sb.AppendLine(FormatTreatmentAsTsvRow(treatment));
        }

        return sb.ToString();
    }

    private static string FormatTreatmentsAsText(Treatment[] treatments)
    {
        var sb = new StringBuilder();

        foreach (var treatment in treatments)
        {
            var parts = new List<string>();
            if (treatment.Insulin.HasValue)
                parts.Add($"{treatment.Insulin}U insulin");
            if (treatment.Carbs.HasValue)
                parts.Add($"{treatment.Carbs}g carbs");

            sb.AppendLine(
                $"Treatment {treatment.Id}: {string.Join(", ", parts)} at {treatment.CreatedAt} ({treatment.EventType})"
            );
        }

        return sb.ToString();
    }

    private static string FormatTreatmentAsCsvRow(Treatment treatment)
    {
        return $"{EscapeCsvField(treatment.Id ?? "")},{treatment.Timestamp?.ToString() ?? ""},{EscapeCsvField(treatment.CreatedAt ?? "")},{EscapeCsvField(treatment.EventType ?? "")},{treatment.Insulin?.ToString() ?? ""},{treatment.Carbs?.ToString() ?? ""},{treatment.Glucose?.ToString() ?? ""},{EscapeCsvField(treatment.GlucoseType ?? "")},{EscapeCsvField(treatment.Notes ?? "")},{EscapeCsvField(treatment.EnteredBy ?? "")}";
    }

    private static string FormatTreatmentAsTsvRow(Treatment treatment)
    {
        return $"{EscapeTsvField(treatment.Id ?? "")}\t{treatment.Timestamp?.ToString() ?? ""}\t{EscapeTsvField(treatment.CreatedAt ?? "")}\t{EscapeTsvField(treatment.EventType ?? "")}\t{treatment.Insulin?.ToString() ?? ""}\t{treatment.Carbs?.ToString() ?? ""}\t{treatment.Glucose?.ToString() ?? ""}\t{EscapeTsvField(treatment.GlucoseType ?? "")}\t{EscapeTsvField(treatment.Notes ?? "")}\t{EscapeTsvField(treatment.EnteredBy ?? "")}";
    }

    private static string FormatDeviceStatusAsCsv(DeviceStatus[] deviceStatuses)
    {
        var sb = new StringBuilder();

        // CSV Header
        sb.AppendLine(
            "_id,mills,created_at,device,uploader_battery,pump_battery_percent,iob_timestamp,iob_bolusiob,iob_basaliob"
        );

        foreach (var status in deviceStatuses)
        {
            sb.AppendLine(FormatDeviceStatusAsCsvRow(status));
        }

        return sb.ToString();
    }

    private static string FormatDeviceStatusAsTsv(DeviceStatus[] deviceStatuses)
    {
        var sb = new StringBuilder();

        // TSV Header
        sb.AppendLine(
            "_id\tmills\tcreated_at\tdevice\tuploader_battery\tpump_battery_percent\tiob_timestamp\tiob_bolusiob\tiob_basaliob"
        );

        foreach (var status in deviceStatuses)
        {
            sb.AppendLine(FormatDeviceStatusAsTsvRow(status));
        }

        return sb.ToString();
    }

    private static string FormatDeviceStatusAsText(DeviceStatus[] deviceStatuses)
    {
        var sb = new StringBuilder();

        foreach (var status in deviceStatuses)
        {
            var batteryInfo = status.Uploader?.Battery?.ToString() ?? "unknown";
            sb.AppendLine(
                $"Device Status {status.Id}: {status.Device} at {status.CreatedAt} (Battery: {batteryInfo}%)"
            );
        }

        return sb.ToString();
    }

    private static string FormatDeviceStatusAsCsvRow(DeviceStatus status)
    {
        return $"{EscapeCsvField(status.Id ?? "")},{status.Mills},{EscapeCsvField(status.CreatedAt ?? "")},{EscapeCsvField(status.Device ?? "")},{status.Uploader?.Battery?.ToString() ?? ""},{status.Pump?.Battery?.Percent?.ToString() ?? ""},{EscapeCsvField(status.Pump?.Iob?.Timestamp ?? "")},{status.Pump?.Iob?.BolusIob?.ToString() ?? ""},{status.Pump?.Iob?.BasalIob?.ToString() ?? ""}";
    }

    private static string FormatDeviceStatusAsTsvRow(DeviceStatus status)
    {
        return $"{EscapeTsvField(status.Id ?? "")}\t{status.Mills}\t{EscapeTsvField(status.CreatedAt ?? "")}\t{EscapeTsvField(status.Device ?? "")}\t{status.Uploader?.Battery?.ToString() ?? ""}\t{status.Pump?.Battery?.Percent?.ToString() ?? ""}\t{EscapeTsvField(status.Pump?.Iob?.Timestamp ?? "")}\t{status.Pump?.Iob?.BolusIob?.ToString() ?? ""}\t{status.Pump?.Iob?.BasalIob?.ToString() ?? ""}";
    }

    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return "";

        // Escape double quotes by doubling them and wrap in quotes if needed
        if (
            field.Contains('"')
            || field.Contains(',')
            || field.Contains('\n')
            || field.Contains('\r')
        )
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }

        return field;
    }

    private static string EscapeTsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return "";

        // Replace tabs, newlines, and carriage returns with spaces
        return field.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
