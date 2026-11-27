using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class PeopleViewEntityConfiguration : IEntityTypeConfiguration<PeopleViewEntity>
{
    public void Configure(EntityTypeBuilder<PeopleViewEntity> entity)
    {
        entity.HasOne<SubjectEntity>()
            .WithMany()
            .HasForeignKey(e => e.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(e => e.NameKey)
            .HasMaxLength(PeopleViewLimits.NameLength)
            .HasComputedColumnSql("lower(name)", stored: true);

        entity.HasIndex(e => new { e.SubjectId, e.NameKey }).IsUnique();

        var periods = string.Join(", ", TenantReviewPeriods.Days);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "ck_people_views_lens",
                $"(lens = '{PeopleViewEntity.Now}' AND review_days IS NULL) OR (lens = '{PeopleViewEntity.Review}' AND review_days IS NOT NULL AND review_days IN ({periods}))");
            t.HasCheckConstraint("ck_people_views_position", "position >= 0");
        });
    }
}

/// <summary>A view's lens as the table stores it.</summary>
internal static class PeopleViewColumns
{
    public static string Of(PeopleLens lens) => lens switch
    {
        PeopleLens.Now => PeopleViewEntity.Now,
        PeopleLens.Review => PeopleViewEntity.Review,
        _ => throw new ArgumentOutOfRangeException(nameof(lens), lens, null),
    };

    public static PeopleLens LensOf(string lens) => lens switch
    {
        PeopleViewEntity.Now => PeopleLens.Now,
        PeopleViewEntity.Review => PeopleLens.Review,
        _ => throw new ArgumentOutOfRangeException(nameof(lens), lens, null),
    };
}
