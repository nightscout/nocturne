
namespace Nocturne.API.Services.Effects;

/// <summary>
/// <see cref="ICollectionEffectDescriptor"/> for the <c>food</c> collection.
/// V4 decomposition is not applicable.
/// </summary>
/// <seealso cref="ICollectionEffectDescriptor"/>
public class FoodEffectDescriptor : ICollectionEffectDescriptor
{
    public string CollectionName => "food";
    public bool DecomposeToV4 => false;
    public bool BroadcastDataUpdateOnCreate => false;
}
