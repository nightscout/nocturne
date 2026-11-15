using Nocturne.Core.Models.AppBuilds;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

public static class AppBuildLinkMapper
{
    public static AppBuildLink ToDomainModel(AppBuildLinkEntity entity) => new(
        entity.TargetId,
        entity.InstallationId,
        entity.AccountLogin,
        entity.AccountIsOrganization,
        entity.RepositoryFullName,
        entity.AdminRepositories,
        entity.LinkedBySubjectId,
        entity.CreatedAt,
        entity.UpdatedAt);

    /// <summary>Copies everything but the id, tenant and timestamps, which the row and the save own.</summary>
    public static void Apply(AppBuildLink link, AppBuildLinkEntity entity)
    {
        entity.TargetId = link.TargetId;
        entity.InstallationId = link.InstallationId;
        entity.AccountLogin = link.AccountLogin;
        entity.AccountIsOrganization = link.AccountIsOrganization;
        entity.RepositoryFullName = link.RepositoryFullName;
        entity.AdminRepositories = [.. link.AdminRepositories];
        entity.LinkedBySubjectId = link.LinkedBySubjectId;
    }
}
