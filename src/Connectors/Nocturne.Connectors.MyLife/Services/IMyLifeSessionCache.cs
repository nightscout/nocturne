using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.MyLife.Services;

public record MyLifeSession(
    string ServiceUrl,
    string RestServiceUrl,
    string AuthToken,
    string UserId,
    string PatientId);

public interface IMyLifeSessionCache : IConnectorCacheInvalidator
{
    MyLifeSession? Get(Guid tenantId);
    void Set(Guid tenantId, MyLifeSession session);
}
