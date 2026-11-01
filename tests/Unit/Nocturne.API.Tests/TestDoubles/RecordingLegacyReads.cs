using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>Records every model type a write reported to <see cref="ILegacyReadCacheInvalidation"/>.</summary>
internal sealed class RecordingLegacyReads : ILegacyReadCacheInvalidation
{
    public List<Type> Models { get; } = [];

    public Task WrittenAsync(IReadOnlyCollection<Type> models)
    {
        Models.AddRange(models);
        return Task.CompletedTask;
    }
}
