
namespace Nocturne.API.Services.Effects;

/// <summary>
/// <see cref="ICollectionEffectDescriptor"/> for the <c>devicestatus</c> collection.
/// Decomposition is handled directly by the controller write path, not as a side effect.
/// </summary>
/// <seealso cref="ICollectionEffectDescriptor"/>
public class DeviceStatusEffectDescriptor : ICollectionEffectDescriptor
{
    public string CollectionName => "devicestatus";
    public bool DecomposeToV4 => false;
    public bool BroadcastDataUpdateOnCreate => false;
}
