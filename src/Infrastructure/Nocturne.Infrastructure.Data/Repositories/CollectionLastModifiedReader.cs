using Nocturne.Core.Contracts.Platform;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary><see cref="ICollectionLastModifiedReader"/> over <see cref="V4RecordTables"/>, one context per call.</summary>
public class CollectionLastModifiedReader(INocturneDbContextFactory contexts) : ICollectionLastModifiedReader
{
    /// <inheritdoc/>
    public async Task<DateTime?> LastModifiedAsync(string collection, CancellationToken ct)
    {
        if (!V4RecordTables.LastModified.TryGetValue(collection, out var reads))
            throw new ArgumentOutOfRangeException(nameof(collection), collection, null);

        await using var db = await contexts.CreateAsync(ct);
        DateTime? latest = null;
        foreach (var read in reads)
        {
            if (await read(db, ct) is { } stamp && (latest is null || stamp > latest))
                latest = stamp;
        }

        return latest;
    }
}
