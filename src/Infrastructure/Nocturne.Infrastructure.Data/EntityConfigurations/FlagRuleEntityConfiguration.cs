using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class FlagRuleEntityConfiguration : IEntityTypeConfiguration<FlagRuleEntity>
{
    public void Configure(EntityTypeBuilder<FlagRuleEntity> entity)
    {
        entity.HasOne<SubjectEntity>()
            .WithMany()
            .HasForeignKey(e => e.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<TeamEntity>()
            .WithMany()
            .HasForeignKey(e => e.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.SubjectId);
        entity.HasIndex(e => e.TeamId);

        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_flag_rules_one_owner", "(subject_id IS NULL) <> (team_id IS NULL)");
            t.HasCheckConstraint(
                "ck_flag_rules_comparison", $"comparison IN ('{FlagRuleEntity.Below}', '{FlagRuleEntity.Above}')");
            t.HasCheckConstraint(
                "ck_flag_rules_level", $"level IN ('{FlagRuleEntity.Info}', '{FlagRuleEntity.Warn}', '{FlagRuleEntity.Hazard}')");
            t.HasCheckConstraint("ck_flag_rules_days", "days IS NULL OR days >= 1");
        });
    }
}

/// <summary>A rule's comparison and level as the table stores them.</summary>
internal static class FlagRuleColumns
{
    public static string Of(FlagRuleComparison comparison) => comparison switch
    {
        FlagRuleComparison.Below => FlagRuleEntity.Below,
        FlagRuleComparison.Above => FlagRuleEntity.Above,
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, null),
    };

    public static FlagRuleComparison ComparisonOf(string comparison) => comparison switch
    {
        FlagRuleEntity.Below => FlagRuleComparison.Below,
        FlagRuleEntity.Above => FlagRuleComparison.Above,
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, null),
    };

    public static string Of(NotificationUrgency level) => level switch
    {
        NotificationUrgency.Info => FlagRuleEntity.Info,
        NotificationUrgency.Warn => FlagRuleEntity.Warn,
        NotificationUrgency.Hazard => FlagRuleEntity.Hazard,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    public static NotificationUrgency LevelOf(string level) => level switch
    {
        FlagRuleEntity.Info => NotificationUrgency.Info,
        FlagRuleEntity.Warn => NotificationUrgency.Warn,
        FlagRuleEntity.Hazard => NotificationUrgency.Hazard,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}
