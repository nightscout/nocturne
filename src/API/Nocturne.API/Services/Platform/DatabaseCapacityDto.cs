namespace Nocturne.API.Services.Platform;

/// <summary>
/// What the instance's database holds and how it is shared out, for a platform administrator.
/// Every figure is in bytes. <paramref name="UsedPercent"/> is the whole database against
/// <paramref name="BudgetBytes"/>, the configured <c>DbSize:MaxMiB</c>.
/// </summary>
public sealed record DatabaseCapacityDto(
    long DatabaseSizeBytes,
    long DataSizeBytes,
    long IndexSizeBytes,
    int TenantCount,
    long AverageTenantBytes,
    long LargestTenantBytes,
    long BudgetBytes,
    int UsedPercent,
    List<TenantStoreSizeDto> Tenants);

/// <summary>One tenant's estimated share of the database. See <see cref="IStoreSizeReader"/>.</summary>
public sealed record TenantStoreSizeDto(
    Guid TenantId,
    string Slug,
    string DisplayName,
    long DataSizeBytes,
    long IndexSizeBytes);
