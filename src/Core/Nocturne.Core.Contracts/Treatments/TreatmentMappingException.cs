namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// A treatment's fields could not be read into the records it is stored as. Thrown only from the
/// mapping step, so it always means the submitted data, never the store or a lookup, and a resend
/// of the same treatment fails the same way.
/// </summary>
public sealed class TreatmentMappingException(Exception innerException)
    : Exception("A field of the treatment has a value Nocturne cannot read", innerException);
