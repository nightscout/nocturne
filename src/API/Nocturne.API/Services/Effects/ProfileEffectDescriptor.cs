
namespace Nocturne.API.Services.Effects;

/// <summary>
/// <see cref="ICollectionEffectDescriptor"/> for the <c>profiles</c> collection.
/// No cache invalidation is performed on profile writes; writes are decomposed to v4.
/// </summary>
/// <seealso cref="ICollectionEffectDescriptor"/>
public class ProfileEffectDescriptor : ICollectionEffectDescriptor
{
    public string CollectionName => "profiles";
    public IReadOnlyList<string> GetCacheKeysToRemove(string tid) => [];
    public IReadOnlyList<string> GetCachePatternsToClear(string tid) => [];
    public bool DecomposeToV4 => true;
    public bool BroadcastDataUpdateOnCreate => false;
}
