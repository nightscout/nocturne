namespace Nocturne.API.Models.Responses;

/// <summary>The recipient of an <c>in_app</c> alert channel.</summary>
public class InAppRecipientResponse
{
    /// <summary>The subject the channel names. Null when it names none.</summary>
    public Guid? SubjectId { get; set; }
    /// <summary>The name the site knows the member by. Null when they cannot receive.</summary>
    public string? Name { get; set; }
    /// <summary>Whether the channel reaches someone: the subject is a member of the site whose
    /// roles grant any access. A channel that does not is sent to whoever next saves the
    /// rule.</summary>
    public bool CanReceive { get; set; }
}
