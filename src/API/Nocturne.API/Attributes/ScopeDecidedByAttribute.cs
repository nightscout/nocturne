namespace Nocturne.API.Attributes;

/// <summary>
/// Declares that an action's scope requirement is decided in its handler, because the scope depends
/// on the record or a request value. Names the guard type that decides it or, where no guard type
/// exists, the type and member that hold the rule: the method that refuses, or the value that makes
/// the stricter scope apply.
/// </summary>
/// <remarks>
/// Enforces nothing: the call in the handler is the gate. It exists so the guard tests, which read
/// attributes and never method bodies, can tell an action whose scope is decided per request from
/// one that declares no scope at all. An action that also has a static requirement declares it
/// with <see cref="RequireScopeAttribute"/> as well.
/// </remarks>
/// <seealso cref="Authorization.ScopeForbiddenExtensions.ForbiddenForScope"/>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ScopeDecidedByAttribute : Attribute
{
    /// <summary>Names a guard type whose methods decide the scope.</summary>
    public ScopeDecidedByAttribute(Type guard) => Type = guard;

    /// <summary>Names the member of <paramref name="type"/> that holds the rule.</summary>
    public ScopeDecidedByAttribute(Type type, string member)
    {
        Type = type;
        Member = member;
    }

    /// <summary>The guard type, or the type that declares <see cref="Member"/>.</summary>
    public Type Type { get; }

    /// <summary>The member that holds the rule, or null when <see cref="Type"/> is a guard.</summary>
    public string? Member { get; }
}
