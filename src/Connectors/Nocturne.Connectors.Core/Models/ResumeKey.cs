namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     One resume point a run can read: a family's, or one treatment type's for a source that
///     fetches each treatment type separately and so can leave one behind while its siblings advance.
/// </summary>
public readonly record struct ResumeKey
{
    private ResumeKey(ResumeFamily family, SyncDataType? treatmentType)
    {
        Family = family;
        TreatmentType = treatmentType;
    }

    public ResumeFamily Family { get; }

    public SyncDataType? TreatmentType { get; }

    public static ResumeKey Of(SyncDataType treatmentType) => new(ResumeFamily.Treatments, treatmentType);

    public static implicit operator ResumeKey(ResumeFamily family) => new(family, null);

    public override string ToString() => TreatmentType?.ToString() ?? Family.ToString();
}
