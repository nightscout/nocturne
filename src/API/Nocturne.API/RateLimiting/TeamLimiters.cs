namespace Nocturne.API.RateLimiting;

/// <summary>Bounds how many teams one person creates.</summary>
public sealed class TeamCreationLimiter(IConfiguration configuration)
    : PerPersonLimiter(configuration, 10, TimeSpan.FromHours(1));

/// <summary>
/// Bounds how often one person renames teams. Each rename tells every owner who gave the team access
/// to a site, so an unbounded loop would put a stream of names in front of them.
/// </summary>
public sealed class TeamRenameLimiter(IConfiguration configuration)
    : PerPersonLimiter(configuration, 10, TimeSpan.FromHours(1));
