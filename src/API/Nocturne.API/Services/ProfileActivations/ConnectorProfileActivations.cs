using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

internal static class ConnectorProfileActivations
{
    /// <summary>The effective activation a connector's Profile span records, keyed by the span's upstream id.</summary>
    public static ProfileActivation FromSpan(StateSpan span)
    {
        var metadata = ProfileSpanMetadata.Parse(span.Metadata);
        return new ProfileActivation
        {
            SyncIdentifier = span.OriginalId,
            StartTimestamp = span.StartTimestamp,
            EndTimestamp = span.EndTimestamp,
            BaseProfileName = metadata.BaseProfileName is { Length: > 0 } name ? name : "Default",
            Percentage = metadata.Percentage is { } percentage && double.IsFinite(percentage)
                ? (int)Math.Round(percentage)
                : 100,
            TimeshiftMs = (long)Math.Round(metadata.TimeshiftHours * 3_600_000),
        };
    }
}
