using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Tests.Services.V4;

internal static partial class TreatmentDecomposerDoubles
{
    /// <summary>The decomposer the container builds, over <paramref name="notes"/> in place of its note repository.</summary>
    public static TreatmentDecomposer Resolved(IServiceProvider services, INoteRepository notes) =>
        ActivatorUtilities.CreateInstance<TreatmentDecomposer>(services, notes);
}
