
namespace Nocturne.API.Services.Effects;

/// <summary>
/// <see cref="ICollectionEffectDescriptor"/> for the <c>profiles</c> collection.
/// Writes are decomposed to v4.
/// </summary>
/// <seealso cref="ICollectionEffectDescriptor"/>
public class ProfileEffectDescriptor : ICollectionEffectDescriptor
{
    public string CollectionName => "profiles";
    public bool DecomposeToV4 => true;
    public bool BroadcastDataUpdateOnCreate => false;
}
