namespace Nocturne.API.Authorization;

/// <summary>What an action of a <see cref="DenyDemoSubjectAttribute"/> controller opens to a demo subject.</summary>
public enum DemoSubjectOpening
{
    /// <summary>A read, open to every demo subject.</summary>
    Read,

    /// <summary>
    /// A write a staff member makes for themselves: their saved views and their own flag rules. Open to a demo subject
    /// who is a member of a team.
    /// </summary>
    StaffWrite,
}

/// <summary>
/// Opens one action of a controller that carries <see cref="DenyDemoSubjectAttribute"/> to a demo subject.
/// </summary>
/// <remarks>
/// The refusal stays on the controller, so an action added later is refused until it is marked here.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OpenToDemoSubjectAttribute(DemoSubjectOpening opening) : Attribute
{
    public DemoSubjectOpening Opening { get; } = opening;
}
