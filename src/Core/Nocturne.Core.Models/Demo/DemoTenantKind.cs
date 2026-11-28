namespace Nocturne.Core.Models.Demo;

/// <summary>What a demo tenant is to the demo: the values of <c>tenant_demo_config.kind</c>.</summary>
public static class DemoTenantKind
{
    /// <summary>
    /// The tenant the public demo runs on: its share link, its visitor sessions and its resets. There is one.
    /// </summary>
    public const string Host = "host";

    /// <summary>
    /// One of the demo clinic's synthetic people. It has no demo member, no share grant and no session of its own:
    /// it is reached only through the clinic's team.
    /// </summary>
    public const string Person = "person";
}
