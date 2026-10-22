using Xunit;

namespace Nocturne.API.Tests;

/// <summary>
/// For tests that set process environment variables the API reads, such as <c>GIT_COMMIT</c> and
/// <c>BUILD_DATE</c>. The environment is process-wide, so a status read anywhere else in the run
/// (the status golden files among them) would see the value; nothing else may run alongside.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}
