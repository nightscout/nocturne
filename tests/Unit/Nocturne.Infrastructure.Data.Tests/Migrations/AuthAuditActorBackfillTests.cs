using System.Text.RegularExpressions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// The actor column was added nullable with no backfill, so every row predating it answers
/// <c>actor_subject_id = X</c> with a miss. BackfillAuthAuditActorSubject mirrors
/// <c>subject_id</c> into it for exactly those rows, and does so without a tenant loop because
/// <c>auth_audit_log</c> is not tenant-scoped.
/// </summary>
[Trait("Category", "Unit")]
public class AuthAuditActorBackfillTests
{
    private const string MigrationName = "BackfillAuthAuditActorSubject";

    private static readonly Regex Backfill = new(
        @"UPDATE\s+auth_audit_log\s+SET\s+actor_subject_id\s*=\s*subject_id\s+"
        + @"WHERE\s+actor_subject_id\s+IS\s+NULL\s+AND\s+subject_id\s+IS\s+NOT\s+NULL",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void TheMigrationMirrorsTheSubjectIntoTheActorOnRowsThatHaveNoActor()
    {
        Backfill.IsMatch(MigrationSourceFiles.Text(MigrationName)).Should().BeTrue(
            "the backfill is the whole migration; a predicate that stops matching means "
            + "pre-existing history either stays unattributed or has an organic actor overwritten");
    }

    /// <summary>
    /// A statement against a tenant-scoped table matches nothing while no tenant GUC is set, so if
    /// <c>auth_audit_log</c> ever became tenant-scoped this migration would be a silent no-op and
    /// would have to be redone inside a tenant loop.
    /// </summary>
    [Fact]
    public void TheTableTheBackfillUpdatesIsNotTenantScoped()
    {
        typeof(ITenantScoped).IsAssignableFrom(typeof(AuthAuditLogEntity)).Should().BeFalse();
        MigrationSourceFiles.TenantScopedTableNames().Should().NotContain("auth_audit_log");
    }

    /// <summary>
    /// A data fill of this shape cannot be told apart from the writer's own output afterwards, so
    /// reversing it would destroy attribution rather than restore a prior state.
    /// </summary>
    [Fact]
    public void TheMigrationIsNotReversed()
    {
        var down = MigrationSourceFiles.Text(MigrationName);
        down = down[down.IndexOf("void Down(", StringComparison.Ordinal)..];

        down.Should().NotContain("migrationBuilder.");
    }
}
