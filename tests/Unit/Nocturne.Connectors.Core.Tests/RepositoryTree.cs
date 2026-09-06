using System;
using System.IO;

namespace Nocturne.Connectors.Core.Tests;

/// <summary>
/// Locates the working tree for tests that assert over source files rather than compiled output.
/// </summary>
internal static class RepositoryTree
{
    /// <summary>
    ///     The repository root, found by walking up from the test binaries.
    /// </summary>
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Connectors")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No src/Connectors directory above {AppContext.BaseDirectory}.");
    }
}
