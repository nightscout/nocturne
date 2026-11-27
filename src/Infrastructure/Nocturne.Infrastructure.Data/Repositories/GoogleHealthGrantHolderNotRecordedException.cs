namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="GoogleHealthGrantHolderRepository.RecordAsync"/> could neither insert nor update the
/// tenant's row, because the tenant is gone.
/// </summary>
public sealed class GoogleHealthGrantHolderNotRecordedException(Guid tenantId)
    : Exception($"Google Health could not record the grant holder state of tenant {tenantId}");
