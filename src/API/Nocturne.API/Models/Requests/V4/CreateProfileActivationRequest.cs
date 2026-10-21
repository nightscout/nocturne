namespace Nocturne.API.Models.Requests.V4;

/// <summary>A profile switch a person records in Nocturne, starting now.</summary>
/// <seealso cref="Validators.V4.CreateProfileActivationRequestValidator"/>
/// <seealso cref="Services.ProfileActivations.NativeProfileSwitches"/>
public class CreateProfileActivationRequest
{
    /// <summary>A profile in the profile store, by exact name.</summary>
    public string BaseProfileName { get; set; } = string.Empty;

    public int Percentage { get; set; } = 100;

    public int TimeshiftHours { get; set; }

    /// <summary>The length in minutes; null or zero for a permanent switch.</summary>
    public int? DurationMinutes { get; set; }

    public string? Notes { get; set; }
}
