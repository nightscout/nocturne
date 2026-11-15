using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A tenant's link to the GitHub App installation that builds one build target, one row per target.
/// It holds no token: installation tokens are minted from the App key on use.
/// </summary>
[Table("app_build_links")]
public class AppBuildLinkEntity : ITenantScoped, IEntityTimestamped, IAuditable
{
    [Key]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("target_id")]
    [MaxLength(32)]
    public string TargetId { get; set; } = string.Empty;

    [Column("installation_id")]
    public long InstallationId { get; set; }

    /// <summary>The GitHub user or organization the App is installed on.</summary>
    [Column("account_login")]
    [MaxLength(100)]
    public string AccountLogin { get; set; } = string.Empty;

    [Column("account_is_organization")]
    public bool AccountIsOrganization { get; set; }

    /// <summary><c>owner/name</c>; null until the owner chooses one of several the installation exposes.</summary>
    [Column("repository_full_name")]
    [MaxLength(201)]
    public string? RepositoryFullName { get; set; }

    /// <summary>
    /// The installation's repositories the linking person administered on GitHub at link time, as
    /// <c>owner/name</c>. <see cref="RepositoryFullName"/> is always one of them.
    /// </summary>
    [Column("admin_repositories")]
    public string[] AdminRepositories { get; set; } = [];

    [Column("linked_by_subject_id")]
    public Guid LinkedBySubjectId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
