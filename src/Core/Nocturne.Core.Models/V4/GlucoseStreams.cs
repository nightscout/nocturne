using Nocturne.Core.Constants;

namespace Nocturne.Core.Models.V4;

/// <summary>Which of a tenant's concurrent CGM streams a glucose read returns.</summary>
public enum GlucoseStreams
{
    /// <summary>Every stream: a V4 multi-stream read, or one device's own series.</summary>
    All,

    /// <summary>The stream each bucket shows (<see cref="CanonicalGlucoseStream"/>).</summary>
    Canonical,

    /// <summary>
    /// <see cref="Canonical"/> without ephemeral sources (<see cref="DataSources.IsEphemeral"/>): what a
    /// single-stream client is shown outside demo mode.
    /// </summary>
    CanonicalExcludingEphemeral,
}
