using Nocturne.Infrastructure.Cache.Keys;

namespace Nocturne.API.Services.Effects;

/// <summary>
/// <see cref="ICollectionEffectDescriptor"/> for the <c>profiles</c> collection.
/// Invalidates the current-profile cache key and the profile timestamp pattern on write.
/// </summary>
/// <seealso cref="ICollectionEffectDescriptor"/>
public class ProfileEffectDescriptor : ICollectionEffectDescriptor
{
    public string CollectionName => "profiles";
    public IReadOnlyList<string> GetCacheKeysToRemove(string tid) => [CacheKeyBuilder.BuildCurrentProfileKey(tid)];
    public IReadOnlyList<string> GetCachePatternsToClear(string tid) => [CacheKeyBuilder.BuildProfileTimestampPattern(tid)];
    public bool DecomposeToV4 => true;
    public bool BroadcastDataUpdateOnCreate => false;
}
