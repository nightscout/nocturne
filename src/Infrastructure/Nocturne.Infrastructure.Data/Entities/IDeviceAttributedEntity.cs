namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A V4 record entity carrying the <c>patient_device_id</c> foreign key, so the shared
/// device-attribution queries can read an unattributed backlog and back-stamp it.
///
/// Implementers MUST map <see cref="PatientDeviceId"/> as an ordinary EF column (a plain
/// auto-property with a <c>[Column]</c> mapping) so generic <c>ctx.Set&lt;TEntity&gt;()</c> queries
/// translate the interface-member access to SQL.
/// </summary>
public interface IDeviceAttributedEntity : IV4Entity
{
    Guid? PatientDeviceId { get; set; }
}
