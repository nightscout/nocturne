namespace Nocturne.Core.Contracts.Alerts;

/// <summary>Who reports the reading an evaluation selects to the clock-anomaly notice.</summary>
public enum ReadingReport
{
    /// <summary>The evaluation reports the reading it selected.</summary>
    Here,

    /// <summary>The caller reports the reading, once for the larger run the evaluation is part of.</summary>
    ByCaller,
}
