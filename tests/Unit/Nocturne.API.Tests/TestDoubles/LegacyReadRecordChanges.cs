using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;

namespace Nocturne.API.Tests.TestDoubles;

internal static class LegacyReadRecordChanges
{
    /// <summary>
    /// The publisher a repository reports its writes to <paramref name="legacyReads"/> through, as the API
    /// wires it, with <paramref name="others"/> beside it.
    /// </summary>
    public static IRecordChangePublisher AsRecordChanges(
        this ILegacyReadCacheInvalidation legacyReads, params IRecordChangeSubscriber[] others) =>
        new RecordChangePublisher([new LegacyReadCacheSubscriber(legacyReads), .. others]);
}
