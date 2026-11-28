namespace Nocturne.API.Services.Demo;

/// <summary>Says which demo persona a subject is.</summary>
public interface IDemoPersonaReader
{
    /// <summary>Whether <paramref name="subjectId"/> is the clinic persona of the demo host.</summary>
    Task<bool> IsClinicPersonaAsync(Guid subjectId, CancellationToken ct = default);
}
