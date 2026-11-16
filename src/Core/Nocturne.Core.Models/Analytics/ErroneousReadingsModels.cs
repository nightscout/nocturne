using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.Analytics;

/// <summary>
/// Where an <see cref="ErroneousReadingEpisode"/> comes from.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ErroneousEpisodeKind>))]
public enum ErroneousEpisodeKind
{
    /// <summary>Readings the uploader stored with <see cref="SensorGlucoseQuality.Invalid"/>.</summary>
    Readings,

    /// <summary>
    /// A <see cref="StateSpanCategory.DataExclusion"/> span with state <c>SensorError</c>, from a
    /// reader that drops flagged readings. There are no readings in it.
    /// </summary>
    Span,

    /// <summary>
    /// A Trio <c>CGM: …</c> note, which marks the start of a bad sensor state; the uploader's next
    /// reading ends it. There are no readings in it.
    /// </summary>
    Note,
}

/// <summary>A sensor-error episode for the Data Quality page.</summary>
public class ErroneousReadingEpisode
{
    public ErroneousEpisodeKind Kind { get; set; }
    public long StartMills { get; set; }

    /// <summary>Null while the episode is still open.</summary>
    public long? EndMills { get; set; }

    public string? Source { get; set; }
    public int InvalidCount { get; set; }
    public int UncertainCount { get; set; }

    /// <summary>The time the episode's invalid readings stand for, which statistics leave out.</summary>
    public double ExcludedMinutes { get; set; }

    public double? MinMgdl { get; set; }
    public double? MaxMgdl { get; set; }

    /// <summary>The distinct vendor states seen, e.g. <c>G7:Errors/info2</c>, or the note's state.</summary>
    public List<string> SensorStatuses { get; set; } = [];

    /// <summary>Finger sticks near the episode: the best evidence of what its values were worth.</summary>
    public List<ErroneousEpisodeMeterReading> MeterReadings { get; set; } = [];
}

public class ErroneousEpisodeMeterReading
{
    public long Mills { get; set; }
    public double Mgdl { get; set; }
}

public class ErroneousReadingsSummary
{
    public int Episodes { get; set; }
    public int InvalidReadings { get; set; }

    /// <summary>Invalid readings with too few others near them to form an episode.</summary>
    public int IsolatedReadings { get; set; }

    public int UncertainReadings { get; set; }
    public double ExcludedMinutes { get; set; }
}

/// <summary>
/// A local day with something the sensor flagged, on the patient's calendar (UTC when no zone is
/// set). Each episode and lone reading belongs to the day it starts on.
/// </summary>
public class ErroneousReadingsDay
{
    /// <summary>The local date, <c>yyyy-MM-dd</c>.</summary>
    public string Date { get; set; } = "";

    /// <summary>Start of the day's chart: local midnight.</summary>
    public long FromMills { get; set; }

    /// <summary>
    /// Exclusive end of the day's chart: the next local midnight, or the end of an episode that runs
    /// past it.
    /// </summary>
    public long ToMills { get; set; }

    /// <summary>Oldest first, to read along the chart.</summary>
    public List<ErroneousReadingEpisode> Episodes { get; set; } = [];

    /// <summary>Times of the day's invalid readings that are in no episode, oldest first.</summary>
    public List<long> IsolatedReadingMills { get; set; } = [];

    /// <summary>The invalid readings of the day's episodes and the ones on their own.</summary>
    public int InvalidReadings { get; set; }
}

/// <summary>Response of <c>GET /api/v4/erroneous-readings</c>.</summary>
public class ErroneousReadingsReport
{
    /// <summary>Newest first.</summary>
    public List<ErroneousReadingsDay> Days { get; set; } = [];

    public ErroneousReadingsSummary Summary { get; set; } = new();
}
