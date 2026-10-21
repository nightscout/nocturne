namespace Nocturne.Core.Models.V4;

/// <summary>A <see cref="ProfileActivation"/> as the v4 API and the chart serve it.</summary>
public sealed class ProfileActivationDto
{
    public Guid Id { get; set; }

    public string BaseProfileName { get; set; } = string.Empty;

    public int Percentage { get; set; }

    /// <summary>The shift in hours; may be negative or fractional.</summary>
    public double TimeshiftHours { get; set; }

    public ProfileActivationPhase Phase { get; set; }

    public ProfileActivationOrigin Origin { get; set; }

    public string? App { get; set; }

    public bool IsTemporary { get; set; }

    public long StartMills { get; set; }

    public long? EndMills { get; set; }

    public static ProfileActivationDto From(ProfileActivation activation) => new()
    {
        Id = activation.Id,
        BaseProfileName = activation.BaseProfileName,
        Percentage = activation.Percentage,
        TimeshiftHours = activation.TimeshiftMs / 3_600_000.0,
        Phase = activation.Phase,
        Origin = activation.Origin,
        App = activation.App,
        IsTemporary = activation.IsTemporary,
        StartMills = activation.Mills,
        EndMills = activation.EndMills,
    };
}
