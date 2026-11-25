namespace Nocturne.Core.Contracts.Connectors;

/// <summary>The two slots of state a connector keeps on its configuration row between runs.</summary>
[Flags]
public enum ConnectorStateSlots
{
    None = 0,

    /// <summary>
    /// Named UTC times saying how far the connector got: its own schedule stamps and the base's crawl
    /// marks. A cursor reset clears the stamps and the marks of how far crawls reached for what it
    /// re-pulls, and keeps the marks of history still owed.
    /// </summary>
    Resume = 1,

    /// <summary>
    /// Opaque state the base does not interpret, such as a vendor's scan cursors or a fingerprint of
    /// what was last published. Kept on a cursor reset unless the connector declares otherwise, and
    /// cleared by a purge that allows re-import.
    /// </summary>
    Vendor = 2,
}
