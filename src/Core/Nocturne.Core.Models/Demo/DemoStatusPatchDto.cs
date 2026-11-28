namespace Nocturne.Core.Models.Demo;

/// <summary>
/// The body of <c>PATCH api/v4/admin/demo/status</c>: the demo service's scheduling state, which the API stores on the
/// demo tenant. A null member leaves that value as it is.
/// </summary>
public record DemoStatusPatchDto(
    DateTime? NextResetAt = null,
    DateTime? LastResetAt = null,
    bool? IsActive = null,
    DateTime? NextVisitorResetAt = null);
