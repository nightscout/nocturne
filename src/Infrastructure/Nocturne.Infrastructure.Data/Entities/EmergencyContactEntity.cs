using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for EmergencyContact (escalation contact management)
/// Maps to emergency contacts for alert escalation
/// </summary>
[Table("emergency_contacts")]
public class EmergencyContactEntity : IHasCreatedAt, IHasUpdatedAt
{
    /// <summary>
    /// Primary key - UUID for emergency contact
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this contact belongs to
    /// </summary>
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Contact name
    /// </summary>
    [Column("name")]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Phone number for contact
    /// </summary>
    [Column("phone_number")]
    [MaxLength(50)]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Email address for contact
    /// </summary>
    [Column("email_address")]
    [MaxLength(255)]
    public string? EmailAddress { get; set; }

    /// <summary>
    /// Type of emergency contact (Family, Caregiver, Healthcare, Emergency)
    /// </summary>
    [Column("contact_type")]
    public EmergencyContactType ContactType { get; set; }

    /// <summary>
    /// Priority for escalation (1 = highest priority)
    /// </summary>
    [Column("priority")]
    public int Priority { get; set; } = 1;

    /// <summary>
    /// Whether this contact is active
    /// </summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Alert types this contact should be notified for (stored as JSON)
    /// </summary>
    [Column("alert_types", TypeName = "jsonb")]
    public string AlertTypes { get; set; } = "[]";

    /// <summary>
    /// When this contact was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this contact was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Emergency contact type enumeration
/// </summary>
public enum EmergencyContactType
{
    /// <summary>
    /// Family member contact
    /// </summary>
    Family,

    /// <summary>
    /// Caregiver or care provider contact
    /// </summary>
    Caregiver,

    /// <summary>
    /// Healthcare provider contact
    /// </summary>
    Healthcare,

    /// <summary>
    /// Emergency services contact
    /// </summary>
    Emergency,
}
