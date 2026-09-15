namespace Nocturne.Core.Models;

/// <summary>
/// A manually-entered lab HbA1c result, used only to compare against the computed eHbA1c
/// estimate — never fed into that calculation.
/// </summary>
public class LabHbA1cResult
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Local calendar date the blood was drawn.</summary>
    public DateTime MeasuredAt { get; set; }

    /// <summary>Lab-reported HbA1c in DCCT/NGSP percent.</summary>
    public double ValuePercent { get; set; }

    /// <summary>Optional free-text note (e.g. lab name).</summary>
    public string? Note { get; set; }

    /// <summary>When this row was created.</summary>
    public DateTime CreatedAt { get; set; }
}
