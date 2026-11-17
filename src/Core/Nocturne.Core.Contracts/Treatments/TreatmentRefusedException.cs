namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// A treatment could not be stored because a value in it can never be stored as sent. It has been
/// kept in the tenant's rejected uploads; <see cref="Exception.Message"/> is the reason, fit to show
/// the client.
/// </summary>
public sealed class TreatmentRefusedException(string reason, Exception innerException)
    : Exception(reason, innerException);
