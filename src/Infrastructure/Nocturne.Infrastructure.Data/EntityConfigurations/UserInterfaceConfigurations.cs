using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class ClockFaceEntityConfiguration : IEntityTypeConfiguration<ClockFaceEntity>
{
    public void Configure(EntityTypeBuilder<ClockFaceEntity> entity)
    {
        entity
            .HasIndex(cf => cf.UserId)
            .HasDatabaseName("ix_clock_faces_user_id");

        entity
            .HasIndex(cf => cf.CreatedAt)
            .HasDatabaseName("ix_clock_faces_created_at")
            .IsDescending();

        entity
            .HasIndex(cf => new { cf.UserId, cf.CreatedAt })
            .HasDatabaseName("ix_clock_faces_user_created_at")
            .IsDescending(false, true);

        entity.Property(e => e.ConfigJson).HasDefaultValue("{}");
    }
}

internal sealed class CoachMarkStateEntityConfiguration : IEntityTypeConfiguration<CoachMarkStateEntity>
{
    public void Configure(EntityTypeBuilder<CoachMarkStateEntity> entity)
    {
        entity
            .HasIndex(e => new { e.SubjectId, e.MarkKey })
            .IsUnique();
    }
}

internal sealed class SetupHubItemEntityConfiguration : IEntityTypeConfiguration<SetupHubItemEntity>
{
    public void Configure(EntityTypeBuilder<SetupHubItemEntity> entity)
    {
        entity.Property(e => e.ItemKey).HasConversion<string>();
        entity.Property(e => e.State).HasConversion<string>();
        entity.HasIndex(e => new { e.TenantId, e.ItemKey })
            .IsUnique()
            .HasDatabaseName("ix_setup_hub_items_tenant_item_key");
    }
}

internal sealed class SetupHubAdditionEntityConfiguration : IEntityTypeConfiguration<SetupHubAdditionEntity>
{
    public void Configure(EntityTypeBuilder<SetupHubAdditionEntity> entity)
    {
        entity.Property(e => e.ItemKey).HasConversion<string>();
        entity.Property(e => e.RecordKind).HasConversion<string>();
        entity.HasIndex(e => new { e.TenantId, e.ItemKey, e.RecordKind })
            .HasDatabaseName("ix_setup_hub_additions_tenant_item_kind");
    }
}

internal sealed class TranslationDraftEntityConfiguration : IEntityTypeConfiguration<TranslationDraftEntity>
{
    public void Configure(EntityTypeBuilder<TranslationDraftEntity> entity)
    {
        // TranslationDraftEntity: the logical key is unique via a functional
        // index created with raw SQL in the migration (see AddTranslationDrafts);
        // only the lookup index is declared here. Both lead with TenantId
        // because a subject is a global membership scope and can hold drafts in
        // more than one tenant.
        entity
            .HasIndex(e => new { e.TenantId, e.SubjectId, e.Locale });
    }
}
