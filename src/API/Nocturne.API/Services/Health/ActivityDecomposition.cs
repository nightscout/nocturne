using Nocturne.API.Services.V4;

namespace Nocturne.API.Services.Health;

/// <summary>The decomposition of a batch of activities, with what each activity wrote.</summary>
public sealed class ActivityDecomposition : GroupedDecomposition
{
    private readonly HashSet<int> _deletedByUser = [];

    /// <summary>Whether the activity at <paramref name="index"/> was not written because the user deleted it.</summary>
    public bool IsDeletedByUser(int index) => _deletedByUser.Contains(index);

    internal void DeletedByUser(int index)
    {
        if (_deletedByUser.Add(index))
            SkippedDeleted++;
    }
}
